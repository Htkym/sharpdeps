import * as vscode from 'vscode';
import type { ExportFormat, Granularity } from '../view/protocol';

/** Persist an exported graph (SVG text or base64 PNG) chosen via a save dialog. */
export async function saveExport(
  format: ExportFormat,
  data: string,
  granularity: Granularity,
  solutionName: string
): Promise<void> {
  const ext = format === 'svg' ? 'svg' : 'png';
  const safeName = (solutionName || 'dependency-map').replace(/[^\w.-]+/g, '_');
  const defaultName = `${safeName}-${granularity}.${ext}`;

  const uri = await vscode.window.showSaveDialog({
    defaultUri: defaultUri(defaultName),
    filters: format === 'svg' ? { 'SVG image': ['svg'] } : { 'PNG image': ['png'] }
  });
  if (!uri) {
    return;
  }

  const bytes = format === 'svg' ? Buffer.from(data, 'utf8') : Buffer.from(data, 'base64');
  await vscode.workspace.fs.writeFile(uri, bytes);

  const choice = await vscode.window.showInformationMessage(
    `SharpDeps: Saved ${ext.toUpperCase()} to ${vscode.workspace.asRelativePath(uri)}.`,
    'Open'
  );
  if (choice === 'Open') {
    await vscode.commands.executeCommand('vscode.open', uri);
  }
}

function defaultUri(name: string): vscode.Uri | undefined {
  const folder = vscode.workspace.workspaceFolders?.[0];
  return folder ? vscode.Uri.joinPath(folder.uri, name) : undefined;
}
