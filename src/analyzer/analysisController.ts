// Analysis run controller (SD-014).
//
// One in-flight analysis at a time, identified by a generation id. A new request
// cancels the previous one, and only the current generation may publish a result, so
// a late completion can never overwrite a newer view.
//
// Cancellation is ordered: a cooperative request first (close stdin so the analyzer
// can stop cleanly, and SIGTERM on POSIX), a grace period, then a forced kill of the
// process tree this controller started. Process trees are never found by name: only
// the child we spawned and its descendants are terminated.

import { spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { analysisId as buildAnalysisId, requestId as buildRequestId } from './identity';

export type AnalysisStage = 'discover' | 'load' | 'compile' | 'extract' | 'aggregate' | 'write';

export type AnalysisStatus = 'completed' | 'cancelled' | 'failed' | 'timeout';

/** Why a run was stopped; the outcome reports `timeout` separately from a stop. */
export type AnalysisCancelReason = 'user' | 'superseded' | 'timeout' | 'disposed';

export interface AnalysisRequest {
  targetPath: string;
  mode: 'quick' | 'semantic';
  configuration?: string;
  platform?: string;
  projectVariants?: { projectLogicalId: string; targetFramework: string }[];
  executable?: { dotnetPath: string; analyzerPath: string };
  maxProjects?: number;
  maxEdges?: number;
  timeoutMs?: number;
  /** Pre-generated id so progress and the stored result refer to the same analysis. */
  analysisId?: string;
}

export interface AnalyzerProcessSpec {
  command: string;
  args: string[];
  cwd?: string;
}

/** Builds the process to run for a request. Injectable so tests can use a stub. */
export type AnalyzerProcessFactory = (
  request: AnalysisRequest,
  workDirectory: string
) => AnalyzerProcessSpec;

export interface AnalysisProgressEvent {
  requestId: string;
  analysisId: string;
  stage: AnalysisStage;
  elapsedMs: number;
  loaded?: number;
  analyzed?: number;
  message?: string;
}

export interface AnalysisOutcome {
  requestId: string;
  analysisId: string;
  status: AnalysisStatus;
  durationMs: number;
  workDirectory: string;
  reportPath?: string;
  error?: string;
  detail?: string;
}

export interface AnalysisControllerOptions {
  processFactory: AnalyzerProcessFactory;
  workRoot?: string;
  /** Grace period between the cooperative stop and the forced tree kill. */
  gracePeriodMs?: number;
  defaultTimeoutMs?: number;
  maxLogLines?: number;
  /**
   * How many run directories to keep under the work root. The store reads evidence from
   * them lazily, so the newest ones must outlive the analysis result (SD-028).
   */
  keepRunDirectories?: number;
  onProgress?: (event: AnalysisProgressEvent) => void;
  onLog?: (line: string, source: 'stdout' | 'stderr') => void;
  /** Called only for the current generation, before the work directory is removed. */
  onCompleted?: (outcome: AnalysisOutcome, isCurrent: () => boolean) => Promise<void> | void;
  now?: () => number;
  spawnImpl?: typeof spawn;
  killTreeImpl?: (pid: number) => Promise<void>;
}

interface ActiveRun {
  requestId: string;
  generation: number;
  analysisId: string;
  child: ChildProcess;
  stage: AnalysisStage;
  startedAt: number;
  timeoutTimer?: NodeJS.Timeout;
  cancelReason?: AnalysisCancelReason;
  terminatePromise?: Promise<void>;
}

const DEFAULT_TIMEOUT_MS = 180_000;
const DEFAULT_GRACE_MS = 3_000;
const DEFAULT_MAX_LOG_LINES = 500;

export class AnalysisController {
  private readonly options: AnalysisControllerOptions;
  private readonly logs: string[] = [];
  private sequence = 0;
  private generation = 0;
  /** The newest generation: only it may register a run or publish a result. */
  private currentGeneration = 0;
  private activeRun: ActiveRun | undefined;
  private disposed = false;
  private readonly successfulDirectories: string[] = [];
  private readonly runningDirectories = new Set<string>();

  constructor(options: AnalysisControllerOptions) {
    this.options = options;
  }

  get active(): { requestId: string; analysisId: string; stage: AnalysisStage } | undefined {
    return this.activeRun
      ? {
          requestId: this.activeRun.requestId,
          analysisId: this.activeRun.analysisId,
          stage: this.activeRun.stage
        }
      : undefined;
  }

  get isRunning(): boolean {
    return this.activeRun !== undefined;
  }

  getLogLines(): readonly string[] {
    return this.logs;
  }

  /** Starts an analysis, superseding any run in flight. */
  async start(request: AnalysisRequest): Promise<AnalysisOutcome> {
    if (this.disposed) {
      throw new Error('The analysis controller has been disposed.');
    }

    // The generation is reserved synchronously, before any await, so two rapid
    // requests cannot both believe they are the current run.
    const generation = ++this.generation;
    this.currentGeneration = generation;

    if (this.activeRun) {
      this.cancel('superseded');
      // The superseded run finishes on its own; its result is ignored either way.
      await this.activeRun.terminatePromise?.catch(() => undefined);
    }

    const requestId = buildRequestId(++this.sequence);
    // The host passes this id to the analyzer so progress and the stored result refer
    // to the same analysis even when the analyzer would derive its own id.
    const analysisId =
      request.analysisId ??
      buildAnalysisId({
        targetId: request.targetPath,
        mode: request.mode,
        profileHash: requestId.slice('req_'.length),
        startedAt: new Date(this.now()).toISOString()
      });
    const workRoot = this.options.workRoot ?? os.tmpdir();
    const workDirectory = await fs.promises.mkdtemp(path.join(workRoot, 'sharpdeps-run-'));
    if (this.disposed || this.currentGeneration !== generation) {
      await removeDirectory(workDirectory);
      return { requestId, analysisId, status: 'cancelled', durationMs: 0, workDirectory };
    }
    this.runningDirectories.add(workDirectory);
    const spec = this.options.processFactory({ ...request, analysisId }, workDirectory);
    const startedAt = this.now();

    const child = (this.options.spawnImpl ?? spawn)(spec.command, spec.args, {
      cwd: spec.cwd ?? workDirectory,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      // POSIX: make the child a group leader so its whole tree can be signalled.
      detached: process.platform !== 'win32'
    });

    const run: ActiveRun = {
      requestId,
      analysisId,
      generation,
      child,
      stage: 'discover',
      startedAt
    };

    if (this.currentGeneration === generation) {
      this.activeRun = run;
    } else {
      // A newer request arrived while this one was starting: stop this process.
      run.cancelReason = 'superseded';
      void this.terminate(run);
    }

    const timeoutMs = request.timeoutMs ?? this.options.defaultTimeoutMs ?? DEFAULT_TIMEOUT_MS;
    if (timeoutMs > 0) {
      run.timeoutTimer = setTimeout(() => {
        if (this.activeRun === run) this.cancel('timeout');
      }, timeoutMs);
      run.timeoutTimer.unref?.();
    }

    this.wireStreams(run, workDirectory);

    const exit = await this.waitForExit(run);
    await run.terminatePromise;
    if (run.timeoutTimer) {
      clearTimeout(run.timeoutTimer);
    }

    const outcome = this.buildOutcome(run, exit, workDirectory);
    const isCurrent = () =>
      !this.disposed && !run.cancelReason && this.currentGeneration === run.generation;
    if (outcome.status === 'completed' && isCurrent()) {
      try {
        await this.options.onCompleted?.(outcome, isCurrent);
        if (isCurrent()) {
          this.successfulDirectories.push(workDirectory);
          this.successfulDirectories.splice(
            0,
            Math.max(0, this.successfulDirectories.length - (this.options.keepRunDirectories ?? 2))
          );
        }
      } catch (error) {
        outcome.status = 'failed';
        outcome.error = error instanceof Error ? error.message : String(error);
        outcome.reportPath = undefined;
      }
    }
    if (!isCurrent()) {
      outcome.status = run.cancelReason === 'timeout' ? 'timeout' : 'cancelled';
      outcome.reportPath = undefined;
    }
    if (this.activeRun === run) this.activeRun = undefined;
    this.runningDirectories.delete(workDirectory);
    await this.pruneRunDirectories();
    return outcome;
  }

  /**
   * Keeps the newest run directories and removes older ones. The current result's
   * evidence lives in the newest directory and is read on demand, so it is never the one
   * removed here.
   */
  private async pruneRunDirectories(): Promise<void> {
    const workRoot = this.options.workRoot ?? os.tmpdir();
    const keep = Math.max(1, this.options.keepRunDirectories ?? 2);
    try {
      const entries = await fs.promises.readdir(workRoot, { withFileTypes: true });
      const runs = entries.filter(
        (entry) => entry.isDirectory() && entry.name.startsWith('sharpdeps-run-')
      );
      const withTimes = await Promise.all(
        runs.map(async (entry) => {
          const full = path.join(workRoot, entry.name);
          const stats = await fs.promises.stat(full).catch(() => undefined);
          return { full, mtime: stats?.mtimeMs ?? 0 };
        })
      );
      withTimes.sort((left, right) => right.mtime - left.mtime);
      for (const entry of withTimes.slice(keep)) {
        if (
          !this.successfulDirectories.includes(entry.full) &&
          !this.runningDirectories.has(entry.full)
        ) {
          await removeDirectory(entry.full);
        }
      }
    } catch {
      // Pruning is best effort: a missing work root is not an analysis failure.
    }
  }

  /**
   * Cooperative stop, grace period, then forced kill of the owned process tree.
   * Safe to call repeatedly; only the run this controller started is affected.
   */
  cancel(reason: AnalysisCancelReason = 'user'): void {
    const run = this.activeRun;
    if (!run || run.terminatePromise) {
      return;
    }

    run.cancelReason = reason;
    run.terminatePromise = this.terminate(run);
  }

  async dispose(): Promise<void> {
    this.disposed = true;
    if (this.activeRun) {
      this.cancel('disposed');
      await this.activeRun.terminatePromise?.catch(() => undefined);
    }
  }

  private async terminate(run: ActiveRun): Promise<void> {
    // 1. Cooperative: closing stdin lets the host stop cleanly.
    try {
      run.child.stdin?.end();
    } catch {
      // The pipe may already be gone.
    }

    if (process.platform !== 'win32') {
      try {
        if (run.child.pid !== undefined) process.kill(-run.child.pid, 'SIGTERM');
      } catch {
        // Already exited.
      }
    }

    // 2. Grace period.
    const exited = await waitForExit(run.child, this.options.gracePeriodMs ?? DEFAULT_GRACE_MS);
    if (exited && process.platform === 'win32') {
      return;
    }

    // 3. Forced kill of the tree we started.
    const pid = run.child.pid;
    if (pid === undefined) {
      return;
    }

    try {
      if (this.options.killTreeImpl) {
        await this.options.killTreeImpl(pid);
      } else {
        await killOwnedProcessTree(pid);
      }
    } catch (error) {
      this.appendLog(
        `Failed to terminate the analyzer process tree (pid ${pid}): ${
          error instanceof Error ? error.message : String(error)
        }`
      );
    }
  }

  private wireStreams(run: ActiveRun, workDirectory: string): void {
    let stdoutBuffer = '';
    run.child.stdout?.on('data', (chunk: Buffer) => {
      stdoutBuffer += chunk.toString('utf8');
      const lines = stdoutBuffer.split('\n');
      stdoutBuffer = lines.pop() ?? '';
      for (const line of lines) {
        this.handleStdoutLine(run, line.trim());
      }
    });

    let stderrBuffer = '';
    run.child.stderr?.on('data', (chunk: Buffer) => {
      stderrBuffer += chunk.toString('utf8');
      const lines = stderrBuffer.split('\n');
      stderrBuffer = lines.pop() ?? '';
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed.length > 0) {
          this.appendLog(trimmed);
          this.options.onLog?.(trimmed, 'stderr');
        }
      }
    });

    run.child.on('error', (error) => {
      this.appendLog(`Analyzer process error: ${error.message}`);
    });

    this.appendLog(`Started analysis ${run.analysisId} in ${path.basename(workDirectory)}`);
  }

  private handleStdoutLine(run: ActiveRun, line: string): void {
    if (line.length === 0) {
      return;
    }

    const prefix = 'sharpdeps:progress ';
    if (line.startsWith(prefix)) {
      try {
        const payload = JSON.parse(line.slice(prefix.length)) as {
          stage?: string;
          payload?: Record<string, unknown>;
        };
        const stage = (payload.stage ?? run.stage) as AnalysisStage;
        run.stage = stage;
        const data = payload.payload ?? {};
        this.options.onProgress?.({
          requestId: run.requestId,
          analysisId: run.analysisId,
          stage,
          elapsedMs: this.now() - run.startedAt,
          loaded: typeof data.loaded === 'number' ? data.loaded : undefined,
          analyzed: typeof data.analyzed === 'number' ? data.analyzed : undefined,
          message: typeof data.message === 'string' ? data.message : undefined
        });
      } catch {
        this.appendLog(`Unparsable progress line: ${line}`);
      }

      return;
    }

    this.appendLog(line);
    this.options.onLog?.(line, 'stdout');
  }

  private buildOutcome(
    run: ActiveRun,
    exit: { code: number | null; signal: NodeJS.Signals | null },
    workDirectory: string
  ): AnalysisOutcome {
    const durationMs = this.now() - run.startedAt;
    const reportPath = path.join(workDirectory, 'report-v2.json');
    const base: AnalysisOutcome = {
      requestId: run.requestId,
      analysisId: run.analysisId,
      status: 'failed',
      durationMs,
      workDirectory
    };

    if (run.cancelReason) {
      return {
        ...base,
        status: run.cancelReason === 'timeout' ? 'timeout' : 'cancelled',
        error:
          run.cancelReason === 'timeout'
            ? 'The analysis exceeded its time limit.'
            : 'The analysis was stopped.'
      };
    }

    if (exit.code === 0) {
      return {
        ...base,
        status: 'completed',
        reportPath: fs.existsSync(reportPath) ? reportPath : undefined
      };
    }

    if (exit.code === 3) {
      // The host reported a cooperative cancellation.
      return { ...base, status: 'cancelled', error: 'The analysis was stopped.' };
    }

    return {
      ...base,
      status: 'failed',
      error: `The analyzer exited with code ${exit.code ?? 'null'}${
        exit.signal ? ` (signal ${exit.signal})` : ''
      }.`,
      detail: this.logs.slice(-5).join('\n')
    };
  }

  private waitForExit(
    run: ActiveRun
  ): Promise<{ code: number | null; signal: NodeJS.Signals | null }> {
    return new Promise((resolve) => {
      run.child.once('close', (code, signal) => resolve({ code, signal }));
    });
  }

  private appendLog(line: string): void {
    const max = this.options.maxLogLines ?? DEFAULT_MAX_LOG_LINES;
    this.logs.push(line);
    while (this.logs.length > max) {
      this.logs.shift();
    }
  }

  private now(): number {
    return this.options.now?.() ?? Date.now();
  }
}

/** Forced termination of the process tree the controller started. */
async function killOwnedProcessTree(pid: number): Promise<void> {
  if (process.platform === 'win32') {
    await new Promise<void>((resolve) => {
      const killer = spawn('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true });
      killer.once('close', () => resolve());
      killer.once('error', () => resolve());
    });
    return;
  }

  // The child was spawned detached, so it leads its own process group.
  try {
    process.kill(-pid, 'SIGKILL');
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error;
  }
}

function waitForExit(child: ChildProcess, timeoutMs: number): Promise<boolean> {
  if (child.exitCode !== null || child.signalCode !== null) {
    return Promise.resolve(true);
  }

  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      child.removeListener('close', onClose);
      resolve(false);
    }, timeoutMs);
    timer.unref?.();

    function onClose(): void {
      clearTimeout(timer);
      resolve(true);
    }

    child.once('close', onClose);
  });
}

async function removeDirectory(directory: string): Promise<void> {
  await fs.promises.rm(directory, { recursive: true, force: true }).catch(() => undefined);
}
