// Read-only document provider for analyzer-generated code (SD-011).
//
// Registered for the `sharpdeps-generated` scheme: VS Code treats provided documents as
// read-only, and the content is read from the registered analysis result. A document
// whose content the analysis did not retain fails to open with a clear error instead of
// falling back to a stale file on disk.

import * as vscode from 'vscode';
import type { ReportStore } from '../analyzer/reportStore';
import {
  GENERATED_DOCUMENT_SCHEME,
  generatedDocumentPath,
  parseGeneratedDocumentPath
} from './documentUri';

export function generatedDocumentUri(analysisId: string, documentId: string): vscode.Uri {
  return vscode.Uri.from({
    scheme: GENERATED_DOCUMENT_SCHEME,
    path: generatedDocumentPath(analysisId, documentId)
  });
}

export function createGeneratedDocumentProvider(
  store: ReportStore
): vscode.TextDocumentContentProvider {
  return {
    async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
      const ref = parseGeneratedDocumentPath(uri.path);
      if (!ref) {
        throw new Error('SharpDeps: the generated document reference is not valid.');
      }

      const document = await store.readGeneratedDocument(ref.analysisId, ref.documentId);
      if (!document) {
        throw new Error(
          'SharpDeps: this generated document is not part of the analysis result ' +
            '(the result was replaced, or its content was too large to retain).'
        );
      }

      return document.text;
    }
  };
}
