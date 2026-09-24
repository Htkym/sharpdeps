// Read-only virtual documents for analyzer-generated code (SD-011).
//
// The reference travels in the URI path as `<analysisId>/<documentId>`; both are opaque
// ids, so a URI never names a file on disk. The content comes from the analysis result,
// and the provider is a TextDocumentContentProvider: VS Code treats it as read-only, so
// opening generated code cannot write to the user's repository.

export const GENERATED_DOCUMENT_SCHEME = 'sharpdeps-generated';

export interface GeneratedDocumentRef {
  analysisId: string;
  documentId: string;
}

/** Path part of a generated document URI. */
export function generatedDocumentPath(analysisId: string, documentId: string): string {
  return `/${analysisId}/${documentId}`;
}

/** Parses a generated document path; anything unexpected resolves to undefined. */
export function parseGeneratedDocumentPath(value: string): GeneratedDocumentRef | undefined {
  const parts = value.replace(/^\/+/, '').split('/');
  if (parts.length !== 2) {
    return undefined;
  }

  const [analysisId, documentId] = parts;
  if (!/^an_[a-f0-9]{16}$/.test(analysisId) || !/^doc_[a-f0-9]{16}$/.test(documentId)) {
    return undefined;
  }

  return { analysisId, documentId };
}
