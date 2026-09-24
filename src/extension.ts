import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { resolveAnalysisTarget } from './solution/resolveTarget';
import { ensureDotnet, DotnetNotAvailableError } from './runtime/ensureDotnet';
import { AnalyzerError, locateAnalyzer } from './analyzer/runAnalyzer';
import { AnalysisController, type AnalysisStage } from './analyzer/analysisController';
import { ReportStore, ReportStoreError } from './analyzer/reportStore';
import type { CodeMapReport } from './analyzer/types';
import { buildViewModel } from './view/viewModel';
import { CodeMapPanel } from './view/codeMapPanel';
import { CycleDiagnostics } from './diagnostics/cycleDiagnostics';
import { createGeneratedDocumentProvider } from './generatedDocuments/generatedDocumentProvider';
import { GENERATED_DOCUMENT_SCHEME } from './generatedDocuments/documentUri';

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

  async function runAndShow(requestedTarget?: vscode.Uri): Promise<void> {
    const target = await resolveAnalysisTarget(requestedTarget);
    if (!target) {
      return;
    }
    lastTarget = target;

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
          const mode = config.get<'quick' | 'semantic'>('analysisMode', 'quick');
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

          // The v1 report stays the panel's render input until the UI migration
          // (SD-015); the v2 result is already registered for the new UI.
          const v1Path = path.join(path.dirname(outcome.reportPath), 'report.json');
          const report = JSON.parse(await fs.promises.readFile(v1Path, 'utf8')) as CodeMapReport;

          const panel = CodeMapPanel.show(context.extensionUri, output);
          panel.setModel(buildViewModel(report));
          diagnostics.update(report);

          const analysisId = store.currentAnalysisId;
          if (analysisId) {
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
    vscode.commands.registerCommand('sharpdeps.copyMermaid', () =>
      CodeMapPanel.currentPanel?.copyMermaid()
    ),
    vscode.commands.registerCommand('sharpdeps.exportSvg', () =>
      CodeMapPanel.currentPanel?.export('svg')
    ),
    vscode.commands.registerCommand('sharpdeps.exportPng', () =>
      CodeMapPanel.currentPanel?.export('png')
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
