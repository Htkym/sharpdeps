import { describe, expect, it } from 'vitest';
import { SavedQueryProvider } from './queryProvider';
import type { SavedQueryClient, QueryWireReply, QueryItem, QuerySnapshot } from './queryClient';
import { validateQueryReply } from './queryClient';
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
  it('applies the current result conditions to detail Symbol and both Impact directions', async () => {
    const document: QueryItem = {
      ...node('document-a'),
      kind: 'Document',
      resultKind: 'Docs',
      node: { id: 'document-a', kind: 'Document', name: '調査' }
    };
    const mention: QueryItem = {
      ...node('mention-a'),
      kind: 'SymbolMention',
      resultKind: 'Docs',
      node: { id: 'mention-a', kind: 'SymbolMention', name: 'Compute' }
    };
    const filtered = (items: QueryItem[]) =>
      reply({
        items,
        snapshot: { ...reply().snapshot, resultFilterVersion: 1 }
      });
    const wire = client(
      [
        filtered([document]),
        filtered([document]),
        filtered([document, mention]),
        filtered([document])
      ],
      filtered([])
    );
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    const filters = { resultKinds: ['docs' as const], includeExternal: false };
    const search = await provider.handle({
      type: 'searchEntities',
      requestId,
      analysisId: provider.analysisId,
      query: '調査',
      filters
    });
    if (search.type !== 'searchResults') throw new Error('search failed');
    const details = await provider.handle({
      type: 'getEntityDetails',
      requestId,
      analysisId: provider.analysisId,
      entityId: search.items[0].id,
      filters
    });
    expect(details).toMatchObject({
      type: 'details',
      dependencies: [{ kind: 'SymbolMention', resultKind: 'docs' }],
      dependents: []
    });
    expect(wire.requests.map((request) => request.kind)).toEqual([
      'Search',
      'Symbol',
      'Impact',
      'Impact'
    ]);
    for (const request of wire.requests.slice(1))
      expect(request.scope).toEqual({
        variantIds: [variant],
        resultKinds: ['Docs'],
        includeExternal: false
      });
    provider.dispose();
  });

  it('keeps result conditions on search pages, revealed symbols and detached exports', async () => {
    const document: QueryItem = {
      ...node('document-a'),
      kind: 'Document',
      resultKind: 'Docs',
      path: 'docs/調査.md',
      reason: 'Matched document name',
      node: { id: 'document-a', kind: 'Document', name: '調査' }
    };
    const filtered = (fields: Partial<QueryWireReply> = {}) =>
      reply({ ...fields, snapshot: { ...reply().snapshot, resultFilterVersion: 1 } });
    const wire = client(
      [
        filtered({ items: [document], nextCursor: 'backend-page-2' }),
        filtered({
          items: [
            {
              ...document,
              id: 'mention-a',
              node: { id: 'mention-a', kind: 'SymbolMention', name: 'Compute' }
            }
          ]
        }),
        filtered(),
        filtered({ items: [document] })
      ],
      filtered()
    );
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    const filters = { resultKinds: ['docs' as const], includeExternal: false };
    const searchRequest = {
      type: 'searchEntities' as const,
      requestId,
      analysisId: provider.analysisId,
      query: '調査',
      filters,
      limit: 1
    };
    const search = await provider.handle(searchRequest);
    if (search.type !== 'searchResults' || !search.nextCursor)
      throw new Error('search did not page');
    expect(search.items[0]).toMatchObject({
      path: document.path,
      reason: document.reason,
      resultKind: 'docs',
      sourceLocationAvailable: false
    });
    const beforeChangedCursor = wire.requests.length;
    expect(
      await provider.handle({
        ...searchRequest,
        cursor: search.nextCursor,
        filters: { resultKinds: ['code'] }
      })
    ).toMatchObject({ type: 'error', code: 'QUERY_CURSOR_INVALID' });
    expect(wire.requests).toHaveLength(beforeChangedCursor);
    const second = await provider.handle({ ...searchRequest, cursor: search.nextCursor });
    expect(second).toMatchObject({
      type: 'searchResults',
      nextCursor: null,
      items: [{ kind: 'SymbolMention', resultKind: 'docs' }]
    });
    expect(wire.requests[1]).toMatchObject({ cursor: 'backend-page-2', pageSize: 1 });
    const projectionRequest = {
      type: 'getProjection' as const,
      requestId: 'req_2222222222222222',
      analysisId: provider.analysisId,
      granularity: 'type' as const,
      scope: { kind: 'root' as const },
      filters,
      includeIds: [search.items[0].id]
    };
    const projection = await provider.handle(projectionRequest);
    expect(projection).toMatchObject({
      type: 'projection',
      projection: { nodes: [{ kind: 'Document' }] }
    });
    expect(wire.requests.map((request) => request.kind)).toEqual([
      'Search',
      'Search',
      'Browse',
      'Symbol'
    ]);
    for (const request of wire.requests)
      expect(request.scope).toEqual({
        variantIds: [variant],
        resultKinds: ['Docs'],
        includeExternal: false
      });
    const exportRequest = {
      ...projectionRequest,
      type: 'export' as const,
      format: 'json' as const,
      projectionRequestId: projectionRequest.requestId
    };
    const prepared = provider.prepareSavedExportFor(exportRequest);
    expect(prepared.data.filters).toEqual(filters);
    expect(prepared.data.responses.map((response) => response.operation)).toEqual([
      'Browse',
      'Symbol'
    ]);
    expect(prepared.data.snapshot.resultFilterVersion).toBe(1);
    expect(() =>
      provider.prepareSavedExportFor({
        ...exportRequest,
        filters: { includeExternal: true, resultKinds: ['docs'] }
      })
    ).toThrow('QUERY_EXPORT_SELECTION_CHANGED');
    filters.resultKinds.length = 0;
    expect(prepared.data.filters).toEqual({ resultKinds: ['docs'], includeExternal: false });
    expect(wire.requests).toHaveLength(4);
    provider.dispose();
  });

  it('rejects unsupported or ignored result filters without presenting an unfiltered page', async () => {
    const request = {
      type: 'searchEntities' as const,
      requestId,
      query: 'Compute',
      filters: { resultKinds: ['docs' as const] }
    };
    const oldWire = client([]);
    const oldProvider = await SavedQueryProvider.create(oldWire, 'D:/fixture');
    expect(
      await oldProvider.handle({ ...request, analysisId: oldProvider.analysisId })
    ).toMatchObject({ type: 'error', code: 'QUERY_FILTER_UNSUPPORTED' });
    expect(oldWire.requests).toHaveLength(0);
    oldProvider.dispose();
    const ready = reply({ snapshot: { ...reply().snapshot, resultFilterVersion: 1 } });
    const wire = client(
      [
        reply({ items: [node('unmarked')] }),
        reply({ snapshot: ready.snapshot, items: [{ ...node('code-a'), resultKind: 'Code' }] })
      ],
      ready
    );
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    expect(await provider.handle({ ...request, analysisId: provider.analysisId })).toMatchObject({
      type: 'error',
      code: 'QUERY_FILTER_UNSUPPORTED'
    });
    expect(await provider.handle({ ...request, analysisId: provider.analysisId })).toMatchObject({
      type: 'error',
      code: 'QUERY_FILTER_RESULT_INVALID'
    });
    provider.dispose();
  });

  it('freezes an in-flight filter selection before the caller mutates it', async () => {
    const ready = reply({ snapshot: { ...reply().snapshot, resultFilterVersion: 1 } });
    const wire = client([], ready);
    let complete!: (value: QueryWireReply) => void;
    wire.request = (request) => {
      wire.requests.push(request);
      return new Promise((resolve) => {
        complete = resolve;
      });
    };
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    const filters = { resultKinds: ['docs' as const], includeExternal: false };
    const request = {
      type: 'getProjection' as const,
      requestId,
      analysisId: provider.analysisId,
      granularity: 'type' as const,
      scope: { kind: 'root' as const },
      filters
    };
    const pending = provider.handle(request);
    filters.resultKinds.length = 0;
    filters.includeExternal = true;
    complete({ ...ready, requestId: String(wire.requests[0].requestId) });
    expect((await pending).type).toBe('projection');
    expect(provider.prepareSavedExport(provider.analysisId).filters).toEqual({
      resultKinds: ['docs'],
      includeExternal: false
    });
    expect(() =>
      provider.prepareSavedExportFor({
        ...request,
        type: 'export',
        format: 'json',
        projectionRequestId: requestId
      })
    ).toThrow('QUERY_EXPORT_SELECTION_CHANGED');
    provider.dispose();
  });

  it('isolates candidate details without undoing a newer normal source capability', async () => {
    const candidate = node('Compute', 'Candidate');
    const resolved: QueryItem = {
      ...node('Compute'),
      path: 'Another.cs',
      node: {
        ...node('Compute').node!,
        location: {
          sourceId: 'other-occurrence',
          contentHash: 'a'.repeat(64),
          rawSpan: { start: 10, length: 5, end: 15 }
        }
      }
    };
    const newest = { ...resolved, path: 'Newest.cs' };
    const wire = client([reply({ candidates: [candidate] }), reply({ items: [newest] })]);
    let finishSymbol!: (value: QueryWireReply) => void;
    const regularRequest = wire.request;
    wire.request = (request) => {
      if (request.kind !== 'Symbol') return regularRequest(request);
      wire.requests.push(request);
      return new Promise((resolve) => {
        finishSymbol = resolve;
      });
    };
    const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
    const search = await provider.handle({
      type: 'searchEntities',
      requestId,
      analysisId: provider.analysisId,
      query: 'Compute'
    });
    if (search.type !== 'searchResults') throw new Error('search failed');
    const entityId = search.items[0].id;
    const pendingDetails = provider.handle({
      type: 'getEntityDetails',
      requestId,
      analysisId: provider.analysisId,
      entityId
    });
    const currentSearch = await provider.handle({
      type: 'searchEntities',
      requestId: 'req_1111111111111111',
      analysisId: provider.analysisId,
      query: 'Compute'
    });
    expect(currentSearch).toMatchObject({
      type: 'searchResults',
      items: [{ certainty: 'Resolved', path: 'Newest.cs' }]
    });
    finishSymbol(reply({ items: [resolved] }));
    expect(await pendingDetails).toMatchObject({
      type: 'details',
      entity: { certainty: 'Candidate', sourceLocationAvailable: false },
      dependencies: [],
      dependents: []
    });
    expect(
      provider.resolveSource({
        type: 'openDeclaration',
        requestId,
        analysisId: provider.analysisId,
        entityId
      }).relativePath
    ).toBe('Newest.cs');
    expect(wire.requests.map((request) => request.kind)).toEqual(['Search', 'Symbol', 'Search']);
    provider.dispose();
  });

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

const investigationTarget = `hsym_${'a'.repeat(64)}`;
const investigationVariant = `hvar_${'b'.repeat(64)}`;
const investigationOccurrence = `hocc_${'c'.repeat(64)}`;
const investigationLocation = {
  sourceId: 'input-source',
  contentHash: 'd'.repeat(64),
  rawSpan: { start: 24, length: 71, end: 95 }
};
function investigationReply(fields: Partial<QueryWireReply> = {}): QueryWireReply {
  return reply({
    ...fields,
    snapshot: {
      ...reply().snapshot,
      variantIds: [investigationVariant],
      resultFilterVersion: 1,
      symbolDeclarationRowsVersion: 1,
      ...fields.snapshot
    }
  });
}
function declarationReply(): QueryWireReply {
  const second = {
    ...investigationLocation,
    sourceId: 'partial-source',
    contentHash: 'e'.repeat(64),
    rawSpan: { start: 0, length: 10, end: 10 }
  };
  const rows: QueryItem[] = [investigationLocation, second].map((location, ordinal) => ({
    id: `${investigationOccurrence}/${ordinal}`,
    kind: 'declaration',
    label: 'Demo.Input',
    certainty: 'Resolved',
    resultKind: 'Code',
    path: ordinal === 0 ? 'Input.cs' : 'Partial.cs',
    evidence: location,
    occurrence: {
      id: investigationOccurrence,
      logicalSymbolId: investigationTarget,
      variantId: investigationVariant,
      location,
      declarations: [location]
    }
  }));
  return investigationReply({
    items: [
      {
        ...node(investigationTarget),
        kind: 'Type',
        resultKind: 'Code',
        node: {
          id: investigationTarget,
          kind: 'Type',
          name: 'Demo.Input',
          location: investigationLocation
        }
      },
      ...rows
    ]
  });
}
async function candidateFixture() {
  const document: QueryItem = {
    ...node('document'),
    kind: 'Document',
    resultKind: 'Docs',
    path: 'docs/Task.md',
    node: {
      id: 'document',
      kind: 'Document',
      name: 'Task',
      location: {
        sourceId: 'markdown-source',
        contentHash: 'f'.repeat(64),
        rawSpan: { start: 0, length: 10, end: 10 }
      }
    }
  };
  const mention: QueryItem = {
    ...node('mention'),
    kind: 'SymbolMention',
    resultKind: 'Docs',
    node: { id: 'mention', kind: 'SymbolMention', name: 'T:Demo.Input', parentId: 'document' }
  };
  const section: QueryItem = {
    ...document,
    id: 'section',
    kind: 'Section',
    node: {
      ...document.node!,
      id: 'section',
      kind: 'Section',
      name: 'Task section',
      parentId: 'document'
    }
  };
  const target: QueryItem = {
    ...node(investigationTarget, 'Candidate'),
    kind: 'Type',
    resultKind: 'Code',
    node: { id: investigationTarget, kind: 'Type', name: 'Demo.Input' }
  };
  const edge: QueryItem = {
    id: 'candidate-edge',
    kind: 'documents_symbol',
    label: 'Demo.Input',
    certainty: 'Candidate',
    reason: 'related via documents_symbol',
    edge: {
      id: 'candidate-edge',
      sourceNodeId: 'mention',
      targetNodeId: investigationTarget,
      kind: 'documents_symbol',
      certainty: 'Candidate',
      producer: 'markdown-symbol-resolver'
    }
  };
  const wire: SavedQueryClient & {
    requests: Record<string, unknown>[];
    investigate: (request: Record<string, unknown>) => Promise<QueryWireReply>;
  } = {
    requests: [],
    ready: Promise.resolve(investigationReply()),
    async investigate() {
      return declarationReply();
    },
    async request(request) {
      this.requests.push(request);
      let result: QueryWireReply;
      if (request.includeDeclarationRows) result = await this.investigate(request);
      else if (request.kind === 'Impact' && request.dependents === false)
        result = investigationReply({
          items:
            request.nodeId === 'mention'
              ? [mention]
              : [request.nodeId === 'section' ? section : document, mention],
          candidates: [target, edge],
          diagnostics: [{ code: 'markdown.catalog-incomplete', message: 'Catalog incomplete' }]
        });
      else if (request.kind === 'Symbol' && request.nodeId === investigationTarget)
        result = investigationReply({ items: declarationReply().items.slice(0, 1) });
      else if (request.kind === 'Symbol')
        result = investigationReply({
          items: [
            request.nodeId === 'mention'
              ? mention
              : request.nodeId === 'section'
                ? section
                : document
          ]
        });
      else if (request.kind === 'Impact')
        result = investigationReply({
          items: request.nodeId === 'mention' ? [mention, document] : [document]
        });
      else result = investigationReply({ items: [document, section] });
      return { ...structuredClone(result), requestId: String(request.requestId) };
    },
    dispose() {}
  };
  const provider = await SavedQueryProvider.create(wire, 'D:/fixture');
  const filters = { resultKinds: ['code', 'docs'] as ('code' | 'docs')[] };
  const projection = {
    type: 'getProjection',
    requestId,
    analysisId: provider.analysisId,
    granularity: 'type',
    scope: { kind: 'root' },
    filters
  } as const;
  const shown = await provider.handle(projection);
  if (shown.type !== 'projection') throw new Error('projection failed');
  const documentDetailsRequest = {
    type: 'getEntityDetails',
    requestId: 'req_1111111111111111',
    analysisId: provider.analysisId,
    entityId: shown.projection.nodes[0].id,
    filters
  } as const;
  const sectionDetailsRequest = {
    ...documentDetailsRequest,
    entityId: shown.projection.nodes[1].id
  };
  const documentDetails = await provider.handle(documentDetailsRequest);
  if (documentDetails.type !== 'details') throw new Error('document details failed');
  const selectedMention = documentDetails.dependencies?.find(
    (entity) => entity.kind === 'SymbolMention'
  );
  if (!selectedMention) throw new Error('mention missing');
  const detailsRequest = { ...documentDetailsRequest, entityId: selectedMention.id };
  const details = await provider.handle(detailsRequest);
  if (details.type !== 'details' || details.candidateOrigins?.length !== 1)
    throw new Error('origin missing');
  const origin = details.candidateOrigins[0];
  const investigation = {
    type: 'investigateCandidate',
    requestId: 'req_2222222222222222',
    analysisId: provider.analysisId,
    originId: origin.originId
  } as const;
  return {
    provider,
    wire,
    projection,
    detailsRequest,
    documentDetailsRequest,
    sectionDetailsRequest,
    details,
    origin,
    investigation
  };
}

describe('isolated Candidate declaration investigation', () => {
  it('keeps normal Candidate/source/export unchanged while an investigation completes after normal work', async () => {
    const fixture = await candidateFixture();
    const { provider, wire, origin, investigation } = fixture;
    let finish!: (value: QueryWireReply) => void;
    wire.investigate = () =>
      new Promise((resolve) => {
        finish = resolve;
      });
    const pending = provider.handle(investigation);
    await provider.handle({
      ...fixture.projection,
      requestId: 'req_3333333333333333',
      scope: { kind: 'root', id: null, depth: 1 }
    });
    const normal = await provider.handle({
      ...fixture.detailsRequest,
      requestId: 'req_4444444444444444'
    });
    expect(normal).toMatchObject({ type: 'details', candidateOrigins: [origin] });
    if (normal.type !== 'details') throw new Error('normal details failed');
    expect(normal.dependencies).toHaveLength(1);
    expect(
      normal.dependencies?.find((entity) => entity.id === origin.targetEntityId)
    ).toMatchObject({ kind: 'Type', certainty: 'Candidate', sourceLocationAvailable: false });
    expect(normal.entity).toMatchObject({
      kind: 'SymbolMention',
      certainty: 'Resolved',
      name: 'T:Demo.Input'
    });
    const exported = provider.prepareSavedExport(provider.analysisId);
    const sourceRequest = {
      type: 'openDeclaration',
      requestId,
      analysisId: provider.analysisId,
      entityId: origin.targetEntityId
    } as const;
    expect(() => provider.resolveSource(sourceRequest)).toThrow('QUERY_SOURCE_NOT_RETURNED');
    finish(declarationReply());
    const result = await pending;
    if (result.type !== 'candidateInvestigation') throw new Error('investigation failed');
    expect(validateHostMessage(result).ok).toBe(true);
    expect(result).toMatchObject({
      certainty: 'Candidate',
      reason: 'catalog-incomplete',
      message: '関係は未確定、宣言の存在を別途確認',
      queryMetadata: { generation: 3, coverage: 'Partial', freshness: 'unverified' },
      declarations: [{ path: 'Input.cs' }, { path: 'Partial.cs' }]
    });
    expect(provider.prepareSavedExport(provider.analysisId)).toEqual(exported);
    expect(() => provider.resolveSource(sourceRequest)).toThrow('QUERY_SOURCE_NOT_RETURNED');
    const selected = provider.prepareCandidateSource({
      analysisId: provider.analysisId,
      originId: origin.originId,
      capabilityId: result.declarations[1].capabilityId
    });
    expect(selected.location).toMatchObject({
      relativePath: 'Partial.cs',
      span: { start: 0, length: 10, end: 10 }
    });
    selected.assertCurrent();
    expect(wire.requests.filter((request) => request.includeDeclarationRows)).toHaveLength(1);
    expect(wire.requests.find((request) => request.includeDeclarationRows)).toMatchObject({
      kind: 'Symbol',
      nodeId: investigationTarget,
      includeDeclarationRows: true,
      scope: { variantIds: [investigationVariant], resultKinds: ['Code'], includeExternal: false }
    });
    provider.dispose();
  });

  it('accepts only the newest isolated response and invalidates capabilities on close, repeated stale and context change', async () => {
    const fixture = await candidateFixture();
    const { provider, wire, origin, investigation } = fixture;
    const finish: ((value: QueryWireReply) => void)[] = [];
    wire.investigate = () =>
      new Promise((resolve) => {
        finish.push(resolve);
      });
    const older = provider.handle(investigation);
    const newer = provider.handle({ ...investigation, requestId: 'req_3333333333333333' });
    finish[1](declarationReply());
    const current = await newer;
    if (current.type !== 'candidateInvestigation') throw new Error('investigation failed');
    finish[0](declarationReply());
    expect(await older).toMatchObject({
      type: 'error',
      code: 'QUERY_CANDIDATE_INVESTIGATION_EXPIRED'
    });
    const capability = {
      analysisId: provider.analysisId,
      originId: origin.originId,
      capabilityId: current.declarations[0].capabilityId
    };
    const prepared = provider.prepareCandidateSource(capability);
    provider.dismissCandidateInvestigation(`ci_${'f'.repeat(32)}`);
    prepared.assertCurrent();
    expect(() =>
      provider.prepareCandidateSource({ ...capability, analysisId: 'an_ffffffffffffffff' })
    ).toThrow();
    expect(() =>
      provider.prepareCandidateSource({ ...capability, originId: `ci_${'f'.repeat(32)}` })
    ).toThrow();
    expect(() =>
      provider.prepareCandidateSource({ ...capability, capabilityId: `cd_${'f'.repeat(32)}` })
    ).toThrow();
    provider.dismissCandidateInvestigation(origin.originId);
    expect(() => prepared.assertCurrent()).toThrow('QUERY_CANDIDATE_SOURCE_EXPIRED');
    wire.investigate = async () => declarationReply();
    const reopened = await provider.handle(investigation);
    expect(reopened.type).toBe('candidateInvestigation');
    provider.markStale('savedChange');
    expect(await provider.handle(investigation)).toMatchObject({
      type: 'error',
      code: 'QUERY_CANDIDATE_ORIGIN_EXPIRED'
    });
    const freshDetails = await provider.handle(fixture.detailsRequest);
    if (freshDetails.type !== 'details' || !freshDetails.candidateOrigins)
      throw new Error('new origin missing');
    const freshOrigin = freshDetails.candidateOrigins[0];
    const response = await provider.handle({ ...investigation, originId: freshOrigin.originId });
    if (response.type !== 'candidateInvestigation') throw new Error('investigation failed');
    const freshSource = provider.prepareCandidateSource({
      analysisId: provider.analysisId,
      originId: freshOrigin.originId,
      capabilityId: response.declarations[0].capabilityId
    });
    provider.markStale('savedChange');
    expect(() => freshSource.assertCurrent()).toThrow('QUERY_CANDIDATE_SOURCE_EXPIRED');
    const scopedDetails = await provider.handle(fixture.detailsRequest);
    if (scopedDetails.type !== 'details' || !scopedDetails.candidateOrigins)
      throw new Error('new origin missing');
    const scopedOrigin = scopedDetails.candidateOrigins[0];
    const scopedResponse = await provider.handle({
      ...investigation,
      originId: scopedOrigin.originId
    });
    if (scopedResponse.type !== 'candidateInvestigation') throw new Error('investigation failed');
    const scopedSource = provider.prepareCandidateSource({
      analysisId: provider.analysisId,
      originId: scopedOrigin.originId,
      capabilityId: scopedResponse.declarations[0].capabilityId
    });
    await provider.handle({
      ...fixture.projection,
      scope: { kind: 'dependencies', id: fixture.detailsRequest.entityId }
    });
    expect(() => scopedSource.assertCurrent()).toThrow('QUERY_CANDIDATE_SOURCE_EXPIRED');
    expect(
      await provider.handle({ ...investigation, originId: scopedOrigin.originId })
    ).toMatchObject({ type: 'error' });
    provider.dispose();
    expect(() => provider.prepareCandidateSource(capability)).toThrow();
  });

  it('rejects foreign target/pin/variant, missing locations and old hosts without registering source', async () => {
    for (const fault of ['target', 'pin', 'variant', 'location', 'old-host'] as const) {
      const { provider, wire, origin, investigation } = await candidateFixture();
      wire.investigate = async () => {
        const result = declarationReply();
        if (fault === 'target') result.items[0].node!.id = `hsym_${'f'.repeat(64)}`;
        if (fault === 'pin') result.snapshot.generation++;
        if (fault === 'variant') result.items[1].occurrence!.variantId = `hvar_${'f'.repeat(64)}`;
        if (fault === 'location') result.items = result.items.slice(0, 1);
        if (fault === 'old-host') delete result.snapshot.symbolDeclarationRowsVersion;
        return result;
      };
      const result = await provider.handle(investigation);
      if (fault === 'location')
        expect(result).toMatchObject({
          type: 'candidateInvestigation',
          declarations: [],
          unavailableReason: 'No declaration source location was returned for this variant.'
        });
      else expect(result.type).toBe('error');
      expect(() =>
        provider.resolveSource({
          type: 'openDeclaration',
          requestId,
          analysisId: provider.analysisId,
          entityId: origin.targetEntityId
        })
      ).toThrow('QUERY_SOURCE_NOT_RETURNED');
      provider.dispose();
    }
    const legacy = client(
      [],
      investigationReply({
        snapshot: { ...investigationReply().snapshot, symbolDeclarationRowsVersion: undefined }
      })
    );
    const provider = await SavedQueryProvider.create(legacy, 'D:/fixture');
    expect(
      await provider.handle({
        type: 'investigateCandidate',
        requestId,
        analysisId: provider.analysisId,
        originId: `ci_${'f'.repeat(32)}`
      })
    ).toMatchObject({ type: 'error', code: 'QUERY_SYMBOL_DECLARATION_ROWS_UNSUPPORTED' });
    expect(legacy.requests).toHaveLength(0);
    provider.dispose();
  });

  it('retires old origins on selection/filter changes or a missing original edge, without a foreign dismissal affecting the new one', async () => {
    for (const change of ['selection', 'filter', 'edge', 'Document', 'Section'] as const) {
      const fixture = await candidateFixture();
      const { provider, wire, origin, investigation } = fixture;
      const first = await provider.handle(investigation);
      if (first.type !== 'candidateInvestigation') throw new Error('investigation failed');
      const oldSource = provider.prepareCandidateSource({
        analysisId: provider.analysisId,
        originId: origin.originId,
        capabilityId: first.declarations[0].capabilityId
      });
      if (change === 'selection')
        await provider.handle({ ...fixture.detailsRequest, entityId: origin.targetEntityId });
      if (change === 'filter')
        await provider.handle({
          ...fixture.projection,
          filters: { ...fixture.projection.filters, includeExternal: false }
        });
      if (change === 'Document' || change === 'Section') {
        const ownerDetails = await provider.handle(
          change === 'Document' ? fixture.documentDetailsRequest : fixture.sectionDetailsRequest
        );
        expect(ownerDetails).toMatchObject({ type: 'details' });
        expect(ownerDetails).not.toHaveProperty('candidateOrigins');
      }
      if (change === 'edge') {
        const normalRequest = wire.request.bind(wire);
        wire.request = async (request) => {
          const value = await normalRequest(request);
          if (request.kind === 'Impact' && request.dependents === false)
            value.candidates = value.candidates.map((item) =>
              item.edge
                ? { ...item, edge: { ...item.edge, sourceNodeId: 'foreign-mention' } }
                : item
            );
          return value;
        };
        const changedDetails = await provider.handle(fixture.detailsRequest);
        expect(changedDetails).not.toHaveProperty('candidateOrigins');
      }
      expect(() => oldSource.assertCurrent()).toThrow('QUERY_CANDIDATE_SOURCE_EXPIRED');
      expect(await provider.handle(investigation)).toMatchObject({
        type: 'error',
        code: 'QUERY_CANDIDATE_ORIGIN_EXPIRED'
      });
      provider.dispose();
    }
  });

  it('uses the transport wire validator for bounded occurrence and declaration row binding', () => {
    const valid = declarationReply();
    expect(
      validateQueryReply(valid as unknown as Record<string, unknown>).items[1].occurrence?.id
    ).toBe(investigationOccurrence);
    expect(() => validateQueryReply(null)).toThrow('QUERY_PROTOCOL_INVALID');
    for (const fault of ['identity', 'row', 'span', 'duplicate', 'limit'] as const) {
      const invalid = structuredClone(valid);
      const row = invalid.items[1];
      if (fault === 'identity') row.occurrence!.logicalSymbolId = 'opaque-legacy-id';
      if (fault === 'row') row.id = 'unrelated-row';
      if (fault === 'span') row.occurrence!.location!.rawSpan!.end++;
      if (fault === 'duplicate') row.occurrence!.declarations!.push(row.occurrence!.location!);
      if (fault === 'limit')
        row.occurrence!.declarations = Array.from({ length: 501 }, () => investigationLocation);
      expect(() => validateQueryReply(invalid as unknown as Record<string, unknown>)).toThrow(
        'QUERY_PROTOCOL_INVALID'
      );
    }
  });
});
