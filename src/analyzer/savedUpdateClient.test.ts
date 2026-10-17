import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import { afterEach, describe, expect, it } from 'vitest';
import {
  runSavedUpdate,
  savedUpdateArguments,
  savedUpdateReply,
  verifySavedUpdateSelection,
  type SavedUpdateSelection
} from './savedUpdateClient';

const roots: string[] = [];
const requestId = 'req_0123456789abcdef';
function fixture(configuration = 'Debug') {
  const root = fs.mkdtempSync(path.join(fs.realpathSync(os.tmpdir()), 'sharpdeps-update-'));
  roots.push(root);
  const target = path.join(root, 'Input.csproj');
  fs.writeFileSync(target, '<Project />');
  const selection: SavedUpdateSelection = { root, target, configuration, timeoutSeconds: 180 };
  const cliPath = path.join(root, 'fake-cli.dll');
  fs.writeFileSync(
    cliPath,
    `
    const fs = require('node:fs'), path = require('node:path');
    const args = process.argv.slice(2);
    const value = name => args[args.indexOf('--' + name) + 1];
    const result = {workspaceId:'workspace-a',snapshotId:'snapshot-b',generation:4};
    const reply = () => JSON.stringify({apiVersion:'1',command:'update',requestId:value('request-id'),result,errors:[]});
    const mode = value('configuration');
    if (mode === 'cancel' || mode === 'overflow') {
      fs.writeFileSync(path.join(value('root'), 'ready.txt'), 'ready');
      process.stdin.resume();
      process.stdin.on('end', () => setTimeout(() => {
        fs.writeFileSync(path.join(value('root'), 'drained.txt'), 'done');
        process.stdout.write(reply() + '\\n', () => process.exit(0));
      }, 20));
      if (mode === 'overflow') process.stdout.write('x'.repeat(600000));
    } else process.stdout.write('generator noise\\n' + reply() + '\\n', () => process.exit(0));
  `
  );
  return { selection, cliPath };
}
afterEach(() => {
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

describe('saved update caller boundaries', () => {
  it('keeps fixed trusted update arguments and rejects a target outside the selected root', async () => {
    const { selection } = fixture();
    expect(savedUpdateArguments(selection, requestId)).toContain('--vscode-mutation-helper');
    expect(savedUpdateArguments(selection, requestId)).toContain('--trusted');
    await verifySavedUpdateSelection(selection);
    expect(() =>
      savedUpdateArguments(
        { ...selection, target: path.resolve(selection.root, '../Other.csproj') },
        requestId
      )
    ).toThrow();
    expect(() =>
      savedUpdateArguments({ ...selection, targetFramework: 'net10.0' }, requestId)
    ).toThrow();
    fs.mkdirSync(path.join(selection.root, 'actual'));
    fs.writeFileSync(path.join(selection.root, 'actual/Input.csproj'), '<Project />');
    fs.symlinkSync(
      path.join(selection.root, 'actual'),
      path.join(selection.root, 'linked'),
      process.platform === 'win32' ? 'junction' : 'dir'
    );
    await expect(
      verifySavedUpdateSelection({
        ...selection,
        target: path.join(selection.root, 'linked/Input.csproj')
      })
    ).rejects.toMatchObject({ code: 'SAVED_UPDATE_LINK' });
  });

  it('accepts only one successful reply identified by the exact caller request', () => {
    const good = JSON.stringify({
      apiVersion: '1',
      command: 'update',
      requestId,
      errors: [],
      result: { workspaceId: 'workspace-a', snapshotId: 'snapshot-b', generation: 4 }
    });
    expect(savedUpdateReply('noise\n' + good, requestId, 0).generation).toBe(4);
    for (const output of [
      good.replace(requestId, 'other'),
      good + '\n' + good,
      good.replace('"generation":4', '"generation":"4"')
    ])
      expect(() => savedUpdateReply(output, requestId, 0)).toThrow();
    expect(() => savedUpdateReply(good, requestId, 1)).toThrow();
  });

  it('runs a child transport without a shell and waits for cooperative cancellation/drain', async () => {
    const normal = fixture();
    expect(
      await runSavedUpdate({
        ...normal,
        dotnetPath: process.execPath,
        requestId,
        signal: new AbortController().signal,
        current: () => true
      })
    ).toEqual({ workspaceId: 'workspace-a', snapshotId: 'snapshot-b', generation: 4 });
    const cancelled = fixture('cancel');
    const stop = new AbortController();
    let watcher: fs.FSWatcher;
    let timeout: ReturnType<typeof setTimeout>;
    const ready = new Promise<void>((resolve, reject) => {
      watcher = fs.watch(cancelled.selection.root, (_, filename) => {
        if (filename?.toString() === 'ready.txt') resolve();
      });
      timeout = setTimeout(() => reject(new Error('fake caller did not start')), 5000);
    });
    const pending = runSavedUpdate({
      ...cancelled,
      dotnetPath: process.execPath,
      requestId,
      signal: stop.signal,
      current: () => true
    });
    const completion = expect(pending).rejects.toMatchObject({
      code: 'SAVED_UPDATE_CANCELLED_CHECK_STATUS'
    });
    // A real ready event admits this fake caller before EOF triggers its simulated drain.
    try {
      await ready;
      stop.abort();
    } finally {
      stop.abort();
      clearTimeout(timeout!);
      watcher!.close();
      await completion;
    }
    expect(fs.existsSync(path.join(cancelled.selection.root, 'drained.txt'))).toBe(true);
  });

  it('bounds noisy output and drains the child rather than killing it', async () => {
    const noisy = fixture('overflow');
    await expect(
      runSavedUpdate({
        ...noisy,
        dotnetPath: process.execPath,
        requestId,
        signal: new AbortController().signal,
        current: () => true
      })
    ).rejects.toMatchObject({ code: 'SAVED_UPDATE_OUTPUT_INVALID_CHECK_STATUS' });
    expect(fs.existsSync(path.join(noisy.selection.root, 'drained.txt'))).toBe(true);
  });
});
