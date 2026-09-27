// Layout worker client (SD-004).
//
// The webview cannot load a worker script directly from the extension's resource
// URI, so the script is fetched and started from a Blob URL. Every request carries
// a request id; responses for a superseded request are dropped. Cancellation
// terminates the worker (and rejects the pending promise) instead of leaving it
// running, so the caller can always re-create a clean worker.

import type { GraphProjection, LayoutResult } from './types';
import { projectionNodeSizes, type NodeSize } from './nodeMetrics';

export class LayoutCancelledError extends Error {
  constructor() {
    super('Layout was cancelled.');
    this.name = 'LayoutCancelledError';
  }
}

export interface LayoutRequestOptions {
  direction?: 'RIGHT' | 'DOWN';
  nodeSpacing?: number;
  rankSpacing?: number;
  measure?: (node: GraphProjection['nodes'][number]) => NodeSize;
}

export interface LayoutWorkerLike {
  postMessage(message: unknown): void;
  terminate(): void;
  addEventListener(type: 'message' | 'error', listener: (event: never) => void): void;
  removeEventListener(type: 'message' | 'error', listener: (event: never) => void): void;
}

export interface LayoutClientOptions {
  workerUrl: string;
  createWorker?: (scriptUrl: string) => LayoutWorkerLike;
}

interface PendingRequest {
  resolve: (layout: LayoutResult) => void;
  reject: (error: Error) => void;
}

export class LayoutClient {
  private readonly options: LayoutClientOptions;
  private worker: LayoutWorkerLike | undefined;
  private scriptUrl: string | undefined;
  private readonly pending = new Map<number, PendingRequest>();
  private nextRequestId = 1;
  private currentRequestId: number | undefined;
  /** Shared so two concurrent layouts cannot create (and leak) two workers. */
  private workerPromise: Promise<LayoutWorkerLike> | undefined;

  constructor(options: LayoutClientOptions) {
    this.options = options;
  }

  /** True when a worker has been created and not yet terminated. */
  get isRunning(): boolean {
    return this.worker !== undefined;
  }

  async layout(
    projection: GraphProjection,
    options: LayoutRequestOptions = {}
  ): Promise<LayoutResult> {
    const worker = await this.ensureWorker();
    const requestId = this.nextRequestId++;

    // A newer request supersedes the previous one: its response is dropped.
    this.currentRequestId = requestId;

    return new Promise<LayoutResult>((resolve, reject) => {
      this.pending.set(requestId, { resolve, reject });
      const sizes = projectionNodeSizes(projection, options.measure);
      worker.postMessage({
        type: 'layout',
        requestId,
        direction: options.direction ?? 'RIGHT',
        nodeSpacing: options.nodeSpacing ?? 48,
        rankSpacing: options.rankSpacing ?? 96,
        nodes: projection.nodes.map((node) => {
          const size = sizes.get(node.id) ?? { width: 132, height: 34 };
          return { id: node.id, width: size.width, height: size.height };
        }),
        edges: projection.edges.map((edge) => ({
          id: edge.id,
          sourceId: edge.sourceId,
          targetId: edge.targetId
        }))
      });
    });
  }

  /** Terminates the worker and rejects anything still pending. */
  cancel(): void {
    this.rejectPending(new LayoutCancelledError());
    this.terminateWorker();
  }

  dispose(): void {
    this.rejectPending(new LayoutCancelledError());
    this.terminateWorker();
    if (this.scriptUrl) {
      URL.revokeObjectURL(this.scriptUrl);
      this.scriptUrl = undefined;
    }
  }

  private ensureWorker(): Promise<LayoutWorkerLike> {
    if (this.worker) {
      return Promise.resolve(this.worker);
    }

    this.workerPromise ??= this.startWorker();
    return this.workerPromise;
  }

  private async startWorker(): Promise<LayoutWorkerLike> {
    if (!this.scriptUrl) {
      const response = await fetch(this.options.workerUrl);
      if (!response.ok) {
        throw new Error(`Layout worker could not be loaded (${response.status}).`);
      }
      const source = await response.text();
      this.scriptUrl = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
    }

    const worker = this.options.createWorker
      ? this.options.createWorker(this.scriptUrl)
      : (new Worker(this.scriptUrl) as unknown as LayoutWorkerLike);

    worker.addEventListener('message', ((event: MessageEvent) => {
      this.handleMessage(event.data as WorkerResponse);
    }) as never);
    worker.addEventListener('error', ((event: ErrorEvent) => {
      this.rejectPending(new Error(event.message || 'Layout worker failed.'));
      this.terminateWorker();
    }) as never);

    this.worker = worker;
    return worker;
  }

  private handleMessage(message: WorkerResponse): void {
    if (message.requestId !== this.currentRequestId) {
      // A superseded response must not overwrite the current projection.
      return;
    }

    const pending = this.pending.get(message.requestId);
    if (!pending) {
      return;
    }
    this.pending.delete(message.requestId);

    if (message.type === 'layoutResult') {
      pending.resolve(message.layout);
    } else {
      pending.reject(new Error(message.message));
    }
  }

  private rejectPending(error: Error): void {
    for (const [requestId, pending] of this.pending) {
      this.pending.delete(requestId);
      pending.reject(error);
    }
    this.currentRequestId = undefined;
  }

  private terminateWorker(): void {
    this.worker?.terminate();
    this.worker = undefined;
    this.workerPromise = undefined;
  }
}

type WorkerResponse =
  | { type: 'layoutResult'; requestId: number; layout: LayoutResult }
  | { type: 'layoutError'; requestId: number; message: string };
