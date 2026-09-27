// Protocol projection -> renderer input (SD-017).
//
// The protocol carries analysis ids and summary fields; the renderer wants labels and
// flags. This module is the only place that decides how a summary becomes a graph node,
// and it is pure so the mapping is unit-testable without a DOM.

import type { EntitySummary, Projection } from '../../src/view/protocolV2';
import type { GraphEdgeInput, GraphNodeInput, GraphProjection, NodeKind } from './types';

export function toGraphProjection(projection: Projection, scopeLabel: string): GraphProjection {
  return {
    scopeLabel,
    granularity: projection.granularity,
    nodes: projection.nodes.map(toNode),
    edges: projection.edges.map(toEdge),
    totalNodeCount: projection.totalNodeCount,
    totalEdgeCount: projection.totalEdgeCount,
    truncated: projection.truncated
  };
}

function toNode(node: EntitySummary): GraphNodeInput {
  return {
    id: node.id,
    label: node.name,
    sublabel: sublabelOf(node),
    kind: kindOf(node),
    projectKind: node.projectKind ?? (node.granularity === 'project' ? node.kind : undefined),
    inCycle: node.inCycle === true,
    isExternal: node.isExternal === true,
    isGenerated: node.isGenerated === true
  };
}

export function projectKindColor(kind: string): string {
  const colors: Record<string, string> = {
    web: 'var(--vscode-charts-blue, #3794ff)',
    library: 'var(--vscode-charts-purple, #b180d7)',
    test: 'var(--vscode-charts-green, #89d185)',
    desktop: 'var(--vscode-charts-orange, #d18616)',
    app: 'var(--vscode-charts-yellow, #cca700)'
  };
  return colors[kind] ?? 'var(--vscode-panel-border, #6b6b6b)';
}

function kindOf(node: EntitySummary): NodeKind {
  return node.granularity;
}

function sublabelOf(node: EntitySummary): string | undefined {
  const parts: string[] = [];
  if (node.projectName) {
    parts.push(node.projectName);
  } else if (node.kind) {
    parts.push(node.kind);
  }

  if (node.isExternal) {
    parts.push('外部');
  }

  return parts.length > 0 ? parts.join(' ・ ') : undefined;
}

function toEdge(edge: Projection['edges'][number]): GraphEdgeInput {
  return {
    id: edge.id,
    sourceId: edge.sourceId,
    targetId: edge.targetId,
    kinds: edge.kinds,
    basis: edge.basis,
    evidenceCount: edge.evidenceCount,
    inCycle: edge.inCycle,
    generatedEvidenceCount: edge.generatedEvidenceCount,
    publicSurfaceEvidenceCount: edge.publicSurfaceEvidenceCount
  };
}

/**
 * A stable key for "the same graph": layout and camera are only recalculated when this
 * changes. Selection and inspector changes never affect it, so they never re-run ELK.
 */
export function projectionKey(projection: GraphProjection): string {
  return [
    projection.granularity,
    projection.scopeLabel,
    projection.nodes.map((node) => node.id).join(','),
    projection.edges.map((edge) => edge.id).join(',')
  ].join('|');
}
