import * as vscode from 'vscode';
import {
  readSavedSource,
  savedEditorSpan,
  SavedSourceError,
  type SavedSourceLocation
} from '../security/savedSource';

/** The current session and trust are checked at every asynchronous editor boundary. */
export async function openSavedLocation(
  location: SavedSourceLocation,
  current: () => boolean
): Promise<void> {
  const check = () => {
    if (!current()) throw new SavedSourceError('QUERY_ANALYSIS_EXPIRED');
    if (!vscode.workspace.isTrusted) throw new SavedSourceError('QUERY_SOURCE_TRUST_REQUIRED');
  };
  check();
  const source = await readSavedSource(location);
  check();
  const document = await vscode.workspace.openTextDocument(vscode.Uri.file(source.filePath));
  check();
  if (document.isDirty) throw new SavedSourceError('QUERY_SOURCE_DIRTY');
  const span = savedEditorSpan(source.text, document.getText(), location.span);
  // Detect replacement during openTextDocument before presenting the editor.
  await readSavedSource(location);
  check();
  if (document.isDirty) throw new SavedSourceError('QUERY_SOURCE_DIRTY');
  savedEditorSpan(source.text, document.getText(), location.span);
  const editor = await vscode.window.showTextDocument(document, { preview: false });
  check();
  if (document.isDirty) throw new SavedSourceError('QUERY_SOURCE_DIRTY');
  savedEditorSpan(source.text, document.getText(), location.span);
  const range = new vscode.Range(document.positionAt(span.start), document.positionAt(span.end));
  editor.selection = new vscode.Selection(range.start, range.end);
  editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
}
