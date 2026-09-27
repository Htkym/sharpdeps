// Opening analysed locations in the editor (SD-019).
//
// The webview selects an opaque evidence or declaration id; the host resolves it
// against the registered analysis and opens the document. Generated documents are served
// read-only by the document provider, so opening them never writes to the repository.
// The recorded positions are checked against the current content before jumping: a file
// that changed since the analysis is not silently revealed.

import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as vscode from 'vscode';
import type { PhysicalSpan, SourceDocument } from '../analyzer/reportV2';
import type { ReportStore } from '../analyzer/reportStore';
import { generatedDocumentUri } from '../generatedDocuments/generatedDocumentProvider';
import { isInsideRoot } from '../security/paths';

export interface OpenLocationOptions {
  store: ReportStore;
  output: vscode.OutputChannel;
  /** Directory the analysed paths are relative to; undefined when no target is known. */
  rootDirectory: () => string | undefined;
}

export async function openEvidenceLocation(
  options: OpenLocationOptions,
  analysisId: string,
  evidenceId: string
): Promise<void> {
  const evidence = await options.store.findEvidence(analysisId, evidenceId);
  if (!evidence) {
    void vscode.window.showWarningMessage(
      'SharpDeps: この根拠は現在の解析結果にありません。解析し直してください。'
    );
    return;
  }

  if (!evidence.physicalSpan) {
    void vscode.window.showInformationMessage(
      'SharpDeps: この根拠には位置情報がありません（宣言のみのため開けません）。'
    );
    return;
  }

  const report = options.store.getReport(analysisId);
  const document = report.sourceManifest.find((entry) => entry.id === evidence.documentId);
  if (!document) {
    void vscode.window.showWarningMessage('SharpDeps: この根拠の文書が解析結果にありません。');
    return;
  }

  await openSpan(options, analysisId, document, evidence.physicalSpan);
}

export async function openDeclarationLocation(
  options: OpenLocationOptions,
  analysisId: string,
  typeId: string,
  declarationIndex: number
): Promise<void> {
  const declarations = await options.store.declarationsForType(analysisId, typeId);
  if (declarations.length === 0) {
    void vscode.window.showInformationMessage(
      'SharpDeps: この型の宣言位置は解析結果に含まれていません（Quick解析では使えません）。'
    );
    return;
  }

  const record = declarations[Math.min(Math.max(declarationIndex, 0), declarations.length - 1)];
  const report = options.store.getReport(analysisId);
  const document = report.sourceManifest.find((entry) => entry.id === record.documentId);
  if (!document) {
    void vscode.window.showWarningMessage('SharpDeps: この宣言の文書が解析結果にありません。');
    return;
  }

  if (declarations.length > 1) {
    options.output.appendLine(
      `Opening declaration ${record.declarationIndex + 1} of ${declarations.length} for ${typeId}.`
    );
  }

  await openSpan(options, analysisId, document, record.span);
}

async function openSpan(
  options: OpenLocationOptions,
  analysisId: string,
  document: SourceDocument,
  span: PhysicalSpan
): Promise<void> {
  const range = new vscode.Range(
    span.startLine,
    span.startCharacter,
    span.endLine,
    span.endCharacter
  );

  if (document.origin === 'generatedSource') {
    // Read-only: the content comes from the analysis result, never from a file that was
    // written into the repository.
    const textDocument = await vscode.workspace.openTextDocument(
      generatedDocumentUri(analysisId, document.id)
    );
    const editor = await vscode.window.showTextDocument(textDocument, { preview: false });
    editor.selection = new vscode.Selection(range.start, range.end);
    editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
    return;
  }

  const rootDirectory = options.rootDirectory();
  if (!rootDirectory) {
    void vscode.window.showWarningMessage(
      'SharpDeps: 解析対象のルートが不明なため、この場所を開けません。'
    );
    return;
  }

  const fileUri = vscode.Uri.joinPath(vscode.Uri.file(rootDirectory), document.relativePath);
  const textDocument = await vscode.workspace.openTextDocument(fileUri);

  if (textDocument.isDirty) {
    void vscode.window.showWarningMessage(
      'SharpDeps: 未保存の変更があります。位置が実際の内容と異なる場合があります。'
    );
  }

  // A linked file outside the analysis root is legitimate and opens, but its content is
  // not read for hash comparison: only paths inside the root are read (SD-023).
  if (isInsideRoot(rootDirectory, document.relativePath)) {
    const verification = await verifyContent(fileUri, document.contentHash);
    if (verification === 'mismatch') {
      const choice = await vscode.window.showWarningMessage(
        'SharpDeps: ファイルの内容が解析時と変わっています。古い行へ移動する可能性があります。',
        { modal: false },
        '開く'
      );
      if (choice !== '開く') {
        return;
      }
    }
  } else {
    options.output.appendLine(
      `The analysed file is outside the workspace root (linked file); the content hash was not compared: ${document.relativePath}`
    );
  }

  const editor = await vscode.window.showTextDocument(textDocument, { preview: false });
  editor.selection = new vscode.Selection(range.start, range.end);
  editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
}

/** 'unknown' when the hash cannot be compared (no hash recorded, or the file is gone). */
async function verifyContent(
  fileUri: vscode.Uri,
  expectedHash: string
): Promise<'match' | 'mismatch' | 'unknown'> {
  if (!/^[0-9a-f]{64}$/.test(expectedHash)) {
    return 'unknown';
  }

  try {
    const bytes = await fs.promises.readFile(fileUri.fsPath);
    const hash = createHash('sha256').update(bytes).digest('hex');
    return hash.toLowerCase() === expectedHash.toLowerCase() ? 'match' : 'mismatch';
  } catch {
    return 'unknown';
  }
}
