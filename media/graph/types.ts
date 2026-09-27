// Rendering model for the interactive SVG graph (SD-004).
//
// The projection is derived from the v2 analysis snapshot (or, for the prototype,
// from a fixture). Node and edge ids are the analysis ids, so a redraw keeps the
// same DOM keys and selection. Coordinates are never part of this model.

export type NodeKind = 'project' | 'namespace' | 'type' | 'member';

export interface GraphNodeInput {
  id: string;
  label: string;
  sublabel?: string;
  kind: NodeKind;
  projectKind?: string;
  inCycle: boolean;
  isExternal?: boolean;
  isGenerated?: boolean;
  isInferred?: boolean;
}

export interface GraphEdgeInput {
  id: string;
  sourceId: string;
  targetId: string;
  /** Relation kinds, sorted; used for the edge description and styling. */
  kinds: string[];
  basis: string;
  evidenceCount: number;
  inCycle: boolean;
  generatedEvidenceCount?: number;
  publicSurfaceEvidenceCount?: number;
}

export interface GraphProjection {
  scopeLabel: string;
  granularity: 'project' | 'namespace' | 'type';
  nodes: GraphNodeInput[];
  edges: GraphEdgeInput[];
  /** Totals before the display budget was applied. */
  totalNodeCount: number;
  totalEdgeCount: number;
  truncated: boolean;
}

export interface LayoutNode {
  id: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface LayoutPoint {
  x: number;
  y: number;
}

export interface LayoutEdgeSection {
  startPoint: LayoutPoint;
  endPoint: LayoutPoint;
  bendPoints: LayoutPoint[];
}

export interface LayoutEdge {
  id: string;
  sections: LayoutEdgeSection[];
}

export interface LayoutResult {
  nodes: LayoutNode[];
  edges: LayoutEdge[];
  width: number;
  height: number;
}
