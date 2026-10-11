import { constants, watch, type FSWatcher } from 'node:fs';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import { harnessTrustDecision } from '../security/harnessTrust';

export interface SavedInput {
  readonly relativePath: string;
  readonly bytes: number;
  readonly sha256: string;
}

export interface SavedInputSnapshot {
  readonly root: string;
  readonly generation: number;
  readonly fingerprint: string;
  readonly files: readonly SavedInput[];
}

export interface SavedInputWatchOptions {
  root: string;
  isTrusted: () => boolean;
  /** Stage a full reconciliation; check isCurrent immediately before atomic commit.
   * Return true only after committing. Reading changed source must recheck its hash.
   */
  reconcile: (snapshot: SavedInputSnapshot, isCurrent: () => boolean) => boolean | Promise<boolean>;
  onError: (error: Error) => void;
  settleMs?: number;
  reconcileIntervalMs?: number;
  maxFiles?: number;
  maxEntries?: number;
  maxFileBytes?: number;
  maxTotalBytes?: number;
  watchImpl?: typeof watch;
}

const excludedDirectories = new Set([
  '.git',
  '.local',
  '.sharpdeps',
  'bin',
  'obj',
  'node_modules',
  'out'
]);
const isExcludedDirectory = (name: string): boolean =>
  excludedDirectories.has(process.platform === 'win32' ? name.toLowerCase() : name);
const inputExtension =
  /\.(md|markdown|mdx|cs|csproj|fsproj|vbproj|vcxproj|sln|slnx|props|targets|json|config)$/i;

/** Opt-in input convergence for the future harness owner. No analysis, restore,
 * source execution, persistent identity assignment, storage or network listener.
 */
export class SavedInputWatch {
  private watcher?: FSWatcher;
  private timer?: NodeJS.Timeout;
  private interval?: NodeJS.Timeout;
  private pump?: Promise<void>;
  private pumpOwner = 0;
  private starting?: { owner: number; promise: Promise<void> };
  private readonly pendingStarts = new Set<Promise<void>>();
  private owner = 0;
  private root?: string;
  private revision = 0;
  private started = false;
  private disposed = false;
  private rearm = true;
  private published?: SavedInputSnapshot;
  private stale = true;
  private forceReconcile = true;
  private periodicPending = false;
  private readonly options: SavedInputWatchOptions;

  constructor(options: SavedInputWatchOptions) {
    this.options = Object.freeze({ ...options });
    for (const [name, value] of Object.entries({
      settleMs: options.settleMs ?? 100,
      reconcileIntervalMs: options.reconcileIntervalMs ?? 30_000,
      maxFiles: options.maxFiles ?? 5_000,
      maxEntries: options.maxEntries ?? 50_000,
      maxFileBytes: options.maxFileBytes ?? 2_097_152,
      maxTotalBytes: options.maxTotalBytes ?? 33_554_432
    })) {
      if (!Number.isSafeInteger(value) || value <= 0)
        throw new RangeError(`${name} must be a positive safe integer.`);
    }
  }

  get currentSnapshot(): SavedInputSnapshot | undefined {
    return this.published;
  }

  get isStale(): boolean {
    return this.stale;
  }

  async start(): Promise<void> {
    if (this.disposed) throw new Error('The saved-input watcher has been disposed.');
    this.requireTrusted();
    if (this.started) return this.reconcileNow();
    if (this.starting?.owner === this.owner) return this.starting.promise;
    const owner = ++this.owner;
    const starting = { owner, promise: this.startOwner(owner) };
    this.starting = starting;
    this.pendingStarts.add(starting.promise);
    try {
      await starting.promise;
    } finally {
      this.pendingStarts.delete(starting.promise);
      if (this.starting === starting) this.starting = undefined;
    }
  }

  private async startOwner(owner: number): Promise<void> {
    const root = await fs.realpath(this.options.root);
    if (this.disposed || this.owner !== owner) return;
    this.requireTrusted();
    this.root = root;
    this.started = true;
    this.rearm = true;
    // A new owner must reconcile even when it sees the same input fingerprint.
    this.forceReconcile = true;
    this.interval = setInterval(() => {
      if (!this.currentOwner(owner)) return;
      this.rearm = true;
      // A periodic check must not supersede a slow but otherwise stable stage.
      if (this.pump) this.periodicPending = true;
      else this.invalidate();
    }, this.options.reconcileIntervalMs ?? 30_000);
    this.interval.unref?.();
    await this.reconcileNow();
  }

  /** Native notifications are hints. Lost events are recovered by full scans. */
  invalidate(): void {
    if (!this.started || this.disposed) return;
    this.revision++;
    this.stale = true;
    this.periodicPending = false;
    if (!this.allowed()) {
      this.stop();
      return;
    }
    this.schedule(this.options.settleMs ?? 100);
  }

  /** Used after owner restart, overflow, reconnect or an explicit refresh. */
  async reconcileNow(): Promise<void> {
    if (!this.started || this.disposed) return;
    this.invalidate();
    if (this.timer) clearTimeout(this.timer);
    this.timer = undefined;
    if (this.started) await this.run();
  }

  /** The host must call this immediately when its trust context changes. */
  trustChanged(): void {
    if (!this.allowed()) this.stop();
  }

  stop(): void {
    this.started = false;
    this.owner++;
    this.starting = undefined;
    this.revision++;
    this.stale = true;
    this.periodicPending = false;
    if (this.timer) clearTimeout(this.timer);
    if (this.interval) clearInterval(this.interval);
    this.timer = undefined;
    this.interval = undefined;
    this.watcher?.close();
    this.watcher = undefined;
  }

  async dispose(): Promise<void> {
    this.disposed = true;
    this.stop();
    // realpath cannot be aborted. Join all starts, including detached older owners.
    await Promise.allSettled([...this.pendingStarts]);
    await this.pump;
  }

  private currentOwner(owner: number): boolean {
    return this.started && !this.disposed && this.owner === owner;
  }

  private allowed(): boolean {
    return harnessTrustDecision('watch', this.options.isTrusted()).allowed;
  }

  private requireTrusted(): void {
    if (!this.allowed()) throw new Error('Saved-input watching requires workspace trust.');
  }

  private schedule(delay: number): void {
    if (this.timer) clearTimeout(this.timer);
    const owner = this.owner;
    this.timer = setTimeout(() => {
      if (!this.currentOwner(owner)) return;
      this.timer = undefined;
      void this.run();
    }, delay);
    this.timer.unref?.();
  }

  private run(): Promise<void> {
    const owner = this.owner;
    if (this.pump) {
      if (this.pumpOwner === owner) return this.pump;
      // A new owner waits for an obsolete stage to exit; it never joins that stage.
      return this.pump.then(() => {
        if (this.currentOwner(owner)) return this.run();
      });
    }
    this.pumpOwner = owner;
    this.pump = this.reconcile(owner).finally(() => {
      this.pump = undefined;
      if (this.periodicPending && this.currentOwner(owner)) {
        this.periodicPending = false;
        this.invalidate();
      }
    });
    return this.pump;
  }

  private armWatcher(owner: number): void {
    if (!this.rearm || !this.root) return;
    this.watcher?.close();
    this.watcher = undefined;
    try {
      this.watcher = (this.options.watchImpl ?? watch)(
        this.root,
        { recursive: true },
        (event, filename) => {
          if (!this.currentOwner(owner)) return;
          if (filename === null) {
            this.rearm = true;
            this.invalidate();
            return;
          }
          const relative = filename.toString();
          if (relative.split(/[\\/]/).some(isExcludedDirectory)) return;
          // A renamed directory can contain inputs even without a file extension.
          if (event === 'rename' || inputExtension.test(relative)) this.invalidate();
        }
      );
      this.watcher.on('error', () => {
        if (!this.currentOwner(owner)) return;
        this.rearm = true;
        this.invalidate();
      });
      this.rearm = false;
    } catch (error) {
      // An exhausted/unsupported native watcher must not disable full scans.
      this.rearm = true;
      this.options.onError(error instanceof Error ? error : new Error(String(error)));
    }
  }

  private async reconcile(owner: number): Promise<void> {
    try {
      // Yield after repeated concurrent saves rather than spinning indefinitely.
      for (let attempt = 0; attempt < 4 && this.currentOwner(owner); attempt++) {
        this.requireTrusted();
        this.armWatcher(owner);
        const revision = this.revision;
        const isCurrent = () =>
          this.currentOwner(owner) && this.allowed() && this.revision === revision;
        try {
          const first = await this.scan(revision);
          const second = await this.scan(revision);
          if (!isCurrent() || first.fingerprint !== second.fingerprint) continue;
          if (this.forceReconcile || this.published?.fingerprint !== second.fingerprint) {
            const committed = await this.options.reconcile(second, isCurrent);
            if (!isCurrent()) continue;
            if (!committed) throw new Error('The input reconciliation did not commit.');
            this.published = second;
            this.forceReconcile = false;
          }
          this.stale = false;
          if (isCurrent()) return;
        } catch (error) {
          if (this.currentOwner(owner) && this.allowed() && this.revision !== revision) continue;
          throw error;
        }
      }
      if (this.currentOwner(owner)) this.schedule(this.options.settleMs ?? 100);
    } catch (error) {
      if (!this.currentOwner(owner)) return;
      this.stale = true;
      if (!this.allowed()) {
        this.stop();
        return;
      } else {
        this.rearm = true;
        this.schedule(Math.max(this.options.settleMs ?? 100, 1_000));
      }
      this.options.onError(error instanceof Error ? error : new Error(String(error)));
    }
  }

  private async scan(generation: number): Promise<SavedInputSnapshot> {
    const root = this.root!;
    const files: SavedInput[] = [];
    let totalBytes = 0;
    let entryCount = 0;
    const requireCurrent = () => {
      if (this.disposed || !this.started || generation !== this.revision)
        throw new Error('The input reconciliation was superseded.');
      this.requireTrusted();
    };
    const visit = async (directory: string, depth = 0): Promise<void> => {
      requireCurrent();
      if (depth > 64) throw new Error('Saved-input directories exceed the traversal depth budget.');
      // Never follow a directory replaced with a link during enumeration.
      const info = await fs.lstat(directory);
      requireCurrent();
      if (!info.isDirectory() || info.isSymbolicLink())
        throw new Error('An input directory changed during reconciliation.');
      if ((await fs.realpath(directory)) !== directory)
        throw new Error('An input directory changed its canonical path.');
      requireCurrent();
      const entries = await fs.opendir(directory);
      for await (const entry of entries) {
        if (++entryCount > (this.options.maxEntries ?? 50_000))
          throw new Error('Saved-input entries exceed the traversal budget.');
        requireCurrent();
        if (entry.isSymbolicLink()) continue;
        const file = path.join(directory, entry.name);
        if (entry.isDirectory()) {
          if (!isExcludedDirectory(entry.name)) await visit(file, depth + 1);
        } else if (entry.isFile() && inputExtension.test(entry.name)) {
          if (files.length >= (this.options.maxFiles ?? 5_000))
            throw new Error('Saved-input file count exceeds the reconciliation budget.');
          const before = await fs.lstat(file);
          requireCurrent();
          if (!before.isFile() || before.isSymbolicLink())
            throw new Error('An input file changed during reconciliation.');
          if ((await fs.realpath(file)) !== file)
            throw new Error('An input file changed its canonical path.');
          requireCurrent();
          if (before.size > (this.options.maxFileBytes ?? 2_097_152))
            throw new Error('Saved-input file exceeds the reconciliation byte budget.');
          // The extra byte detects growth without allocating an unbounded buffer.
          const handle = await fs.open(file, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
          let bytes: Buffer;
          try {
            requireCurrent();
            const opened = await handle.stat();
            requireCurrent();
            if (!opened.isFile() || opened.dev !== before.dev || opened.ino !== before.ino)
              throw new Error('An input file changed before it could be read.');
            const buffer = Buffer.alloc(before.size + 1);
            let count = 0;
            while (count < buffer.length) {
              requireCurrent();
              const read = await handle.read(buffer, count, buffer.length - count, null);
              requireCurrent();
              if (read.bytesRead === 0) break;
              count += read.bytesRead;
            }
            if (count !== before.size) throw new Error('An input file changed while reading.');
            bytes = buffer.subarray(0, count);
          } finally {
            await handle.close();
          }
          totalBytes += bytes.length;
          if (totalBytes > (this.options.maxTotalBytes ?? 33_554_432))
            throw new Error('Saved inputs exceed the total reconciliation byte budget.');
          files.push(
            Object.freeze({
              relativePath: path.relative(root, file).split(path.sep).join('/'),
              bytes: bytes.length,
              sha256: createHash('sha256').update(bytes).digest('hex')
            })
          );
        }
      }
    };
    await visit(root);
    files.sort((a, b) =>
      a.relativePath < b.relativePath ? -1 : a.relativePath > b.relativePath ? 1 : 0
    );
    return Object.freeze({
      root,
      generation,
      fingerprint: createHash('sha256').update(JSON.stringify(files)).digest('hex'),
      files: Object.freeze(files)
    });
  }
}
