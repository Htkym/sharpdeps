// ELK layout worker (SD-004).
//
// Runs the layered layout off the main thread so a large graph never blocks the
// UI. The worker owns no state between requests: the client sends the whole
// layout input and receives node positions plus edge routes.

// ELK runs the layout synchronously inside this worker: importing the GWT worker
// defines the "fake worker" class that the API uses when no nested worker is
// created. A nested worker would need a second script URL, which the webview CSP
// deliberately does not allow.
import ELK from 'elkjs/lib/elk-api.js';
import { Worker as ElkFakeWorker } from 'elkjs/lib/elk-worker.min.js';
import type { LayoutEdge, LayoutNode, LayoutResult } from '../graph/types';

interface WorkerNodeInput {
  id: string;
  width: number;
  height: number;
}

interface WorkerEdgeInput {
  id: string;
  sourceId: string;
  targetId: string;
}

interface LayoutRequest {
  type: 'layout';
  requestId: number;
  direction: 'RIGHT' | 'DOWN';
  nodeSpacing: number;
  rankSpacing: number;
  nodes: WorkerNodeInput[];
  edges: WorkerEdgeInput[];
}

interface ElkChild {
  id: string;
  x?: number;
  y?: number;
  width: number;
  height: number;
}

interface ElkSection {
  startPoint: { x: number; y: number };
  endPoint: { x: number; y: number };
  bendPoints?: { x: number; y: number }[];
}

interface ElkEdge {
  id: string;
  sections?: ElkSection[];
}

interface ElkResult {
  children?: ElkChild[];
  edges?: ElkEdge[];
  width?: number;
  height?: number;
}

// The GWT layout engine runs synchronously in this worker through ELK's
// "fake worker": a nested worker would need a second script URL, which the
// webview CSP deliberately does not allow. See esbuild.js for the elk-worker
// load shim that makes the engine importable from inside a worker.
const elk = new ELK({
  // The engine implements postMessage/onmessage/terminate; the DOM Worker type is
  // wider than what the API uses.
  workerFactory: () => new ElkFakeWorker('') as unknown as Worker
});

self.onmessage = (event: MessageEvent<LayoutRequest>): void => {
  const request = event.data;
  if (!request || request.type !== 'layout') {
    return;
  }

  void runLayout(request);
};

async function runLayout(request: LayoutRequest): Promise<void> {
  try {
    const graph = {
      id: 'root',
      layoutOptions: {
        'elk.algorithm': 'layered',
        'elk.direction': request.direction,
        'elk.layered.spacing.nodeNodeBetweenLayers': String(request.rankSpacing),
        'elk.spacing.nodeNode': String(request.nodeSpacing),
        'elk.edgeRouting': 'ORTHOGONAL',
        'elk.layered.mergeEdges': 'false',
        'elk.layered.nodePlacement.strategy': 'NETWORK_SIMPLEX',
        'elk.layered.cycleBreaking.strategy': 'GREEDY'
      },
      children: request.nodes.map((node) => ({
        id: node.id,
        width: node.width,
        height: node.height
      })),
      edges: request.edges.map((edge) => ({
        id: edge.id,
        sources: [edge.sourceId],
        targets: [edge.targetId]
      }))
    };

    const result = (await elk.layout(graph)) as ElkResult;
    const layout: LayoutResult = {
      nodes: (result.children ?? []).map((child): LayoutNode => ({
        id: child.id,
        x: child.x ?? 0,
        y: child.y ?? 0,
        width: child.width,
        height: child.height
      })),
      edges: (result.edges ?? []).map((edge): LayoutEdge => ({
        id: edge.id,
        sections: (edge.sections ?? []).map((section) => ({
          startPoint: { x: section.startPoint.x, y: section.startPoint.y },
          endPoint: { x: section.endPoint.x, y: section.endPoint.y },
          bendPoints: (section.bendPoints ?? []).map((point) => ({ x: point.x, y: point.y }))
        }))
      })),
      width: result.width ?? 0,
      height: result.height ?? 0
    };

    (self as unknown as Worker).postMessage({
      type: 'layoutResult',
      requestId: request.requestId,
      layout
    });
  } catch (error) {
    (self as unknown as Worker).postMessage({
      type: 'layoutError',
      requestId: request.requestId,
      message: error instanceof Error ? error.message : String(error)
    });
  }
}
