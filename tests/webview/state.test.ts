// View state tests (SD-015).
//
// The reducer is pure, so every UI state the plan requires can be produced from a
// fixture without a browser. The structural assertions (which state renders which
// message, filters, breadcrumbs) live in the Playwright pass for this fixture.

import { describe, expect, it } from 'vitest';
import {
  INITIAL_STATE,
  HISTORY_LIMIT,
  selectBreadcrumbs,
  selectStatusFooter,
  selectVisibleData,
  selectTableRows,
  selectPrimaryAction,
  selectStatusMessage,
  candidateContextKey,
  viewReducer,
  type ViewAction,
  type ViewState
} from '../../media/app/state';
import type {
  HostToWebviewMessage,
  Projection,
  QueryResultMetadata
} from '../../src/view/protocolV2';
import { restoreViewState, serializeViewState } from '../../media/app/serializer';
import { toViewActions, evidenceRequestContext } from '../../media/app/hostMessages';

const savedMetadata: QueryResultMetadata = {
  provider: 'savedIndex',
  workspaceId: 'hw_fixture',
  snapshotId: 'snapshot-3',
  generation: 3,
  variantIds: ['variant-net10'],
  coverage: 'CompleteWithinScope',
  freshness: 'unverified',
  truncated: true,
  truncationReasons: ['PAGE_LIMIT'],
  diagnostics: [{ code: 'INPUTS_UNVERIFIED', message: 'No configuration proof.' }],
  returnedCount: 2,
  totalKind: 'returned',
  candidateCount: 1,
  unresolvedCount: 0
};

function projection(overrides: Partial<Projection> = {}): Projection {
  return {
    scope: { kind: 'root', id: null },
    granularity: 'type',
    nodes: [
      { id: 'ty_1111111111111111', name: 'App.Order', granularity: 'type', kind: 'class' },
      {
        id: 'ty_2222222222222222',
        name: 'Core.Order',
        granularity: 'type',
        kind: 'class',
        inCycle: true
      },
      {
        id: 'ty_3333333333333333',
        name: 'System.String',
        granularity: 'type',
        kind: 'class',
        isExternal: true
      }
    ],
    edges: [
      {
        id: 'rel_1111111111111111',
        sourceId: 'ty_1111111111111111',
        targetId: 'ty_2222222222222222',
        basis: 'symbolResolved',
        kinds: ['constructs'],
        evidenceCount: 2,
        inCycle: false
      },
      {
        id: 'rel_2222222222222222',
        sourceId: 'ty_2222222222222222',
        targetId: 'ty_3333333333333333',
        basis: 'symbolResolved',
        kinds: ['calls'],
        evidenceCount: 1,
        inCycle: false
      }
    ],
    totalNodeCount: 42,
    totalEdgeCount: 77,
    truncated: true,
    ...overrides
  };
}

function withProjection(state: ViewState = INITIAL_STATE): ViewState {
  return viewReducer(
    viewReducer(state, { type: 'targetSelected', name: 'Baseline', relativePath: 'Baseline.sln' }),
    { type: 'projectionReceived', projection: projection() }
  );
}

describe('isolated Candidate declaration investigation', () => {
  const originId = 'ci_' + '1'.repeat(32);
  const mentionId = 'ty_1111111111111111';
  const targetEntityId = 'ty_2222222222222222';
  const variantId = 'hvar_' + '1'.repeat(64);
  const metadata = { ...savedMetadata, variantIds: [variantId] };
  const response: Extract<HostToWebviewMessage, { type: 'candidateInvestigation' }> = {
    type: 'candidateInvestigation',
    requestId: 'req_1111111111111111',
    analysisId: 'an_0123456789abcdef',
    originId,
    relationId: 'rel_1111111111111111',
    targetEntityId,
    certainty: 'Candidate',
    relationKind: 'mentionsSymbol',
    reason: 'name only',
    message: 'Relation remains uncertain; declarations are checked separately.',
    queryMetadata: metadata,
    declarations: [
      {
        capabilityId: 'cd_' + '1'.repeat(32),
        occurrenceId: 'hocc_' + '1'.repeat(64),
        variantId,
        path: 'src/Helper.cs'
      },
      {
        capabilityId: 'cd_' + '2'.repeat(32),
        occurrenceId: 'hocc_' + '2'.repeat(64),
        variantId,
        path: 'src/Helper.Partial.cs'
      }
    ]
  };
  function current(): ViewState {
    return {
      ...INITIAL_STATE,
      analysisId: response.analysisId,
      granularity: 'type',
      status: 'partial',
      queryMetadata: metadata,
      selection: { entityId: mentionId },
      inspectorOpen: true,
      search: 'Compute',
      filters: { resultKinds: ['docs'] },
      projection: projection({ queryMetadata: metadata }),
      projectionRequestId: 'req_2222222222222222',
      details: {
        entityId: mentionId,
        entity: {
          id: mentionId,
          name: 'Compute mention',
          kind: 'SymbolMention',
          certainty: 'Resolved',
          granularity: 'type'
        },
        dependencies: [
          {
            id: targetEntityId,
            name: 'Compute',
            kind: 'Member',
            certainty: 'Candidate',
            granularity: 'type'
          }
        ],
        dependents: [],
        queryMetadata: metadata,
        candidateOrigins: [
          { originId, relationId: response.relationId, targetEntityId, targetName: 'Compute' }
        ]
      }
    };
  }

  it('keeps investigation data outside the normal projection, details, metadata and persisted state', () => {
    const before = current();
    const pending = viewReducer(before, { type: 'candidateInvestigationRequested', originId });
    expect(pending.candidateInvestigation).toMatchObject({ originId, pending: true });
    const investigated = viewReducer(pending, { type: 'candidateInvestigationReceived', response });
    expect(investigated.candidateInvestigation).toMatchObject({
      originId,
      pending: false,
      response
    });
    expect(investigated.projection).toBe(before.projection);
    expect(investigated.details).toBe(before.details);
    expect(investigated.queryMetadata).toBe(before.queryMetadata);
    expect(investigated.projectionRequestId).toBe(before.projectionRequestId);
    expect(investigated.details?.dependencies[0].certainty).toBe('Candidate');
    const persisted = serializeViewState(investigated);
    expect(persisted).not.toHaveProperty('candidateInvestigation');
    expect(JSON.stringify(persisted)).not.toContain(response.declarations[0].capabilityId);
    expect(JSON.stringify(persisted)).not.toContain('src/Helper.cs');
    expect(
      restoreViewState({
        ...persisted,
        candidateInvestigation: investigated.candidateInvestigation
      }).state
    ).not.toHaveProperty('candidateInvestigation');
  });

  it('drops late results after closing or changing the selected query context', () => {
    const pending = viewReducer(current(), { type: 'candidateInvestigationRequested', originId });
    const changes: ViewAction[] = [
      { type: 'candidateInvestigationDismissed' },
      { type: 'entitySelected', entityId: targetEntityId },
      { type: 'scopeChanged', scope: { kind: 'dependencies', id: mentionId, depth: 2 } },
      { type: 'filtersChanged', filters: { resultKinds: ['code'] } },
      { type: 'searchChanged', search: 'different' },
      { type: 'temporaryDisplayAdded', entityId: targetEntityId },
      {
        type: 'analysisComplete',
        analysisId: 'an_1111111111111111',
        completeness: 'partial',
        coverage: undefined,
        queryMetadata: metadata
      }
    ];
    for (const action of changes) {
      const changed = viewReducer(pending, action);
      expect(changed.candidateInvestigation).toBeUndefined();
      expect(
        viewReducer(changed, { type: 'candidateInvestigationReceived', response })
          .candidateInvestigation
      ).toBeUndefined();
    }
  });

  it('binds Candidate context to the analysis pin and every query/selection input', () => {
    const before = current(),
      key = candidateContextKey(before);
    const changes: Partial<ViewState>[] = [
      { analysisId: 'an_1111111111111111' },
      { queryMetadata: { ...metadata, generation: metadata.generation + 1 } },
      { queryMetadata: { ...metadata, variantIds: ['hvar_' + '2'.repeat(64)] } },
      { selection: { entityId: targetEntityId } },
      { scope: { kind: 'dependencies', id: mentionId, depth: 2 } },
      { filters: { resultKinds: ['code'] } },
      { search: 'different' },
      { granularity: 'project' },
      { temporaryDisplayIds: [targetEntityId] }
    ];
    for (const change of changes)
      expect(candidateContextKey({ ...before, ...change })).not.toBe(key);
    expect(
      candidateContextKey({ ...before, camera: { zoom: 2, scrollLeft: 5, scrollTop: 10 } })
    ).toBe(key);
  });
});

describe('viewReducer status transitions', () => {
  it('restores source navigation without adding history or reusing old projection, details and cursors', () => {
    const relationId = 'rel_1111111111111111';
    const before: ViewState = {
      ...INITIAL_STATE,
      analysisId: 'an_0123456789abcdef',
      status: 'partial',
      queryMetadata: savedMetadata,
      savedUpdate: { phase: 'idle', canUpdate: true },
      projection: projection({ queryMetadata: savedMetadata }),
      projectionRequestId: 'req_1111111111111111',
      projectionTruncated: true,
      details: { entityId: 'ty_1111111111111111', dependencies: [], dependents: [] },
      evidence: {
        relationId,
        items: [{ id: 'ev_1111111111111111' }],
        total: 3,
        nextCursor: 'cur_1111111111111111',
        pending: true
      },
      tree: { root: { items: [], total: 2, nextCursor: 'cur_1111111111111111' } },
      searchResults: {
        query: 'old',
        items: [],
        total: 20,
        pending: true,
        nextCursor: 'cur_1111111111111111'
      },
      history: [{ scope: { kind: 'root' }, granularity: 'project' }]
    };
    const history: ViewState['history'] = [
      { scope: { kind: 'root' }, granularity: 'type', search: 'earlier' },
      {
        scope: { kind: 'type', id: 'ty_1111111111111111' },
        granularity: 'type',
        selectionId: 'ty_1111111111111111',
        search: 'Compute'
      }
    ];
    const returned: Extract<Parameters<typeof viewReducer>[1], { type: 'sourceReturned' }> = {
      type: 'sourceReturned',
      state: {
        scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 2 },
        granularity: 'type',
        selection: { relationId },
        history,
        search: 'Compute',
        filters: { resultKinds: ['code'], includeExternal: false },
        viewKind: 'table',
        camera: { zoom: 1.2, scrollLeft: 60, scrollTop: 250 },
        tablePage: 2,
        paneWidths: { navigation: 250, inspector: 400 }
      },
      ui: {
        navTab: 'cycles',
        focus: 'evidence',
        id: 'ev_1111111111111111',
        scroll: { navigation: 140, inspector: 680, map: 250 },
        searchCount: 40
      }
    };
    const next = viewReducer(before, returned);
    expect(next).toMatchObject({
      analysisId: before.analysisId,
      status: 'partial',
      queryMetadata: savedMetadata,
      savedUpdate: before.savedUpdate,
      scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 2 },
      granularity: 'type',
      selection: { relationId },
      history,
      search: 'Compute',
      filters: { resultKinds: ['code'], includeExternal: false },
      viewKind: 'table',
      camera: { zoom: 1.2, scrollLeft: 60, scrollTop: 250 },
      tablePage: 2,
      paneWidths: { navigation: 250, inspector: 400 },
      inspectorOpen: true,
      projection: null,
      details: null,
      evidence: null,
      tree: {},
      cycles: [],
      searchResults: { query: 'Compute', items: [], total: 0, pending: false }
    });
    expect(next.projectionRequestId).toBeUndefined();
    expect(next.searchResults.nextCursor).toBeUndefined();
    expect(next.history).toHaveLength(history.length);
    const refreshed = viewReducer(next, {
      type: 'projectionReceived',
      projection: projection({
        scope: next.scope,
        granularity: next.granularity,
        queryMetadata: savedMetadata
      }),
      requestId: 'req_2222222222222222'
    });
    expect(refreshed.selection).toEqual(next.selection);
    expect(refreshed.history).toEqual(next.history);
    expect(refreshed.projectionRequestId).toBe('req_2222222222222222');

    const quickBefore: ViewState = {
      ...before,
      queryMetadata: undefined,
      savedUpdate: undefined,
      mode: 'quick',
      resultMode: 'quick',
      capabilities: { ...before.capabilities, typeGraph: true }
    };
    const quick = viewReducer(quickBefore, returned);
    expect(quick.queryMetadata).toBeUndefined();
    expect(quick.analysisId).toBe(before.analysisId);
    expect(quick.scope).toEqual(next.scope);
    expect(quick.history).toEqual(history);
    expect(quick.selection).toEqual(next.selection);
    const complete = {
      type: 'analysisComplete' as const,
      analysisId: before.analysisId!,
      mode: 'quick' as const,
      completeness: 'partial' as const,
      coverage: undefined
    };
    expect(viewReducer(quick, complete).granularity).toBe('type');
    expect(viewReducer(quick, { ...complete, capabilities: quick.capabilities }).granularity).toBe(
      'type'
    );
    expect(
      viewReducer(quick, { ...complete, capabilities: { ...quick.capabilities, typeGraph: false } })
        .granularity
    ).toBe('project');
  });

  it('accumulates saved search pages without losing non-page limits, then invalidates filtered display data', () => {
    const first = {
      id: 'ty_1111111111111111',
      name: '調査',
      kind: 'Document',
      granularity: 'type' as const,
      certainty: 'Resolved' as const
    };
    const second = {
      id: 'ty_2222222222222222',
      name: 'Compute',
      kind: 'SymbolMention',
      granularity: 'type' as const,
      certainty: 'Candidate' as const
    };
    let state: ViewState = {
      ...INITIAL_STATE,
      analysisId: 'an_0123456789abcdef',
      search: '調査',
      queryMetadata: savedMetadata
    };
    state = viewReducer(state, {
      type: 'searchResultsReceived',
      query: '調査',
      items: [first],
      total: 1,
      nextCursor: 'cur_1111111111111111',
      queryMetadata: {
        ...savedMetadata,
        returnedCount: 1,
        candidateCount: 0,
        truncationReasons: ['PAGE_LIMIT', 'DEPTH_LIMIT']
      }
    });
    state = viewReducer(state, { type: 'searchPageRequested' });
    expect(state.searchResults).toMatchObject({
      pending: true,
      nextCursor: 'cur_1111111111111111',
      items: [first]
    });
    state = viewReducer(state, {
      type: 'searchResultsReceived',
      query: '調査',
      items: [first, second],
      total: 2,
      append: true,
      nextCursor: null,
      queryMetadata: {
        ...savedMetadata,
        returnedCount: 2,
        candidateCount: 1,
        truncated: false,
        truncationReasons: []
      }
    });
    expect(state.searchResults).toMatchObject({
      items: [first, second],
      total: 2,
      pending: false,
      nextCursor: null,
      queryMetadata: {
        returnedCount: 2,
        candidateCount: 1,
        truncated: true,
        truncationReasons: ['DEPTH_LIMIT']
      }
    });
    state = {
      ...state,
      projection: projection(),
      projectionRequestId: 'req_1111111111111111',
      selection: { entityId: first.id },
      temporaryDisplayIds: [first.id],
      tree: { root: { items: [first], total: 1 } }
    };
    state = viewReducer(state, {
      type: 'filtersChanged',
      filters: { resultKinds: ['code'], includeExternal: false }
    });
    expect(state).toMatchObject({
      filters: { resultKinds: ['code'], includeExternal: false },
      tree: {},
      projection: null,
      selection: {},
      details: null,
      evidence: null,
      temporaryDisplayIds: [],
      searchResults: INITIAL_STATE.searchResults
    });
    expect(state.projectionRequestId).toBeUndefined();
  });

  it('shows a saved display subset separately from complete engine coverage and returned totals', () => {
    let state = viewReducer(INITIAL_STATE, {
      type: 'analysisComplete',
      analysisId: 'an_0123456789abcdef',
      completeness: 'completeWithinScope',
      coverage: undefined,
      queryMetadata: savedMetadata
    });
    expect(state.status).toBe('partial');
    expect(state.granularity).toBe('type');
    expect(state.queryMetadata?.coverage).toBe('CompleteWithinScope');
    state = viewReducer(state, {
      type: 'projectionReceived',
      projection: projection({ queryMetadata: savedMetadata })
    });
    const footer = selectStatusFooter(state);
    expect(footer).toContain('Saved index');
    expect(footer).toContain('Generation 3');
    expect(footer).toContain('variant-net10');
    expect(footer).toContain('2 item(s) returned');
    expect(footer).toContain('1 candidate(s)');
    expect(footer).toContain('unverified');
    expect(footer).toContain('PAGE_LIMIT');
    expect(footer).not.toContain('/42');
    expect(footer).not.toContain('/77');
    expect(footer).not.toContain('Quick');
    expect(state.cycles).toEqual([]);
  });

  it('retains response metadata and rejects unsupported aggregation without restoring snapshot claims', () => {
    let state = viewReducer(INITIAL_STATE, {
      type: 'analysisComplete',
      analysisId: 'an_0123456789abcdef',
      completeness: 'partial',
      coverage: undefined,
      queryMetadata: savedMetadata
    });
    state = viewReducer(state, { type: 'searchChanged', search: 'Run' });
    const replyMetadata = {
      ...savedMetadata,
      returnedCount: 1,
      truncated: false,
      truncationReasons: []
    };
    state = viewReducer(state, {
      type: 'searchResultsReceived',
      query: 'Run',
      items: [],
      total: 999,
      queryMetadata: replyMetadata
    });
    expect(state.searchResults.queryMetadata).toEqual(replyMetadata);
    const unsupported = viewReducer(state, {
      type: 'granularityChanged',
      granularity: 'namespace'
    });
    expect(unsupported.granularity).toBe('type');
    expect(unsupported.error?.code).toBe('query.unsupportedOperation');
    expect(
      viewReducer(INITIAL_STATE, { type: 'stateRestored', state: { queryMetadata: savedMetadata } })
        .queryMetadata
    ).toBeUndefined();
    const legacy = viewReducer(state, {
      type: 'analysisComplete',
      analysisId: 'an_ffffffffffffffff',
      completeness: 'completeWithinScope',
      coverage: undefined,
      mode: 'quick'
    });
    expect(legacy.queryMetadata).toBeUndefined();
    expect(legacy.searchResults.queryMetadata).toBeUndefined();
    expect(legacy.status).toBe('complete');
    const scoped = viewReducer(state, {
      type: 'revealRequested',
      entityId: 'ty_1111111111111111',
      scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 1 }
    });
    const reopened = viewReducer(scoped, {
      type: 'analysisComplete',
      analysisId: 'an_ffffffffffffffff',
      completeness: 'partial',
      coverage: undefined,
      queryMetadata: savedMetadata
    });
    expect(reopened.scope.kind).toBe('root');
    expect(reopened.history).toEqual([]);
    expect(reopened.selection).toEqual({});
  });
  it('starts without a target and reports it explicitly', () => {
    expect(INITIAL_STATE.status).toBe('noTarget');
    expect(INITIAL_STATE.statusMessage).toContain('No analysis target');
  });

  it('moves ready → analyzing → complete', () => {
    const ready = viewReducer(INITIAL_STATE, {
      type: 'targetSelected',
      name: 'Baseline',
      relativePath: 'Baseline.sln'
    });
    expect(ready.status).toBe('ready');

    const analyzing = viewReducer(ready, {
      type: 'analyzeStarted',
      analysisId: 'an_0123456789abcdef',
      mode: 'quick'
    });
    expect(analyzing.status).toBe('analyzing');

    const complete = viewReducer(analyzing, {
      type: 'analysisComplete',
      analysisId: 'an_0123456789abcdef',
      completeness: 'completeWithinScope',
      coverage: { discovered: 6, loaded: 6, analyzed: 6, failed: 0, skipped: 0 }
    });
    expect(complete.status).toBe('complete');
    expect(complete.statusMessage).toContain('6/6');
    expect(complete.progress).toBeUndefined();
  });

  it('keeps partial results partial and failures distinct', () => {
    const partial = viewReducer(
      viewReducer(INITIAL_STATE, {
        type: 'analyzeStarted',
        analysisId: 'an_0123456789abcdef',
        mode: 'semantic'
      }),
      {
        type: 'analysisComplete',
        analysisId: 'an_0123456789abcdef',
        completeness: 'partial',
        coverage: { discovered: 6, loaded: 5, analyzed: 5, failed: 1, skipped: 0 },
        limitations: [{ code: 'quick.conditionNotEvaluated', message: 'conditions not evaluated' }]
      }
    );
    expect(partial.status).toBe('partial');
    expect(partial.limitations).toHaveLength(1);

    const failed = viewReducer(partial, {
      type: 'analysisFailed',
      message: 'The analyzer exited with code 1.'
    });
    expect(failed.status).toBe('failed');
    expect(failed.error?.message).toContain('code 1');

    const cancelled = viewReducer(partial, {
      type: 'analysisFailed',
      message: 'stopped',
      cancelled: true
    });
    expect(cancelled.status).toBe('cancelled');
    expect(cancelled.error).toBeUndefined();
  });

  it('marks stale results without discarding them', () => {
    const stale = viewReducer(withProjection(), {
      type: 'analysisStale',
      message: 'files changed'
    });

    expect(stale.status).toBe('stale');
    expect(stale.projection).not.toBeNull();
    expect(selectStatusFooter(stale)).toContain('stale result');
  });

  it('keeps the previous projection while a new analysis runs', () => {
    const running = viewReducer(withProjection(), {
      type: 'analyzeStarted',
      analysisId: 'an_ffffffffffffffff',
      mode: 'quick'
    });

    expect(running.status).toBe('analyzing');
    expect(running.projection).not.toBeNull();
  });
});

describe('viewReducer navigation and selection', () => {
  it('does not retain a restored relation when its current evidence request reports it missing', () => {
    const selected = viewReducer(
      { ...withProjection(), analysisId: 'an_0123456789abcdef', queryMetadata: savedMetadata },
      { type: 'relationSelected', relationId: 'rel_1111111111111111' }
    );
    const scoped = viewReducer(selected, {
      type: 'scopeChanged',
      scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 1 }
    });
    const back = viewReducer(scoped, { type: 'historyBack' });
    const requestId = 'req_1111111111111111';
    const actions = toViewActions(
      {
        type: 'error',
        requestId,
        analysisId: back.analysisId,
        code: 'QUERY_EDGE_UNKNOWN',
        message: 'Relation is unavailable in the saved provider.'
      },
      new Map([[requestId, evidenceRequestContext(back, 'rel_1111111111111111')]]),
      back
    );
    const missing = actions.reduce((state, action) => viewReducer(state, action), back);
    expect(missing.error?.code).toBe('QUERY_EDGE_UNKNOWN');
    expect(missing.evidence).toBeNull();
    expect(missing.selection).toEqual({});
  });

  it('does not restore old relation history after the same analysis id receives a new snapshot', () => {
    const selected = viewReducer(
      { ...withProjection(), analysisId: 'an_0123456789abcdef', queryMetadata: savedMetadata },
      { type: 'relationSelected', relationId: 'rel_1111111111111111' }
    );
    const scoped = viewReducer(selected, {
      type: 'scopeChanged',
      scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 1 }
    });
    const populated: ViewState = {
      ...scoped,
      projection: projection(),
      projectionRequestId: 'req_1111111111111111',
      selection: { relationId: 'rel_1111111111111111' },
      details: { entityId: 'ty_1111111111111111', dependencies: [], dependents: [] },
      evidence: {
        relationId: 'rel_1111111111111111',
        total: 2,
        items: [{ id: 'old' }],
        nextCursor: 'old-cursor',
        pending: false
      },
      tree: { root: { items: [], total: 1 } }
    };
    const changed = viewReducer(populated, {
      type: 'analysisComplete',
      analysisId: 'an_0123456789abcdef',
      completeness: 'partial',
      coverage: undefined,
      queryMetadata: { ...savedMetadata, snapshotId: 'snapshot-4', generation: 4 }
    });
    const back = viewReducer(changed, { type: 'historyBack' });
    expect(back.selection).toEqual({});
    expect(back.history).toHaveLength(0);
    expect(back.evidence).toBeNull();
    expect(changed.projection).toBeNull();
    expect(changed.projectionRequestId).toBeUndefined();
    expect(changed.details).toBeNull();
    expect(changed.tree).toEqual({});
  });

  it('restores a relation selection on Back without carrying its old evidence page or cursor', () => {
    const relationId = 'rel_1111111111111111';
    const selected = viewReducer(
      { ...withProjection(), queryMetadata: savedMetadata },
      { type: 'relationSelected', relationId }
    );
    const evidence = viewReducer(selected, {
      type: 'evidenceReceived',
      relationId,
      total: 2,
      items: [{ id: 'old-evidence' }],
      nextCursor: 'old-cursor',
      queryMetadata: savedMetadata
    });
    const scoped = viewReducer(evidence, {
      type: 'scopeChanged',
      scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 1 }
    });
    const returned = viewReducer(scoped, { type: 'historyBack' });
    expect(returned.selection).toEqual({ relationId });
    expect(returned.scope).toEqual(selected.scope);
    expect(returned.evidence).toBeNull();
    expect(returned.details).toBeNull();
    expect(returned.projection).toBeNull();
    expect(returned.queryMetadata).toBe(savedMetadata);
    const fresh = viewReducer(returned, {
      type: 'evidenceReceived',
      relationId,
      total: 1,
      items: [{ id: 'new-evidence' }],
      queryMetadata: savedMetadata
    });
    expect(fresh.evidence?.items).toEqual([{ id: 'new-evidence' }]);
    expect(fresh.evidence?.nextCursor).toBeUndefined();
    const mixedHistory = {
      ...scoped,
      history: [{ ...scoped.history[0], selectionId: 'ty_1111111111111111' }],
      details: { entityId: 'ty_1111111111111111', dependencies: [], dependents: [] }
    };
    const mixedBack = viewReducer(mixedHistory, { type: 'historyBack' });
    expect(mixedBack.selection).toEqual({ relationId });
    expect(mixedBack.details).toBeNull();
  });

  it('retains saved details only while navigating the same entity in the same analysis', () => {
    const entityId = 'ty_1111111111111111';
    const selected = viewReducer(
      { ...withProjection(), queryMetadata: savedMetadata },
      { type: 'entitySelected', entityId }
    );
    const state = viewReducer(selected, {
      type: 'detailsReceived',
      entityId,
      dependencies: [],
      dependents: [],
      queryMetadata: savedMetadata
    });
    const reveal = {
      type: 'revealRequested' as const,
      entityId,
      scope: { kind: 'dependencies' as const, id: entityId, depth: 1 }
    };
    const scoped = viewReducer(state, reveal);
    expect(scoped.details).toBe(state.details);
    expect(viewReducer(scoped, { type: 'historyBack' }).details).toBe(state.details);

    const other = viewReducer(scoped, { ...reveal, entityId: 'ty_2222222222222222' });
    expect(other.details).toBeNull();
    const otherSelected = viewReducer(scoped, {
      type: 'entitySelected',
      entityId: 'ty_2222222222222222'
    });
    const otherDetails = viewReducer(otherSelected, {
      type: 'detailsReceived',
      entityId: 'ty_2222222222222222',
      dependencies: [],
      dependents: [],
      queryMetadata: savedMetadata
    });
    expect(viewReducer(otherDetails, { type: 'historyBack' }).details).toBeNull();
    expect(
      viewReducer(scoped, {
        type: 'analysisComplete',
        analysisId: 'an_ffffffffffffffff',
        completeness: 'partial',
        coverage: undefined,
        queryMetadata: savedMetadata
      }).details
    ).toBeNull();
    expect(viewReducer({ ...state, queryMetadata: undefined }, reveal).details).toBeNull();
    expect(
      viewReducer({ ...scoped, queryMetadata: undefined }, { type: 'historyBack' }).details
    ).toBeNull();
  });

  it('keeps scope, search, filters, and selection when switching graph/table', () => {
    let state = withProjection();
    state = viewReducer(state, { type: 'searchChanged', search: 'order' });
    state = viewReducer(state, { type: 'filtersChanged', filters: { includeExternal: false } });
    state = viewReducer(state, { type: 'entitySelected', entityId: 'ty_1111111111111111' });

    const table = viewReducer(state, { type: 'viewKindChanged', viewKind: 'table' });

    expect(table.viewKind).toBe('table');
    expect(table.search).toBe('order');
    expect(table.filters).toEqual({ includeExternal: false });
    expect(table.selection.entityId).toBe('ty_1111111111111111');
    expect(table.projection).toBe(state.projection);
  });

  it('resets the projection when the granularity changes', () => {
    const changed = viewReducer(withProjection(), {
      type: 'granularityChanged',
      granularity: 'namespace'
    });

    expect(changed.granularity).toBe('namespace');
    expect(changed.projection).toBeNull();
    expect(changed.scope.kind).toBe('root');
  });

  it('walks back through history and caps it', () => {
    let state = withProjection();
    for (let index = 0; index < HISTORY_LIMIT + 5; index++) {
      state = viewReducer(state, {
        type: 'scopeChanged',
        scope: { kind: 'namespace', id: `ns_${String(index).padStart(16, '0')}` }
      });
    }

    expect(state.history.length).toBeLessThanOrEqual(HISTORY_LIMIT);

    const back = viewReducer(state, { type: 'historyBack' });
    expect(back.scope).not.toEqual(state.scope);
  });

  it('returns to the same sorted table page after exploring another scope and view', () => {
    const original = {
      ...withProjection(),
      viewKind: 'table' as const,
      tableSort: { key: 'name' as const, direction: 'desc' as const },
      tablePage: 2,
      projection: projection({
        nodes: Array.from({ length: 250 }, (_, index) => ({
          id: `ty_${index.toString(16).padStart(16, '0')}`,
          name: `Type${index.toString().padStart(3, '0')}`,
          granularity: 'type' as const,
          kind: 'class'
        }))
      })
    };
    const before = selectTableRows(original);
    let explored = viewReducer(original, {
      type: 'scopeChanged',
      scope: { kind: 'dependencies', id: original.projection.nodes[0].id, depth: 1 }
    });
    explored = viewReducer(explored, { type: 'viewKindChanged', viewKind: 'graph' });
    explored = viewReducer(explored, { type: 'tableSortChanged', key: 'kind' });
    explored = viewReducer(explored, { type: 'tablePageChanged', page: 0 });
    const returned = viewReducer(explored, { type: 'historyBack' });
    const received = viewReducer(returned, {
      type: 'projectionReceived',
      projection: original.projection
    });
    expect(received.viewKind).toBe('table');
    expect(received.tableSort).toEqual(original.tableSort);
    expect(selectTableRows(received)).toEqual(before);
    expect(before.page).toBe(2);
    expect(before.rows[0].entity.name).toBe('Type049');
  });

  it('tracks pane widths and inspector visibility', () => {
    const resized = viewReducer(INITIAL_STATE, {
      type: 'paneResized',
      pane: 'inspector',
      width: 480
    });
    expect(resized.paneWidths.inspector).toBe(480);

    const toggled = viewReducer(resized, { type: 'inspectorToggled' });
    expect(toggled.inspectorOpen).toBe(true);
  });
});

describe('selectors', () => {
  it('applies search and filters without touching the analysis', () => {
    let state = withProjection();
    state = viewReducer(state, { type: 'searchChanged', search: 'core' });

    const filtered = selectVisibleData(state);
    expect(filtered.nodes.map((node) => node.name)).toEqual(['Core.Order']);
    // The edge to the hidden external node is dropped, so nothing dangles.
    expect(filtered.edges).toHaveLength(0);
    expect(filtered.totalNodeCount).toBe(42);
    expect(filtered.isFilteredEmpty).toBe(false);
  });

  it('reports a filtered-empty result distinctly', () => {
    let state = withProjection();
    state = viewReducer(state, { type: 'searchChanged', search: 'nothing matches this' });

    const visible = selectVisibleData(state);
    expect(visible.isFilteredEmpty).toBe(true);
    expect(
      selectVisibleData({ ...state, projection: { ...state.projection!, nodes: [], edges: [] } })
        .isFilteredEmpty
    ).toBe(true);
    expect(visible.nodes).toHaveLength(0);
    expect(visible.totalNodeCount).toBe(42);
  });

  it('counts filters and shows them in the footer', () => {
    let state = withProjection();
    state = viewReducer(state, {
      type: 'filtersChanged',
      filters: { includeExternal: false, includeGenerated: true }
    });

    expect(selectVisibleData(state).filterCount).toBe(1);
    expect(selectStatusFooter(state)).toContain('display budget applied');
    expect(selectStatusFooter(state)).toContain('Quick');
  });

  it('builds breadcrumbs from the target and scope', () => {
    const named = viewReducer(INITIAL_STATE, {
      type: 'targetSelected',
      name: 'Baseline',
      relativePath: 'Baseline.sln'
    });
    expect(selectBreadcrumbs(named)).toEqual(['Baseline']);

    const scoped = viewReducer(named, {
      type: 'scopeChanged',
      scope: { kind: 'namespace', id: 'ns_1' }
    });
    expect(selectBreadcrumbs(scoped)).toEqual(['Baseline', 'namespace']);
  });
});

describe('state restoration', () => {
  it('never restores an analyzing state or invalid layout values', () => {
    const restored = viewReducer(INITIAL_STATE, {
      type: 'stateRestored',
      state: {
        granularity: 'namespace',
        viewKind: 'table',
        status: 'analyzing',
        paneWidths: { navigation: -50, inspector: 99_999 }
      }
    });

    expect(restored.granularity).toBe('namespace');
    expect(restored.viewKind).toBe('table');
    expect(restored.status).toBe('ready');
    expect(restored.paneWidths.navigation).toBe(120);
    expect(restored.paneWidths.inspector).toBe(800);
  });
});

describe('inspector close action', () => {
  it('closes idempotently instead of toggling back open', () => {
    const opened = viewReducer(INITIAL_STATE, { type: 'inspectorToggled' });
    expect(opened.inspectorOpen).toBe(true);

    const closed = viewReducer(opened, { type: 'inspectorClosed' });
    expect(closed.inspectorOpen).toBe(false);

    // Escape may be seen by more than one handler: a second close must stay closed.
    expect(viewReducer(closed, { type: 'inspectorClosed' })).toBe(closed);
  });

  it('ignores a close when the pane is already closed', () => {
    expect(viewReducer(INITIAL_STATE, { type: 'inspectorClosed' })).toBe(INITIAL_STATE);
  });
});

describe('primary action for a pinned saved reader', () => {
  it('keeps the old result through cancellation and requires host permission to retry', () => {
    const analysisId = 'an_0123456789abcdef';
    let state = viewReducer(INITIAL_STATE, {
      type: 'analysisComplete',
      analysisId,
      completeness: 'partial',
      coverage: undefined,
      queryMetadata: savedMetadata
    });
    state = viewReducer(state, {
      type: 'projectionReceived',
      projection: projection({ queryMetadata: savedMetadata })
    });
    state = viewReducer(state, { type: 'analysisStale', message: 'Saved input changed.' });
    const oldProjection = state.projection,
      oldMetadata = state.queryMetadata;
    expect(selectPrimaryAction(state).disabled).toBe(true);
    state = viewReducer(state, {
      type: 'savedUpdateState',
      analysisId,
      phase: 'idle',
      canUpdate: true
    });
    expect(selectPrimaryAction(state)).toMatchObject({
      intent: 'updateSavedIndex',
      disabled: false
    });
    for (const phase of ['selecting', 'updating', 'cancelling', 'cancelled', 'failed'] as const) {
      state = viewReducer(state, {
        type: 'savedUpdateState',
        analysisId,
        phase,
        canUpdate: phase === 'failed'
      });
      expect(state.projection).toBe(oldProjection);
      expect(state.queryMetadata).toBe(oldMetadata);
      expect(state.status).toBe('stale');
      if (phase === 'cancelling')
        expect(selectPrimaryAction(state)).toMatchObject({ intent: 'cancel', disabled: true });
    }
    expect(selectPrimaryAction(state)).toEqual({
      intent: 'updateSavedIndex',
      label: 'Retry update',
      disabled: false
    });
    expect(selectStatusMessage(state)).toContain('Refresh');
    const restored = viewReducer(state, {
      type: 'stateRestored',
      state: { savedUpdate: { phase: 'updating', canUpdate: false } }
    });
    expect(restored.savedUpdate).toEqual(state.savedUpdate);
    expect(
      viewReducer(state, {
        type: 'savedUpdateState',
        analysisId: 'an_1111111111111111',
        phase: 'updating',
        canUpdate: false
      })
    ).toBe(state);
    const legacy = viewReducer(state, {
      type: 'analysisComplete',
      analysisId: 'an_2222222222222222',
      completeness: 'completeWithinScope',
      coverage: undefined
    });
    expect(legacy.savedUpdate).toBeUndefined();
    expect(selectPrimaryAction(legacy).intent).toBe('analyze');
  });

  it('uses the same legacy button for analyze, update, retry and cancel', () => {
    expect(selectPrimaryAction(INITIAL_STATE).label).toBe('Analyze');
    for (const [status, label] of [
      ['stale', 'Update analysis'],
      ['failed', 'Retry analysis'],
      ['analyzing', 'Cancel']
    ] as const)
      expect(selectPrimaryAction({ ...INITIAL_STATE, status }).label).toBe(label);
  });
});
