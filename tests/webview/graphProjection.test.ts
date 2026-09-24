// Graph projection tests (SD-017, first half): aggregation to namespace/project
// granularity, scope handling, and display budgets.

import { describe, expect, it } from 'vitest';
import type { AnalysisSnapshot } from '../../src/analyzer/reportV2';
import { buildProjection } from '../../src/analyzer/graphProjection';
import { IDS, makeSnapshot } from '../helpers/reportV2Fixtures';

function snapshotWithHierarchy(): AnalysisSnapshot {
  const base = makeSnapshot();
  return {
    ...base,
    projects: [
      {
        id: IDS.projectA,
        variantId: IDS.variantA,
        name: 'App',
        relativePath: 'src/App/App.csproj',
        groupPath: 'src',
        kind: 'app',
        targetFramework: 'net10.0',
        loadState: 'loaded'
      },
      {
        id: IDS.projectB,
        variantId: IDS.variantB,
        name: 'Core',
        relativePath: 'src/Core/Core.csproj',
        groupPath: 'src',
        kind: 'library',
        targetFramework: 'net10.0',
        loadState: 'loaded'
      }
    ],
    namespaces: [
      { id: IDS.namespaceA, projectVariantId: IDS.variantA, name: 'App', typeCount: 1 },
      { id: IDS.namespaceB, projectVariantId: IDS.variantB, name: 'Core', typeCount: 2 }
    ],
    types: [
      {
        id: IDS.typeA,
        projectVariantId: IDS.variantA,
        namespaceId: IDS.namespaceA,
        name: 'Program',
        fullName: 'App.Program',
        kind: 'class',
        accessibility: 'internal',
        isPartial: false,
        declarationCount: 1,
        memberCount: 1
      },
      {
        id: IDS.typeB,
        projectVariantId: IDS.variantB,
        namespaceId: IDS.namespaceB,
        name: 'Order',
        fullName: 'Core.Order',
        kind: 'class',
        accessibility: 'public',
        isPartial: false,
        declarationCount: 1,
        memberCount: 2
      },
      {
        id: 'ty_3333333333333333',
        projectVariantId: IDS.variantB,
        namespaceId: IDS.namespaceB,
        name: 'Store',
        fullName: 'Core.Store',
        kind: 'class',
        accessibility: 'public',
        isPartial: false,
        declarationCount: 1,
        memberCount: 1
      }
    ],
    relations: [
      {
        ...base.relations[0],
        id: 'rel_1111111111111111',
        sourceEntityId: IDS.typeA,
        targetEntityId: IDS.typeB,
        kinds: ['constructs'],
        evidenceCount: 2
      },
      {
        ...base.relations[0],
        id: 'rel_2222222222222222',
        sourceEntityId: IDS.typeA,
        targetEntityId: 'ty_3333333333333333',
        kinds: ['calls'],
        evidenceCount: 1
      },
      {
        // Inside one namespace and one project: never an edge at coarser granularity.
        ...base.relations[0],
        id: 'rel_3333333333333333',
        sourceEntityId: IDS.typeB,
        targetEntityId: 'ty_3333333333333333',
        kinds: ['typeUse'],
        evidenceCount: 5
      }
    ],
    cycleGroups: []
  };
}

describe('buildProjection', () => {
  it('aggregates type relations into namespace and project edges', () => {
    const snapshot = snapshotWithHierarchy();

    const namespaces = buildProjection(snapshot, { granularity: 'namespace' });
    const namespaceEdge = namespaces.edges.find(
      (edge) => edge.sourceId === IDS.namespaceA && edge.targetId === IDS.namespaceB
    );

    expect(namespaceEdge).toBeDefined();
    // Two type relations aggregate into one namespace edge; occurrence counts add up.
    expect(namespaceEdge?.evidenceCount).toBe(3);
    expect(namespaceEdge?.kinds.sort()).toEqual(['calls', 'constructs']);
    // The edge id is the representative relation (most evidence first), so selecting the
    // edge yields a relation the store can page.
    expect(namespaceEdge?.id).toBe('rel_1111111111111111');
    expect(namespaceEdge?.underlyingRelationIds).toHaveLength(2);
    // The relation inside Core is not an edge of the Core namespace.
    expect(namespaces.edges.some((edge) => edge.sourceId === edge.targetId)).toBe(false);
    expect(namespaces.totalNodeCount).toBe(2);

    const projects = buildProjection(snapshot, { granularity: 'project' });
    expect(projects.edges).toHaveLength(1);
    expect(projects.edges[0].sourceId).toBe(IDS.projectA);
    expect(projects.edges[0].targetId).toBe(IDS.projectB);
  });

  it('keeps type granularity one-to-one with the stored relations', () => {
    const snapshot = snapshotWithHierarchy();
    const types = buildProjection(snapshot, { granularity: 'type' });

    expect(types.nodes).toHaveLength(3);
    expect(types.edges).toHaveLength(3);
  });

  it('limits a local scope to the requested direction and depth', () => {
    const snapshot = snapshotWithHierarchy();

    const dependencies = buildProjection(snapshot, {
      granularity: 'namespace',
      scope: { kind: 'dependencies', id: IDS.namespaceA, depth: 1 }
    });
    expect(dependencies.nodes.map((node) => node.name).sort()).toEqual(['App', 'Core']);

    const dependentsOfCore = buildProjection(snapshot, {
      granularity: 'namespace',
      scope: { kind: 'dependents', id: IDS.namespaceB, depth: 1 }
    });
    expect(dependentsOfCore.nodes.map((node) => node.name).sort()).toEqual(['App', 'Core']);
  });

  it('applies the display budget and reports what it omitted', () => {
    const snapshot = snapshotWithHierarchy();
    const budgeted = buildProjection(snapshot, { granularity: 'type', maxNodes: 1, maxEdges: 1 });

    expect(budgeted.nodes).toHaveLength(1);
    expect(budgeted.truncated).toBe(true);
    expect(budgeted.totalNodeCount).toBe(3);
    expect(budgeted.totalEdgeCount).toBe(3);
  });
});
