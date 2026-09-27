// Analysis controller tests (SD-014).
//
// A fake analyzer process exercises the real process handling: progress parsing,
// superseding, cooperative cancellation, forced tree termination, timeouts, and
// cleanup. The fake is a Node script, so the tests run everywhere without a .NET SDK.

import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import {
  AnalysisController,
  type AnalysisOutcome,
  type AnalysisProgressEvent
} from './analysisController';

const FAKE_ANALYZER = `
import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';

const args = process.argv.slice(2);
const value = (name) => {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
};

const output = value('--output');
const behaviour = value('--behaviour') ?? 'ok';
const workDirectory = path.dirname(output);

process.stdout.write('sharpdeps:progress {"stage":"load","payload":{"loaded":2}}\\n');
process.stdin.resume();
process.stdin.on('end', () => {
  process.stderr.write('cancelled by stdin\\n');
  process.exit(3);
});

function writeReport() {
  fs.writeFileSync(path.join(workDirectory, 'report-v2.json'), '{"schemaVersion":2}', 'utf8');
  fs.writeFileSync(path.join(workDirectory, 'evidence.ndjson'), '', 'utf8');
}

switch (behaviour) {
  case 'slow':
    setInterval(() => {}, 1000);
    break;
  case 'tree': {
    const grandchild = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
    process.stdout.write('grandchild:' + grandchild.pid + '\\n');
    setInterval(() => {}, 1000);
    break;
  }
  case 'fail':
    process.stderr.write('boom\\n');
    process.exit(1);
    break;
  default:
    process.stdout.write('sharpdeps:progress {"stage":"write","payload":{"analyzed":3}}\\n');
    writeReport();
    process.exit(0);
}
`;

const temporaryDirectories: string[] = [];
const extraProcesses: number[] = [];
const controllers: AnalysisController[] = [];

afterEach(async () => {
  for (const controller of controllers.splice(0)) {
    await controller.dispose().catch(() => undefined);
  }

  for (const pid of extraProcesses.splice(0)) {
    try {
      process.kill(pid, 'SIGKILL');
    } catch {
      // Already gone.
    }
  }

  while (temporaryDirectories.length > 0) {
    const directory = temporaryDirectories.pop();
    if (directory) {
      try {
        fs.rmSync(directory, { recursive: true, force: true });
      } catch {
        // A killed process may still hold a handle for a moment.
      }
    }
  }
});

function createEnvironment(): { workRoot: string; scriptPath: string } {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-controller-'));
  temporaryDirectories.push(root);
  const scriptPath = path.join(root, 'fake-analyzer.mjs');
  fs.writeFileSync(scriptPath, FAKE_ANALYZER, 'utf8');
  return { workRoot: root, scriptPath };
}

function createController(
  scriptPath: string,
  workRoot: string,
  hooks: {
    onProgress?: (event: AnalysisProgressEvent) => void;
    onCompleted?: (outcome: AnalysisOutcome) => void;
    logs?: string[];
    maxLogLines?: number;
  } = {}
): AnalysisController {
  const controller = new AnalysisController({
    workRoot,
    gracePeriodMs: 300,
    maxLogLines: hooks.maxLogLines ?? 50,
    onProgress: hooks.onProgress,
    onCompleted: hooks.onCompleted,
    onLog: (line) => hooks.logs?.push(line),
    processFactory: (request, workDirectory) => ({
      command: process.execPath,
      args: [
        scriptPath,
        '--output',
        path.join(workDirectory, 'report.json'),
        '--analysis-id',
        request.analysisId ?? '',
        '--behaviour',
        (request as { behaviour?: string }).behaviour ?? 'ok'
      ],
      cwd: workDirectory
    })
  });
  controllers.push(controller);
  return controller;
}

function isAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

async function waitFor(predicate: () => boolean, timeoutMs = 3000): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) {
      return true;
    }

    await new Promise((resolve) => setTimeout(resolve, 25));
  }

  return predicate();
}

describe('AnalysisController', () => {
  it('completes a run, reports progress, and keeps the newest work directory', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const stages: string[] = [];
    const outcomes: AnalysisOutcome[] = [];
    const controller = createController(scriptPath, workRoot, {
      onProgress: (event) => stages.push(event.stage),
      onCompleted: (outcome) => outcomes.push(outcome)
    });

    const outcome = await controller.start({
      targetPath: 'Solution.sln',
      mode: 'quick',
      analysisId: 'an_0000000000000001'
    });

    expect(outcome.status).toBe('completed');
    expect(outcome.analysisId).toBe('an_0000000000000001');
    expect(outcome.reportPath).toContain('report-v2.json');
    expect(stages).toEqual(['load', 'write']);
    expect(outcomes.map((entry) => entry.analysisId)).toEqual(['an_0000000000000001']);
    expect(controller.isRunning).toBe(false);
    // The store reads evidence from the run directory on demand, so the newest one is
    // kept (SD-028); older runs are pruned instead.
    expect(fs.existsSync(outcome.workDirectory)).toBe(true);
  });

  it('prunes older run directories and keeps the newest two', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const controller = createController(scriptPath, workRoot);

    const first = await controller.start({
      targetPath: 'Solution.sln',
      mode: 'quick',
      analysisId: 'an_0000000000000001'
    });
    const second = await controller.start({
      targetPath: 'Solution.sln',
      mode: 'quick',
      analysisId: 'an_0000000000000002'
    });
    const third = await controller.start({
      targetPath: 'Solution.sln',
      mode: 'quick',
      analysisId: 'an_0000000000000003'
    });

    const runs = fs
      .readdirSync(workRoot)
      .filter((name) => name.startsWith('sharpdeps-run-'))
      .sort();
    expect(runs).toHaveLength(2);
    // The newest result's directory and report survive, so evidence paging keeps working.
    expect(fs.existsSync(path.join(third.workDirectory, 'report-v2.json'))).toBe(true);
    expect(runs).not.toContain(path.basename(first.workDirectory));
    void second;
  });

  it('reports a failed run with its exit code and log tail', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const controller = createController(scriptPath, workRoot);
    const outcome = await controller.start({
      targetPath: 'Solution.sln',
      mode: 'quick',
      behaviour: 'fail'
    } as never);

    expect(outcome.status).toBe('failed');
    expect(outcome.error).toContain('code 1');
    expect(outcome.detail).toContain('boom');
  });

  it('preserves the last successful result through repeated failures', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const controller = createController(scriptPath, workRoot);
    const success = await controller.start({ targetPath: 'Good.sln', mode: 'quick' });
    for (let index = 0; index < 3; index++) {
      await controller.start({ targetPath: 'Bad.sln', mode: 'quick', behaviour: 'fail' } as never);
    }
    expect(fs.existsSync(success.reportPath!)).toBe(true);
  });

  it('guards an asynchronous publication and returns cancelled when superseded', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    let release!: () => void;
    let entered!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const registering = new Promise<void>((resolve) => {
      entered = resolve;
    });
    const published: string[] = [];
    const controller = new AnalysisController({
      workRoot,
      processFactory: (_request, directory) => ({
        command: process.execPath,
        args: [scriptPath, '--output', path.join(directory, 'report.json')]
      }),
      onCompleted: async (outcome, isCurrent) => {
        if (outcome.analysisId.endsWith('1')) {
          entered();
          await gate;
        }
        if (isCurrent()) published.push(outcome.analysisId);
      }
    });
    controllers.push(controller);
    const first = controller.start({
      targetPath: 'Old.sln',
      mode: 'quick',
      analysisId: 'an_0000000000000001'
    });
    await registering;
    const next = await controller.start({
      targetPath: 'New.sln',
      mode: 'quick',
      analysisId: 'an_0000000000000002'
    });
    release();
    expect((await first).status).toBe('cancelled');
    expect(next.status).toBe('completed');
    expect(published).toEqual(['an_0000000000000002']);
  });

  it('does not publish when the user cancels during asynchronous registration', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    let release!: () => void;
    let entered!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const registering = new Promise<void>((resolve) => {
      entered = resolve;
    });
    let published = false;
    const controller = new AnalysisController({
      workRoot,
      processFactory: (_request, directory) => ({
        command: process.execPath,
        args: [scriptPath, '--output', path.join(directory, 'report.json')]
      }),
      onCompleted: async (_outcome, isCurrent) => {
        entered();
        await gate;
        published = isCurrent();
      }
    });
    controllers.push(controller);
    const run = controller.start({ targetPath: 'Example.sln', mode: 'quick' });
    await registering;
    controller.cancel('user');
    release();
    expect((await run).status).toBe('cancelled');
    expect(published).toBe(false);
  });

  it('never lets a superseded run publish its result', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const outcomes: AnalysisOutcome[] = [];
    const controller = createController(scriptPath, workRoot, {
      onCompleted: (outcome) => outcomes.push(outcome)
    });

    const slow = controller.start({
      targetPath: 'Slow.sln',
      mode: 'quick',
      analysisId: 'an_0000000000000002',
      behaviour: 'slow'
    } as never);

    const fast = controller.start({
      targetPath: 'Fast.sln',
      mode: 'quick',
      analysisId: 'an_0000000000000003'
    });

    const [slowOutcome, fastOutcome] = await Promise.all([slow, fast]);

    expect(fastOutcome.status).toBe('completed');
    expect(['cancelled', 'failed']).toContain(slowOutcome.status);
    expect(outcomes.map((entry) => entry.analysisId)).toEqual(['an_0000000000000003']);
  });

  it('terminates the whole owned process tree on cancel', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const logs: string[] = [];
    const controller = createController(scriptPath, workRoot, { logs });

    const run = controller.start({
      targetPath: 'Tree.sln',
      mode: 'quick',
      behaviour: 'tree'
    } as never);

    expect(await waitFor(() => logs.some((line) => line.startsWith('grandchild:')))).toBe(true);
    const grandchildPid = Number(
      logs.find((line) => line.startsWith('grandchild:'))?.split(':')[1] ?? Number.NaN
    );
    expect(isAlive(grandchildPid)).toBe(true);

    controller.cancel('user');
    const outcome = await run;

    expect(['cancelled', 'timeout']).toContain(outcome.status);
    expect(await waitFor(() => !isAlive(grandchildPid))).toBe(true);
  });

  it('leaves unrelated processes alone', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const unrelated = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {
      stdio: 'ignore'
    });
    extraProcesses.push(unrelated.pid ?? 0);
    const controller = createController(scriptPath, workRoot);

    const run = controller.start({
      targetPath: 'Slow.sln',
      mode: 'quick',
      behaviour: 'slow'
    } as never);

    await waitFor(() => controller.isRunning);
    controller.cancel('user');
    await run;

    expect(unrelated.pid).toBeDefined();
    expect(isAlive(unrelated.pid ?? 0)).toBe(true);
  });

  it('stops a run that exceeds its time limit', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const controller = createController(scriptPath, workRoot);

    const outcome = await controller.start({
      targetPath: 'Slow.sln',
      mode: 'quick',
      behaviour: 'slow',
      timeoutMs: 300
    } as never);

    expect(outcome.status).toBe('timeout');
    expect(outcome.error).toContain('time limit');
    expect(controller.isRunning).toBe(false);
  });

  it('stops the run when disposed', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const controller = createController(scriptPath, workRoot);

    const run = controller.start({
      targetPath: 'Slow.sln',
      mode: 'quick',
      behaviour: 'slow'
    } as never);

    await waitFor(() => controller.isRunning);
    await controller.dispose();

    const outcome = await run;
    expect(['cancelled', 'timeout']).toContain(outcome.status);
    expect(controller.active).toBeUndefined();
  });

  it('keeps the log bounded', async () => {
    const { workRoot, scriptPath } = createEnvironment();
    const controller = createController(scriptPath, workRoot, { maxLogLines: 2 });
    await controller.start({ targetPath: 'Solution.sln', mode: 'quick' });

    expect(controller.getLogLines().length).toBeLessThanOrEqual(2);
  });
}, 20000);
