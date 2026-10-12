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
  it.each(['Document', 'Section'])(
    'returns candidate code across %s containment while keeping it a leaf',
    async (kind) => {
      const owner: QueryItem = {
        ...node('document-a'),
        node: { ...node('document-a').node!, kind }
      };
      const candidate = node('Compute', 'Candidate');
      const wire: SavedQueryClient & { requests: Record<string, unknown>[] } = {
        ready: Promise.resolve(reply()),
        requests: [],
        async request(request) {
          this.requests.push(request);
          if (request.kind === 'Impact') {
            const budget = request.budget as { maxDepth: number };
            return reply({
              requestId: String(request.requestId),
              items: [owner],
              candidates:
                !request.dependents && request.includeDocuments && budget.maxDepth >= 4
                  ? [candidate]
                  : []
            });
          }
          return reply({ items: request.nodeId === 'Compute' ? [node('Compute')] : [owner] });
        },
        dispose() {}
      };
      const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
      const search = await provider.handle({
        type: 'searchEntities',
        requestId,
        analysisId: provider.analysisId,
        query: 'document-a'
      });
      if (search.type !== 'searchResults') throw new Error('search failed');
      const details = await provider.handle({
        type: 'getEntityDetails',
        requestId,
        analysisId: provider.analysisId,
        entityId: search.items[0].id
      });
      if (details.type !== 'details') throw new Error('details failed');
      const dependencies = details.dependencies ?? [];
      expect(dependencies).toEqual([
        expect.objectContaining({ name: 'Compute', certainty: 'Candidate' })
      ]);
      expect(details.queryMetadata).toMatchObject({ coverage: 'Partial', generation: 3 });
      const before = wire.requests.length;
      const candidateDetails = await provider.handle({
        type: 'getEntityDetails',
        requestId,
        analysisId: provider.analysisId,
        entityId: dependencies[0].id
      });
      expect(candidateDetails).toMatchObject({
        type: 'details',
        entity: { certainty: 'Candidate' },
        dependencies: [],
        dependents: []
      });
      expect(wire.requests.slice(before).map((request) => request.kind)).toEqual(['Symbol']);
      expect(
        wire.requests.filter((request) => request.kind === 'Impact' && request.dependents)
      ).toHaveLength(1);
      expect(
        wire.requests.find((request) => request.kind === 'Impact' && request.dependents)?.budget
      ).toMatchObject({ maxDepth: 1 });
      expect(
        wire.requests.every(
          (request) => (request.scope as { variantIds: string[] }).variantIds[0] === variant
        )
      ).toBe(true);
      provider.dispose();
    }
  );

  it('binds saved export to the received projection and invalidates a lease on new work', async () => {
    const wire = client([
      reply({ items: [node('member-a')] }),
      reply({ items: [node('member-b')] })
    ]);
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    const projection = {
      type: 'getProjection' as const,
      requestId,
      analysisId: provider.analysisId,
      granularity: 'type' as const,
      scope: { kind: 'root' as const },
      filters: {},
      search: '',
      includeIds: []
    };
    await provider.handle(projection);
    const request = {
      ...projection,
      type: 'export' as const,
      format: 'json' as const,
      requestId: 'req_2222222222222222',
      projectionRequestId: requestId
    };
    for (const changed of [
      { projectionRequestId: undefined },
      { projectionRequestId: 'req_1111111111111111' },
      { scope: { kind: 'dependencies' as const, id: 'ty_1111111111111111' } },
      { filters: { includeTests: false } },
      { search: 'other' },
      { includeIds: ['ty_1111111111111111'] },
      { granularity: 'project' as const }
    ])
      expect(() => provider.prepareSavedExportFor({ ...request, ...changed })).toThrow(
        'QUERY_EXPORT_SELECTION_CHANGED'
      );
    const prepared = provider.prepareSavedExportFor(request);
    prepared.assertCurrent();
    expect(prepared.data.display.nodeIds).toEqual(['member-a']);
    expect(wire.requests).toHaveLength(1);
    const next = provider.handle({ ...projection, requestId: 'req_1111111111111111' });
    expect(() => prepared.assertCurrent()).toThrow('QUERY_EXPORT_SELECTION_CHANGED');
    await next;
    expect(() => prepared.assertCurrent()).toThrow('QUERY_EXPORT_SELECTION_CHANGED');
    expect(() => provider.prepareSavedExportFor(request)).toThrow('QUERY_EXPORT_SELECTION_CHANGED');
    provider.dispose();
  });

  it('refuses unsupported payloads and rechecks pin, input changes and disposal at output', async () => {
    const ready = reply();
    const wire = client([reply({ items: [node('member-a')] })], ready);
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    await provider.handle({
      type: 'getProjection',
      requestId,
      analysisId: provider.analysisId,
      granularity: 'type',
      scope: { kind: 'root' }
    });
    const request = {
      type: 'export' as const,
      requestId,
      analysisId: provider.analysisId,
      projectionRequestId: requestId,
      granularity: 'type' as const,
      scope: { kind: 'root' as const },
      format: 'json' as const
    };
    expect(() => provider.prepareSavedExportFor({ ...request, format: 'svg' })).toThrow(
      'QUERY_EXPORT_FORMAT_UNSUPPORTED'
    );
    expect(() => provider.prepareSavedExportFor({ ...request, data: '{}' })).toThrow(
      'QUERY_EXPORT_FORMAT_UNSUPPORTED'
    );
    expect(() =>
      provider.prepareSavedExportFor({ ...request, type: 'copyContext', includeSnippets: true })
    ).toThrow('QUERY_EXPORT_SNIPPETS_UNSUPPORTED');
    const prepared = provider.prepareSavedExportFor(request);
    ready.snapshot.generation++;
    expect(() => prepared.assertCurrent()).toThrow('QUERY_EXPORT_PIN_CHANGED');
    ready.snapshot.generation--;
    provider.markStale('savedChange');
    expect(() => prepared.assertCurrent()).toThrow('QUERY_EXPORT_INPUT_CHANGED');
    const stale = provider.prepareSavedExportFor(request);
    expect(stale.data.observedInputChange).toBe('savedChange');
    expect(stale.data.snapshot.freshness).toBe('unverified');
    stale.assertCurrent();
    provider.dispose();
    expect(() => stale.assertCurrent()).toThrow('QUERY_ANALYSIS_EXPIRED');
    expect(wire.requests).toHaveLength(1);
  });

  it.each(['complete', 'failed'] as const)(
    'ignores a late old projection when the latest export projection is %s',
    async (latest) => {
      const wire = client([]);
      const pending: ((reply: QueryWireReply) => void)[] = [];
      wire.request = (request) => {
        wire.requests.push(request);
        return new Promise<QueryWireReply>((resolve) => {
          pending.push((value) => resolve({ ...value, requestId: String(request.requestId) }));
        });
      };
      const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
      const request = {
        type: 'getProjection',
        requestId,
        analysisId: provider.analysisId,
        granularity: 'type',
        scope: { kind: 'root' }
      };
      const older = provider.handle(request);
      const newer = provider.handle({ ...request, requestId: 'req_1111111111111111' });
      expect(pending).toHaveLength(2);
      pending[1](
        latest === 'complete'
          ? reply({ items: [node('newer')] })
          : reply({ snapshot: { ...reply().snapshot, generation: 4 } })
      );
      expect((await newer).type).toBe(latest === 'complete' ? 'projection' : 'error');
      pending[0](reply({ items: [node('older')] }));
      expect((await older).type).toBe('projection');
      if (latest === 'complete')
        expect(provider.prepareSavedExport(provider.analysisId).display.nodeIds).toEqual(['newer']);
      else
        expect(() => provider.prepareSavedExport(provider.analysisId)).toThrow(
          'QUERY_EXPORT_UNAVAILABLE'
        );
      provider.dispose();
    }
  );

  it('prepares detached export data from the pinned projection without another Query', async () => {
    const wire = client([reply({ items: [node('member-a'), node('member-b')] })]);
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture', undefined, {
      maxNodes: 1,
      maxEdges: 1
    });
    expect(() => provider.prepareSavedExport(provider.analysisId)).toThrow(
      'QUERY_EXPORT_UNAVAILABLE'
    );
    const result = await provider.handle({
      type: 'getProjection',
      requestId,
      analysisId: provider.analysisId,
      granularity: 'type',
      scope: { kind: 'root' }
    });
    if (result.type !== 'projection') throw new Error('projection failed');
    const data = provider.prepareSavedExport(provider.analysisId);
    expect(data.analysisId).toBe(provider.analysisId);
    expect(data.snapshot).toEqual(reply().snapshot);
    expect(data.responses[0].items.map((item) => item.id)).toEqual(['member-a', 'member-b']);
    expect(data.display).toMatchObject({ nodeIds: ['member-a'], edgeIds: [], truncated: true });
    expect(data.responses[0].truncated).toBe(false);
    expect(result.projection.nodes[0].id).not.toBe(data.display.nodeIds[0]);
    data.display.nodeIds[0] = 'mutated';
    data.responses[0].items[0].node!.name = 'mutated';
    provider.markStale('savedChange');
    const again = provider.prepareSavedExport(provider.analysisId);
    expect(again.display.nodeIds).toEqual(['member-a']);
    expect(again.responses[0].items[0].node?.name).toBe('member-a');
    expect(again.observedInputChange).toBe('savedChange');
    expect(again.snapshot.freshness).toBe('unverified');
    expect(wire.requests).toHaveLength(1);
    expect(() => provider.prepareSavedExport('an_0000000000000000')).toThrow(
      'QUERY_ANALYSIS_EXPIRED'
    );
    provider.dispose();
    expect(() => provider.prepareSavedExport(provider.analysisId)).toThrow(
      'QUERY_ANALYSIS_EXPIRED'
    );
  });

  it('refuses to reuse export data after a failed projection or provider replacement', async () => {
    const wire = client([
      reply({ items: [node('member-a')] }),
      reply({ snapshot: { ...reply().snapshot, generation: 4 } })
    ]);
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    const request = {
      type: 'getProjection',
      requestId,
      analysisId: provider.analysisId,
      granularity: 'type',
      scope: { kind: 'root' }
    };
    expect((await provider.handle(request)).type).toBe('projection');
    expect(provider.prepareSavedExport(provider.analysisId).responses).toHaveLength(1);
    expect(await provider.handle(request)).toMatchObject({
      type: 'error',
      code: 'QUERY_GENERATION_CHANGED'
    });
    expect(() => provider.prepareSavedExport(provider.analysisId)).toThrow(
      'QUERY_EXPORT_UNAVAILABLE'
    );
    const replacement = await SavedQueryProvider.create(client([]), 'D:/fixture');
    expect(() => replacement.prepareSavedExport(provider.analysisId)).toThrow(
      'QUERY_ANALYSIS_EXPIRED'
    );
    expect(() => replacement.prepareSavedExport(replacement.analysisId)).toThrow(
      'QUERY_EXPORT_UNAVAILABLE'
    );
    provider.dispose();
    replacement.dispose();
  });

  it.each(['Member', 'Document', 'Section'])(
    'opens only host-issued %s source IDs from the pinned session',
    async (kind) => {
      const location = {
        sourceId: 'source-a',
        contentHash: 'a'.repeat(64),
        rawSpan: { start: 5, length: 3, end: 8 }
      };
      const input = {
        ...node('member-a'),
        kind,
        path: kind === 'Member' ? 'Input.cs' : 'docs/注文.md',
        node: { ...node('member-a').node!, kind, location }
      };
      const wire = client([reply({ items: [input] })]);
      const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
      const search = await provider.handle({
        type: 'searchEntities',
        requestId,
        analysisId: provider.analysisId,
        query: 'member-a'
      });
      if (search.type !== 'searchResults') throw new Error('search failed');
      const request = {
        type: 'openDeclaration' as const,
        requestId,
        analysisId: provider.analysisId,
        entityId: search.items[0].id
      };
      expect(provider.resolveSource(request)).toMatchObject({
        root: 'D:/fixture',
        relativePath: input.path,
        contentHash: location.contentHash,
        span: location.rawSpan
      });
      expect(() => provider.resolveSource({ ...request, declarationIndex: 1 })).toThrow(
        'QUERY_DECLARATION_UNAVAILABLE'
      );
      expect(() => provider.resolveSource({ ...request, entityId: 'ty_0000000000000000' })).toThrow(
        'QUERY_SOURCE_NOT_RETURNED'
      );
      const other = await SavedQueryProvider.create(client([]), 'D:/fixture');
      expect(() => other.resolveSource({ ...request, analysisId: other.analysisId })).toThrow(
        'QUERY_SOURCE_NOT_RETURNED'
      );
      const returned = provider.resolveSource(request);
      returned.span.start = 0;
      expect(provider.resolveSource(request).span.start).toBe(5);
      provider.dispose();
      expect(() => provider.resolveSource(request)).toThrow('QUERY_ANALYSIS_EXPIRED');
      other.dispose();
    }
  );

  it('issues evidence source IDs only with their evidence page and preserves real edge counts', async () => {
    const location = {
      sourceId: 'source-a',
      contentHash: 'b'.repeat(64),
      rawSpan: { start: 0, length: 3, end: 3 }
    };
    const edge = {
      id: 'edge-a',
      sourceNodeId: 'member-a',
      targetNodeId: 'member-b',
      kind: 'calls',
      certainty: 'Resolved' as const,
      producer: 'fixture',
      evidence: location
    };
    const provider = await SavedQueryProvider.create(
      client([
        reply({
          items: [
            node('member-a'),
            node('member-b'),
            {
              id: edge.id,
              kind: 'calls',
              label: 'a → b',
              certainty: 'Resolved',
              edge,
              path: 'Input.cs'
            }
          ]
        })
      ]),
      'D:/fixture'
    );
    const projection = await provider.handle({
      type: 'getProjection',
      requestId,
      analysisId: provider.analysisId,
      granularity: 'type',
      scope: { kind: 'root' }
    });
    if (projection.type !== 'projection') throw new Error('projection failed');
    const relationId = projection.projection.edges[0].id;
    expect(projection.projection.edges[0].evidenceCount).toBe(1);
    expect(() =>
      provider.resolveSource({
        type: 'openEvidence',
        requestId,
        analysisId: provider.analysisId,
        evidenceId: relationId.replace('rel_', 'ev_')
      })
    ).toThrow('QUERY_SOURCE_NOT_RETURNED');
    const page = await provider.handle({
      type: 'getEvidencePage',
      requestId,
      analysisId: provider.analysisId,
      relationId
    });
    if (page.type !== 'evidencePage') throw new Error('evidence failed');
    const evidenceId = (page.items[0] as { id: string }).id;
    expect(
      provider.resolveSource({
        type: 'openEvidence',
        requestId,
        analysisId: provider.analysisId,
        evidenceId
      })
    ).toMatchObject({ relativePath: 'Input.cs', span: location.rawSpan });
    provider.dispose();
  });

  it('retains observed input changes when rereading the same pinned snapshot', async () => {
    const original = await SavedQueryProvider.create(client([]), 'D:/fixture');
    original.markStale('savedChange');
    const reread = await SavedQueryProvider.create(client([]), 'D:/fixture');
    reread.inheritStaleness(original);
    expect(reread.staleReason).toBe('savedChange');
    expect(reread.analysisState('fixture').queryMetadata?.freshness).toBe('unverified');
    const newer = reply();
    newer.snapshot = { ...newer.snapshot, id: 'snapshot-b', generation: 4 };
    const updated = await SavedQueryProvider.create(client([], newer), 'D:/fixture');
    updated.inheritStaleness(original);
    expect(updated.staleReason).toBeUndefined();
    for (const provider of [original, reread, updated]) provider.dispose();
  });

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
