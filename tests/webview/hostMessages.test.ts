// Host -> webview message mapping (SD-017).
//
// A malformed host message must not half-update the shell: every field is read
// defensively, and unknown types produce no action at all.

import { describe, expect, it } from 'vitest';
import { toViewActions } from '../../media/app/hostMessages';

const analysisId = 'an_0123456789abcdef';
const queries = new Map([['req_0000000000000001', 'OrderStore']]);

describe('toViewActions', () => {
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
});
