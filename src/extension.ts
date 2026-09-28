import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import { randomBytes } from 'node:crypto';
import type { ProfileRequest } from './view/protocolV2';
import * as vscode from 'vscode';
import { resolveAnalysisTarget } from './solution/resolveTarget';
import {
  ensureDotnet,
  ensureSemanticDotnet,
  DotnetNotAvailableError
} from './runtime/ensureDotnet';
import { AnalyzerError, locateAnalyzer } from './analyzer/runAnalyzer';
import { AnalysisController, type AnalysisStage } from './analyzer/analysisController';
import { ReportStore, ReportStoreError } from './analyzer/reportStore';
import { createReportBridge } from './analyzer/reportBridge';
import type { CodeMapReport } from './analyzer/types';
import { CodeMapPanel } from './view/codeMapPanel';
import { CycleDiagnostics } from './diagnostics/cycleDiagnostics';
import { createGeneratedDocumentProvider } from './generatedDocuments/generatedDocumentProvider';
import { GENERATED_DOCUMENT_SCHEME } from './generatedDocuments/documentUri';
import { resolveTypeAtCursor } from './commands/typeNavigation';
import { trustDecision } from './security/trust';

const STAGE_LABELS: Record<AnalysisStage, string> = {
  discover: 'Discovering projects…',
  load: 'Loading projects…',
  compile: 'Compiling…',
  extract: 'Collecting evidence…',
  aggregate: 'Aggregating…',
  write: 'Writing results…'
};

export function activate(context: vscode.ExtensionContext): ReturnType<typeof extensionTestApi> {
  const channel = vscode.window.createOutputChannel('SharpDeps');
  // A short ring buffer so the end-to-end suite can report why a run failed (SD-027).
  // The channel object itself is not patched: VS Code's output channel properties are
  // read-only, so the tail lives in a thin wrapper.
  const outputTail: string[] = [];
  const output = {
    name: 'SharpDeps',
    append: (value: string) => channel.append(value),
    appendLine: (value: string) => {
      outputTail.push(value);
      if (outputTail.length > 60) {
        outputTail.shift();
      }

      channel.appendLine(value);
    },
    replace: (value: string) => channel.replace(value),
    clear: () => channel.clear(),
    show: (...args: unknown[]) => {
      (channel.show as (...parameters: unknown[]) => void)(...args);
    },
    hide: () => channel.hide(),
    dispose: () => channel.dispose()
  } as unknown as vscode.OutputChannel;
  const diagnostics = new CycleDiagnostics(output);
  const store = new ReportStore();
  const projectionLimits = (granularity: 'project' | 'namespace' | 'type') => {
    const config = vscode.workspace.getConfiguration('sharpdeps');
    return {
      maxNodes: config.get<number>(
        granularity === 'type' ? 'maxVisibleTypes' : 'maxProjects',
        granularity === 'type' ? 100 : 60
      ),
      maxEdges: config.get<number>('maxEdges', 200)
    };
  };
  const bridge = createReportBridge(store, { projectionLimits });
  context.subscriptions.push(output, diagnostics);

  // Generated code is opened read-only from the analysis result (SD-011): the provider
  // serves the retained content and never writes a file into the workspace.
  context.subscriptions.push(
    vscode.workspace.registerTextDocumentContentProvider(
      GENERATED_DOCUMENT_SCHEME,
      createGeneratedDocumentProvider(store)
    )
  );

  let workRoot = path.join(context.globalStorageUri?.fsPath ?? context.extensionUri.fsPath, 'runs');
  try {
    fs.mkdirSync(workRoot, { recursive: true });
  } catch {
    workRoot = os.tmpdir();
  }

  const targets = new Map<string, string>();
  let requestGeneration = 0;
  let activeAnalysisId: string | undefined;
  let inputVersion = 0;
  const storedTarget = context.workspaceState.get<string>('sharpdeps.lastTarget');
  let lastTarget: vscode.Uri | undefined = storedTarget ? vscode.Uri.file(storedTarget) : undefined;
  let lastMode: 'quick' | 'semantic' | undefined;
  let lastProfile: ProfileRequest = { configuration: 'Debug' };
  const VIEW_STATE_KEY = 'sharpdeps.viewState';
  const controller = new AnalysisController({
    workRoot,
    onLog: (line, source) => output.appendLine(`[${source}] ${line}`),
    onProgress: (event) => {
      if (event.analysisId === activeAnalysisId) CodeMapPanel.currentPanel?.notifyProgress(event);
    },
    processFactory: (request, workDirectory) => {
      const launcher = request.executable;
      if (!launcher) throw new AnalyzerError('The analyzer has not been resolved yet.');
      return {
        command: launcher.dotnetPath,
        args: [
          launcher.analyzerPath,
          '--solution',
          request.targetPath,
          '--output',
          path.join(workDirectory, 'report.json'),
          '--analysis-id',
          request.analysisId!,
          '--watch-stdin',
          ...(request.mode === 'semantic'
            ? [
                '--configuration',
                request.configuration ?? 'Debug',
                ...(request.platform ? ['--platform', request.platform] : []),
                ...(request.projectVariants?.length
                  ? ['--project-variants', JSON.stringify(request.projectVariants)]
                  : [])
              ]
            : [
                '--max-projects',
                String(request.maxProjects ?? 60),
                '--max-edges',
                String(request.maxEdges ?? 200)
              ])
        ],
        cwd: path.dirname(request.targetPath)
      };
    },
    onCompleted: async (outcome, isCurrent) => {
      if (!outcome.reportPath) throw new AnalyzerError('The analyzer did not write a result.');
      await store.register({
        directory: path.dirname(outcome.reportPath),
        reportFileName: path.basename(outcome.reportPath),
        targetPath: targets.get(outcome.analysisId),
        isCurrent
      });
    }
  });
  context.subscriptions.push({
    dispose: () => {
      requestGeneration++;
      void controller.dispose();
    }
  });
  const rootDirectory = (analysisId = store.currentAnalysisId): string | undefined => {
    const target = analysisId ? store.getTargetPath(analysisId) : undefined;
    return target ? path.dirname(target) : undefined;
  };

  /**
   * An untrusted workspace must not run the analyzer: it evaluates MSBuild and project
   * logic (SD-023). Reading a stored result stays allowed because it is data only.
   */
  function requireTrustedWorkspace(): boolean {
    const decision = trustDecision(vscode.workspace.isTrusted);
    if (decision.allowed) {
      return true;
    }

    output.appendLine('Analysis refused: the workspace is not trusted.');
    void vscode.window
      .showWarningMessage(decision.message ?? 'SharpDeps: 解析できません。', '信頼を管理')
      .then((choice) => {
        if (choice === '信頼を管理') {
          void vscode.commands.executeCommand('workbench.trust.manage');
        }
      });
    return false;
  }

  function cancelAnalysis(): void {
    requestGeneration++;
    controller.cancel('user');
    if (activeAnalysisId)
      CodeMapPanel.currentPanel?.notifyFailure(activeAnalysisId, 'Analysis stopped.', true);
    activeAnalysisId = undefined;
  }

  function panelHost(): Parameters<typeof CodeMapPanel.show>[1] {
    return {
      store,
      bridge,
      output,
      rootDirectory,
      projectionLimits,
      targetName: () =>
        store.currentAnalysisId
          ? path.basename(store.getTargetPath(store.currentAnalysisId) ?? '')
          : '',
      saveViewState: (state) => void context.workspaceState.update(VIEW_STATE_KEY, state),
      loadViewState: () => context.workspaceState.get<Record<string, unknown>>(VIEW_STATE_KEY),
      onAnalyze: (mode, profile) => void runAndShow(lastTarget, mode, profile),
      onCancel: cancelAnalysis,
      onDispose: cancelAnalysis
    };
  }
  context.subscriptions.push(
    vscode.window.registerWebviewPanelSerializer(CodeMapPanel.viewType, {
      async deserializeWebviewPanel(panel, state) {
        if (state && typeof state === 'object')
          await context.workspaceState.update(VIEW_STATE_KEY, state);
        CodeMapPanel.restore(panel, context.extensionUri, panelHost());
      }
    })
  );

  const targetWatchers: vscode.Disposable[] = [];
  const affectsInput = (file: string, analysisId: string): boolean => {
    const root = rootDirectory(analysisId);
    if (!root || /[\\/](bin|obj|node_modules|\.git)[\\/]/.test(file)) return false;
    const relative = path.relative(root, file).replace(/\\/g, '/');
    return (
      !!store.documentIdForPath(analysisId, relative) ||
      (!relative.startsWith('../') &&
        /\.(cs|csproj|fsproj|vbproj|vcxproj|sln|slnx|props|targets)$/.test(file)) ||
      [
        'global.json',
        'Directory.Build.props',
        'Directory.Build.targets',
        'Directory.Packages.props',
        'NuGet.Config'
      ].includes(path.basename(file))
    );
  };
  const changed = (uri: vscode.Uri, reason: 'savedChange' | 'unsavedChange') => {
    if (uri.scheme !== 'file') return;
    if (/[\\/](bin|obj|node_modules|\.git)[\\/]/.test(uri.fsPath)) return;
    inputVersion++;
    for (const id of store.analysisIds) {
      if (affectsInput(uri.fsPath, id)) {
        store.markStale(id);
        CodeMapPanel.currentPanel?.notifyStale(id, reason);
      }
    }
  };
  function watchResult(analysisId: string): void {
    targetWatchers.splice(0).forEach((watcher) => watcher.dispose());
    const root = rootDirectory(analysisId)!;
    const directories = new Set([root]);
    for (const doc of store.getReport(analysisId).sourceManifest) {
      if (doc.origin === 'userSource' && doc.relativePath.startsWith('..'))
        directories.add(path.dirname(path.resolve(root, doc.relativePath)));
    }
    for (const directory of directories) {
      const watcher = vscode.workspace.createFileSystemWatcher(
        new vscode.RelativePattern(
          directory,
          '**/*.{cs,csproj,fsproj,vbproj,vcxproj,sln,slnx,props,targets,json,config}'
        )
      );
      targetWatchers.push(
        watcher,
        watcher.onDidCreate((uri) => changed(uri, 'savedChange')),
        watcher.onDidChange((uri) => changed(uri, 'savedChange')),
        watcher.onDidDelete((uri) => changed(uri, 'savedChange'))
      );
    }
    // MSBuild and SDK selection also read configuration above the workspace root.
    for (let ancestor = path.dirname(root); ; ancestor = path.dirname(ancestor)) {
      const watcher = vscode.workspace.createFileSystemWatcher(
        new vscode.RelativePattern(
          ancestor,
          '{global.json,Directory.Build.props,Directory.Build.targets,Directory.Packages.props,NuGet.Config}'
        )
      );
      targetWatchers.push(
        watcher,
        watcher.onDidCreate((uri) => changed(uri, 'savedChange')),
        watcher.onDidChange((uri) => changed(uri, 'savedChange')),
        watcher.onDidDelete((uri) => changed(uri, 'savedChange'))
      );
      if (path.dirname(ancestor) === ancestor) break;
    }
  }
  const configurationWatcher = vscode.workspace.createFileSystemWatcher(
    '**/{global.json,Directory.Build.*,Directory.Packages.props,NuGet.Config}'
  );
  context.subscriptions.push(
    configurationWatcher,
    configurationWatcher.onDidChange((uri) => changed(uri, 'savedChange')),
    configurationWatcher.onDidCreate((uri) => changed(uri, 'savedChange')),
    configurationWatcher.onDidDelete((uri) => changed(uri, 'savedChange')),
    { dispose: () => targetWatchers.splice(0).forEach((watcher) => watcher.dispose()) },
    vscode.workspace.onDidChangeTextDocument((event) => {
      if (event.document.isDirty) changed(event.document.uri, 'unsavedChange');
    }),
    vscode.workspace.onDidSaveTextDocument((document) => changed(document.uri, 'savedChange')),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration('sharpdeps'))
        for (const id of store.analysisIds) {
          store.markStale(id);
          CodeMapPanel.currentPanel?.notifyStale(id, 'profileChange');
        }
    })
  );

  async function showTypeFromEditor(kind: 'dependencies' | 'dependents'): Promise<void> {
    const resolution = await resolveTypeAtCursor(store, rootDirectory(), output);
    if (!resolution.ok) {
      // No result, Quick only, or no declaration index: say why and offer to analyze.
      const choice = await vscode.window.showInformationMessage(
        `SharpDeps: ${resolution.reason}`,
        '解析する'
      );
      if (choice === '解析する') {
        await runAndShow(lastTarget, 'semantic');
      }
      return;
    }

    const panel = CodeMapPanel.show(context.extensionUri, panelHost());
    panel.revealEntity(
      resolution.value.typeId,
      {
        kind,
        id: resolution.value.typeId,
        depth: 1
      },
      'type'
    );
    output.appendLine(
      `Revealing ${kind} of ${resolution.value.typeId} in ${
        resolution.value.projectName ?? 'the current analysis'
      }.`
    );
  }

  async function runAndShow(
    requestedTarget?: vscode.Uri,
    requestedMode?: 'quick' | 'semantic',
    profile: ProfileRequest = lastProfile
  ): Promise<void> {
    if (!requireTrustedWorkspace()) return;
    const ticket = ++requestGeneration;
    controller.cancel('superseded');
    const target = await resolveAnalysisTarget(requestedTarget);
    if (!target || ticket !== requestGeneration || !requireTrustedWorkspace()) return;
    const config = vscode.workspace.getConfiguration('sharpdeps');
    const mode = requestedMode ?? config.get<'quick' | 'semantic'>('analysisMode', 'quick');
    const analysisId = `an_${randomBytes(8).toString('hex')}`;
    const versionAtStart = inputVersion;
    activeAnalysisId = analysisId;
    targets.set(analysisId, target.fsPath);
    const panel = CodeMapPanel.show(context.extensionUri, panelHost());
    panel.notifyStarted(analysisId, mode, {
      name: path.basename(target.fsPath),
      relativePath: vscode.workspace.asRelativePath(target)
    });
    diagnostics.clear();
    await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: 'SharpDeps: Analyzing dependencies…',
        cancellable: true
      },
      async (progress, token) => {
        const cancellation = token.onCancellationRequested(cancelAnalysis);
        try {
          const dotnet =
            mode === 'semantic'
              ? await ensureSemanticDotnet(path.dirname(target.fsPath))
              : await ensureDotnet(context);
          const analyzer = locateAnalyzer(context, mode);
          if (
            ticket !== requestGeneration ||
            token.isCancellationRequested ||
            !requireTrustedWorkspace()
          )
            return;
          progress.report({ message: STAGE_LABELS.discover });
          const outcome = await controller.start({
            targetPath: target.fsPath,
            mode,
            analysisId,
            configuration: profile.configuration ?? 'Debug',
            platform: profile.platform ?? undefined,
            projectVariants: profile.projectVariants,
            executable: { dotnetPath: dotnet.dotnetPath, analyzerPath: analyzer.path },
            maxProjects: config.get<number>('maxProjects', 60),
            maxEdges: config.get<number>('maxEdges', 200),
            timeoutMs: config.get<number>('analysisTimeoutSeconds', 180) * 1000
          });
          if (ticket !== requestGeneration) return;
          if (outcome.status !== 'completed' || !outcome.reportPath) {
            panel.notifyFailure(
              analysisId,
              outcome.error ?? 'The analysis did not complete.',
              outcome.status === 'cancelled'
            );
            if (outcome.status !== 'cancelled')
              reportError(new AnalyzerError(outcome.error ?? 'The analysis failed.'), output);
            return;
          }
          lastTarget = target;
          lastMode = mode;
          lastProfile = profile;
          void context.workspaceState.update('sharpdeps.lastTarget', target.fsPath);
          const stored = store.getReport(analysisId);
          if (
            versionAtStart !== inputVersion ||
            vscode.workspace.textDocuments.some(
              (doc) => doc.isDirty && affectsInput(doc.uri.fsPath, analysisId)
            )
          )
            store.markStale(analysisId);
          panel.notifyAnalysis(analysisId);
          watchResult(analysisId);
          if (mode === 'semantic')
            await diagnostics.updateFromStore(store, analysisId, rootDirectory(analysisId));
          else {
            const report = JSON.parse(
              await fs.promises.readFile(
                path.join(path.dirname(outcome.reportPath), 'report.json'),
                'utf8'
              )
            ) as CodeMapReport;
            if (ticket === requestGeneration) diagnostics.update(report, analysisId, 'quick');
          }
          progress.report({ message: `Analyzed ${stored.coverage.analyzed} project(s)` });
          output.appendLine(
            `Analysis ${analysisId}: ${stored.completeness}, ${stored.relations.length} relation(s).`
          );
        } catch (error) {
          if (ticket === requestGeneration) {
            panel.notifyFailure(analysisId, error instanceof Error ? error.message : String(error));
            reportError(error, output);
          }
        } finally {
          cancellation.dispose();
          targets.delete(analysisId);
          if (activeAnalysisId === analysisId) activeAnalysisId = undefined;
        }
      }
    );
  }

  context.subscriptions.push(
    vscode.commands.registerCommand('sharpdeps.showDependencyMap', (uri?: vscode.Uri) =>
      runAndShow(uri)
    ),
    vscode.commands.registerCommand('sharpdeps.refresh', () =>
      runAndShow(lastTarget, lastMode, lastProfile)
    ),
    vscode.commands.registerCommand('sharpdeps.showTypeDependencies', () =>
      showTypeFromEditor('dependencies')
    ),
    vscode.commands.registerCommand('sharpdeps.showTypeDependents', () =>
      showTypeFromEditor('dependents')
    ),
    vscode.commands.registerCommand(
      'sharpdeps.copyMermaid',
      () => void CodeMapPanel.currentPanel?.copyMermaid()
    ),
    vscode.commands.registerCommand('sharpdeps.exportSvg', () =>
      CodeMapPanel.currentPanel?.requestExport('svg')
    ),
    vscode.commands.registerCommand('sharpdeps.exportPng', () =>
      CodeMapPanel.currentPanel?.requestExport('png')
    )
  );

  return extensionTestApi(store, context, outputTail);
}

export function deactivate(): void {
  // The controller and output channel are disposed through context.subscriptions.
}

/**
 * Test surface for the end-to-end suite (SD-027): the analysis ids the store holds and
 * the persisted view state. Nothing here changes behaviour; it only lets an automated
 * run assert what the UI would show.
 */
export function extensionTestApi(
  store: ReportStore,
  context: vscode.ExtensionContext,
  outputTail: string[]
) {
  return {
    getAnalysisIds: () => store.analysisIds,
    getCurrentAnalysisId: () => store.currentAnalysisId,
    getCurrentReport: () =>
      store.currentAnalysisId ? store.getReport(store.currentAnalysisId) : undefined,
    getViewState: () => context.workspaceState.get<Record<string, unknown>>('sharpdeps.viewState'),
    /** Last lines of the SharpDeps output channel, for end-to-end failure reports. */
    getOutputTail: () => [...outputTail]
  };
}

function reportError(err: unknown, output: vscode.OutputChannel): void {
  if (err instanceof DotnetNotAvailableError) {
    output.appendLine(err.message);
    return;
  }

  if (err instanceof AnalyzerError || err instanceof ReportStoreError) {
    output.appendLine(err.message);
    void vscode.window
      .showErrorMessage(`SharpDeps: ${err.message}`, 'Show Output')
      .then((choice) => {
        if (choice === 'Show Output') {
          output.show();
        }
      });
    return;
  }

  const message = err instanceof Error ? err.message : String(err);
  output.appendLine(message);
  void vscode.window.showErrorMessage(`SharpDeps: ${message}`);
}
