// Editor -> type resolution (SD-019).
//
// The cursor's type is resolved through the analysis' declaration index: the type that
// is declared in this document at this offset. Names are never used for lookup, so a
// same-named type in another project cannot be selected by mistake. When the analysis
// cannot answer (no result, Quick, no declaration index), the caller gets a reason to
// show instead of a silent no-op.

import * as path from 'node:path';
import * as vscode from 'vscode';
import type { DeclarationMatch, ReportStore } from '../analyzer/reportStore';
import type { AnalysisSnapshot } from '../analyzer/reportV2';

export interface ResolvedType {
  analysisId: string;
  typeId: string;
  variantId: string;
  projectName?: string;
  relativePath: string;
  declarationIndex: number;
}

export type TypeResolution = { ok: true; value: ResolvedType } | { ok: false; reason: string };

export async function resolveTypeAtCursor(
  store: ReportStore,
  rootDirectory: string | undefined,
  output: vscode.OutputChannel
): Promise<TypeResolution> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    return { ok: false, reason: 'カーソル位置を調べるエディターがありません。' };
  }

  if (editor.document.uri.scheme !== 'file') {
    return { ok: false, reason: 'このドキュメントは解析対象のファイルではありません。' };
  }

  const analysisId = store.currentAnalysisId;
  if (!analysisId) {
    return { ok: false, reason: '解析結果がありません。先に解析してください。' };
  }

  if (!rootDirectory) {
    return { ok: false, reason: '解析対象のルートが不明です。もう一度解析してください。' };
  }

  const report = store.getReport(analysisId);
  if (!report.declarationIndex) {
    return {
      ok: false,
      reason:
        report.mode === 'quick'
          ? 'Quick解析には宣言位置がありません。Semanticで解析すると使えます。'
          : 'この結果には宣言位置が含まれていません。'
    };
  }

  const relativePath = path.relative(rootDirectory, editor.document.uri.fsPath).replace(/\\/g, '/');
  const documentId = store.documentIdForPath(analysisId, relativePath);
  if (!documentId) {
    return { ok: false, reason: 'このファイルは解析結果に含まれていません。' };
  }

  const offset = editor.document.offsetAt(editor.selection.active);
  const matches = await store.findTypesAt(analysisId, documentId, offset);
  if (matches.length === 0) {
    return { ok: false, reason: 'カーソル位置に型宣言がありません。' };
  }

  const chosen = preferEnclosingProject(matches, report, relativePath);
  const project = report.projects.find((entry) => entry.variantId === chosen.projectVariantId);
  output.appendLine(
    `Type at cursor: ${chosen.typeId} in ${project?.name ?? 'unknown project'} (${relativePath})`
  );

  return {
    ok: true,
    value: {
      analysisId,
      typeId: chosen.typeId,
      variantId: chosen.projectVariantId,
      projectName: project?.name,
      relativePath: chosen.relativePath,
      declarationIndex: chosen.declarationIndex
    }
  };
}

/**
 * The declaration is textually in this file, so every match describes the same source
 * text. When one file is compiled into several projects, the project whose directory
 * encloses the file wins; the analysis result shows which project was used.
 */
function preferEnclosingProject(
  matches: DeclarationMatch[],
  report: AnalysisSnapshot,
  relativePath: string
): DeclarationMatch {
  let best = matches[0];
  let bestLength = -1;
  for (const match of matches) {
    const project = report.projects.find((entry) => entry.variantId === match.projectVariantId);
    const directory = project
      ? project.relativePath.replace(/\\/g, '/').replace(/\/[^/]*$/, '')
      : '';
    if (directory.length > bestLength && relativePath.startsWith(directory)) {
      best = match;
      bestLength = directory.length;
    }
  }

  return best;
}
