// Evidence presentation (SD-018): locations, origin, confidence, and the edge summary
// that reconciles an aggregated edge's occurrence count with the paged evidence.

import { describe, expect, it } from 'vitest';
import {
  describeBasis,
  relevantLimitations,
  summarizeEdge,
  toEvidenceView
} from '../../media/app/evidenceView';

describe('toEvidenceView', () => {
  it('shows 1-based locations and marks generated and public evidence', () => {
    const view = toEvidenceView({
      id: 'ev_1111111111111111',
      kind: 'constructs',
      origin: 'generatedSource',
      confidence: 'resolved',
      publicSurface: true,
      documentId: 'doc_1111111111111111',
      documentPath: 'generated/Domain/OrderFactory.g.cs',
      physicalSpan: {
        start: 10,
        length: 4,
        startLine: 4,
        startCharacter: 7,
        endLine: 4,
        endCharacter: 11
      },
      mappedLocation: { relativePath: 'src/Domain/Order.cs', line: 2, character: 4 },
      snippet: 'new Order()'
    });

    expect(view.line).toBe(5);
    expect(view.character).toBe(8);
    expect(view.generated).toBe(true);
    expect(view.publicSurface).toBe(true);
    expect(view.mappedPath).toBe('src/Domain/Order.cs');
    expect(view.mappedLine).toBe(3);
    expect(view.reference).toBe('generated/Domain/OrderFactory.g.cs:5:8 constructs');
  });

  it('survives a record with missing or malformed fields', () => {
    const view = toEvidenceView({ id: 'ev_2222222222222222' });

    expect(view.kind).toBe('unknown');
    expect(view.origin).toBe('unknown');
    expect(view.confidence).toBe('unknown');
    expect(view.generated).toBe(false);
    expect(view.reference).toContain('ev_2222222222222222');
  });
});

describe('describeBasis', () => {
  it('separates resolved Semantic references from Quick declarations and inference', () => {
    expect(describeBasis('symbolResolved')).toMatchObject({ resolved: true });
    expect(describeBasis('symbolResolved').label).toContain('Semantic');
    expect(describeBasis('usingInferred')).toMatchObject({ resolved: false });
    expect(describeBasis('usingInferred').label).toContain('推定');
    expect(describeBasis('projectDeclared')).toMatchObject({ resolved: false });
  });
});

describe('summarizeEdge', () => {
  const aggregated = {
    id: 'rel_1111111111111111',
    basis: 'symbolResolved',
    kinds: ['calls'],
    evidenceCount: 7,
    generatedEvidenceCount: 2,
    publicSurfaceEvidenceCount: 3,
    underlyingRelations: [
      { id: 'rel_1111111111111111', basis: 'symbolResolved', kinds: ['calls'], evidenceCount: 5 },
      { id: 'rel_2222222222222222', basis: 'symbolResolved', kinds: ['typeUse'], evidenceCount: 2 }
    ]
  };

  it('sums the underlying relations so the edge count can be reconciled', () => {
    const summary = summarizeEdge(aggregated);

    expect(summary.total).toBe(7);
    expect(summary.relationCount).toBe(2);
    expect(summary.aggregated).toBe(true);
    expect(summary.relations.map((relation) => relation.evidenceCount)).toEqual([5, 2]);
  });

  it('treats a single-relation edge as not aggregated', () => {
    const summary = summarizeEdge({
      id: 'rel_3333333333333333',
      basis: 'usingInferred',
      kinds: ['typeUse'],
      evidenceCount: 4
    });

    expect(summary.total).toBe(4);
    expect(summary.aggregated).toBe(false);
    expect(summary.relations).toHaveLength(1);
  });
});

describe('relevantLimitations', () => {
  const limitations = [
    { code: 'semantic.unresolvedReferences', message: 'unresolved' },
    { code: 'semantic.generatedDocumentTooLarge', message: 'generated' },
    { code: 'semantic.nonCSharpProjects', message: 'other' }
  ];

  it('keeps evidence and generated notes, and drops unrelated ones', () => {
    expect(relevantLimitations(limitations, 'evidence').map((entry) => entry.code)).toEqual([
      'semantic.unresolvedReferences',
      'semantic.generatedDocumentTooLarge'
    ]);
    expect(relevantLimitations(limitations, 'generated').map((entry) => entry.code)).toEqual([
      'semantic.generatedDocumentTooLarge'
    ]);
  });
});
