// Host -> webview message mapping (SD-017).
//
// A malformed host message must not half-update the shell: every field is read
// defensively, and unknown types produce no action at all.

import { describe, expect, it } from 'vitest';
import { toViewActions } from '../../media/app/hostMessages';
import {
  INITIAL_STATE,
  selectPrimaryAction,
  viewReducer,
  type ViewState
} from '../../media/app/state';
import type { QueryResultMetadata } from '../../src/view/protocolV2';

const analysisId = 'an_0123456789abcdef';
const savedMetadata: QueryResultMetadata = {
  provider: 'savedIndex',
  workspaceId: 'hw_fixture',
  snapshotId: 'snapshot-3',
  generation: 3,
  variantIds: ['variant-net10'],
  coverage: 'Partial',
  freshness: 'unverified',
  truncated: true,
  truncationReasons: ['PAGE_LIMIT'],
  diagnostics: [{ code: 'INPUTS_UNVERIFIED', message: 'No configuration proof.' }],
  returnedCount: 2,
  totalKind: 'returned',
  candidateCount: 1,
  unresolvedCount: 1
};
const queries = new Map<string, { query?: string; appendEvidence?: boolean }>([
  ['req_0000000000000001', { query: 'OrderStore' }]
]);

describe('toViewActions', () => {
  it('accepts source return only for the current saved session and strips transient payload fields', () => {
    const relationId = 'rel_1111111111111111';
    const ui = {
      navTab: 'cycles',
      focus: 'evidence',
      id: 'ev_1111111111111111',
      scroll: { navigation: 140, inspector: 680, map: 250 },
      searchCount: 40
    };
    const state: ViewState = { ...INITIAL_STATE, analysisId, queryMetadata: savedMetadata };
    const message = {
      type: 'sourceReturn',
      requestId: 'req_1111111111111111',
      analysisId,
      ui,
      state: {
        version: 1,
        scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 2 },
        granularity: 'type',
        selection: { relationId },
        search: 'Compute',
        filters: { resultKinds: ['code'], includeExternal: false },
        history: [{ scope: { kind: 'root' }, granularity: 'type', search: 'earlier' }],
        analysisId: 'an_ffffffffffffffff',
        queryMetadata: { ...savedMetadata, generation: 99 },
        projectionRequestId: 'req_1111111111111111',
        evidence: { nextCursor: 'old' }
      }
    };
    const actions = toViewActions(message, new Map(), state);
    expect(actions).toHaveLength(1);
    expect(actions[0]).toMatchObject({
      type: 'sourceReturned',
      ui,
      state: {
        granularity: 'type',
        selection: { relationId },
        search: 'Compute',
        scope: message.state.scope,
        filters: message.state.filters,
        history: [expect.objectContaining({ search: 'earlier' })]
      }
    });
    const restored = actions[0].type === 'sourceReturned' ? actions[0].state : {};
    expect(restored).not.toHaveProperty('analysisId');
    expect(restored).not.toHaveProperty('queryMetadata');
    expect(restored).not.toHaveProperty('projectionRequestId');
    expect(restored).not.toHaveProperty('evidence');
    for (const rejected of [
      { ...message, analysisId: 'an_1111111111111111' },
      { ...message, state: { ...message.state, version: 2 } },
      { ...message, ui: { ...ui, id: 'D:/source.cs' } }
    ])
      expect(toViewActions(rejected, new Map(), state)).toEqual([]);
    expect(toViewActions(message, new Map(), { ...INITIAL_STATE, analysisId })).toEqual([]);
  });

  it('ends a failed current search page while keeping its prior items and retry cursor', () => {
    const filters = { resultKinds: ['docs' as const], includeExternal: false };
    const item = {
      id: 'ty_1111111111111111',
      name: '調査',
      kind: 'Document',
      granularity: 'type' as const
    };
    const requestId = 'req_1111111111111111';
    const state: ViewState = {
      ...INITIAL_STATE,
      analysisId,
      filters,
      search: '調査',
      queryMetadata: savedMetadata,
      searchResults: {
        query: '調査',
        items: [item],
        total: 1,
        pending: true,
        nextCursor: 'cur_1111111111111111'
      }
    };
    const context = new Map([
      [
        requestId,
        {
          type: 'searchEntities',
          query: '調査',
          appendSearch: true,
          filtersKey: JSON.stringify(filters),
          analysisId
        }
      ]
    ]);
    const error = {
      type: 'error',
      requestId,
      analysisId,
      code: 'QUERY_BUDGET_EXCEEDED',
      message: 'Page did not complete.'
    };
    const next = toViewActions(error, context, state).reduce(viewReducer, state);
    expect(next.searchResults).toEqual({ ...state.searchResults, pending: false });
    expect(next.error).toEqual({ code: error.code, message: error.message });
    const replaced = { ...state, filters: { resultKinds: ['code' as const] } };
    const staleActions = toViewActions(error, context, replaced);
    expect(staleActions).toEqual([]);
    expect(staleActions.reduce(viewReducer, replaced).searchResults.pending).toBe(true);
  });

  it('drops a late result from the previous filter and carries the current page display fields', () => {
    const filters = { resultKinds: ['docs' as const], includeExternal: false };
    const state: ViewState = {
      ...INITIAL_STATE,
      analysisId,
      filters,
      search: '調査',
      queryMetadata: savedMetadata
    };
    const requestId = 'req_1111111111111111';
    const context = new Map([
      [requestId, { query: '調査', appendSearch: true, filtersKey: JSON.stringify(filters) }]
    ]);
    const message = {
      type: 'searchResults',
      requestId,
      analysisId,
      nextCursor: 'cur_1111111111111111',
      total: 1,
      queryMetadata: { ...savedMetadata, resultFilterVersion: 1 },
      items: [
        {
          id: 'ty_1111111111111111',
          name: 'Compute',
          kind: 'SymbolMention',
          path: 'docs/<img>.md',
          reason: '文書への参照',
          resultKind: 'docs',
          sourceLocationAvailable: false
        }
      ]
    };
    expect(toViewActions(message, context, state)[0]).toMatchObject({
      type: 'searchResultsReceived',
      append: true,
      nextCursor: message.nextCursor,
      queryMetadata: { resultFilterVersion: 1 },
      items: [
        {
          path: 'docs/<img>.md',
          reason: '文書への参照',
          resultKind: 'docs',
          sourceLocationAvailable: false
        }
      ]
    });
    const changed = { ...state, filters: { resultKinds: ['code' as const] } };
    expect(toViewActions(message, context, changed)).toEqual([]);
    expect(
      toViewActions(
        {
          type: 'projection',
          requestId,
          analysisId,
          projection: {
            scope: { kind: 'root' },
            granularity: 'type',
            nodes: [],
            edges: [],
            queryMetadata: savedMetadata
          }
        },
        context,
        changed
      )
    ).toEqual([]);
  });

  it('keeps received saved projection provenance only until the next projection or session', () => {
    const requestId = 'req_3333333333333333';
    let state: ViewState = { ...INITIAL_STATE, analysisId, queryMetadata: savedMetadata };
    const message = {
      type: 'projection',
      requestId,
      analysisId,
      projection: {
        scope: { kind: 'root' },
        granularity: 'type',
        nodes: [],
        edges: [],
        queryMetadata: savedMetadata
      }
    };
    state = toViewActions(message, new Map(), state).reduce(viewReducer, state);
    expect(state.projectionRequestId).toBe(requestId);
    state = viewReducer(state, { type: 'projectionRequested' });
    expect(state.projectionRequestId).toBeUndefined();
    expect(state.projection).not.toBeNull();
    const rejected = {
      ...message,
      projection: { ...message.projection, queryMetadata: { ...savedMetadata, generation: 4 } }
    };
    expect(toViewActions(rejected, new Map(), state)).toEqual([]);
    state = toViewActions(message, new Map(), state).reduce(viewReducer, state);
    state = viewReducer(state, {
      type: 'analyzeStarted',
      analysisId: 'an_1111111111111111',
      mode: 'quick'
    });
    expect(state.projectionRequestId).toBeUndefined();
  });
  it.each(['cancelled', 'failed', 'complete'] as const)(
    'keeps the primary legacy operation after saved → legacy → %s',
    (terminal) => {
      const legacyId = 'an_1111111111111111';
      const receive = (state: ViewState, message: unknown) =>
        toViewActions(message, queries, state).reduce(viewReducer, state);
      let state = receive(INITIAL_STATE, {
        type: 'analysisComplete',
        analysisId,
        target: { name: 'Saved workspace', relativePath: 'src/Legacy.csproj' },
        queryMetadata: savedMetadata
      });
      state = receive(state, {
        type: 'projection',
        analysisId,
        projection: {
          scope: { kind: 'root' },
          granularity: 'type',
          nodes: [],
          edges: [],
          queryMetadata: savedMetadata
        }
      });
      state = receive(state, {
        type: 'savedUpdateState',
        analysisId,
        phase: 'idle',
        canUpdate: true
      });
      expect(selectPrimaryAction(state).intent).toBe('updateSavedIndex');
      const target = { name: 'Legacy.csproj', relativePath: 'src/Legacy.csproj' };
      state = receive(state, {
        type: 'analysisStarted',
        analysisId: legacyId,
        mode: 'quick',
        target
      });
      expect(state.target).toEqual(target);
      expect(selectPrimaryAction(state)).toEqual({
        intent: 'cancel',
        label: 'Cancel',
        disabled: false
      });
      expect(state.runningAnalysisId).toBe(legacyId);
      expect(state.queryMetadata).toBeUndefined();
      expect(state.savedUpdate).toBeUndefined();
      expect(state.analysisId).toBeUndefined();
      expect(state.projection).toBeNull();
      expect(
        toViewActions(
          { type: 'savedUpdateState', analysisId, phase: 'updating', canUpdate: false },
          queries,
          state
        )
      ).toEqual([]);
      expect(
        toViewActions(
          { type: 'projection', analysisId, projection: { queryMetadata: savedMetadata } },
          queries,
          state
        )
      ).toEqual([]);
      state = receive(
        state,
        terminal === 'complete'
          ? {
              type: 'analysisComplete',
              analysisId: legacyId,
              mode: 'quick',
              completeness: 'completeWithinScope'
            }
          : {
              type: 'analysisFailed',
              analysisId: legacyId,
              message: 'Legacy stopped.',
              cancelled: terminal === 'cancelled'
            }
      );
      expect(state.runningAnalysisId).toBeUndefined();
      expect(state.queryMetadata).toBeUndefined();
      expect(state.status).toBe(terminal === 'complete' ? 'complete' : terminal);
      expect(state.target).toEqual(target);
      if (terminal === 'complete') {
        state = receive(state, {
          type: 'projection',
          analysisId: legacyId,
          projection: {
            scope: { kind: 'root' },
            granularity: 'project',
            nodes: [{ id: 'prj_1111111111111111', name: 'Legacy', granularity: 'project' }],
            edges: []
          }
        });
        expect(state.projection?.nodes[0].name).toBe('Legacy');
        expect(state.projection?.queryMetadata).toBeUndefined();
      }
      expect(selectPrimaryAction(state)).toEqual({
        intent: 'analyze',
        label: terminal === 'failed' ? 'Retry analysis' : 'Analyze',
        disabled: false
      });
    }
  );

  it('returns from cancelled legacy to a new saved provider and keeps its pinned cancellation state', () => {
    const legacyId = 'an_1111111111111111';
    const nextSavedId = 'an_2222222222222222';
    const receive = (state: ViewState, message: unknown) =>
      toViewActions(message, queries, state).reduce(viewReducer, state);
    let state = receive(INITIAL_STATE, {
      type: 'analysisComplete',
      analysisId,
      target: { name: 'Saved workspace', relativePath: '' },
      queryMetadata: savedMetadata
    });
    const legacyTarget = { name: 'Legacy.csproj', relativePath: 'src/Legacy.csproj' };
    state = receive(state, {
      type: 'analysisStarted',
      analysisId: legacyId,
      mode: 'semantic',
      target: legacyTarget
    });
    expect(state.target).toEqual(legacyTarget);
    expect(selectPrimaryAction(state).intent).toBe('cancel');
    state = receive(state, {
      type: 'analysisFailed',
      analysisId: legacyId,
      message: 'Analysis stopped for saved Query.',
      cancelled: true
    });
    state = receive(state, {
      type: 'analysisComplete',
      analysisId: nextSavedId,
      target: { name: 'New saved workspace', relativePath: '' },
      queryMetadata: savedMetadata
    });
    expect(state.target).toEqual({ name: 'New saved workspace', relativePath: '' });
    expect(state.runningAnalysisId).toBeUndefined();
    expect(state.queryMetadata).toEqual(savedMetadata);
    state = receive(state, {
      type: 'savedUpdateState',
      analysisId: nextSavedId,
      phase: 'idle',
      canUpdate: true
    });
    expect(selectPrimaryAction(state)).toEqual({
      intent: 'updateSavedIndex',
      label: 'Update saved index',
      disabled: false
    });
    for (const phase of ['updating', 'cancelling', 'cancelled'] as const) {
      state = receive(state, {
        type: 'savedUpdateState',
        analysisId: nextSavedId,
        phase,
        canUpdate: phase === 'cancelled'
      });
      expect(state.queryMetadata).toEqual(savedMetadata);
      expect(selectPrimaryAction(state)).toMatchObject(
        phase === 'cancelled'
          ? { intent: 'updateSavedIndex', disabled: false }
          : { intent: 'cancel', disabled: phase === 'cancelling' }
      );
    }
  });

  it('ignores stale, malformed and legacy saved-update notifications', () => {
    const state = { ...INITIAL_STATE, analysisId, queryMetadata: savedMetadata };
    const message = { type: 'savedUpdateState', analysisId, phase: 'cancelling', canUpdate: false };
    expect(toViewActions(message, queries, state)).toEqual([message]);
    for (const malformed of [
      { ...message, analysisId: 'an_1111111111111111' },
      { ...message, phase: 'rolledBack' },
      { ...message, canUpdate: 'true' }
    ])
      expect(toViewActions(malformed, queries, state)).toEqual([]);
    expect(toViewActions(message, queries, { ...INITIAL_STATE, analysisId })).toEqual([]);
  });
  it('shows a current source refusal and ignores an error from a replaced saved session', () => {
    const state = { ...INITIAL_STATE, analysisId, queryMetadata: savedMetadata };
    const refusal = {
      type: 'error',
      analysisId,
      requestId: 'req_0000000000000001',
      code: 'QUERY_SOURCE_OPEN_FAILED',
      message: 'QUERY_SOURCE_DIRTY'
    };
    expect(toViewActions(refusal, queries, state)).toEqual([
      { type: 'errorRaised', code: refusal.code, message: refusal.message }
    ]);
    expect(
      toViewActions({ ...refusal, analysisId: 'an_1111111111111111' }, queries, state)
    ).toEqual([]);
  });
  it('asks to update the saved index after an input change without claiming freshness', () => {
    const state = viewReducer(INITIAL_STATE, {
      type: 'analysisComplete',
      analysisId,
      completeness: 'partial',
      coverage: undefined,
      queryMetadata: savedMetadata
    });
    const actions = toViewActions(
      { type: 'stale', analysisId, reason: 'savedChange' },
      queries,
      state
    );
    expect(actions).toEqual([
      {
        type: 'analysisStale',
        message: 'Saved inputs changed. Update the saved index, then refresh this view.'
      }
    ]);
    const stale = viewReducer(state, actions[0]);
    expect(stale.queryMetadata).toEqual(savedMetadata);
    expect(stale.status).toBe('stale');
  });

  it('keeps saved metadata and actual symbol/edge certainty without legacy completion', () => {
    const actions = toViewActions(
      {
        type: 'analysisComplete',
        analysisId,
        completeness: 'completeWithinScope',
        coverage: {},
        queryMetadata: savedMetadata
      },
      queries
    );
    expect(actions[0]).toMatchObject({
      type: 'analysisComplete',
      completeness: 'partial',
      queryMetadata: savedMetadata
    });
    const result = toViewActions(
      {
        type: 'projection',
        analysisId,
        projection: {
          granularity: 'type',
          scope: { kind: 'root' },
          queryMetadata: savedMetadata,
          nodes: [
            {
              id: 'mb_1111111111111111',
              name: 'Run',
              kind: 'Member',
              granularity: 'type',
              certainty: 'Candidate'
            }
          ],
          edges: [
            {
              id: 'rel_1111111111111111',
              sourceId: 'mb_1111111111111111',
              targetId: 'mb_2222222222222222',
              certainty: 'Unresolved',
              sourceOccurrenceId: null,
              targetOccurrenceId: 'occurrence-1',
              variantId: 'variant-net10'
            }
          ]
        }
      },
      queries
    );
    expect(result[0]).toMatchObject({
      type: 'projectionReceived',
      projection: {
        queryMetadata: savedMetadata,
        nodes: [{ kind: 'Member', certainty: 'Candidate' }],
        edges: [
          {
            certainty: 'Unresolved',
            sourceOccurrenceId: null,
            targetOccurrenceId: 'occurrence-1',
            variantId: 'variant-net10'
          }
        ]
      }
    });
  });

  it('drops malformed metadata and mismatched generation/variant/analysis replies', () => {
    const state = viewReducer(INITIAL_STATE, {
      type: 'analysisComplete',
      analysisId,
      completeness: 'partial',
      coverage: undefined,
      queryMetadata: savedMetadata
    });
    const reply = {
      type: 'searchResults',
      requestId: 'req_0000000000000001',
      analysisId,
      total: 9999,
      items: [],
      queryMetadata: savedMetadata
    };
    expect(toViewActions(reply, queries, state)[0]).toMatchObject({
      total: 9999,
      queryMetadata: savedMetadata
    });
    for (const queryMetadata of [
      { ...savedMetadata, returnedCount: -1 },
      { ...savedMetadata, coverage: 'unknown' },
      { ...savedMetadata, diagnostics: [{ code: 'x', message: 42 }] },
      { ...savedMetadata, generation: 4 },
      { ...savedMetadata, snapshotId: 'other' },
      { ...savedMetadata, variantIds: ['other-variant'] }
    ])
      expect(toViewActions({ ...reply, queryMetadata }, queries, state)).toEqual([]);
    expect(toViewActions({ ...reply, analysisId: 'an_ffffffffffffffff' }, queries, state)).toEqual(
      []
    );
    expect(toViewActions({ ...reply, queryMetadata: undefined }, queries, state)).toEqual([]);
  });

  it('keeps returned metadata on tree, details and evidence replies', () => {
    const treeRequests = new Map([['req_tree', { treeParentId: 'root', appendTree: true }]]);
    expect(
      toViewActions(
        {
          type: 'searchResults',
          requestId: 'req_tree',
          analysisId,
          total: 2,
          items: [],
          queryMetadata: savedMetadata
        },
        treeRequests
      )[0]
    ).toMatchObject({ type: 'treeReceived', queryMetadata: savedMetadata });
    expect(
      toViewActions(
        {
          type: 'details',
          analysisId,
          entity: { id: 'ty_1111111111111111', name: 'A' },
          queryMetadata: savedMetadata
        },
        queries
      )[0]
    ).toMatchObject({ type: 'detailsReceived', queryMetadata: savedMetadata });
    expect(
      toViewActions(
        {
          type: 'evidencePage',
          analysisId,
          relationId: 'rel_1111111111111111',
          items: [],
          queryMetadata: savedMetadata
        },
        queries
      )[0]
    ).toMatchObject({ type: 'evidenceReceived', queryMetadata: savedMetadata });
  });
  it('retains per-project completeness and limits through the host message boundary', () => {
    const actions = toViewActions(
      {
        type: 'searchResults',
        analysisId,
        requestId: 'req_0000000000000001',
        total: 1,
        items: [
          {
            id: 'prj_1111111111111111',
            name: 'Broken',
            granularity: 'project',
            analysisStatus: 'partial',
            analysisLimitations: ['Compiler error', 42]
          }
        ]
      },
      queries
    );
    expect(actions).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          items: expect.arrayContaining([
            expect.objectContaining({
              analysisStatus: 'partial',
              analysisLimitations: ['Compiler error']
            })
          ])
        })
      ])
    );
  });
  it('maps analysis progress and completion', () => {
    expect(
      toViewActions(
        {
          type: 'analysisProgress',
          analysisId,
          stage: 'extract',
          analyzed: 3,
          elapsedMs: 1200
        },
        queries
      )
    ).toEqual([
      {
        type: 'analysisProgress',
        stage: 'extract',
        loaded: undefined,
        analyzed: 3,
        elapsedMs: 1200
      }
    ]);

    expect(
      toViewActions(
        {
          type: 'analysisComplete',
          analysisId,
          completeness: 'partial',
          coverage: { discovered: 4, loaded: 4, analyzed: 3, failed: 1, skipped: 0 },
          limitations: [{ code: 'semantic.compilationErrors', message: 'x' }]
        },
        queries
      )
    ).toEqual([
      {
        type: 'analysisComplete',
        analysisId,
        completeness: 'partial',
        coverage: { discovered: 4, loaded: 4, analyzed: 3, failed: 1, skipped: 0 },
        limitations: [{ code: 'semantic.compilationErrors', message: 'x' }]
      }
    ]);
  });

  it('maps a projection with the fields the graph needs', () => {
    const actions = toViewActions(
      {
        type: 'projection',
        requestId: 'req_0000000000000002',
        analysisId,
        projection: {
          scope: { kind: 'root' },
          granularity: 'namespace',
          nodes: [{ id: 'ns_1111111111111111', name: 'Core', granularity: 'namespace' }],
          edges: [
            {
              id: 'rel_1111111111111111',
              sourceId: 'ns_1111111111111111',
              targetId: 'ns_2222222222222222',
              basis: 'usingInferred',
              kinds: ['typeUse'],
              evidenceCount: 2,
              inCycle: false,
              underlyingRelationIds: ['rel_1111111111111111', 'rel_2222222222222222']
            }
          ],
          totalNodeCount: 2,
          totalEdgeCount: 1,
          truncated: false
        }
      },
      queries
    );

    expect(actions).toHaveLength(1);
    expect(actions[0]).toMatchObject({
      type: 'projectionReceived',
      projection: {
        granularity: 'namespace',
        edges: [
          {
            id: 'rel_1111111111111111',
            underlyingRelationIds: ['rel_1111111111111111', 'rel_2222222222222222']
          }
        ]
      }
    });
  });

  it('maps search results back to the query that asked for them', () => {
    const actions = toViewActions(
      {
        type: 'searchResults',
        requestId: 'req_0000000000000001',
        analysisId,
        total: 1,
        items: [{ id: 'ty_1111111111111111', name: 'OrderStore', granularity: 'type' }]
      },
      queries
    );

    expect(actions[0]).toMatchObject({
      type: 'searchResultsReceived',
      query: 'OrderStore',
      total: 1
    });
  });

  it('ignores unknown or malformed messages', () => {
    expect(toViewActions(null, queries)).toEqual([]);
    expect(toViewActions({ type: 'projection' }, queries)).toEqual([]);
    expect(
      toViewActions({ type: 'projection', projection: { granularity: 'type' } }, queries)
    ).toEqual([]);
    expect(toViewActions({ type: 'cycleWitness', witness: {} }, queries)).toEqual([]);
  });

  it('turns host errors into a raised error action', () => {
    expect(
      toViewActions({ type: 'error', code: 'bridge.notImplemented', message: 'x' }, queries)
    ).toEqual([{ type: 'errorRaised', code: 'bridge.notImplemented', message: 'x' }]);
  });

  it('marks an evidence page that continues the list', () => {
    const paging = new Map([
      ['req_0000000000000003', { appendEvidence: true }],
      ['req_0000000000000004', { query: 'Order' }]
    ]);
    const append = toViewActions(
      {
        type: 'evidencePage',
        requestId: 'req_0000000000000003',
        analysisId,
        relationId: 'rel_1111111111111111',
        total: 3,
        items: [{ id: 'ev_2222222222222222', documentPath: 'src/Domain/Order.cs' }],
        nextCursor: null
      },
      paging
    );
    expect(append[0]).toMatchObject({
      type: 'evidenceReceived',
      append: true,
      total: 3,
      items: [{ documentPath: 'src/Domain/Order.cs' }]
    });

    const replace = toViewActions(
      {
        type: 'evidencePage',
        requestId: 'req_0000000000000004',
        analysisId,
        relationId: 'rel_1111111111111111',
        total: 3,
        items: []
      },
      paging
    );
    expect(replace[0]).toMatchObject({ type: 'evidenceReceived', append: false });
  });

  it('maps a reveal to a selection with the requested scope', () => {
    const actions = toViewActions(
      {
        type: 'reveal',
        analysisId,
        entityId: 'ty_1111111111111111',
        scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 1 },
        granularity: 'type'
      },
      queries
    );

    expect(actions).toEqual([
      {
        type: 'revealRequested',
        entityId: 'ty_1111111111111111',
        scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 1 },
        granularity: 'type'
      }
    ]);

    // A reveal without an entity, or with a broken scope, is ignored or repaired.
    expect(toViewActions({ type: 'reveal', analysisId }, queries)).toEqual([]);
    expect(
      toViewActions(
        {
          type: 'reveal',
          analysisId,
          entityId: 'ty_1111111111111111',
          scope: { kind: 'nonsense' }
        },
        queries
      )
    ).toEqual([
      {
        type: 'revealRequested',
        entityId: 'ty_1111111111111111',
        scope: { kind: 'root', id: null, depth: null },
        granularity: undefined
      }
    ]);
  });

  it('reads the cycle groups and keeps the witness separate from the member set', () => {
    const actions = toViewActions(
      {
        type: 'projection',
        requestId: 'req_0000000000000006',
        analysisId,
        projection: {
          scope: { kind: 'root' },
          granularity: 'type',
          nodes: [
            { id: 'ty_1111111111111111', name: 'A', granularity: 'type' },
            { id: 'ty_2222222222222222', name: 'B', granularity: 'type' }
          ],
          edges: [],
          totalNodeCount: 2,
          totalEdgeCount: 0,
          truncated: false,
          cycleGroups: [
            {
              id: 'cyc_1111111111111111',
              scope: 'type',
              basis: 'symbolResolved',
              memberIds: ['ty_2222222222222222', 'ty_1111111111111111'],
              internalRelationIds: ['rel_1111111111111111'],
              witness: {
                memberIds: ['ty_1111111111111111', 'ty_2222222222222222'],
                relationIds: ['rel_1111111111111111']
              }
            },
            { id: 'cyc_2222222222222222', memberIds: ['ty_1111111111111111'] }
          ]
        }
      },
      queries
    );

    expect(actions[0]).toMatchObject({
      type: 'projectionReceived',
      projection: {
        cycleGroups: [
          {
            id: 'cyc_1111111111111111',
            memberIds: ['ty_2222222222222222', 'ty_1111111111111111'],
            witness: {
              memberIds: ['ty_1111111111111111', 'ty_2222222222222222'],
              relationIds: ['rel_1111111111111111']
            }
          },
          { id: 'cyc_2222222222222222', witness: null, internalRelationIds: [] }
        ]
      }
    });
  });

  it('restores a persisted view state without starting an analysis', () => {
    const actions = toViewActions(
      {
        type: 'viewState',
        state: {
          version: 1,
          targetName: 'Sample.sln',
          targetRelativePath: 'src/Sample.sln',
          selection: { entityId: 'ty_1111111111111111' },
          paneWidths: { navigation: 260, inspector: 400 },
          camera: { zoom: 1.4, scrollLeft: 0, scrollTop: 0 }
        }
      },
      queries
    );

    expect(actions).toHaveLength(1);
    expect(actions[0]).toMatchObject({
      type: 'stateRestored',
      state: {
        target: { name: 'Sample.sln', relativePath: 'src/Sample.sln' },
        selection: { entityId: 'ty_1111111111111111' },
        camera: { zoom: 1.4, scrollLeft: 0, scrollTop: 0 }
      }
    });
    // Nothing in a restored state can ask for an analysis or a projection.
    expect(actions[0]).not.toMatchObject({ type: 'analyzeStarted' });

    expect(toViewActions({ type: 'viewState', state: { version: 99 } }, queries)).toEqual([]);
  });

  it('reads the aggregated relation breakdown defensively', () => {
    const actions = toViewActions(
      {
        type: 'projection',
        requestId: 'req_0000000000000005',
        analysisId,
        projection: {
          scope: { kind: 'root' },
          granularity: 'namespace',
          nodes: [{ id: 'ns_1111111111111111', name: 'Core', granularity: 'namespace' }],
          edges: [
            {
              id: 'rel_1111111111111111',
              sourceId: 'ns_1111111111111111',
              targetId: 'ns_2222222222222222',
              basis: 'symbolResolved',
              kinds: ['calls'],
              evidenceCount: 3,
              inCycle: false,
              underlyingRelationIds: ['rel_1111111111111111', 'rel_2222222222222222'],
              underlyingRelations: [
                {
                  id: 'rel_1111111111111111',
                  basis: 'symbolResolved',
                  kinds: ['calls'],
                  evidenceCount: 2
                },
                {
                  id: 'rel_2222222222222222',
                  basis: 'symbolResolved',
                  kinds: ['typeUse'],
                  evidenceCount: 1
                },
                { id: 'broken' }
              ]
            }
          ],
          totalNodeCount: 2,
          totalEdgeCount: 1,
          truncated: false
        }
      },
      queries
    );

    expect(actions[0]).toMatchObject({
      type: 'projectionReceived',
      projection: {
        edges: [
          {
            underlyingRelations: [
              { id: 'rel_1111111111111111', evidenceCount: 2 },
              { id: 'rel_2222222222222222', evidenceCount: 1 }
            ]
          }
        ]
      }
    });
  });
});
