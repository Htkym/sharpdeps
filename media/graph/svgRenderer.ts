// Pure SVG renderer for the dependency graph (SD-004).
//
// Renders the projection + layout as SVG elements with keyed updates: the same
// ids reuse the same DOM elements, so selection and camera are not disturbed by a
// redraw. All code-derived text is written with textContent; no HTML, no
// foreignObject, and no inline event attributes are produced (exports stay inert).

import type {
  GraphEdgeInput,
  GraphNodeInput,
  GraphProjection,
  LayoutEdge,
  LayoutNode,
  LayoutPoint,
  LayoutResult
} from './types';
import { projectKindColor } from './projectionAdapter';

const SVG_NS = 'http://www.w3.org/2000/svg';

export interface RenderState {
  selectedNodeIds: ReadonlySet<string>;
  selectedEdgeIds: ReadonlySet<string>;
  /** Hidden node ids stay in the layout but are not drawn. */
  hiddenNodeIds?: ReadonlySet<string>;
}

export interface RenderResult {
  nodeElements: Map<string, SVGGElement>;
  edgeElements: Map<string, SVGGElement>;
}

export interface RenderTargets {
  nodeLayer: SVGGElement;
  edgeLayer: SVGGElement;
}

export function createLayers(container: SVGElement): RenderTargets {
  const edgeLayer = document.createElementNS(SVG_NS, 'g');
  edgeLayer.setAttribute('class', 'edge-layer');
  const nodeLayer = document.createElementNS(SVG_NS, 'g');
  nodeLayer.setAttribute('class', 'node-layer');
  container.append(edgeLayer, nodeLayer);
  return { nodeLayer, edgeLayer };
}

export function renderGraph(
  targets: RenderTargets,
  projection: GraphProjection,
  layout: LayoutResult,
  state: RenderState
): RenderResult {
  const layoutNodes = new Map(layout.nodes.map((node) => [node.id, node]));
  const nodeElements = renderNodes(targets.nodeLayer, projection.nodes, layoutNodes, state);
  const edgeElements = renderEdges(targets.edgeLayer, projection.edges, layout, state);
  return { nodeElements, edgeElements };
}

function renderNodes(
  layer: SVGGElement,
  nodes: readonly GraphNodeInput[],
  layoutNodes: ReadonlyMap<string, LayoutNode>,
  state: RenderState
): Map<string, SVGGElement> {
  const elements = new Map<string, SVGGElement>();
  const seen = new Set<string>();

  for (const node of nodes) {
    const layoutNode = layoutNodes.get(node.id);
    if (!layoutNode) {
      continue;
    }
    seen.add(node.id);

    let group = layer.querySelector<SVGGElement>(`g.node[data-id="${cssEscape(node.id)}"]`);
    if (!group) {
      group = createNodeElement(node);
      layer.append(group);
    }

    group.setAttribute('transform', `translate(${round(layoutNode.x)} ${round(layoutNode.y)})`);
    group.setAttribute('class', nodeClass(node, state));
    group.setAttribute('aria-label', nodeDescription(node));
    group.setAttribute('aria-selected', state.selectedNodeIds.has(node.id) ? 'true' : 'false');
    group.setAttribute('data-cycle', node.inCycle ? 'true' : 'false');
    group.setAttribute('data-inferred', node.isInferred ? 'true' : 'false');
    group.setAttribute('data-generated', node.isGenerated ? 'true' : 'false');
    group.style.visibility =
      state.hiddenNodeIds && state.hiddenNodeIds.has(node.id) ? 'hidden' : 'visible';

    const rect = group.querySelector('rect');
    rect?.style.setProperty('--sd-kind-color', projectKindColor(node.projectKind ?? 'unknown'));
    rect?.setAttribute('width', String(layoutNode.width));
    rect?.setAttribute('height', String(layoutNode.height));

    const label = group.querySelector<SVGTextElement>('text.node-label');
    if (label) {
      label.textContent = node.label;
      label.setAttribute('x', String(layoutNode.width / 2));
      label.setAttribute('y', node.sublabel ? '15' : String(layoutNode.height / 2 + 5));
    }

    const sublabel = group.querySelector<SVGTextElement>('text.node-sublabel');
    if (sublabel) {
      sublabel.textContent = node.sublabel ?? '';
      sublabel.setAttribute('x', String(layoutNode.width / 2));
      sublabel.setAttribute('y', String(layoutNode.height - 7));
      sublabel.style.display = node.sublabel ? '' : 'none';
    }

    const badge = group.querySelector<SVGTextElement>('text.node-badge');
    if (badge) {
      badge.textContent = nodeBadge(node);
      badge.setAttribute('x', String(layoutNode.width - 8));
      badge.setAttribute('y', '14');
      badge.style.display = nodeBadge(node) ? '' : 'none';
    }

    elements.set(node.id, group);
  }

  for (const group of Array.from(layer.querySelectorAll<SVGGElement>('g.node'))) {
    const id = group.getAttribute('data-id');
    if (!id || !seen.has(id)) {
      group.remove();
    }
  }

  return elements;
}

function createNodeElement(node: GraphNodeInput): SVGGElement {
  const group = document.createElementNS(SVG_NS, 'g');
  group.setAttribute('class', 'node');
  group.setAttribute('data-id', node.id);
  group.setAttribute('tabindex', '0');
  group.setAttribute('role', 'button');

  const rect = document.createElementNS(SVG_NS, 'rect');
  rect.setAttribute('rx', '6');
  rect.setAttribute('ry', '6');

  const label = document.createElementNS(SVG_NS, 'text');
  label.setAttribute('class', 'node-label');
  label.setAttribute('text-anchor', 'middle');

  const sublabel = document.createElementNS(SVG_NS, 'text');
  sublabel.setAttribute('class', 'node-sublabel');
  sublabel.setAttribute('text-anchor', 'middle');

  const badge = document.createElementNS(SVG_NS, 'text');
  badge.setAttribute('class', 'node-badge');
  badge.setAttribute('text-anchor', 'end');

  group.append(rect, label, sublabel, badge);
  return group;
}

function nodeClass(node: GraphNodeInput, state: RenderState): string {
  const classes = ['node', `node-${node.kind}`];
  if (node.inCycle) {
    classes.push('in-cycle');
  }
  if (node.isInferred) {
    classes.push('inferred');
  }
  if (node.isGenerated) {
    classes.push('generated');
  }
  if (node.isExternal) {
    classes.push('external');
  }
  if (state.selectedNodeIds.has(node.id)) {
    classes.push('selected');
  }
  return classes.join(' ');
}

function nodeBadge(node: GraphNodeInput): string {
  const badges: string[] = [];
  if (node.isInferred) {
    badges.push('推定');
  }
  if (node.isGenerated) {
    badges.push('生成');
  }
  return badges.join(' ');
}

function nodeDescription(node: GraphNodeInput): string {
  const parts = [node.label];
  if (node.sublabel) {
    parts.push(node.sublabel);
  }
  if (node.inCycle) {
    parts.push('循環に含まれる');
  }
  if (node.isInferred) {
    parts.push('推定の依存');
  }
  return parts.join(', ');
}

function renderEdges(
  layer: SVGGElement,
  edges: readonly GraphEdgeInput[],
  layout: LayoutResult,
  state: RenderState
): Map<string, SVGGElement> {
  const elements = new Map<string, SVGGElement>();
  const seen = new Set<string>();
  const layoutEdges = new Map(layout.edges.map((edge) => [edge.id, edge]));
  const pairCounts = new Map<string, number>();

  for (const edge of edges) {
    const layoutEdge = layoutEdges.get(edge.id);
    if (!layoutEdge || layoutEdge.sections.length === 0) {
      continue;
    }
    seen.add(edge.id);

    const pairKey = [edge.sourceId, edge.targetId].sort().join('\u001f');
    const pairIndex = pairCounts.get(pairKey) ?? 0;
    pairCounts.set(pairKey, pairIndex + 1);
    const offset =
      pairIndex === 0 ? 0 : (pairIndex % 2 === 1 ? 1 : -1) * (6 + 4 * Math.floor(pairIndex / 2));

    const points = edgePoints(layoutEdge, offset);
    if (points.length < 2) {
      continue;
    }

    let group = layer.querySelector<SVGGElement>(`g.edge[data-id="${cssEscape(edge.id)}"]`);
    if (!group) {
      group = createEdgeElement(edge);
      layer.append(group);
    }

    group.setAttribute('class', edgeClass(edge, state));
    group.setAttribute('aria-label', edgeDescription(edge));
    group.setAttribute('aria-selected', state.selectedEdgeIds.has(edge.id) ? 'true' : 'false');
    group.setAttribute('data-basis', edge.basis);
    group.setAttribute('data-cycle', edge.inCycle ? 'true' : 'false');

    const pathData = toPathData(points);
    group.querySelector('path.edge-hit')?.setAttribute('d', pathData);
    group.querySelector('path.edge-line')?.setAttribute('d', pathData);

    const arrow = group.querySelector<SVGPolygonElement>('polygon.edge-arrow');
    if (arrow) {
      arrow.setAttribute(
        'points',
        arrowPoints(points[points.length - 2], points[points.length - 1])
      );
    }

    elements.set(edge.id, group);
  }

  for (const group of Array.from(layer.querySelectorAll<SVGGElement>('g.edge'))) {
    const id = group.getAttribute('data-id');
    if (!id || !seen.has(id)) {
      group.remove();
    }
  }

  return elements;
}

function createEdgeElement(edge: GraphEdgeInput): SVGGElement {
  const group = document.createElementNS(SVG_NS, 'g');
  group.setAttribute('class', 'edge');
  group.setAttribute('data-id', edge.id);
  group.setAttribute('tabindex', '0');
  group.setAttribute('role', 'button');

  // The hit area is a separate, wide, transparent stroke so thin lines stay easy
  // to select without aiming at the line itself.
  const hit = document.createElementNS(SVG_NS, 'path');
  hit.setAttribute('class', 'edge-hit');

  const line = document.createElementNS(SVG_NS, 'path');
  line.setAttribute('class', 'edge-line');

  const arrow = document.createElementNS(SVG_NS, 'polygon');
  arrow.setAttribute('class', 'edge-arrow');

  group.append(hit, line, arrow);
  return group;
}

function edgeClass(edge: GraphEdgeInput, state: RenderState): string {
  const classes = ['edge'];
  // The styling class follows what the edge means, not the exact basis value: inferred
  // bases share the dashed style, the resolved basis stays solid.
  classes.push(edge.basis === 'usingInferred' ? 'edge-inferred' : `edge-${edge.basis}`);
  if (edge.inCycle) {
    classes.push('in-cycle');
  }
  if (edge.generatedEvidenceCount && edge.generatedEvidenceCount > 0) {
    classes.push('generated');
  }
  if (state.selectedEdgeIds.has(edge.id)) {
    classes.push('selected');
  }
  return classes.join(' ');
}

function edgeDescription(edge: GraphEdgeInput): string {
  const parts = [`${edge.kinds.join(', ') || edge.basis}`, `根拠 ${edge.evidenceCount} 件`];
  if (edge.inCycle) {
    parts.push('循環に含まれる');
  }
  return parts.join(', ');
}

function edgePoints(edge: LayoutEdge, offset: number): LayoutPoint[] {
  const points: LayoutPoint[] = [];
  for (const section of edge.sections) {
    points.push(section.startPoint);
    for (const bend of section.bendPoints) {
      points.push(bend);
    }
    points.push(section.endPoint);
  }

  if (offset === 0) {
    return points;
  }

  // Offsetting keeps the two directions of a bidirectional pair apart.
  return points.map((point, index) => {
    const previous = points[Math.max(0, index - 1)];
    const next = points[Math.min(points.length - 1, index + 1)];
    const dx = next.x - previous.x;
    const dy = next.y - previous.y;
    const length = Math.hypot(dx, dy) || 1;
    return {
      x: point.x + (-dy / length) * offset,
      y: point.y + (dx / length) * offset
    };
  });
}

function toPathData(points: readonly LayoutPoint[]): string {
  return points
    .map((point, index) => `${index === 0 ? 'M' : 'L'} ${round(point.x)} ${round(point.y)}`)
    .join(' ');
}

function arrowPoints(from: LayoutPoint, to: LayoutPoint): string {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const length = Math.hypot(dx, dy) || 1;
  const ux = dx / length;
  const uy = dy / length;
  const size = 8;
  const halfWidth = 4;
  const tipX = to.x - ux * 1;
  const tipY = to.y - uy * 1;
  const baseX = tipX - ux * size;
  const baseY = tipY - uy * size;
  const leftX = baseX - uy * halfWidth;
  const leftY = baseY + ux * halfWidth;
  const rightX = baseX + uy * halfWidth;
  const rightY = baseY - ux * halfWidth;
  return `${round(tipX)},${round(tipY)} ${round(leftX)},${round(leftY)} ${round(rightX)},${round(rightY)}`;
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

function cssEscape(value: string): string {
  return value.replace(/["\\]/g, '\\$&');
}
