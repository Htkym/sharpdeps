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
  viewReducer,
  type ViewState
} from '../../media/app/state';
import type { Projection } from '../../src/view/protocolV2';

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

describe('viewReducer status transitions', () => {
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
