import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { resolveAnalysisTarget } from './solution/resolveTarget';
import { ensureDotnet, DotnetNotAvailableError } from './runtime/ensureDotnet';
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

export function activate(context: vscode.ExtensionContext): void {
  const output = vscode.window.createOutputChannel('SharpDeps');
  const diagnostics = new CycleDiagnostics(output);
  const store = new ReportStore();
  const bridge = createReportBridge(store, { maxProjectionNodes: 300, maxProjectionEdges: 1000 });
  context.subscriptions.push(output, diagnostics);

  // Generated code is opened read-only from the analysis result (SD-011): the provider
  // serves the retained content and never writes a file into the workspace.
  context.subscriptions.push(
    vscode.workspace.registerTextDocumentContentProvider(
      GENERATED_DOCUMENT_SCHEME,
      createGeneratedDocumentProvider(store)
    )
  );

  const workRoot = path.join(
    context.globalStorageUri?.fsPath ?? context.extensionUri.fsPath,
    'runs'
  );
  try {
    fs.mkdirSync(workRoot, { recursive: true });
  } catch {
    // Falling back to the temp directory is handled by the controller.
  }

  let launcher: { dotnetPath: string; analyzerPath: string } | undefined;

  const controller = new AnalysisController({
    workRoot,
    onLog: (line, source) => output.appendLine(`[${source}] ${line}`),
    processFactory: (request, workDirectory) => {
      if (!launcher) {
        throw new AnalyzerError('The analyzer has not been resolved yet.');
      }

      return {
        command: launcher.dotnetPath,
        args: [
          launcher.analyzerPath,
          '--solution',
          request.targetPath,
          '--output',
          path.join(workDirectory, 'report.json'),
          '--max-projects',
          String(request.maxProjects ?? 60),
          '--max-edges',
          String(request.maxEdges ?? 200),
          '--analysis-id',
          request.analysisId ?? '',
          '--watch-stdin'
        ],
        cwd: path.dirname(launcher.analyzerPath)
      };
    },
    onCompleted: async (outcome) => {
      if (!outcome.reportPath) {
        return;
      }

      try {
        await store.register({
          directory: path.dirname(outcome.reportPath),
          reportFileName: path.basename(outcome.reportPath)
        });
      } catch (error) {
        // A result that fails validation must not be shown as if it were complete.
        const message = error instanceof ReportStoreError ? error.message : String(error);
        output.appendLine(`The analysis result was rejected: ${message}`);
      }
    }
  });
  context.subscriptions.push({ dispose: () => void controller.dispose() });

  let lastTarget: vscode.Uri | undefined;

  const rootDirectory = (): string | undefined =>
    lastTarget ? path.dirname(lastTarget.fsPath) : undefined;

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

  function panelHost(): Parameters<typeof CodeMapPanel.show>[1] {
    return {
      store,
      bridge,
      output,
      rootDirectory,
      targetName: () => (lastTarget ? path.basename(lastTarget.fsPath) : ''),
      saveViewState: (state) => void context.workspaceState.update(VIEW_STATE_KEY, state),
      loadViewState: () => context.workspaceState.get<Record<string, unknown>>(VIEW_STATE_KEY),
      onAnalyze: (mode) => void runAndShow(lastTarget, mode),
      onCancel: () => controller.cancel('user')
    };
  }

  const VIEW_STATE_KEY = 'sharpdeps.viewState';

  /**
   * Marks the registered result as stale when an analysed file changes (SD-021).
   * Auto-refresh stays off by default: the user decides when to analyze again.
   */
  function watchForStaleness(): void {
    const relativePathOf = (document: vscode.TextDocument): string | undefined => {
      const analysisId = store.currentAnalysisId;
      const root = rootDirectory();
      if (!analysisId || !root || document.uri.scheme !== 'file') {
        return undefined;
      }

      const relative = path.relative(root, document.uri.fsPath).replace(/\\/g, '/');
      return store.documentIdForPath(analysisId, relative) ? relative : undefined;
    };

    const markStale = (reason: 'unsavedChange' | 'savedChange', relative: string): void => {
      const analysisId = store.currentAnalysisId;
      if (!analysisId) {
        return;
      }

      output.appendLine(`Result ${analysisId} is stale (${reason}): ${relative}`);
      CodeMapPanel.currentPanel?.notifyStale(analysisId, reason, [relative]);
    };

    context.subscriptions.push(
      vscode.workspace.onDidChangeTextDocument((event) => {
        if (!event.document.isDirty) {
          return;
        }

        const relative = relativePathOf(event.document);
        if (relative) {
          markStale('unsavedChange', relative);
        }
      }),
      vscode.workspace.onDidSaveTextDocument((document) => {
        const relative = relativePathOf(document);
        if (relative) {
          markStale('savedChange', relative);
        }
      }),
      vscode.workspace.onDidChangeConfiguration((event) => {
        if (event.affectsConfiguration('sharpdeps')) {
          const analysisId = store.currentAnalysisId;
          if (analysisId) {
            CodeMapPanel.currentPanel?.notifyStale(analysisId, 'profileChange');
          }
        }
      })
    );
  }

  watchForStaleness();

  async function showTypeFromEditor(kind: 'dependencies' | 'dependents'): Promise<void> {
    const resolution = await resolveTypeAtCursor(store, rootDirectory(), output);
    if (!resolution.ok) {
      // No result, Quick only, or no declaration index: say why and offer to analyze.
      const choice = await vscode.window.showInformationMessage(
        `SharpDeps: ${resolution.reason}`,
        '解析する'
      );
      if (choice === '解析する') {
        await runAndShow(lastTarget);
      }
      return;
    }

    const panel = CodeMapPanel.show(context.extensionUri, panelHost());
    panel.revealEntity(resolution.value.typeId, {
      kind,
      id: resolution.value.typeId,
      depth: 1
    });
    output.appendLine(
      `Revealing ${kind} of ${resolution.value.typeId} in ${
        resolution.value.projectName ?? 'the current analysis'
      }.`
    );
  }

  async function runAndShow(
    requestedTarget?: vscode.Uri,
    requestedMode?: 'quick' | 'semantic'
  ): Promise<void> {
    const target = await resolveAnalysisTarget(requestedTarget);
    if (!target) {
      return;
    }
    lastTarget = target;
    // A new run replaces the Problems entries: findings from a previous analysis are
    // never left behind while this one is running or after it fails.
    diagnostics.clear();

    const trusted = requireTrustedWorkspace();
    if (!trusted) {
      return;
    }

    await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: 'SharpDeps: Analyzing dependencies…',
        cancellable: true
      },
      async (progress, token) => {
        try {
          const dotnet = await ensureDotnet(context);
          const analyzer = locateAnalyzer(context);
          const config = vscode.workspace.getConfiguration('sharpdeps');
          const mode = requestedMode ?? config.get<'quick' | 'semantic'>('analysisMode', 'quick');
          if (mode === 'semantic') {
            // Semantic analysis is wired in SD-007/SD-015; Quick stays the default and
            // the mode is never silently substituted.
            void vscode.window.showWarningMessage(
              'SharpDeps: Semantic analysis is not available in this build; using Quick is an explicit choice.'
            );
            return;
          }

          launcher = { dotnetPath: dotnet.dotnetPath, analyzerPath: analyzer.path };
          const cancellation = token.onCancellationRequested(() => controller.cancel('user'));

          progress.report({ message: STAGE_LABELS.discover });
          const outcome = await controller.start({
            targetPath: target.fsPath,
            mode,
            maxProjects: config.get<number>('maxProjects', 60),
            maxEdges: config.get<number>('maxEdges', 200),
            timeoutMs: config.get<number>('analysisTimeoutSeconds', 180) * 1000
          });
          cancellation.dispose();

          if (outcome.status !== 'completed' || !outcome.reportPath) {
            if (outcome.error) {
              output.appendLine(outcome.error);
              if (outcome.detail) {
                output.appendLine(outcome.detail);
              }
            }

            if (outcome.status === 'failed') {
              void vscode.window
                .showErrorMessage(
                  `SharpDeps: ${outcome.error ?? 'The analysis failed.'}`,
                  'Show Output'
                )
                .then((choice) => {
                  if (choice === 'Show Output') {
                    output.show();
                  }
                });
            }

            return;
          }

          // The v1 report still feeds the cycle diagnostics; the panel itself renders
          // the v2 shell from the result store.
          const v1Path = path.join(path.dirname(outcome.reportPath), 'report.json');
          const report = JSON.parse(await fs.promises.readFile(v1Path, 'utf8')) as CodeMapReport;

          const panel = CodeMapPanel.show(context.extensionUri, panelHost());

          const analysisId = store.currentAnalysisId;
          if (analysisId) {
            panel.notifyAnalysis(analysisId);
            if (store.getReport(analysisId).mode === 'semantic') {
              // Semantic cycles are anchored at a real evidence position.
              void diagnostics.updateFromStore(store, analysisId, rootDirectory());
            } else {
              diagnostics.update(report, analysisId, 'quick');
            }
            const stored = store.getReport(analysisId);
            progress.report({
              message: `Analyzed ${stored.coverage.analyzed} project(s) · ${stored.relations.length} relation(s)`
            });
            output.appendLine(
              `Analysis ${analysisId} completed: ${stored.completeness}, ` +
                `${stored.relations.length} relation(s), ${stored.namespaces.length} namespace(s).`
            );
          }
        } catch (err) {
          reportError(err, output);
        }
      }
    );
  }

  context.subscriptions.push(
    vscode.commands.registerCommand('sharpdeps.showDependencyMap', (uri?: vscode.Uri) =>
      runAndShow(uri)
    ),
    vscode.commands.registerCommand('sharpdeps.refresh', () => runAndShow(lastTarget)),
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
    vscode.commands.registerCommand(
      'sharpdeps.exportSvg',
      () =>
        void vscode.window.showInformationMessage(
          'SharpDeps: use Export ▾ in the map panel to save the current selection as SVG.'
        )
    ),
    vscode.commands.registerCommand(
      'sharpdeps.exportPng',
      () =>
        void vscode.window.showInformationMessage(
          'SharpDeps: PNG export from the map arrives with SD-022.'
        )
    )
  );
}

export function deactivate(): void {
  // The controller and output channel are disposed through context.subscriptions.
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
