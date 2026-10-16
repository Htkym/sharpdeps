import { afterEach, describe, expect, it, vi } from 'vitest';
import { SavedIndexAutoRefresh, type SavedOwnerStatus } from './savedIndexAutoRefresh';
import { SavedQueryProvider } from './queryProvider';
import type { QueryWireReply } from './queryClient';

const ready = (generation: number): QueryWireReply => ({
  apiVersion: '1',
  requestId: 'ready',
  snapshot: {
    workspaceId: 'workspace',
    id: `snapshot-${generation}`,
    generation,
    variantIds: ['variant'],
    variantCount: 1,
    coverage: 'Partial',
    freshness: 'unverified'
  },
  items: [],
  candidates: [],
  unresolved: [],
  truncated: false,
  truncationReasons: [],
  diagnostics: [],
  errors: []
});
async function provider(generation: number) {
  const dispose = vi.fn();
  const value = await SavedQueryProvider.create(
    {
      ready: Promise.resolve(ready(generation)),
      request: async () => {
        throw new Error('No graph request in owner selection test');
      },
      dispose
    },
    'D:/root'
  );
  return { value, dispose };
}
const status = (generation: number): SavedOwnerStatus => ({
  ownerInstance: 'owner',
  coordinatorState: 'idle',
  revision: generation,
  desiredRevision: generation,
  result: { workspaceId: 'workspace', snapshotId: `snapshot-${generation}`, generation }
});
let monitor: SavedIndexAutoRefresh | undefined;
afterEach(() => {
  monitor?.dispose();
  vi.useRealTimers();
});

describe('saved committed generation following', () => {
  it('adopts one new reader without promoting Partial/unverified and rejects owner/sample races', async () => {
    const old = await provider(1),
      next = await provider(2),
      crossed = await provider(3);
    let selected = old.value;
    const adopt = vi.fn((value: SavedQueryProvider) => {
      selected.dispose();
      selected = value;
    });
    const unavailable = vi.fn();
    const readStatus = vi
      .fn()
      .mockResolvedValueOnce(status(2))
      .mockResolvedValueOnce(status(2))
      .mockResolvedValueOnce(status(3))
      .mockResolvedValueOnce({ ...status(3), ownerInstance: 'replacement' });
    const load = vi.fn().mockResolvedValueOnce(next.value).mockResolvedValueOnce(crossed.value);
    vi.useFakeTimers();
    monitor = new SavedIndexAutoRefresh({
      provider: () => selected,
      captureCurrent: () => () => true,
      readStatus,
      load,
      adopt,
      unavailable
    });
    await vi.advanceTimersByTimeAsync(0);
    expect(selected).toBe(next.value);
    expect(old.dispose).toHaveBeenCalledOnce();
    expect(selected.analysisState('saved').queryMetadata).toMatchObject({
      generation: 2,
      coverage: 'Partial',
      freshness: 'unverified'
    });
    await vi.advanceTimersByTimeAsync(2000);
    expect(adopt).toHaveBeenCalledOnce();
    expect(selected).toBe(next.value);
    expect(crossed.dispose).toHaveBeenCalledOnce();
    expect(unavailable).toHaveBeenCalledWith(next.value);
    selected.dispose();
  });

  it('keeps the pinned reader stale on stopped or failed owner and never loads an uncommitted result', async () => {
    const old = await provider(1),
      load = vi.fn(),
      adopt = vi.fn();
    const readStatus = vi
      .fn()
      .mockResolvedValueOnce({ ...status(2), coordinatorState: 'updating' })
      .mockRejectedValueOnce(new Error('watch failed'))
      .mockRejectedValueOnce(new Error('owner absent'));
    const unavailable = vi.fn((value: SavedQueryProvider) => value.markStale('unknown'));
    vi.useFakeTimers();
    monitor = new SavedIndexAutoRefresh({
      provider: () => old.value,
      captureCurrent: () => () => true,
      readStatus,
      load,
      adopt,
      unavailable
    });
    await vi.advanceTimersByTimeAsync(4000);
    expect(unavailable).toHaveBeenCalledTimes(3);
    expect(old.value.staleReason).toBe('unknown');
    expect(old.value.analysisState('saved').queryMetadata!.generation).toBe(1);
    expect(old.dispose).not.toHaveBeenCalled();
    expect(load).not.toHaveBeenCalled();
    expect(adopt).not.toHaveBeenCalled();
    old.value.dispose();
  });

  it('discards a delayed candidate after input/context change and drains on disposal', async () => {
    const old = await provider(1),
      next = await provider(2);
    let finish!: (value: SavedQueryProvider) => void;
    let context = 1;
    const adopt = vi.fn(),
      unavailable = vi.fn(),
      readStatus = vi.fn().mockResolvedValue(status(2));
    const load = vi.fn(
      () =>
        new Promise<SavedQueryProvider>((resolve) => {
          finish = resolve;
        })
    );
    vi.useFakeTimers();
    monitor = new SavedIndexAutoRefresh({
      provider: () => old.value,
      captureCurrent: () => {
        const before = context;
        return () => before === context;
      },
      readStatus,
      load,
      adopt,
      unavailable
    });
    await vi.advanceTimersByTimeAsync(0);
    context++;
    finish(next.value);
    await vi.advanceTimersByTimeAsync(0);
    expect(next.dispose).toHaveBeenCalledOnce();
    expect(adopt).not.toHaveBeenCalled();
    expect(old.dispose).not.toHaveBeenCalled();
    expect(readStatus).toHaveBeenCalledOnce();
    monitor.dispose();
    await vi.advanceTimersByTimeAsync(20000);
    expect(load).toHaveBeenCalledOnce();
    old.value.dispose();
  });
});
