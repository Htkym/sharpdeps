import { describe, expect, it } from 'vitest';
import { SavedQueryProvider } from './queryProvider';
import type { SavedQueryClient, QueryWireReply, QueryItem, QuerySnapshot } from './queryClient';
import { validateHostMessage } from '../view/protocolV2';

const variant = 'variant-a';
const requestId = 'req_0123456789abcdef';
const node = (id: string, certainty: QueryItem['certainty'] = 'Resolved'): QueryItem => ({
  id,
  kind: 'Member',
  label: id,
  certainty,
  node: { id, kind: 'Member', name: id }
});
function reply(fields: Partial<QueryWireReply> = {}): QueryWireReply {
  return {
    apiVersion: '1',
    requestId: 'ready',
    snapshot: {
      workspaceId: 'workspace-a',
      id: 'snapshot-a',
      generation: 3,
      variantIds: [variant],
      variantCount: 1,
      coverage: 'Partial',
      freshness: 'unverified'
    },
    items: [],
    candidates: [],
    unresolved: [],
    truncated: false,
    truncationReasons: [],
    diagnostics: [],
    errors: [],
    ...fields
  };
}
function client(
  responses: QueryWireReply[],
  ready = reply()
): SavedQueryClient & { requests: Record<string, unknown>[] } {
  const requests: Record<string, unknown>[] = [];
  return {
    ready: Promise.resolve(ready),
    requests,
    async request(request) {
      requests.push(request);
      const result = responses.shift();
      if (!result) throw new Error('unexpected extra Query');
      return { ...result, requestId: String(request.requestId) };
    },
    dispose() {}
  };
}

describe('saved Query provider boundary', () => {
  it('proves an exact variant omitted from the bounded ready header by scoped Status', async () => {
    const ready = reply();
    ready.snapshot = {
      ...ready.snapshot,
      variantIds: Array.from({ length: 64 }, (_, i) => `variant-${i}`),
      variantCount: 80
    };
    const wire = client([reply()], ready);
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture', variant);
    expect(wire.requests[0]).toMatchObject({ kind: 'Status', scope: { variantIds: [variant] } });
    expect(provider.analysisState('fixture').queryMetadata?.variantIds).toEqual([variant]);
    provider.dispose();
  });

  it('preserves candidate paths and explicit edges without inventing legacy totals', async () => {
    const edge = {
      id: 'edge-a',
      sourceNodeId: 'member-a',
      targetNodeId: 'member-b',
      kind: 'calls',
      certainty: 'Resolved' as const,
      producer: 'fixture',
      variantId: variant,
      sourceOccurrenceId: 'occ-a',
      targetOccurrenceId: 'occ-b'
    };
    const wire = client([
      reply({ items: [node('member-a')] }),
      reply({
        items: [node('member-a')],
        candidates: [
          node('member-b', 'Candidate'),
          {
            id: edge.id,
            kind: 'calls',
            label: 'a → b',
            certainty: 'Candidate',
            edge
          }
        ],
        truncated: true,
        truncationReasons: ['DEPTH_LIMIT']
      })
    ]);
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    const search = await provider.handle({
      type: 'searchEntities',
      requestId,
      analysisId: provider.analysisId,
      query: 'member-a',
      granularity: 'type'
    });
    if (search.type !== 'searchResults') throw new Error('search failed');
    expect(search.total).toBe(1);
    expect(search.queryMetadata?.totalKind).toBe('returned');
    expect(wire.requests[0].scope).not.toHaveProperty('kind');
    const result = await provider.handle({
      type: 'getProjection',
      requestId,
      analysisId: provider.analysisId,
      granularity: 'type',
      scope: { kind: 'dependencies', id: search.items[0].id }
    });
    if (result.type !== 'projection') throw new Error('projection failed');
    expect(validateHostMessage(result).ok).toBe(true);
    expect(result.projection.nodes.find((item) => item.name === 'member-b')?.certainty).toBe(
      'Candidate'
    );
    expect(result.projection.edges[0]).toMatchObject({
      certainty: 'Candidate',
      variantId: variant,
      sourceOccurrenceId: 'occ-a',
      targetOccurrenceId: 'occ-b'
    });
    expect(result.projection.queryMetadata).toMatchObject({
      generation: 3,
      coverage: 'Partial',
      truncated: true,
      candidateCount: 2,
      returnedCount: 2,
      totalKind: 'returned'
    });
    const candidate = result.projection.nodes.find((item) => item.name === 'member-b')!;
    expect(
      await provider.handle({
        type: 'getProjection',
        requestId,
        analysisId: provider.analysisId,
        granularity: 'type',
        scope: { kind: 'dependencies', id: candidate.id }
      })
    ).toMatchObject({
      type: 'error',
      code: 'QUERY_CANDIDATE_LEAF'
    });
    expect(
      wire.requests.every(
        (request) => (request.scope as { variantIds: string[] }).variantIds[0] === variant
      )
    ).toBe(true);
    provider.dispose();
  });

  it('rejects a different generation or variant instead of registering those entities', async () => {
    const cases: [Partial<QuerySnapshot>, string][] = [
      [{ generation: 4 }, 'QUERY_GENERATION_CHANGED'],
      [{ variantIds: ['variant-b'] }, 'QUERY_VARIANT_CHANGED']
    ];
    for (const [change, code] of cases) {
      const changed = reply();
      changed.snapshot = { ...changed.snapshot, ...change };
      changed.items = [node('foreign')];
      const provider = await SavedQueryProvider.create(client([changed]), 'D:/fixture');
      const result = await provider.handle({
        type: 'searchEntities',
        requestId,
        analysisId: provider.analysisId,
        query: 'foreign'
      });
      expect(result).toMatchObject({ type: 'error', code });
      provider.dispose();
    }
  });

  it('binds opaque cursors to the original search and rejects unsupported filters', async () => {
    const wire = client([
      reply({ items: [node('member-a')], nextCursor: 'private-signed-cursor' })
    ]);
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    const search = await provider.handle({
      type: 'searchEntities',
      requestId,
      analysisId: provider.analysisId,
      query: 'member'
    });
    if (search.type !== 'searchResults') throw new Error('search failed');
    expect(search.nextCursor).toMatch(/^cur_[0-9a-f]{16}$/);
    expect(JSON.stringify(search)).not.toContain('private-signed-cursor');
    expect(
      await provider.handle({
        type: 'searchEntities',
        requestId,
        analysisId: provider.analysisId,
        query: 'changed',
        cursor: search.nextCursor
      })
    ).toMatchObject({
      type: 'error',
      code: 'QUERY_CURSOR_INVALID'
    });
    expect(
      await provider.handle({
        type: 'searchEntities',
        requestId,
        analysisId: provider.analysisId,
        query: 'member',
        filters: { includeGenerated: false }
      })
    ).toMatchObject({
      type: 'error',
      code: 'QUERY_FILTER_UNSUPPORTED'
    });
    expect(wire.requests).toHaveLength(1);
    provider.dispose();
  });

  it('keeps truncation and diagnostics from the extra reveal Query', async () => {
    const wire = client([
      reply({ items: [node('member-a')] }),
      reply(),
      reply({
        items: [node('member-a')],
        truncated: true,
        truncationReasons: ['OUTPUT_LIMIT'],
        diagnostics: [
          { code: 'CONFIGURATION_UNVERIFIED', message: 'Configuration is not verified.' }
        ]
      })
    ]);
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    const search = await provider.handle({
      type: 'searchEntities',
      requestId,
      analysisId: provider.analysisId,
      query: 'member-a'
    });
    if (search.type !== 'searchResults') throw new Error('search failed');
    const result = await provider.handle({
      type: 'getProjection',
      requestId,
      analysisId: provider.analysisId,
      granularity: 'type',
      scope: { kind: 'root' },
      includeIds: [search.items[0].id]
    });
    if (result.type !== 'projection') throw new Error('projection failed');
    expect(result.projection.truncated).toBe(true);
    expect(result.projection.queryMetadata?.truncationReasons).toEqual(['OUTPUT_LIMIT']);
    expect(result.projection.queryMetadata?.diagnostics[0]?.code).toBe('CONFIGURATION_UNVERIFIED');
    provider.dispose();
  });
});
