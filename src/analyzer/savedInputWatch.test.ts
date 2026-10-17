import { EventEmitter } from 'node:events';
import { type FSWatcher, type watch } from 'node:fs';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SavedInputWatch, type SavedInputSnapshot } from './savedInputWatch';

// Delegate all filesystem operations to Node; make its open boundary observable.
vi.mock('node:fs/promises', async (importOriginal) => ({
  ...(await importOriginal<typeof import('node:fs/promises')>())
}));

class WatchHandle extends EventEmitter {
  closed = false;
  changed?: (event: 'rename' | 'change', filename: string | null) => void;
  close(): void {
    this.closed = true;
    this.removeAllListeners();
  }
}

const roots: string[] = [];
const services: SavedInputWatch[] = [];
afterEach(async () => {
  for (const service of services.splice(0)) await service.dispose();
  vi.useRealTimers();
  vi.restoreAllMocks();
  for (const root of roots.splice(0)) await fs.rm(root, { recursive: true, force: true });
});

async function createRoot() {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'sharpdeps-saved-watch-'));
  roots.push(root);
  return root;
}

async function fixture() {
  const root = await createRoot();
  const snapshots: SavedInputSnapshot[] = [];
  const errors: Error[] = [];
  const handles: WatchHandle[] = [];
  let failNextWatch = false;
  const watchImpl = ((_root, _options, listener) => {
    if (failNextWatch) {
      failNextWatch = false;
      throw new Error('simulated native watcher allocation failure');
    }
    const handle = new WatchHandle();
    // Production uses the default string encoding for this watch overload.
    handle.changed = listener as WatchHandle['changed'];
    handles.push(handle);
    return handle as unknown as FSWatcher;
  }) as typeof watch;
  let trusted = true;
  let stage: (() => Promise<void>) | undefined;
  let committed: (() => void) | undefined;
  const service = new SavedInputWatch({
    root,
    isTrusted: () => trusted,
    watchImpl,
    settleMs: 60_000,
    reconcileIntervalMs: 600_000,
    maxFileBytes: 64,
    reconcile: async (snapshot, isCurrent) => {
      await stage?.();
      if (!isCurrent()) return false;
      snapshots.push(snapshot);
      committed?.();
      return true;
    },
    onError: (error) => errors.push(error)
  });
  services.push(service);
  return {
    root,
    service,
    snapshots,
    errors,
    handles,
    failWatchOnce: () => {
      failNextWatch = true;
    },
    setTrusted: (value: boolean) => {
      trusted = value;
    },
    setStage: (value?: () => Promise<void>) => {
      stage = value;
    },
    setCommitted: (value: () => void) => {
      committed = value;
    }
  };
}

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

describe('saved-input convergence (real files, controllable lost/overflow events)', () => {
  it('starts a new owner while an obsolete realpath is pending and preserves the new starting promise', async () => {
    const f = await fixture();
    await fs.writeFile(path.join(f.root, 'page.md'), '# Restarted');
    const realpath = fs.realpath;
    const firstRelease = deferred();
    const secondRelease = deferred();
    const lookup = vi
      .spyOn(fs, 'realpath')
      .mockImplementationOnce(async (...args) => {
        await firstRelease.promise;
        return realpath(...args);
      })
      .mockImplementationOnce(async (...args) => {
        await secondRelease.promise;
        return realpath(...args);
      });
    try {
      const first = f.service.start();
      f.service.stop();
      const second = f.service.start();
      expect(lookup).toHaveBeenCalledTimes(2);
      firstRelease.resolve();
      await first;
      expect(f.handles).toHaveLength(0);
      const joining = f.service.start();
      // Completion of the obsolete start must not clear the current start.
      expect(lookup).toHaveBeenCalledTimes(2);
      secondRelease.resolve();
      await Promise.all([second, joining]);
      expect(f.handles).toHaveLength(1);
      expect(f.snapshots).toHaveLength(1);
      expect(f.service.isStale).toBe(false);
      expect(f.service.currentSnapshot!.files[0].sha256).toBe(
        createHash('sha256').update('# Restarted').digest('hex')
      );
      expect(f.errors).toEqual([]);
    } finally {
      firstRelease.resolve();
      secondRelease.resolve();
    }
  });

  it('joins every pending startup during disposal without allowing obsolete owners to attach', async () => {
    const f = await fixture();
    const realpath = fs.realpath;
    const firstRelease = deferred();
    const secondRelease = deferred();
    const lookup = vi
      .spyOn(fs, 'realpath')
      .mockImplementationOnce(async (...args) => {
        await firstRelease.promise;
        return realpath(...args);
      })
      .mockImplementationOnce(async (...args) => {
        await secondRelease.promise;
        return realpath(...args);
      });
    try {
      const first = f.service.start();
      f.service.stop();
      const second = f.service.start();
      expect(lookup).toHaveBeenCalledTimes(2);
      let disposed = false;
      const disposal = f.service.dispose().then(() => {
        disposed = true;
      });
      secondRelease.resolve();
      await second;
      expect(disposed).toBe(false);
      firstRelease.resolve();
      await Promise.all([first, disposal]);
      expect(disposed).toBe(true);
      expect(f.handles).toHaveLength(0);
      expect(f.snapshots).toHaveLength(0);
      expect(f.service.isStale).toBe(true);
      await expect(f.service.start()).rejects.toThrow('disposed');
    } finally {
      firstRelease.resolve();
      secondRelease.resolve();
    }
  });

  it('restarts after an interrupted stage and ignores callbacks from the stopped watcher', async () => {
    const f = await fixture();
    const file = path.join(f.root, 'page.md');
    await fs.writeFile(file, '# Initial');
    await f.service.start();
    const obsoleteHandle = f.handles[0];
    const entered = deferred();
    const release = deferred();
    f.setStage(async () => {
      entered.resolve();
      await release.promise;
    });
    try {
      await fs.writeFile(file, '# Obsolete');
      const pending = f.service.reconcileNow();
      await entered.promise;
      f.service.stop();
      const resumed = f.service.start();
      expect(obsoleteHandle.closed).toBe(true);
      expect(f.service.isStale).toBe(true);
      f.setStage(undefined);
      await fs.writeFile(file, '# Resumed');
      release.resolve();
      await Promise.all([pending, resumed]);
      expect(f.snapshots).toHaveLength(2);
      expect(f.handles).toHaveLength(2);
      expect(f.service.currentSnapshot!.files[0].sha256).toBe(
        createHash('sha256').update('# Resumed').digest('hex')
      );
      obsoleteHandle.changed!('change', 'page.md');
      expect(f.service.isStale).toBe(false);
      expect(f.errors).toEqual([]);
    } finally {
      release.resolve();
    }
  });

  it('waits for an interrupted stage when the restarted root lookup fails during disposal', async () => {
    const f = await fixture();
    const file = path.join(f.root, 'page.md');
    await fs.writeFile(file, '# Initial');
    await f.service.start();
    const entered = deferred();
    const releaseStage = deferred();
    const releaseLookup = deferred();
    const realpath = fs.realpath;
    const moved = `${f.root}.moved`;
    let rootMoved = false;
    f.setStage(async () => {
      entered.resolve();
      await releaseStage.promise;
    });
    try {
      await fs.writeFile(file, '# Interrupted');
      const pending = f.service.reconcileNow();
      await entered.promise;
      f.service.stop();
      vi.spyOn(fs, 'realpath').mockImplementationOnce(async (...args) => {
        await releaseLookup.promise;
        return realpath(...args);
      });
      const restarted = f.service.start();
      const rejected = expect(restarted).rejects.toThrow('ENOENT');
      await fs.rename(f.root, moved);
      rootMoved = true;
      let disposed = false;
      const disposal = f.service.dispose().then(() => {
        disposed = true;
      });
      releaseLookup.resolve();
      await rejected;
      expect(disposed).toBe(false);
      releaseStage.resolve();
      await Promise.all([pending, disposal]);
      expect(disposed).toBe(true);
      expect(f.snapshots).toHaveLength(1);
      expect(f.handles.every((handle) => handle.closed)).toBe(true);
      expect(f.service.isStale).toBe(true);
    } finally {
      releaseLookup.resolve();
      releaseStage.resolve();
      if (rootMoved) await fs.rename(moved, f.root);
    }
  });

  it('reconciles a real native save notification before any periodic resync', async () => {
    const root = await createRoot();
    const file = path.join(root, 'page.md');
    const errors: Error[] = [];
    const snapshots: SavedInputSnapshot[] = [];
    const service = new SavedInputWatch({
      root,
      isTrusted: () => true,
      settleMs: 10,
      reconcileIntervalMs: 600_000,
      reconcile: (snapshot, isCurrent) => {
        if (!isCurrent()) return false;
        snapshots.push(snapshot);
        return true;
      },
      onError: (error) => errors.push(error)
    });
    services.push(service);
    await fs.writeFile(file, '# Before');
    await service.start();
    await fs.writeFile(file, '# Native');
    await vi.waitFor(
      () => {
        expect(service.currentSnapshot!.files[0].sha256).toBe(
          createHash('sha256').update('# Native').digest('hex')
        );
        expect(service.isStale).toBe(false);
      },
      { timeout: 2_000 }
    );
    expect(snapshots.length).toBeGreaterThan(1);
    expect(errors).toEqual([]);
    service.stop();
    expect(service.currentSnapshot).toBe(snapshots[snapshots.length - 1]);
    expect(service.isStale).toBe(true);
  });

  it('recovers same-size same-mtime edits and coalesces rename/delete into the final full manifest', async () => {
    const f = await fixture();
    const markdown = path.join(f.root, 'a.md');
    await fs.writeFile(markdown, '# One');
    await fs.writeFile(path.join(f.root, 'Directory.Build.props'), '<Project/>');
    await fs.mkdir(path.join(f.root, 'obj'));
    await fs.writeFile(path.join(f.root, 'obj', 'ignored.md'), '# Ignore');
    const storeDirectory = process.platform === 'win32' ? '.SHARPDEPS' : '.sharpdeps';
    await fs.mkdir(path.join(f.root, storeDirectory));
    await fs.writeFile(path.join(f.root, storeDirectory, 'index.sqlite.owner.json'), '{}');
    await f.service.start();
    const original = f.service.currentSnapshot!;
    f.handles[0].changed!('change', 'obj/ignored.md');
    f.handles[0].changed!('change', `${storeDirectory}/index.sqlite.owner.json`);
    expect(f.service.isStale).toBe(false);
    const stat = await fs.stat(markdown);
    // No native event: the next full reconciliation must not trust size/mtime.
    await fs.writeFile(markdown, '# Two');
    await fs.utimes(markdown, stat.atime, stat.mtime);
    await f.service.reconcileNow();
    expect(f.service.currentSnapshot!.fingerprint).not.toBe(original.fingerprint);
    expect(
      f.service.currentSnapshot!.files.find((file) => file.relativePath === 'a.md')!.sha256
    ).toBe(createHash('sha256').update('# Two').digest('hex'));
    const beforeBurst = f.snapshots.length;
    await fs.rename(markdown, path.join(f.root, 'b.md'));
    f.service.invalidate();
    await fs.rename(path.join(f.root, 'b.md'), path.join(f.root, 'c.mdx'));
    f.service.invalidate();
    await fs.unlink(path.join(f.root, 'c.mdx'));
    await fs.writeFile(path.join(f.root, 'final.md'), '# Last');
    await fs.writeFile(path.join(f.root, 'final.markdown'), '# Extension alignment');
    await fs.writeFile(
      path.join(f.root, 'Directory.Build.props'),
      '<Project><!-- saved --></Project>'
    );
    f.service.invalidate();
    await f.service.reconcileNow();
    expect(f.snapshots).toHaveLength(beforeBurst + 1);
    expect(f.service.currentSnapshot!.files.map((file) => file.relativePath)).toEqual([
      'Directory.Build.props',
      'final.markdown',
      'final.md'
    ]);
    expect(f.service.isStale).toBe(false);
    expect(f.service.currentSnapshot!.files[0].sha256).toBe(
      createHash('sha256').update('<Project><!-- saved --></Project>').digest('hex')
    );
    expect(f.errors).toEqual([]);
  });

  it('rejects a staged generation after another save, then publishes the latest full content', async () => {
    const f = await fixture();
    const file = path.join(f.root, 'page.md');
    await fs.writeFile(file, '# Initial');
    await f.service.start();
    const entered = deferred();
    const release = deferred();
    f.setStage(async () => {
      entered.resolve();
      await release.promise;
    });
    await fs.writeFile(file, '# Middle');
    const pending = f.service.reconcileNow();
    await entered.promise;
    await fs.writeFile(file, '# Final');
    f.service.invalidate();
    f.setStage(undefined);
    release.resolve();
    await pending;
    expect(f.snapshots).toHaveLength(2);
    expect(f.service.currentSnapshot!.files[0].sha256).toBe(
      createHash('sha256').update('# Final').digest('hex')
    );
    expect(f.errors).toEqual([]);
  });

  it('keeps the old snapshot stale on failure and recovers after overflow and an owner restart', async () => {
    const f = await fixture();
    const file = path.join(f.root, 'page.md');
    await fs.writeFile(file, '# Valid');
    await f.service.start();
    const old = f.service.currentSnapshot;
    await fs.writeFile(file, 'x'.repeat(65));
    await f.service.reconcileNow();
    expect(f.service.currentSnapshot).toBe(old);
    expect(f.service.isStale).toBe(true);
    expect(f.errors[f.errors.length - 1].message).toContain('byte budget');
    await fs.writeFile(file, '# Recovered');
    f.failWatchOnce();
    f.handles[0].emit('error', new Error('simulated event queue overflow'));
    await f.service.reconcileNow();
    expect(f.handles[0].closed).toBe(true);
    expect(f.service.isStale).toBe(false);
    expect(f.errors[f.errors.length - 1].message).toContain('watcher allocation');
    await f.service.reconcileNow();
    expect(f.handles).toHaveLength(2);
    expect(f.service.isStale).toBe(false);
    const recovered = f.service.currentSnapshot;
    const moved = `${f.root}.moved`;
    await fs.rename(f.root, moved);
    try {
      await f.service.reconcileNow();
      expect(f.service.currentSnapshot).toBe(recovered);
      expect(f.service.isStale).toBe(true);
      expect(f.errors[f.errors.length - 1].message).toContain('ENOENT');
    } finally {
      await fs.rename(moved, f.root);
    }
    await f.service.reconcileNow();
    expect(f.service.isStale).toBe(false);
    const count = f.snapshots.length;
    f.service.stop();
    await f.service.start();
    expect(f.snapshots).toHaveLength(count + 1);
    expect(f.handles.slice(0, -1).every((handle) => handle.closed)).toBe(true);
  });

  it('uses periodic full reconciliation when the native event is lost', async () => {
    vi.useFakeTimers();
    const f = await fixture();
    const file = path.join(f.root, 'page.md');
    await fs.writeFile(file, '# Before');
    await f.service.start();
    const committed = deferred();
    const entered = deferred();
    const release = deferred();
    f.setCommitted(committed.resolve);
    f.setStage(async () => {
      entered.resolve();
      await release.promise;
    });
    await fs.writeFile(file, '# After');
    await vi.advanceTimersByTimeAsync(660_000);
    await entered.promise;
    // Periodic checks must not invalidate a slow stage without an actual save.
    await vi.advanceTimersByTimeAsync(1_200_000);
    release.resolve();
    await committed.promise;
    await vi.waitFor(() =>
      expect(f.service.currentSnapshot!.files[0].sha256).toBe(
        createHash('sha256').update('# After').digest('hex')
      )
    );
  });

  it('denies untrusted startup and invalidates staged publication on trust loss or disposal', async () => {
    const f = await fixture();
    f.setTrusted(false);
    await expect(f.service.start()).rejects.toThrow('workspace trust');
    expect(f.handles).toEqual([]);
    f.setTrusted(true);
    const file = path.join(f.root, 'page.md');
    await fs.writeFile(file, '# Before');
    await f.service.start();
    const old = f.service.currentSnapshot;
    const entered = deferred();
    const release = deferred();
    f.setStage(async () => {
      entered.resolve();
      await release.promise;
    });
    await fs.writeFile(file, '# After');
    const pending = f.service.reconcileNow();
    await entered.promise;
    f.setTrusted(false);
    f.service.trustChanged();
    const disposal = f.service.dispose();
    release.resolve();
    await Promise.all([pending, disposal]);
    expect(f.snapshots).toHaveLength(1);
    expect(f.service.currentSnapshot).toBe(old);
    expect(f.service.isStale).toBe(true);
    expect(f.handles.every((handle) => handle.closed)).toBe(true);
    f.setTrusted(true);
    await expect(f.service.start()).rejects.toThrow('disposed');
  });

  it('does not start a new body read if trust is revoked while open is awaiting OS completion', async () => {
    const f = await fixture();
    const file = path.join(f.root, 'page.md');
    await fs.writeFile(file, '# Before');
    await f.service.start();
    const old = f.service.currentSnapshot;
    const open = fs.open;
    const readCounts: Array<() => number> = [];
    vi.spyOn(fs, 'open').mockImplementation(async (...args) => {
      const handle = await open(...args);
      const read = vi.spyOn(handle, 'read');
      readCounts.push(() => read.mock.calls.length);
      f.setTrusted(false);
      f.service.trustChanged();
      return handle;
    });
    await fs.writeFile(file, '# After');
    await f.service.reconcileNow();
    expect(readCounts.map((count) => count())).toEqual([0]);
    expect(f.service.currentSnapshot).toBe(old);
    expect(f.service.isStale).toBe(true);
    expect(f.errors).toEqual([]);
  });
});
