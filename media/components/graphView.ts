// Production graph component (SD-017).
//
// Owns the SVG, camera, selection, and the ELK worker for one webview. Layout runs only
// when the graph itself changes: a selection change or an inspector toggle re-renders
// the same layout, so ELK is never re-run for a UI state change. A layout failure is
// reported instead of swallowed, and the caller keeps the table as the fallback.

import type { Projection } from '../../src/view/protocolV2';
import { translate, type Language } from '../app/i18n';
import { Camera, CAMERA_LIMITS } from '../graph/camera';
import {
  GRAPH_EXPORT_STYLES,
  readThemeStyles,
  serializeSvg,
  svgToPngDataUrl
} from '../graph/exportImage';
import { LayoutCancelledError, LayoutClient } from '../graph/layoutClient';
import { projectionKey, toGraphProjection } from '../graph/projectionAdapter';
import { createSelectionController, wireGraphInteraction } from '../graph/selection';
import { createLayers, renderGraph, type RenderResult } from '../graph/svgRenderer';
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
  /** Fired after pan/zoom so the caller can persist the camera (SD-021). */
  onCameraChanged?: (camera: { zoom: number; scrollLeft: number; scrollTop: number }) => void;
}

export interface GraphView {
  setLanguage(language: Language): void;
  readonly element: HTMLElement;
  update(projection: Projection, scopeLabel: string): Promise<void>;
  setLayout(options: {
    direction: 'RIGHT' | 'DOWN';
    nodeSpacing: number;
    rankSpacing: number;
  }): void;
  cancelLayout(): void;
  retryLayout(): void;
  setSelection(nodeIds: readonly string[], edgeIds: readonly string[]): void;
  /** Restores a persisted camera instead of fitting the next projection (SD-021). */
  applyCamera(camera: { zoom: number; scrollLeft: number; scrollTop: number }): void;
  cameraState(): { zoom: number; scrollLeft: number; scrollTop: number };
  fit(): void;
  zoomBy(factor: number): void;
  exportSvg(metadata?: string[]): string | undefined;
  exportPng(metadata?: string[]): Promise<string | undefined>;
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
  let rendered: RenderResult | undefined;
  let renderedKey = '';
  let renderedSelection = selection.get();
  const edgeRepresentatives = new Map<string, string>();
  const displaySelection = () => {
    const current = selection.get();
    return {
      nodeIds: current.nodeIds,
      edgeIds: new Set([...current.edgeIds].map((id) => edgeRepresentatives.get(id) ?? id))
    };
  };
  let generation = 0;
  let spacing: { direction: 'RIGHT' | 'DOWN'; nodeSpacing: number; rankSpacing: number } = {
    direction: 'RIGHT',
    nodeSpacing: 40,
    rankSpacing: 80
  };
  let fitOnResize = true;
  let language: Language = 'en';
  let minimumFitZoom: number = CAMERA_LIMITS.min;
  let pendingLayout: Promise<void> = Promise.resolve();
  let pendingCamera: { zoom: number; scrollLeft: number; scrollTop: number } | undefined;

  const report = (current: {
    nodeIds: ReadonlySet<string>;
    edgeIds: ReadonlySet<string>;
  }): void => {
    options.onSelect?.({ nodeIds: [...current.nodeIds], edgeIds: [...current.edgeIds] });
  };

  const renderSelection = (): void => {
    if (!rendered || !layout) return;
    const current = displaySelection();
    const update = (
      elements: Map<string, SVGGElement>,
      previous: ReadonlySet<string>,
      next: ReadonlySet<string>
    ): void => {
      for (const id of new Set([...previous, ...next])) {
        if (previous.has(id) === next.has(id)) continue;
        const element = elements.get(id);
        element?.classList.toggle('selected', next.has(id));
        element?.setAttribute('aria-selected', String(next.has(id)));
      }
    };
    update(rendered.nodeElements, renderedSelection.nodeIds, current.nodeIds);
    update(rendered.edgeElements, renderedSelection.edgeIds, current.edgeIds);
    renderedSelection = current;
  };

  const renderLayout = (): void => {
    if (!projection || !layout) return;
    const key = JSON.stringify([projection, language]);
    if (key === renderedKey) {
      renderSelection();
      return;
    }
    renderedSelection = displaySelection();
    rendered = renderGraph(targets, projection, layout, {
      language,
      selectedNodeIds: renderedSelection.nodeIds,
      selectedEdgeIds: renderedSelection.edgeIds
    });
    renderedKey = key;
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
    const key = projectionKey(projection_) + JSON.stringify(spacing);
    projection = projection_;
    if (key === currentKey) {
      await pendingLayout;
      // Same graph: a redraw keeps the camera and never asks the worker again.
      renderLayout();
      return;
    }

    currentKey = key;
    layoutClient.cancel();
    layout = undefined;
    const request = ++generation;
    let result: LayoutResult;
    try {
      result = await layoutClient.layout(projection_, spacing);
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
    renderedKey = '';
    options.onError?.(undefined);
    renderLayout();
    if (pendingCamera) {
      // A restored camera wins over fitting: hiding and returning to the tab must not
      // reset the zoom the user had set. The content size is still applied first so the
      // SVG has a real size for the restored zoom.
      camera.setContentSize(result.width, result.height);
      camera.applyState(pendingCamera);
      pendingCamera = undefined;
    } else {
      camera.setContentSize(result.width, result.height);
      // Long horizontal graphs may scroll instead of shrinking text while height is free.
      minimumFitZoom = spacing.direction === 'RIGHT' ? 1 : CAMERA_LIMITS.min;
      camera.fit(24, minimumFitZoom);
    }
    options.onCameraChanged?.(camera.state);
  }

  camera.wireWheel();
  camera.wireDrag();

  const reportCamera = (): void => {
    fitOnResize = false;
    options.onCameraChanged?.(camera.state);
  };
  viewport.addEventListener('pointerup', reportCamera);
  viewport.addEventListener('wheel', reportCamera, { passive: true });
  const resizeObserver = new ResizeObserver(() => {
    if (layout && fitOnResize) camera.fit(24, minimumFitZoom);
  });
  resizeObserver.observe(viewport);

  return {
    element,
    setLanguage: (next) => {
      if (language === next) return;
      language = next;
      viewport.setAttribute('aria-label', translate(language, 'Dependency graph'));
      svg.setAttribute('aria-label', translate(language, 'Dependency graph'));
      renderLayout();
    },
    cancelLayout: () => {
      generation++;
      layoutClient.cancel();
      layout = undefined;
      options.onError?.('Layout cancelled. Retry layout or use the table.');
    },
    retryLayout: () => {
      currentKey = '';
      options.onError?.(undefined);
    },
    setLayout: (next) => {
      spacing = next;
      element.dataset.direction = next.direction;
    },
    update: (nextProjection, scopeLabel) => {
      edgeRepresentatives.clear();
      for (const edge of nextProjection.edges)
        for (const id of edge.underlyingRelationIds ?? [edge.id])
          edgeRepresentatives.set(id, edge.id);
      pendingLayout = apply(toGraphProjection(nextProjection, scopeLabel));
      return pendingLayout;
    },
    setSelection: (nodeIds, edgeIds) => {
      selection.set(nodeIds, edgeIds);
    },
    applyCamera: (camera_) => {
      fitOnResize = false;
      if (layout) camera.applyState(camera_);
      else pendingCamera = camera_;
    },
    cameraState: () => camera.state,
    fit: () => {
      fitOnResize = true;
      minimumFitZoom = CAMERA_LIMITS.min;
      camera.fit();
      options.onCameraChanged?.(camera.state);
    },
    zoomBy: (factor) => {
      fitOnResize = false;
      camera.zoomBy(factor);
      options.onCameraChanged?.(camera.state);
    },
    exportSvg: (metadata) => {
      if (!layout) {
        return undefined;
      }

      return serializeSvg(content, {
        width: layout.width,
        metadata,
        height: layout.height,
        styles: `${readThemeStyles(element)} ${GRAPH_EXPORT_STYLES}`,
        caption: projection ? `SharpDeps dependency graph — ${projection.scopeLabel}` : 'SharpDeps'
      });
    },
    exportPng: async (metadata) => {
      if (!layout) {
        return undefined;
      }

      const svgText = serializeSvg(content, {
        metadata,
        width: layout.width,
        height: layout.height,
        styles: `${readThemeStyles(element)} ${GRAPH_EXPORT_STYLES}`,
        caption: projection ? `SharpDeps dependency graph — ${projection.scopeLabel}` : 'SharpDeps'
      });
      const exported = new DOMParser().parseFromString(svgText, 'image/svg+xml').documentElement;
      return svgToPngDataUrl(
        svgText,
        Number(exported.getAttribute('width')),
        Number(exported.getAttribute('height'))
      );
    },
    dispose: () => {
      resizeObserver.disconnect();
      unwire();
      layoutClient.cancel();
      layoutClient.dispose();
      layout = undefined;
      projection = undefined;
    }
  };
}
