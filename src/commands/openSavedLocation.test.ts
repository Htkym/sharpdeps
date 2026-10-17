import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import { afterEach, describe, expect, it, vi } from 'vitest';
import * as vscode from 'vscode';
import { openSavedLocation } from './openSavedLocation';
import type { SavedSourceLocation } from '../security/savedSource';

vi.mock('vscode', () => ({
  Uri: { file: (fsPath: string) => ({ fsPath }) },
  Range: class {
    constructor(
      readonly start: object,
      readonly end: object
    ) {}
  },
  Selection: class {
    constructor(
      readonly start: object,
      readonly end: object
    ) {}
  },
  TextEditorRevealType: { InCenterIfOutsideViewport: 0 },
  workspace: { isTrusted: true, openTextDocument: vi.fn() },
  window: { showTextDocument: vi.fn() }
}));
const roots: string[] = [];
function fixture(): SavedSourceLocation {
  const root = fs.mkdtempSync(path.join(fs.realpathSync(os.tmpdir()), 'sharpdeps-saved-open-'));
  roots.push(root);
  const text = '\ufeff// 🐈\r\nclass Input {}';
  fs.writeFileSync(path.join(root, 'Input.cs'), text);
  const start = text.indexOf('Input');
  return {
    root,
    relativePath: 'Input.cs',
    contentHash: createHash('sha256').update(text).digest('hex'),
    span: { start, length: 5, end: start + 5 }
  };
}
function document(text: string, dirty = false) {
  return { isDirty: dirty, getText: () => text, positionAt: (offset: number) => ({ offset }) };
}
afterEach(() => {
  vi.resetAllMocks();
  Object.assign(vscode.workspace, { isTrusted: true });
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

describe('saved source editor boundaries', () => {
  it('selects the verified raw span with the editor BOM adjustment', async () => {
    const location = fixture();
    const editor = { selection: undefined, revealRange: vi.fn() };
    vi.mocked(vscode.workspace.openTextDocument).mockResolvedValue(
      document('// 🐈\r\nclass Input {}') as never
    );
    vi.mocked(vscode.window.showTextDocument).mockResolvedValue(editor as never);
    await openSavedLocation(location, () => true);
    expect(editor.selection).toMatchObject({
      start: { offset: location.span.start - 1 },
      end: { offset: location.span.end - 1 }
    });
    expect(editor.revealRange).toHaveBeenCalledOnce();
  });

  it('refuses dirty or mismatching editor text and a session replaced while opening', async () => {
    const location = fixture();
    for (const doc of [document('// 🐈\r\nclass Input {}', true), document('different')]) {
      vi.mocked(vscode.workspace.openTextDocument).mockResolvedValue(doc as never);
      await expect(openSavedLocation(location, () => true)).rejects.toMatchObject({
        code: doc.isDirty ? 'QUERY_SOURCE_DIRTY' : 'QUERY_SOURCE_CHANGED'
      });
    }
    let current = true;
    vi.mocked(vscode.workspace.openTextDocument).mockImplementation(async () => {
      current = false;
      return document('// 🐈\r\nclass Input {}') as never;
    });
    await expect(openSavedLocation(location, () => current)).rejects.toMatchObject({
      code: 'QUERY_ANALYSIS_EXPIRED'
    });
    expect(vscode.window.showTextDocument).not.toHaveBeenCalled();
  });

  it('checks trust before source access and after an awaited editor call', async () => {
    const location = fixture();
    Object.assign(vscode.workspace, { isTrusted: false });
    await expect(openSavedLocation(location, () => true)).rejects.toMatchObject({
      code: 'QUERY_SOURCE_TRUST_REQUIRED'
    });
    expect(vscode.workspace.openTextDocument).not.toHaveBeenCalled();
    Object.assign(vscode.workspace, { isTrusted: true });
    vi.mocked(vscode.workspace.openTextDocument).mockImplementation(async () => {
      Object.assign(vscode.workspace, { isTrusted: false });
      return document('// 🐈\r\nclass Input {}') as never;
    });
    await expect(openSavedLocation(location, () => true)).rejects.toMatchObject({
      code: 'QUERY_SOURCE_TRUST_REQUIRED'
    });
    expect(vscode.window.showTextDocument).not.toHaveBeenCalled();
  });
});
