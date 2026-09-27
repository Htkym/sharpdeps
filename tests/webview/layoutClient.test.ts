// Layout worker lifecycle (SD-026): superseded responses are dropped, cancel and
// dispose release the worker and the Blob URL, and an error rejects what is pending.

import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { LayoutCancelledError, LayoutClient } from '../../media/graph/layoutClient';
import type { LayoutWorkerLike } from '../../media/graph/layoutClient';
import type { GraphProjection } from '../../media/graph/types';

// The client fetches the worker script and starts it from a Blob URL; both are stubbed
// so the lifecycle is testable outside a browser.
const originalCreateObjectUrl = (URL as unknown as { createObjectURL?: unknown }).createObjectURL;
const originalRevokeObjectUrl = (URL as unknown as { revokeObjectURL?: unknown }).revokeObjectURL;

beforeAll(() => {
  vi.stubGlobal('fetch', async () => ({ ok: true, text: async () => '/* worker */' }));
  (URL as unknown as { createObjectURL: () => string }).createObjectURL = () => 'blob:test';
  (URL as unknown as { revokeObjectURL: () => void }).revokeObjectURL = () => undefined;
});

afterAll(() => {
  vi.unstubAllGlobals();
  (URL as unknown as { createObjectURL?: unknown }).createObjectURL = originalCreateObjectUrl;
  (URL as unknown as { revokeObjectURL?: unknown }).revokeObjectURL = originalRevokeObjectUrl;
});

interface SentMessage {
  type: string;
  requestId: number;
  nodes: Array<{ id: string; width: number; height: number }>;
  edges: Array<{ id: string; sourceId: string; targetId: string }>;
}

class FakeWorker implements LayoutWorkerLike {
  public readonly sent: SentMessage[] = [];
  public terminated = false;
  private readonly listeners = new Map<string, Set<(event: never) => void>>();

  postMessage(message: unknown): void {
    this.sent.push(message as SentMessage);
  }

  terminate(): void {
    this.terminated = true;
  }

  addEventListener(type: 'message' | 'error', listener: (event: never) => void): void {
    const set = this.listeners.get(type) ?? new Set();
    set.add(listener);
    this.listeners.set(type, set);
  }

  removeEventListener(type: 'message' | 'error', listener: (event: never) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  emitMessage(data: unknown): void {
    for (const listener of this.listeners.get('message') ?? []) {
      (listener as unknown as (event: { data: unknown }) => void)({ data });
    }
  }

  emitError(message: string): void {
    for (const listener of this.listeners.get('error') ?? []) {
      (listener as unknown as (event: { message: string }) => void)({ message });
    }
  }
}

function projection(): GraphProjection {
  return {
    scopeLabel: 'test',
    granularity: 'type',
    nodes: [
      { id: 'ty_1111111111111111', label: 'A', kind: 'type', inCycle: false },
      { id: 'ty_2222222222222222', label: 'B', kind: 'type', inCycle: false }
    ],
    edges: [
      {
        id: 'rel_1111111111111111',
        sourceId: 'ty_1111111111111111',
        targetId: 'ty_2222222222222222',
        kinds: ['calls'],
        basis: 'symbolResolved',
        evidenceCount: 1,
        inCycle: false
      }
    ],
    totalNodeCount: 2,
    totalEdgeCount: 1,
    truncated: false
  };
}

function layout(requestId: number, nodes: Array<{ id: string }>): unknown {
  return {
    type: 'layoutResult',
    requestId,
    layout: {
      nodes: nodes.map((node, index) => ({
        id: node.id,
        x: index * 10,
        y: 0,
        width: 100,
        height: 40
      })),
      edges: [],
      width: 200,
      height: 40
    }
  };
}

function setup(): { client: LayoutClient; workers: FakeWorker[] } {
  const workers: FakeWorker[] = [];
  const client = new LayoutClient({
    workerUrl: 'vscode-webview://worker.js',
    createWorker: () => {
      const worker = new FakeWorker();
      workers.push(worker);
      return worker;
    }
  });
  return { client, workers };
}

/** The worker is created asynchronously (fetch + Blob URL), so tests wait for it. */
async function waitForWorkers(workers: FakeWorker[], count: number): Promise<void> {
  await vi.waitFor(() => expect(workers.length).toBe(count));
}

describe('LayoutClient lifecycle', () => {
  it('drops the response of a superseded request (generation rollback)', async () => {
    const { client, workers } = setup();
    const first = client.layout(projection());
    const second = client.layout(projection());
    await waitForWorkers(workers, 1);

    const worker = workers[0];
    expect(worker.sent).toHaveLength(2);
    const [firstMessage, secondMessage] = worker.sent;

    // The older response arrives last: it must not win.
    worker.emitMessage(layout(firstMessage.requestId, [{ id: 'ty_1111111111111111' }]));
    worker.emitMessage(layout(secondMessage.requestId, projection().nodes));

    const result = await second;
    expect(result.nodes.map((node) => node.id)).toEqual([
      'ty_1111111111111111',
      'ty_2222222222222222'
    ]);
    // The superseded promise stays pending (its response is dropped), so it must not
    // resolve with a stale layout: a cancel releases it.
    client.cancel();
    await expect(first).rejects.toBeInstanceOf(LayoutCancelledError);
  });

  it('rejects pending work, terminates the worker, and recreates it after a cancel', async () => {
    const { client, workers } = setup();
    const pending = client.layout(projection());
    await waitForWorkers(workers, 1);
    expect(client.isRunning).toBe(true);

    client.cancel();
    await expect(pending).rejects.toBeInstanceOf(LayoutCancelledError);
    expect(workers[0].terminated).toBe(true);
    expect(client.isRunning).toBe(false);

    const again = client.layout(projection());
    await waitForWorkers(workers, 2);
    workers[1].emitMessage(layout(workers[1].sent[0].requestId, projection().nodes));
    await expect(again).resolves.toMatchObject({ width: 200 });
  });

  it('rejects pending work and releases the worker on dispose', async () => {
    const { client, workers } = setup();
    const pending = client.layout(projection());
    await waitForWorkers(workers, 1);
    client.dispose();

    await expect(pending).rejects.toBeInstanceOf(LayoutCancelledError);
    expect(workers[0].terminated).toBe(true);
    expect(client.isRunning).toBe(false);
    // A disposed client refuses to start again only by being re-created by its owner;
    // a second dispose stays harmless.
    expect(() => client.dispose()).not.toThrow();
  });

  it('rejects everything pending when the worker reports an error', async () => {
    const { client, workers } = setup();
    const pending = client.layout(projection());
    await waitForWorkers(workers, 1);
    workers[0].emitError('worker blew up');

    await expect(pending).rejects.toThrow(/worker blew up/);
    expect(client.isRunning).toBe(false);
  });

  it('measures the projection nodes for the worker', async () => {
    const { client, workers } = setup();
    const pending = client.layout(projection());
    await waitForWorkers(workers, 1);
    const message = workers[0].sent[0];

    expect(message.nodes.map((node) => node.id)).toEqual([
      'ty_1111111111111111',
      'ty_2222222222222222'
    ]);
    expect(message.nodes.every((node) => node.width > 0 && node.height > 0)).toBe(true);
    expect(message.edges[0]).toMatchObject({
      id: 'rel_1111111111111111',
      sourceId: 'ty_1111111111111111',
      targetId: 'ty_2222222222222222'
    });

    workers[0].emitMessage(layout(message.requestId, projection().nodes));
    await pending;
  });
});
