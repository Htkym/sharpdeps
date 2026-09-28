import { describe, expect, it } from 'vitest';
import { buildProjection } from '../../src/analyzer/graphProjection';
import { validateWebviewMessage } from '../../src/view/protocolV2';
import { INITIAL_STATE, viewReducer } from '../../media/app/state';
import { toViewActions } from '../../media/app/hostMessages';
import { buildNavigationTree } from '../../media/components/navigationPane';
import { makeSnapshot, IDS } from '../helpers/reportV2Fixtures';

describe('review regressions in the projection and protocol', () => {
  it('rejects malformed projection filters and profile selections at the message boundary', () => {
    const request = {
      type: 'getProjection',
      requestId: 'req_1111111111111111',
      analysisId: makeSnapshot().analysisId,
      granularity: 'type',
      scope: { kind: 'root' }
    };
    expect(validateWebviewMessage({ ...request, includeIds: [42] }).ok).toBe(false);
    expect(validateWebviewMessage({ ...request, filters: { basis: 'semantic' } }).ok).toBe(false);
    expect(
      validateWebviewMessage({
        type: 'analyze',
        requestId: request.requestId,
        mode: 'semantic',
        profile: { configuration: {} }
      }).ok
    ).toBe(false);
  });

  it('accepts a cycle scope and keeps explicit cycle members beyond the display budget', () => {
    const snapshot = makeSnapshot();
    const cycle = {
      id: 'cyc_1111111111111111',
      scope: 'project' as const,
      basis: 'projectDeclared' as const,
      memberIds: snapshot.projects.map((project) => project.id),
      internalRelationIds: [],
      witness: null
    };
    snapshot.cycleGroups = [cycle];
    const outside = { ...snapshot.projects[0], id: 'prj_ffffffffffffffff' };
    snapshot.projects.push(outside);
    const scope = { kind: 'cycle' as const, id: cycle.id };
    expect(
      validateWebviewMessage({
        type: 'getProjection',
        requestId: 'req_1111111111111111',
        analysisId: snapshot.analysisId,
        granularity: 'project',
        scope
      }).ok
    ).toBe(true);
    expect(
      buildProjection(snapshot, {
        scope,
        granularity: 'project',
        maxNodes: 1,
        includeIds: [outside.id]
      })
        .nodes.map((node) => node.id)
        .sort()
    ).toEqual(cycle.memberIds.sort());
  });

  it('adds requested entities outside the normal budget and filters', () => {
    const snapshot = makeSnapshot();
    const candidate = snapshot.namespaces[0];
    const projection = buildProjection(snapshot, {
      granularity: 'namespace',
      search: 'no-match',
      maxNodes: 1,
      includeIds: [candidate.id]
    });
    expect(projection.nodes.map((node) => node.id)).toContain(candidate.id);
  });

  it('does not apply responses from an old analysis to the new view', () => {
    const analysisId = makeSnapshot().analysisId;
    const state = { ...INITIAL_STATE, analysisId };
    expect(
      toViewActions(
        {
          type: 'projection',
          analysisId: 'an_ffffffffffffffff',
          projection: { scope: { kind: 'root' }, granularity: 'project', nodes: [], edges: [] }
        },
        new Map(),
        state
      )
    ).toEqual([]);
    const running = viewReducer(state, {
      type: 'analyzeStarted',
      analysisId: 'an_2222222222222222',
      mode: 'semantic'
    });
    expect(running.analysisId).toBe(analysisId);
    expect(
      toViewActions({ type: 'analysisComplete', analysisId, coverage: {} }, new Map(), running)
    ).toEqual([]);
    const failed = viewReducer(running, {
      type: 'analysisFailed',
      analysisId: running.runningAnalysisId,
      message: 'failed'
    });
    expect(failed.analysisId).toBe(analysisId);
  });

  it('keeps navigation ownership for equally named namespaces in different projects', () => {
    const nodes = buildNavigationTree([
      {
        id: 'ty_1111111111111111',
        name: 'First',
        granularity: 'type',
        projectId: 'prj_1111111111111111',
        projectName: 'One',
        namespaceId: 'ns_1111111111111111',
        namespaceName: 'Shared'
      },
      {
        id: 'ty_2222222222222222',
        name: 'Second',
        granularity: 'type',
        projectId: 'prj_2222222222222222',
        projectName: 'Two',
        namespaceId: 'ns_2222222222222222',
        namespaceName: 'Shared'
      }
    ]);
    expect(nodes.map((project) => project.children[0].children.map((type) => type.label))).toEqual([
      ['First'],
      ['Second']
    ]);
  });

  it('returns to the immediately preceding scope with its filters and camera', () => {
    const start = {
      ...INITIAL_STATE,
      search: 'Order',
      filters: { includeTests: false },
      camera: { zoom: 2, scrollLeft: 3, scrollTop: 4 }
    };
    const next = viewReducer(start, {
      type: 'scopeChanged',
      scope: { kind: 'dependencies', id: IDS.typeA, depth: 1 }
    });
    const restored = viewReducer(next, { type: 'historyBack' });
    expect(restored.scope).toEqual(start.scope);
    expect(restored.camera).toEqual(start.camera);
    expect(restored.filters).toEqual(start.filters);
    expect(restored.history).toEqual([]);
  });
});
