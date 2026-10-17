import { describe, expect, it } from 'vitest';
import type { QuerySnapshot, QueryWireReply } from '../analyzer/queryClient';
import {
  buildSavedContextExport,
  buildSavedQueryExport,
  serializeSavedQueryExport,
  type SavedQueryExportInput
} from './savedQueryExport';

const pinned: QuerySnapshot = {
  workspaceId: 'workspace-a',
  id: 'snapshot-a',
  generation: 3,
  variantIds: ['variant-a'],
  variantCount: 1,
  coverage: 'Partial',
  freshness: 'unverified'
};
const reply: QueryWireReply = {
  apiVersion: '1',
  requestId: 'provider_1',
  snapshot: pinned,
  items: [
    {
      id: 'item-a',
      kind: 'Member',
      label: 'Run',
      certainty: 'Resolved',
      path: 'D:/SECRET_PATH/Input.cs',
      node: {
        id: 'node-a',
        kind: 'Member',
        name: 'Run',
        location: { sourceId: 'SECRET_LOCATION', contentHash: 'a'.repeat(64) }
      }
    }
  ],
  candidates: [
    {
      id: 'candidate-a',
      kind: 'calls',
      label: '候補🐈\r\n"x"```',
      certainty: 'Candidate',
      edge: {
        id: 'edge-a',
        kind: 'calls',
        certainty: 'Resolved',
        producer: 'fixture',
        sourceNodeId: 'node-a',
        targetNodeId: 'external-a',
        sourceOccurrenceId: 'occurrence-a',
        variantId: 'variant-a',
        evidence: { sourceId: 'SECRET_EVIDENCE' }
      }
    }
  ],
  unresolved: [
    { id: 'unresolved-a', kind: 'ExternalSymbol', label: 'External', certainty: 'Unresolved' }
  ],
  truncated: true,
  truncationReasons: ['EDGE_LIMIT'],
  nextCursor: 'SECRET_CURSOR',
  diagnostics: [{ code: 'INPUTS_UNVERIFIED', message: 'Saved input inventory is unverified.' }],
  errors: []
};
function input(): SavedQueryExportInput {
  return {
    analysisId: 'an_0123456789abcdef',
    pinned,
    selectedVariantId: 'variant-a',
    scope: { kind: 'dependencies', id: 'node-a', depth: 1 },
    includedNodeIds: [],
    display: { granularity: 'type', nodeIds: ['node-a'], edgeIds: [], truncated: false },
    responses: [{ operation: 'Impact', reply }]
  };
}

describe('saved Query export conversion', () => {
  it('preserves the pin, raw identities, bucket and edge certainty, and returned subset limits', () => {
    const data = buildSavedQueryExport(input());
    expect(data).toMatchObject({
      kind: 'sharpdeps.saved-query-export',
      version: 1,
      snapshot: pinned,
      selectedVariantId: 'variant-a',
      scope: { id: 'node-a' },
      display: { nodeIds: ['node-a'], edgeIds: [], truncated: false }
    });
    expect(data.responses[0]).toMatchObject({
      operation: 'Impact',
      snapshot: pinned,
      returnedCount: 3,
      totalKind: 'returned',
      truncated: true,
      truncationReasons: ['EDGE_LIMIT'],
      hasMore: true,
      diagnostics: reply.diagnostics,
      items: [
        {
          id: 'item-a',
          kind: 'Member',
          certainty: 'Resolved',
          node: { id: 'node-a', kind: 'Member' }
        }
      ],
      candidates: [
        {
          id: 'candidate-a',
          certainty: 'Candidate',
          edge: {
            id: 'edge-a',
            kind: 'calls',
            certainty: 'Resolved',
            sourceNodeId: 'node-a',
            targetNodeId: 'external-a',
            sourceOccurrenceId: 'occurrence-a',
            variantId: 'variant-a',
            hasEvidence: true
          }
        }
      ],
      unresolved: [{ id: 'unresolved-a', certainty: 'Unresolved' }]
    });
    const json = serializeSavedQueryExport(data);
    expect(JSON.parse(json).responses[0].candidates[0].label).toBe(reply.candidates[0].label);
    expect(json).not.toContain('SECRET_');
    expect(json).not.toContain('contentHash');
    expect(json.endsWith('\n')).toBe(true);
    expect(buildSavedContextExport(data)).toContain(json);
    expect(buildSavedContextExport(data)).toContain('not the whole scope');
    data.snapshot.variantIds[0] = 'changed';
    data.responses[0].truncationReasons.push('changed');
    expect(pinned.variantIds).toEqual(['variant-a']);
    expect(reply.truncationReasons).toEqual(['EDGE_LIMIT']);
  });

  it('keeps separate response provenance and limits instead of merging overlapping pages', () => {
    const second = {
      ...reply,
      requestId: 'provider_2',
      truncated: false,
      truncationReasons: [],
      nextCursor: undefined,
      snapshot: { ...pinned, coverage: 'CompleteWithinScope' as const }
    };
    const data = buildSavedQueryExport({
      ...input(),
      responses: [
        { operation: 'Browse', reply },
        { operation: 'Symbol', reply: second }
      ],
      display: { ...input().display, truncated: true }
    });
    expect(data.responses.map((page) => page.returnedCount)).toEqual([3, 3]);
    expect(data.responses.map((page) => page.snapshot.coverage)).toEqual([
      'Partial',
      'CompleteWithinScope'
    ]);
    expect(data.responses.map((page) => page.truncated)).toEqual([true, false]);
    expect(data.display.truncated).toBe(true);
    expect(data.responses[0].items[0].id).toBe(data.responses[1].items[0].id);
  });

  it('refuses mixed workspace, snapshot, generation, variant and failed responses', () => {
    for (const snapshot of [
      { ...pinned, workspaceId: 'other' },
      { ...pinned, id: 'other' },
      { ...pinned, generation: 4 }
    ])
      expect(() =>
        buildSavedQueryExport({
          ...input(),
          responses: [{ operation: 'Browse', reply: { ...reply, snapshot } }]
        })
      ).toThrow('QUERY_EXPORT_PIN_CHANGED');
    expect(() => buildSavedQueryExport({ ...input(), selectedVariantId: 'other' })).toThrow(
      'QUERY_EXPORT_VARIANT_CHANGED'
    );
    expect(() =>
      buildSavedQueryExport({
        ...input(),
        responses: [
          {
            operation: 'Browse',
            reply: { ...reply, snapshot: { ...pinned, variantIds: ['other'] } }
          }
        ]
      })
    ).toThrow('QUERY_EXPORT_VARIANT_CHANGED');
    expect(() =>
      buildSavedQueryExport({
        ...input(),
        responses: [
          {
            operation: 'Browse',
            reply: { ...reply, errors: [{ code: 'FAILED', message: 'failed' }] }
          }
        ]
      })
    ).toThrow('QUERY_EXPORT_RESPONSE_FAILED');
    expect(() => buildSavedQueryExport({ ...input(), responses: [] })).toThrow(
      'QUERY_EXPORT_UNAVAILABLE'
    );
  });
});
