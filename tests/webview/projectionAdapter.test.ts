// Protocol projection -> renderer input (SD-017).
//
// The mapping is pure, so the graph's labels, flags, and the "same graph" key are
// pinned without a DOM: selection changes must never alter the key, because a changed
// key re-runs ELK.

import { describe, expect, it } from 'vitest';
import type { Projection } from '../../src/view/protocolV2';
import { projectionKey, toGraphProjection } from '../../media/graph/projectionAdapter';

const projection: Projection = {
  scope: { kind: 'root' },
  granularity: 'type',
  nodes: [
    {
      id: 'ty_1111111111111111',
      name: '注文サービス',
      granularity: 'type',
      kind: 'class',
      projectName: 'Domain',
      inCycle: true,
      isExternal: false
    },
    {
      id: 'ty_2222222222222222',
      name: 'Infrastructure.RepositoryFactory',
      granularity: 'type',
      isExternal: true
    },
    {
      id: 'prj_3333333333333333',
      name: 'App',
      granularity: 'project',
      kind: 'app',
      inCycle: false
    }
  ],
  edges: [
    {
      id: 'rel_1111111111111111',
      sourceId: 'ty_1111111111111111',
      targetId: 'ty_2222222222222222',
      basis: 'usingInferred',
      kinds: ['typeUse'],
      evidenceCount: 7,
      inCycle: true,
      generatedEvidenceCount: 2,
      underlyingRelationIds: ['rel_1111111111111111']
    }
  ],
  totalNodeCount: 3,
  totalEdgeCount: 1,
  truncated: false
};

describe('toGraphProjection', () => {
  it('maps summaries to labels, kinds, and flags', () => {
    const graph = toGraphProjection(projection, 'all type');

    expect(graph.scopeLabel).toBe('all type');
    expect(graph.granularity).toBe('type');
    expect(graph.nodes[0]).toMatchObject({
      id: 'ty_1111111111111111',
      label: '注文サービス',
      kind: 'type',
      inCycle: true,
      sublabel: 'Domain'
    });
    // An external type without a project shows its kind and the external marker.
    expect(graph.nodes[1]).toMatchObject({ sublabel: 'External', isExternal: true });
    expect(graph.nodes[2]).toMatchObject({ kind: 'project', sublabel: undefined });
    expect(graph.edges[0]).toMatchObject({
      id: 'rel_1111111111111111',
      basis: 'usingInferred',
      evidenceCount: 7,
      generatedEvidenceCount: 2,
      inCycle: true
    });
  });

  it('omits repeated project labels while retaining useful context', () => {
    const graph = toGraphProjection(
      {
        ...projection,
        nodes: [
          {
            id: 'prj_3333333333333333',
            name: 'Domain',
            projectName: 'Domain',
            granularity: 'project'
          },
          {
            id: 'ty_1111111111111111',
            name: 'Customer',
            projectName: 'Domain',
            granularity: 'type'
          }
        ]
      },
      'test'
    );
    expect(graph.nodes[0].sublabel).toBeUndefined();
    expect(graph.nodes[1].sublabel).toBe('Domain');
  });

  it('keeps the layout key stable for selection-only changes', () => {
    const first = toGraphProjection(projection, 'all type');
    const second = toGraphProjection({ ...projection, nodes: [...projection.nodes] }, 'all type');

    expect(projectionKey(first)).toBe(projectionKey(second));
    expect(projectionKey(first)).not.toBe(projectionKey({ ...first, scopeLabel: 'other' }));
    expect(projectionKey(first)).not.toBe(
      projectionKey({
        ...first,
        nodes: first.nodes.map((node) => ({ ...node, label: node.label + ' changed' }))
      })
    );
  });
});
