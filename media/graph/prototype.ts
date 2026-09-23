// SD-004 prototype: 50-node SVG graph laid out by ELK in a worker.
//
// This entry point exists to prove the renderer, worker, CSP, and export paths
// before the production UI (SD-015/SD-017) is built. It is loaded by
// tests/webview/fixtures/graph-prototype.html.

import { Camera } from './camera';
import { GRAPH_EXPORT_STYLES, readThemeStyles, serializeSvg, svgToPngDataUrl } from './exportImage';
import { LayoutCancelledError, LayoutClient } from './layoutClient';
import { createSelectionController, wireGraphInteraction, type GraphSelection } from './selection';
import { createLayers, renderGraph, type RenderResult } from './svgRenderer';
import type { GraphEdgeInput, GraphNodeInput, GraphProjection, LayoutResult } from './types';

const SVG_NS = 'http://www.w3.org/2000/svg';

interface PrototypeState {
  projection: GraphProjection;
  layout?: LayoutResult;
  renderResult?: RenderResult;
  renders: number;
  layoutRequests: number;
  lastError?: string;
}

const state: PrototypeState = {
  projection: buildFixtureProjection(50),
  renders: 0,
  layoutRequests: 0
};

export interface PrototypeApi {
  ready: Promise<void>;
  getState(): {
    nodeCount: number;
    edgeCount: number;
    renders: number;
    layoutRequests: number;
    selectedNodeIds: string[];
    selectedEdgeIds: string[];
    lastError?: string;
    workerRunning: boolean;
  };
  layoutFrom(projection: GraphProjection): Promise<void>;
  selectNode(id: string): void;
  selectEdge(id: string): void;
  clearSelection(): void;
  cancelLayout(): void;
  exportSvg(): string;
  exportPng(): Promise<string>;
  fit(): void;
  projection: GraphProjection;
}

declare global {
  interface Window {
    sharpdepsPrototype?: PrototypeApi;
  }
}

void start();

async function start(): Promise<void> {
  const host = document.getElementById('app');
  if (!host) {
    return;
  }
  const hostElement = host;

  const workerUrl = host.dataset.workerUri;
  if (!workerUrl) {
    throw new Error('The prototype host must set data-worker-uri.');
  }

  const { svg, viewport, content } = buildShell(host);
  const targets = createLayers(content);
  const camera = new Camera(viewport, svg);
  const selection = createSelectionController();
  const layoutClient = new LayoutClient({ workerUrl });

  const ready = (async () => {
    await layoutAndRender();
    camera.setContentSize(state.layout?.width ?? 0, state.layout?.height ?? 0);
    camera.fit();
  })();

  function renderSelection(selectionState: GraphSelection): void {
    if (!state.layout) {
      return;
    }
    state.renderResult = renderGraph(targets, state.projection, state.layout, {
      selectedNodeIds: selectionState.nodeIds,
      selectedEdgeIds: selectionState.edgeIds
    });
    state.renders++;
    updateStatus();
  }

  async function layoutAndRender(): Promise<void> {
    state.layoutRequests++;
    try {
      state.layout = await layoutClient.layout(state.projection);
      state.lastError = undefined;
    } catch (error) {
      if (error instanceof LayoutCancelledError) {
        state.lastError = 'cancelled';
        updateStatus();
        return;
      }
      state.lastError = error instanceof Error ? error.message : String(error);
      updateStatus();
      return;
    }
    renderSelection(selection.get());
  }

  selection.onChange(renderSelection);
  wireGraphInteraction(svg, selection, {
    onActivate: (selectionState) => {
      const id = [...selectionState.nodeIds, ...selectionState.edgeIds][0];
      host.dispatchEvent(
        new CustomEvent('sharpdeps-prototype-activate', { detail: { id }, bubbles: true })
      );
    }
  });

  wireControls({ host: hostElement, camera, layoutClient, state });

  window.sharpdepsPrototype = {
    ready,
    projection: state.projection,
    getState: () => ({
      nodeCount: state.projection.nodes.length,
      edgeCount: state.projection.edges.length,
      renders: state.renders,
      layoutRequests: state.layoutRequests,
      selectedNodeIds: [...selection.get().nodeIds],
      selectedEdgeIds: [...selection.get().edgeIds],
      lastError: state.lastError,
      workerRunning: layoutClient.isRunning
    }),
    layoutFrom: async (projection) => {
      state.projection = projection;
      await layoutAndRender();
      camera.setContentSize(state.layout?.width ?? 0, state.layout?.height ?? 0);
      camera.fit();
    },
    selectNode: (id) => selection.selectNode(id),
    selectEdge: (id) => selection.selectEdge(id),
    clearSelection: () => selection.clear(),
    cancelLayout: () => layoutClient.cancel(),
    exportSvg: () => buildSvgExport(state.layout, content, host),
    exportPng: async () => {
      const svgText = buildSvgExport(state.layout, content, host);
      return svgToPngDataUrl(svgText, state.layout?.width ?? 0, state.layout?.height ?? 0);
    },
    fit: () => camera.fit()
  };

  updateStatus();

  function updateStatus(): void {
    const status = hostElement.querySelector('.prototype-status');
    if (status) {
      const selected = [...selection.get().nodeIds, ...selection.get().edgeIds];
      status.textContent =
        `${state.projection.nodes.length} ノード / ${state.projection.edges.length} 辺` +
        ` ・ 描画 ${state.renders} 回 ・ レイアウト ${state.layoutRequests} 回` +
        (selected.length > 0 ? ` ・ 選択: ${selected.join(', ')}` : '') +
        (state.lastError ? ` ・ エラー: ${state.lastError}` : '');
    }
  }
}

function buildSvgExport(
  layout: LayoutResult | undefined,
  content: SVGGElement,
  host: HTMLElement
): string {
  if (!layout) {
    throw new Error('The graph has not been laid out yet.');
  }
  return serializeSvg(content, {
    width: layout.width,
    height: layout.height,
    styles: `${readThemeStyles(host)} ${GRAPH_EXPORT_STYLES}`,
    caption: 'SharpDeps dependency graph'
  });
}

function buildShell(host: HTMLElement): {
  svg: SVGSVGElement;
  viewport: HTMLElement;
  content: SVGGElement;
} {
  host.textContent = '';

  const toolbar = document.createElement('div');
  toolbar.className = 'prototype-toolbar';
  toolbar.append(
    button('fit', 'Fit'),
    button('zoom-in', '拡大'),
    button('zoom-out', '縮小'),
    button('cycle-only', '循環のみ'),
    button('export-svg', 'SVG出力'),
    button('export-png', 'PNG出力'),
    button('cancel', 'レイアウト中断')
  );

  const status = document.createElement('div');
  status.className = 'prototype-status';
  status.setAttribute('role', 'status');

  const viewport = document.createElement('div');
  viewport.className = 'graph-viewport';
  viewport.setAttribute('tabindex', '0');
  viewport.setAttribute('aria-label', 'Dependency graph');

  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('class', 'graph-svg');
  svg.setAttribute('role', 'group');
  svg.setAttribute('aria-label', 'Dependency graph');

  const content = document.createElementNS(SVG_NS, 'g');
  content.setAttribute('class', 'graph-content');
  svg.append(content);
  viewport.append(svg);

  host.append(toolbar, status, viewport);
  return { svg, viewport, content };
}

function button(action: string, label: string): HTMLButtonElement {
  const element = document.createElement('button');
  element.type = 'button';
  element.dataset.action = action;
  element.textContent = label;
  return element;
}

function wireControls(context: {
  host: HTMLElement;
  camera: Camera;
  layoutClient: LayoutClient;
  state: PrototypeState;
}): void {
  const { host, camera, layoutClient, state } = context;

  host.addEventListener('click', (event) => {
    const target = event.target;
    if (!(target instanceof HTMLElement) || target.tagName !== 'BUTTON') {
      return;
    }
    const action = target.dataset.action;
    switch (action) {
      case 'fit':
        camera.fit();
        break;
      case 'zoom-in':
        camera.zoomBy(1.2);
        break;
      case 'zoom-out':
        camera.zoomBy(1 / 1.2);
        break;
      case 'cycle-only': {
        const cycleNodes = state.projection.nodes.filter((node) => node.inCycle);
        const cycleIds = new Set(cycleNodes.map((node) => node.id));
        const edges = state.projection.edges.filter(
          (edge) => cycleIds.has(edge.sourceId) && cycleIds.has(edge.targetId)
        );
        void window.sharpdepsPrototype?.layoutFrom({
          ...state.projection,
          scopeLabel: '循環のみ',
          nodes: cycleNodes,
          edges
        });
        break;
      }
      case 'export-svg': {
        const text = window.sharpdepsPrototype?.exportSvg();
        if (text) {
          host.dispatchEvent(
            new CustomEvent('sharpdeps-prototype-export', { detail: { format: 'svg', text } })
          );
        }
        break;
      }
      case 'export-png':
        void window.sharpdepsPrototype?.exportPng().then((dataUrl) => {
          host.dispatchEvent(
            new CustomEvent('sharpdeps-prototype-export', {
              detail: { format: 'png', text: dataUrl }
            })
          );
        });
        break;
      case 'cancel':
        layoutClient.cancel();
        break;
      default:
        break;
    }
  });

  camera.wireWheel();
  camera.wireDrag();
}

/** Deterministic 50-node fixture with Japanese labels, long generic names, a cycle, and a bidirectional pair. */
export function buildFixtureProjection(nodeCount: number): GraphProjection {
  const projects = ['WebFrontend', 'Application', 'Domain', 'Infrastructure', 'Shared', 'Legacy'];
  const nodes: GraphNodeInput[] = [];
  const edges: GraphEdgeInput[] = [];

  for (let index = 0; index < nodeCount; index++) {
    const project = projects[index % projects.length];
    const isCycle = index % 17 === 0;
    const isInferred = index % 11 === 0;
    const isGenerated = index % 13 === 0;
    const longName =
      index % 7 === 0
        ? `${project}.Ordering.Pipeline.Infrastructure.RepositoryFactory`
        : `${project}.Service${index}`;
    nodes.push({
      id: `ty_${index.toString(16).padStart(16, '0')}`,
      label: index % 5 === 0 ? `注文サービス${index}` : longName,
      sublabel: `${project} ・ net10.0`,
      kind: 'type',
      inCycle: isCycle,
      isInferred,
      isGenerated,
      isExternal: index % 19 === 0
    });
  }

  for (let index = 0; index < nodeCount - 1; index++) {
    const source = nodes[index];
    const target = nodes[(index + 3) % nodeCount];
    edges.push({
      id: `rel_${index.toString(16).padStart(16, '0')}`,
      sourceId: source.id,
      targetId: target.id,
      kinds: index % 4 === 0 ? ['constructs'] : ['signature', 'calls'],
      basis: index % 4 === 0 ? 'symbolResolved' : 'projectDeclared',
      evidenceCount: 1 + (index % 9),
      inCycle: source.inCycle && target.inCycle,
      generatedEvidenceCount: index % 13 === 0 ? 1 : 0
    });
  }

  // A deliberate cycle among the cycle nodes so cycle styling is exercised with
  // real edges, not only with node flags.
  const cycleNodeIds = nodes.filter((node) => node.inCycle).map((node) => node.id);
  for (let index = 0; index < cycleNodeIds.length; index++) {
    edges.push({
      id: `rel_cycle_${index}`,
      sourceId: cycleNodeIds[index],
      targetId: cycleNodeIds[(index + 1) % cycleNodeIds.length],
      kinds: ['calls', 'memberAccess'],
      basis: 'symbolResolved',
      evidenceCount: 2 + index,
      inCycle: true
    });
  }

  // A deliberate bidirectional pair: the renderer must keep both directions apart.
  edges.push({
    id: 'rel_bidirectional_forward',
    sourceId: nodes[1].id,
    targetId: nodes[2].id,
    kinds: ['calls'],
    basis: 'symbolResolved',
    evidenceCount: 2,
    inCycle: false
  });
  edges.push({
    id: 'rel_bidirectional_backward',
    sourceId: nodes[2].id,
    targetId: nodes[1].id,
    kinds: ['calls'],
    basis: 'symbolResolved',
    evidenceCount: 1,
    inCycle: false
  });

  return {
    scopeLabel: 'fixture',
    granularity: 'type',
    nodes,
    edges,
    totalNodeCount: nodes.length,
    totalEdgeCount: edges.length,
    truncated: false
  };
}
