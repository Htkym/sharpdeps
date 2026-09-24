// Host -> webview message mapping (SD-017).
//
// A malformed host message must not half-update the shell: every field is read
// defensively, and unknown types produce no action at all.

import { describe, expect, it } from 'vitest';
import { toViewActions } from '../../media/app/hostMessages';

const analysisId = 'an_0123456789abcdef';
const queries = new Map<string, { query?: string; appendEvidence?: boolean }>([
  ['req_0000000000000001', { query: 'OrderStore' }]
]);

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
