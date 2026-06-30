import * as vscode from 'vscode';
import { resolveSolution } from './solution/resolveTarget';
import { ensureDotnet, DotnetNotAvailableError } from './runtime/ensureDotnet';
import { AnalyzerError, locateAnalyzer, runAnalyzer } from './analyzer/runAnalyzer';
import { buildViewModel } from './view/viewModel';
import { CodeMapPanel } from './view/codeMapPanel';
import { CycleDiagnostics } from './diagnostics/cycleDiagnostics';

export function activate(context: vscode.ExtensionContext): void {
  const output = vscode.window.createOutputChannel('SharpDeps');
  const diagnostics = new CycleDiagnostics(output);
  context.subscriptions.push(output, diagnostics);

  let lastSolution: vscode.Uri | undefined;

  async function runAndShow(target?: vscode.Uri): Promise<void> {
    const solution = await resolveSolution(target);
    if (!solution) {
      return;
    }
    lastSolution = solution;

    await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: 'SharpDeps: Analyzing solution…',
        cancellable: true
      },
      async (progress, token) => {
        try {
          const dotnet = await ensureDotnet(context);
          const analyzer = locateAnalyzer(context);
          const config = vscode.workspace.getConfiguration('sharpdeps');

          progress.report({ message: 'Running analyzer…' });
          const report = await runAnalyzer({
            dotnetPath: dotnet.dotnetPath,
            analyzer,
            solutionPath: solution.fsPath,
            maxProjects: config.get<number>('maxProjects', 60),
            maxEdges: config.get<number>('maxEdges', 200),
            token
          });

          const panel = CodeMapPanel.show(context.extensionUri, output);
          panel.setModel(buildViewModel(report));
          diagnostics.update(report);
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
    vscode.commands.registerCommand('sharpdeps.refresh', () => runAndShow(lastSolution)),
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
  // Nothing to clean up beyond context.subscriptions.
}

function reportError(err: unknown, output: vscode.OutputChannel): void {
  if (err instanceof DotnetNotAvailableError) {
    output.appendLine(err.message);
    return;
  }

  if (err instanceof AnalyzerError) {
    output.appendLine(err.message);
    if (err.detail) {
      output.appendLine(err.detail);
    }
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
