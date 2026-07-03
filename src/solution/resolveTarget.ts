import * as vscode from 'vscode';

const solutionExtensions = ['.sln', '.slnx'];
const projectExtensions = ['.csproj', '.fsproj', '.vbproj', '.vcxproj'];

function hasAnyExtension(uri: vscode.Uri, extensions: readonly string[]): boolean {
  const fsPath = uri.fsPath.toLowerCase();
  return extensions.some((extension) => fsPath.endsWith(extension));
}

function isSolutionFile(uri: vscode.Uri): boolean {
  return hasAnyExtension(uri, solutionExtensions);
}

function isProjectFile(uri: vscode.Uri): boolean {
  return hasAnyExtension(uri, projectExtensions);
}

function isSupportedAnalysisTarget(uri: vscode.Uri): boolean {
  return isSolutionFile(uri) || isProjectFile(uri);
}

/**
 * Resolve the solution or project file to analyze, in priority order:
 *   1. An explicit target (e.g. the Explorer right-click resource).
 *   2. The active editor, if it is a supported analysis target.
 *   3. A single .sln or .slnx found in the workspace.
 *   4. A QuickPick when multiple solutions are present.
 * Returns undefined when nothing is found or the user cancels.
 */
export async function resolveAnalysisTarget(target?: vscode.Uri): Promise<vscode.Uri | undefined> {
  if (target && isSupportedAnalysisTarget(target)) {
    return target;
  }

  const active = vscode.window.activeTextEditor?.document.uri;
  if (active && isSupportedAnalysisTarget(active)) {
    return active;
  }

  const found = await vscode.workspace.findFiles(
    '**/*.{sln,slnx}',
    '**/{node_modules,bin,obj}/**',
    100
  );
  if (found.length === 0) {
    void vscode.window.showErrorMessage(
      'SharpDeps: No .sln or .slnx file was found in the workspace.'
    );
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
