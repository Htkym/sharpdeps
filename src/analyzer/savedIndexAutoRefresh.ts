import { execFile } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import * as path from 'node:path';
import type { SavedQueryProvider } from './queryProvider';
import type { SavedUpdateResult } from './savedUpdateClient';

export interface SavedOwnerStatus {
  ownerInstance: string;
  coordinatorState: string;
  revision: number;
  desiredRevision: number;
  result?: SavedUpdateResult;
}

/** Read-only CLI command: never starts a writer, evaluates a project or follows owner argv. */
export async function readSavedOwnerStatus(options: {
  dotnetPath: string;
  cliPath: string;
  root: string;
  signal: AbortSignal;
}): Promise<SavedOwnerStatus> {
  if (
    ![options.dotnetPath, options.cliPath, options.root].every(
      (value) => path.isAbsolute(value) && !value.includes('\0')
    ) ||
    path.extname(options.cliPath).toLowerCase() !== '.dll'
  )
    throw new Error('SAVED_WATCH_CLI_REQUIRED');
  const requestId = `watch_${randomBytes(8).toString('hex')}`;
  const text = await new Promise<string>((resolve, reject) => {
    execFile(
      options.dotnetPath,
      [
        options.cliPath,
        'coordinator-status',
        '--root',
        options.root,
        '--quiet',
        '--request-id',
        requestId
      ],
      {
        cwd: options.root,
        windowsHide: true,
        timeout: 5000,
        maxBuffer: 65536,
        encoding: 'utf8',
        signal: options.signal
      },
      (error, stdout) => (error ? reject(new Error('SAVED_WATCH_UNAVAILABLE')) : resolve(stdout))
    );
  });
  const replies: Record<string, unknown>[] = [];
  for (const line of text.split('\n')) {
    try {
      const reply = JSON.parse(line) as Record<string, unknown>;
      if (
        reply?.apiVersion === '1' &&
        reply.command === 'coordinator-status' &&
        reply.requestId === requestId
      )
        replies.push(reply);
    } catch {
      // Noise is never used as a different request's status.
    }
  }
  const reply = replies[0];
  const value = reply?.result as SavedOwnerStatus | undefined;
  if (
    replies.length !== 1 ||
    !Array.isArray(reply.errors) ||
    reply.errors.length ||
    !value ||
    typeof value.ownerInstance !== 'string' ||
    !value.ownerInstance ||
    !['idle', 'dirty', 'updating', 'degraded', 'stopping'].includes(value.coordinatorState) ||
    !Number.isSafeInteger(value.revision) ||
    value.revision < 0 ||
    !Number.isSafeInteger(value.desiredRevision) ||
    value.desiredRevision < value.revision ||
    (value.result !== undefined && value.result !== null && !validTuple(value.result))
  )
    throw new Error('SAVED_WATCH_UNAVAILABLE');
  return value;
}

function validTuple(value: SavedUpdateResult): boolean {
  return (
    typeof value.workspaceId === 'string' &&
    !!value.workspaceId &&
    typeof value.snapshotId === 'string' &&
    !!value.snapshotId &&
    Number.isSafeInteger(value.generation) &&
    value.generation > 0
  );
}

function sameTuple(left: SavedUpdateResult, right: SavedUpdateResult): boolean {
  return (
    left.workspaceId === right.workspaceId &&
    left.snapshotId === right.snapshotId &&
    left.generation === right.generation
  );
}

/** Serial, bounded read-only samples; owner state is not a freshness proof. */
export class SavedIndexAutoRefresh {
  private readonly stopped = new AbortController();
  private timer?: ReturnType<typeof setTimeout>;

  constructor(
    private readonly options: {
      provider: () => SavedQueryProvider | undefined;
      captureCurrent: () => () => boolean;
      readStatus: (signal: AbortSignal) => Promise<SavedOwnerStatus>;
      load: (signal: AbortSignal) => Promise<SavedQueryProvider>;
      adopt: (next: SavedQueryProvider) => void;
      unavailable: (previous: SavedQueryProvider) => void;
    }
  ) {
    void this.sample();
  }

  private async sample(): Promise<void> {
    const previous = this.options.provider();
    const unchanged = this.options.captureCurrent();
    const current = () =>
      !this.stopped.signal.aborted && this.options.provider() === previous && unchanged();
    let next: SavedQueryProvider | undefined;
    try {
      if (!previous || !current()) return;
      const before = await this.options.readStatus(this.stopped.signal);
      if (!current()) return;
      if (before.coordinatorState !== 'idle') {
        this.options.unavailable(previous);
        return;
      }
      if (!before.result) return;
      const old = previous.analysisState('saved').queryMetadata!;
      if (before.result.workspaceId !== old.workspaceId)
        throw new Error('SAVED_WATCH_WORKSPACE_CHANGED');
      if (before.result.generation <= old.generation) {
        if (
          before.result.generation < old.generation ||
          before.result.snapshotId !== old.snapshotId
        )
          throw new Error('SAVED_WATCH_GENERATION_CHANGED');
        return;
      }
      next = await this.options.load(this.stopped.signal);
      if (!current()) return;
      const after = await this.options.readStatus(this.stopped.signal);
      if (!current()) return;
      const metadata = next.analysisState('saved').queryMetadata!;
      if (
        after.coordinatorState !== 'idle' ||
        !after.result ||
        before.ownerInstance !== after.ownerInstance ||
        before.revision !== after.revision ||
        before.desiredRevision !== after.desiredRevision ||
        !sameTuple(before.result, after.result) ||
        !sameTuple(before.result, metadata)
      ) {
        this.options.unavailable(previous);
        return;
      }
      this.options.adopt(next);
      next = undefined;
    } catch {
      if (previous && current()) this.options.unavailable(previous);
    } finally {
      next?.dispose();
      if (!this.stopped.signal.aborted) this.timer = setTimeout(() => void this.sample(), 2000);
    }
  }

  dispose(): void {
    this.stopped.abort();
    clearTimeout(this.timer);
  }
}
