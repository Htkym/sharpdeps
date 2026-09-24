// Production graph component (SD-017).
//
// Owns the SVG, camera, selection, and the ELK worker for one webview. Layout runs only
// when the graph itself changes: a selection change or an inspector toggle re-renders
// the same layout, so ELK is never re-run for a UI state change. A layout failure is
// reported instead of swallowed, and the caller keeps the table as the fallback.

import type { Projection } from '../../src/view/protocolV2';
import { Camera } from '../graph/camera';
import {
  GRAPH_EXPORT_STYLES,
  readThemeStyles,
  serializeSvg,
  svgToPngDataUrl
} from '../graph/exportImage';
import { LayoutCancelledError, LayoutClient } from '../graph/layoutClient';
import { projectionKey, toGraphProjection } from '../graph/projectionAdapter';
import { createSelectionController, wireGraphInteraction } from '../graph/selection';
import { createLayers, renderGraph } from '../graph/svgRenderer';
import type { GraphProjection, LayoutResult } from '../graph/types';

const SVG_NS = 'http://www.w3.org/2000/svg';

export interface GraphSelectionIds {
  nodeIds: string[];
  edgeIds: string[];
}

export interface GraphViewOptions {
  workerUrl: string;
  onSelect?: (selection: GraphSelectionIds) => void;
  onActivate?: (selection: GraphSelectionIds) => void;
  /** Layout/render failures; the caller keeps the analysis result visible. */
  onError?: (message: string | undefined) => void;
}

export interface GraphView {
  readonly element: HTMLElement;
  update(projection: Projection, scopeLabel: string): void;
  setSelection(nodeIds: readonly string[], edgeIds: readonly string[]): void;
  fit(): void;
  zoomBy(factor: number): void;
  exportSvg(): string | undefined;
  exportPng(): Promise<string | undefined>;
  dispose(): void;
}

export function createGraphView(options: GraphViewOptions): GraphView {
  const element = document.createElement('div');
  element.className = 'sd-graph';

  const viewport = document.createElement('div');
  viewport.className = 'graph-viewport';
  viewport.tabIndex = 0;
  viewport.setAttribute('aria-label', 'Dependency graph');

  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('class', 'graph-svg');
  svg.setAttribute('role', 'group');
  svg.setAttribute('aria-label', 'Dependency graph');

  const content = document.createElementNS(SVG_NS, 'g');
  content.setAttribute('class', 'graph-content');
  svg.append(content);
  viewport.append(svg);
  element.append(viewport);

  const targets = createLayers(content);
  const camera = new Camera(viewport, svg);
  const selection = createSelectionController();
  const layoutClient = new LayoutClient({ workerUrl: options.workerUrl });

  let projection: GraphProjection | undefined;
  let currentKey = '';
  let layout: LayoutResult | undefined;
  let generation = 0;

  const report = (current: {
    nodeIds: ReadonlySet<string>;
    edgeIds: ReadonlySet<string>;
  }): void => {
    options.onSelect?.({ nodeIds: [...current.nodeIds], edgeIds: [...current.edgeIds] });
  };

  const renderSelection = (): void => {
    if (!projection || !layout) {
      return;
    }

    const current = selection.get();
    renderGraph(targets, projection, layout, {
      selectedNodeIds: current.nodeIds,
      selectedEdgeIds: current.edgeIds
    });
  };

  selection.onChange((current) => {
    renderSelection();
    report(current);
  });

  const unwire = wireGraphInteraction(svg, selection, {
    onActivate: (current) =>
      options.onActivate?.({ nodeIds: [...current.nodeIds], edgeIds: [...current.edgeIds] })
  });

  async function apply(projection_: GraphProjection): Promise<void> {
    const key = projectionKey(projection_);
    projection = projection_;
    if (key === currentKey) {
      // Same graph: a redraw keeps the camera and never asks the worker again.
      renderSelection();
      return;
    }

    currentKey = key;
    const request = ++generation;
    let result: LayoutResult;
    try {
      result = await layoutClient.layout(projection_);
    } catch (error) {
      if (error instanceof LayoutCancelledError || request !== generation) {
        return;
      }

      layout = undefined;
      options.onError?.(error instanceof Error ? error.message : 'The graph layout failed.');
      return;
    }

    if (request !== generation) {
      // A newer projection superseded this layout; its result is not shown.
      return;
    }

    layout = result;
    options.onError?.(undefined);
    renderSelection();
    camera.setContentSize(result.width, result.height);
    camera.fit();
  }

  camera.wireWheel();
  camera.wireDrag();

  return {
    element,
    update: (nextProjection, scopeLabel) => {
      void apply(toGraphProjection(nextProjection, scopeLabel));
    },
    setSelection: (nodeIds, edgeIds) => {
      selection.set(nodeIds, edgeIds);
    },
    fit: () => camera.fit(),
    zoomBy: (factor) => camera.zoomBy(factor),
    exportSvg: () => {
      if (!layout) {
        return undefined;
      }

      return serializeSvg(content, {
        width: layout.width,
        height: layout.height,
        styles: `${readThemeStyles(element)} ${GRAPH_EXPORT_STYLES}`,
        caption: projection ? `SharpDeps dependency graph — ${projection.scopeLabel}` : 'SharpDeps'
      });
    },
    exportPng: async () => {
      if (!layout) {
        return undefined;
      }

      const svgText = serializeSvg(content, {
        width: layout.width,
        height: layout.height,
        styles: `${readThemeStyles(element)} ${GRAPH_EXPORT_STYLES}`,
        caption: projection ? `SharpDeps dependency graph — ${projection.scopeLabel}` : 'SharpDeps'
      });
      return svgToPngDataUrl(svgText, layout.width, layout.height);
    },
    dispose: () => {
      unwire();
      layoutClient.cancel();
      layoutClient.dispose();
      layout = undefined;
      projection = undefined;
    }
  };
}
