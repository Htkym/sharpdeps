// Read-only generated document URIs (SD-011): opaque ids only, no file paths.

import { describe, expect, it } from 'vitest';
import {
  GENERATED_DOCUMENT_SCHEME,
  generatedDocumentPath,
  parseGeneratedDocumentPath
} from './documentUri';

describe('generated document paths', () => {
  it('round-trips an analysis and document id', () => {
    const path = generatedDocumentPath('an_0123456789abcdef', 'doc_abcdefabcdefabcd');

    expect(path).toBe('/an_0123456789abcdef/doc_abcdefabcdefabcd');
    expect(parseGeneratedDocumentPath(path)).toEqual({
      analysisId: 'an_0123456789abcdef',
      documentId: 'doc_abcdefabcdefabcd'
    });
    expect(parseGeneratedDocumentPath(path.replace(/^\/+/, ''))).toEqual({
      analysisId: 'an_0123456789abcdef',
      documentId: 'doc_abcdefabcdefabcd'
    });
  });

  it('rejects anything that is not two opaque ids', () => {
    expect(parseGeneratedDocumentPath('')).toBeUndefined();
    expect(parseGeneratedDocumentPath('/an_0123456789abcdef')).toBeUndefined();
    expect(
      parseGeneratedDocumentPath('/an_0123456789abcdef/doc_abcdefabcdefabcd/extra')
    ).toBeUndefined();
    expect(parseGeneratedDocumentPath('/an_0123456789abcdef/../secret.cs')).toBeUndefined();
    expect(parseGeneratedDocumentPath('/an_zzzzzzzzzzzzzzzz/doc_abcdefabcdefabcd')).toBeUndefined();
    expect(GENERATED_DOCUMENT_SCHEME).toBe('sharpdeps-generated');
  });
});
