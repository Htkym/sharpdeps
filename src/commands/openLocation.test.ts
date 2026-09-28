import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import * as vscode from 'vscode';
import { openEvidenceLocation } from './openLocation';
import type { ReportStore } from '../analyzer/reportStore';

vi.mock('vscode', () => ({
  Uri: {
    file: (fsPath: string) => ({ fsPath }),
    joinPath: (root: { fsPath: string }, relative: string) => ({
      fsPath: path.resolve(root.fsPath, relative)
    })
  },
  Range: class {
    start: object;
    end: object;
    constructor(line: number, character: number, endLine: number, endCharacter: number) {
      this.start = { line, character };
      this.end = { line: endLine, character: endCharacter };
    }
  },
  Selection: class {},
  TextEditorRevealType: { InCenterIfOutsideViewport: 0 },
  window: {
    showQuickPick: vi.fn(async (items: { mapped: boolean }[]) => items.find((item) => item.mapped)),
    showWarningMessage: vi.fn(),
    showInformationMessage: vi.fn(),
    showTextDocument: vi.fn(async () => ({ revealRange: vi.fn() }))
  },
  workspace: { openTextDocument: vi.fn(async (uri: object) => ({ uri, isDirty: false })) }
}));
const directories: string[] = [];
afterEach(() => {
  vi.clearAllMocks();
  for (const dir of directories.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});
describe('mapped source boundaries', () => {
  it('opens the mapped line only after explicitly choosing it', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-location-'));
    directories.push(root);
    fs.writeFileSync(path.join(root, 'Mapped.cs'), 'one\ntwo\nthree');
    await open(root, 'Mapped.cs');
    expect(vscode.window.showQuickPick).toHaveBeenCalledOnce();
    expect(vscode.workspace.openTextDocument).toHaveBeenCalledWith({
      fsPath: path.join(root, 'Mapped.cs')
    });
    expect(vscode.window.showTextDocument).toHaveBeenCalledOnce();
  });
  it('rejects a mapped junction that escapes the real analysis root', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-location-'));
    directories.push(root);
    const outside = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-outside-'));
    directories.push(outside);
    fs.writeFileSync(path.join(outside, 'Mapped.cs'), 'outside');
    fs.symlinkSync(
      outside,
      path.join(root, 'link'),
      process.platform === 'win32' ? 'junction' : 'dir'
    );
    await open(root, 'link/Mapped.cs');
    expect(vscode.window.showWarningMessage).toHaveBeenCalledOnce();
    expect(vscode.workspace.openTextDocument).not.toHaveBeenCalled();
  });
});
async function open(root: string, relativePath: string) {
  const document = {
    id: 'doc_1111111111111111',
    relativePath: 'Input.cs',
    origin: 'userSource',
    contentHash: 'unavailable'
  };
  const store = {
    findEvidence: async () => ({
      documentId: document.id,
      physicalSpan: { startLine: 0, startCharacter: 0, endLine: 0, endCharacter: 1 },
      mappedLocation: { relativePath, line: 2, character: 0 }
    }),
    getReport: () => ({ sourceManifest: [document] }),
    isStale: () => false
  } as unknown as ReportStore;
  await openEvidenceLocation(
    {
      store,
      rootDirectory: () => root,
      output: { appendLine: vi.fn() } as unknown as vscode.OutputChannel
    },
    'an_1111111111111111',
    'ev_1111111111111111'
  );
}
