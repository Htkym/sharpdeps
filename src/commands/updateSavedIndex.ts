import * as path from 'node:path';
import * as vscode from 'vscode';
import { randomBytes } from 'node:crypto';
import { ensureSemanticDotnet } from '../runtime/ensureDotnet';
import {
  runSavedUpdate,
  verifySavedUpdateSelection,
  SavedUpdateError,
  type SavedUpdateResult,
  type SavedUpdateSelection
} from '../analyzer/savedUpdateClient';

/** Explicit host UI for one trusted update; refresh alone never starts this operation. */
export async function updateSavedIndex(options: {
  root: string;
  current: () => boolean;
  stop: AbortController;
  output: vscode.OutputChannel;
}): Promise<SavedUpdateResult | undefined> {
  const check = () => {
    if (!vscode.workspace.isTrusted || !options.current() || options.stop.signal.aborted)
      throw new SavedUpdateError('SAVED_UPDATE_CANCELLED_CHECK_STATUS');
  };
  check();
  const cliPath = vscode.workspace
    .getConfiguration('sharpdeps')
    .get<string>('savedIndexCliPath', '')
    .trim();
  if (!cliPath || !path.isAbsolute(cliPath)) {
    void vscode.window.showInformationMessage(
      'SharpDeps: Set an absolute savedIndexCliPath to a compatible installed SharpDeps.Cli.dll before updating a saved index.'
    );
    return;
  }
  const targets = await vscode.window.showOpenDialog({
    defaultUri: vscode.Uri.file(options.root),
    canSelectMany: false,
    canSelectFiles: true,
    canSelectFolders: false,
    openLabel: 'Select saved index target',
    filters: { '.NET target': ['sln', 'slnx', 'csproj'] }
  });
  check();
  if (!targets?.[0] || targets[0].scheme !== 'file') return;
  const configuration = await vscode.window.showInputBox({
    prompt: 'Configuration to evaluate for this update',
    value: 'Debug',
    validateInput: (value) =>
      value.trim() && !/[\r\n\0]/.test(value) ? undefined : 'Enter a configuration name.'
  });
  check();
  if (!configuration) return;
  const platform = await vscode.window.showInputBox({
    prompt: 'Platform (leave blank to use the project default)'
  });
  check();
  if (platform === undefined) return;
  const scope = await vscode.window.showQuickPick(
    ['All projects and target frameworks', 'Select one project and target framework'],
    {
      placeHolder:
        'Choose the evaluated update scope; saved variant IDs do not imply this selection'
    }
  );
  check();
  if (!scope) return;
  let project: string | undefined, targetFramework: string | undefined;
  if (scope === 'Select one project and target framework') {
    const projects =
      path.extname(targets[0].fsPath).toLowerCase() === '.csproj'
        ? targets
        : await vscode.window.showOpenDialog({
            defaultUri: vscode.Uri.file(options.root),
            canSelectMany: false,
            canSelectFiles: true,
            canSelectFolders: false,
            filters: { 'C# project': ['csproj'] }
          });
    check();
    if (!projects?.[0] || projects[0].scheme !== 'file') return;
    project = projects[0].fsPath;
    targetFramework = await vscode.window.showInputBox({
      prompt: 'Exact target framework of the selected project (for example net10.0)'
    });
    check();
    if (!targetFramework) return;
  }
  const selection: SavedUpdateSelection = {
    root: options.root,
    target: targets[0].fsPath,
    configuration: configuration.trim(),
    platform: platform.trim() || undefined,
    project,
    targetFramework: targetFramework?.trim(),
    timeoutSeconds: 180
  };
  await verifySavedUpdateSelection(selection);
  check();
  const dotnet = await ensureSemanticDotnet(path.dirname(selection.target));
  check();
  return vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: 'Updating saved SharpDeps index',
      cancellable: true
    },
    async (progress, token) => {
      const cancellation = token.onCancellationRequested(() => {
        options.stop.abort();
        progress.report({ message: 'Cancelling this request; waiting for shared work to finish…' });
      });
      if (token.isCancellationRequested) options.stop.abort();
      try {
        check();
        const result = await runSavedUpdate({
          dotnetPath: dotnet.dotnetPath,
          cliPath,
          selection,
          requestId: `req_${randomBytes(8).toString('hex')}`,
          signal: options.stop.signal,
          current: () => vscode.workspace.isTrusted && options.current()
        });
        check();
        options.output.appendLine(
          `Saved update confirmed: ${result.snapshotId}, generation ${result.generation}.`
        );
        return result;
      } finally {
        cancellation.dispose();
      }
    }
  );
}
