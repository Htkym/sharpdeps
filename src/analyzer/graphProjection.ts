// Graph projection (SD-017, first half).
//
// The analysis stores relations at type granularity only, so coarse views are derived
// here: each type relation maps to its namespace or project parent, and the underlying
// relations stay reachable through the projection edge. Display budgets never change
// the analysis, and totals always describe the whole scope.

import type { AnalysisRelation, AnalysisSnapshot, Granularity } from './reportV2';
import type { EntitySummary, Projection, ProjectionEdge, Scope } from '../view/protocolV2';

export interface ProjectionRequest {
  scope?: Scope;
  granularity?: Granularity;
  maxNodes?: number;
  maxEdges?: number;
}

const DEFAULT_MAX_NODES = 300;
const DEFAULT_MAX_EDGES = 1000;

export function buildProjection(
  snapshot: AnalysisSnapshot,
  request: ProjectionRequest = {}
): Projection {
  const granularity = request.granularity ?? 'type';
  const maxNodes = Math.max(1, request.maxNodes ?? DEFAULT_MAX_NODES);
  const maxEdges = Math.max(1, request.maxEdges ?? DEFAULT_MAX_EDGES);

  const nodes = entitiesOf(snapshot, granularity);
  const parentOf = parentResolver(snapshot, granularity);
  const cycleMembers = new Set(snapshot.cycleGroups.flatMap((group) => group.memberIds));
  const summaries = new Map<string, EntitySummary>(
    nodes.map((node) => [node.id, { ...node, inCycle: cycleMembers.has(node.id) }])
  );

  // Aggregate type-level relations onto the requested granularity.
  const aggregated = new Map<
    string,
    { sourceId: string; targetId: string; relations: AnalysisRelation[] }
  >();
  for (const relation of snapshot.relations) {
    const sourceId = parentOf(relation.sourceEntityId);
    const targetId = parentOf(relation.targetEntityId);
    if (!sourceId || !targetId || sourceId === targetId) {
      // A relation inside one parent is not an edge of the parent graph.
      continue;
    }

    if (!summaries.has(sourceId) || !summaries.has(targetId)) {
      continue;
    }

    const key = `${sourceId}\u001f${targetId}`;
    const entry = aggregated.get(key) ?? { sourceId, targetId, relations: [] };
    entry.relations.push(relation);
    aggregated.set(key, entry);
  }

  // Pairs that share a cycle group are flagged on the aggregated edge.
  const cyclePairs = new Set<string>();
  for (const group of snapshot.cycleGroups) {
    for (const source of group.memberIds) {
      for (const target of group.memberIds) {
        if (source !== target) {
          cyclePairs.add(`${source}\u001f${target}`);
        }
      }
    }
  }

  const allEdges = [...aggregated.values()].map((entry) => toEdge(entry, cyclePairs));

  // Scope: the whole graph, one entity's neighbourhood, or a cycle under inspection.
  const scope = request.scope ?? { kind: 'root' };
  const priorityIds = scopePriorityIds(snapshot, scope, granularity, summaries);
  const scopedNodeIds =
    priorityIds === null
      ? new Set(summaries.keys())
      : neighbourhood(summaries, allEdges, priorityIds, scope);

  const scopedNodes = [...summaries.values()].filter((node) => scopedNodeIds.has(node.id));
  const scopedEdges = allEdges.filter(
    (edge) => scopedNodeIds.has(edge.sourceId) && scopedNodeIds.has(edge.targetId)
  );

  const degree = new Map<string, number>();
  for (const edge of scopedEdges) {
    degree.set(edge.sourceId, (degree.get(edge.sourceId) ?? 0) + 1);
    degree.set(edge.targetId, (degree.get(edge.targetId) ?? 0) + 1);
  }

  const ordered = [...scopedNodes].sort(
    (left, right) =>
      (degree.get(right.id) ?? 0) - (degree.get(left.id) ?? 0) ||
      left.name.localeCompare(right.name) ||
      left.id.localeCompare(right.id)
  );
  // The origin of a local scope is never dropped by the display budget: a node the user
  // explicitly scoped to (or a cycle member) must stay visible.
  const prioritySet = new Set(priorityIds ?? []);
  const priority = ordered.filter((node) => prioritySet.has(node.id));
  const rest = ordered.filter((node) => !prioritySet.has(node.id));
  const selected = [...priority, ...rest].slice(0, maxNodes);
  const selectedIds = new Set(selected.map((node) => node.id));
  const edges = scopedEdges
    .filter((edge) => selectedIds.has(edge.sourceId) && selectedIds.has(edge.targetId))
    .slice(0, maxEdges);

  return {
    scope,
    granularity,
    nodes: selected,
    edges,
    totalNodeCount: scopedNodes.length,
    totalEdgeCount: scopedEdges.length,
    truncated: selected.length < scopedNodes.length || edges.length < scopedEdges.length
  };
}

function toEdge(
  entry: { sourceId: string; targetId: string; relations: AnalysisRelation[] },
  cyclePairs: ReadonlySet<string>
): ProjectionEdge {
  // The representative relation is the one with the most evidence; its id becomes the
  // edge id, so selecting an edge always yields a relation the store can page for
  // evidence. Ties break on the id to stay deterministic.
  const ordered = [...entry.relations].sort(
    (left, right) => right.evidenceCount - left.evidenceCount || left.id.localeCompare(right.id)
  );
  const representative = ordered[0];
  const kinds = [...new Set(ordered.flatMap((relation) => relation.kinds))].sort();

  return {
    id: representative.id,
    sourceId: entry.sourceId,
    targetId: entry.targetId,
    basis: representative.basis,
    kinds,
    evidenceCount: ordered.reduce((total, relation) => total + relation.evidenceCount, 0),
    // An aggregated edge is "in cycle" when both ends belong to the same cycle group;
    // the per-relation detail stays reachable through underlyingRelationIds.
    inCycle: cyclePairs.has(`${entry.sourceId}\u001f${entry.targetId}`),
    generatedEvidenceCount: ordered.reduce(
      (total, relation) => total + relation.generatedEvidenceCount,
      0
    ),
    publicSurfaceEvidenceCount: ordered.reduce(
      (total, relation) => total + relation.publicSurfaceEvidenceCount,
      0
    ),
    underlyingRelationIds: ordered.map((relation) => relation.id)
  };
}

function entitiesOf(snapshot: AnalysisSnapshot, granularity: Granularity): EntitySummary[] {
  const projectNameByVariant = new Map(
    snapshot.projects.map((project) => [project.variantId, project.name])
  );

  switch (granularity) {
    case 'project':
      return snapshot.projects.map((project) => ({
        id: project.id,
        name: project.name,
        granularity: 'project',
        kind: project.kind,
        projectName: project.name,
        inCycle: false,
        isExternal: project.kind === 'unknown' && project.name === '(external)'
      }));
    case 'namespace':
      return snapshot.namespaces.map((node) => ({
        id: node.id,
        name: node.name,
        granularity: 'namespace',
        kind: 'namespace',
        projectName: projectNameByVariant.get(node.projectVariantId),
        inCycle: false
      }));
    default:
      return snapshot.types.map((type) => ({
        id: type.id,
        name: type.name,
        granularity: 'type',
        kind: type.kind,
        projectName: projectNameByVariant.get(type.projectVariantId),
        inCycle: false,
        isExternal: type.isExternal === true
      }));
  }
}

/** Maps any entity id to its parent id at the requested granularity. */
function parentResolver(
  snapshot: AnalysisSnapshot,
  granularity: Granularity
): (entityId: string) => string | undefined {
  const projectByVariant = new Map(
    snapshot.projects.map((project) => [project.variantId, project.id])
  );
  const variantByType = new Map(snapshot.types.map((type) => [type.id, type.projectVariantId]));
  const namespaceByType = new Map(
    snapshot.types.map((type) => [type.id, type.namespaceId ?? undefined])
  );
  const variantByNamespace = new Map(
    snapshot.namespaces.map((node) => [node.id, node.projectVariantId])
  );

  return (entityId: string) => {
    if (granularity === 'type') {
      return snapshot.types.some((type) => type.id === entityId) ? entityId : undefined;
    }

    if (granularity === 'namespace') {
      const namespaceId = namespaceByType.get(entityId);
      if (namespaceId) {
        return namespaceId;
      }

      return snapshot.namespaces.some((node) => node.id === entityId) ? entityId : undefined;
    }

    const variantId = variantByType.get(entityId) ?? variantByNamespace.get(entityId);
    if (variantId) {
      return projectByVariant.get(variantId);
    }

    return snapshot.projects.some((project) => project.id === entityId) ? entityId : undefined;
  };
}

/**
 * The ids that must always be kept: the scope origin, or a cycle group's members.
 * Returns null when the whole graph is in scope.
 */
function scopePriorityIds(
  snapshot: AnalysisSnapshot,
  scope: Scope,
  granularity: Granularity,
  summaries: ReadonlyMap<string, EntitySummary>
): string[] | null {
  if (scope.kind === 'root' || !scope.id) {
    return null;
  }

  if (scope.kind === 'cycle') {
    const group = snapshot.cycleGroups.find((entry) => entry.id === scope.id);
    return group ? group.memberIds.filter((id) => summaries.has(id)) : [];
  }

  return summaries.has(scope.id) ? [scope.id] : [];
}

function neighbourhood(
  summaries: ReadonlyMap<string, EntitySummary>,
  edges: readonly ProjectionEdge[],
  originIds: readonly string[],
  scope: Scope
): Set<string> {
  const reverse = scope.kind === 'dependents';
  const depth = Math.min(Math.max(scope.depth ?? 1, 1), 3);
  const visited = new Set<string>(originIds);
  let frontier = [...originIds];

  for (let level = 0; level < depth && frontier.length > 0; level++) {
    const next: string[] = [];
    for (const current of frontier) {
      for (const edge of edges) {
        const neighbour =
          edge.sourceId === current
            ? edge.targetId
            : edge.targetId === current
              ? edge.sourceId
              : undefined;
        if (!neighbour) {
          continue;
        }

        // Direction is respected for dependency/dependent scopes.
        if (scope.kind === 'dependencies' && edge.sourceId !== current) {
          continue;
        }

        if (reverse && edge.targetId !== current) {
          continue;
        }

        if (!summaries.has(neighbour) || visited.has(neighbour)) {
          continue;
        }

        visited.add(neighbour);
        next.push(neighbour);
      }
    }

    frontier = next;
  }

  return visited;
}
