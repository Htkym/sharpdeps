import * as vscode from 'vscode';

function isSolution(uri: vscode.Uri): boolean {
  return uri.fsPath.toLowerCase().endsWith('.sln');
}

/**
 * Resolve the solution to analyze, in priority order:
 *   1. An explicit target (e.g. the Explorer right-click resource).
 *   2. The active editor, if it is a .sln.
 *   3. A single .sln found in the workspace.
 *   4. A QuickPick when multiple solutions are present.
 * Returns undefined when nothing is found or the user cancels.
 */
export async function resolveSolution(target?: vscode.Uri): Promise<vscode.Uri | undefined> {
  if (target && isSolution(target)) {
    return target;
  }

  const active = vscode.window.activeTextEditor?.document.uri;
  if (active && isSolution(active)) {
    return active;
  }

  const found = await vscode.workspace.findFiles('**/*.sln', '**/{node_modules,bin,obj}/**', 100);
  if (found.length === 0) {
    void vscode.window.showErrorMessage('SharpDeps: No .sln file was found in the workspace.');
    return undefined;
  }
  if (found.length === 1) {
    return found[0];
  }

  const sorted = found
    .slice()
    .sort((a, b) =>
      vscode.workspace.asRelativePath(a).localeCompare(vscode.workspace.asRelativePath(b))
    );
  const pick = await vscode.window.showQuickPick(
    sorted.map((uri) => ({ label: vscode.workspace.asRelativePath(uri), uri })),
    { placeHolder: 'Select a solution to map' }
  );
  return pick?.uri;
}
