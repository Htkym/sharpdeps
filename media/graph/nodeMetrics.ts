// Layout input sizes for the SVG graph (SD-004).
//
// Text is measured in the webview (where the DOM exists) and the resulting sizes
// are handed to the layout worker, so the worker never needs fonts or a DOM.

import type { GraphNodeInput, GraphProjection } from './types';

export interface NodeSize {
  width: number;
  height: number;
}

export const NODE_METRICS = {
  minWidth: 132,
  maxWidth: 320,
  baseHeight: 34,
  sublabelHeight: 14,
  horizontalPadding: 20,
  charWidth: 7.2,
  maxLabelChars: 42
} as const;

/** Deterministic size estimate; used when no DOM measurement is available. */
export function estimateNodeSize(node: GraphNodeInput): NodeSize {
  const labelChars = Math.min(node.label.length, NODE_METRICS.maxLabelChars);
  const width = clamp(
    labelChars * NODE_METRICS.charWidth + NODE_METRICS.horizontalPadding * 2,
    NODE_METRICS.minWidth,
    NODE_METRICS.maxWidth
  );
  const height = NODE_METRICS.baseHeight + (node.sublabel ? NODE_METRICS.sublabelHeight : 0);
  return { width: Math.round(width), height: Math.round(height) };
}

export function truncateLabel(
  label: string,
  maxChars: number = NODE_METRICS.maxLabelChars
): string {
  return label.length <= maxChars ? label : `${label.slice(0, maxChars - 1)}…`;
}

export function projectionNodeSizes(
  projection: GraphProjection,
  measure?: (node: GraphNodeInput) => NodeSize
): Map<string, NodeSize> {
  const sizes = new Map<string, NodeSize>();
  for (const node of projection.nodes) {
    sizes.set(node.id, measure ? measure(node) : estimateNodeSize(node));
  }
  return sizes;
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}
