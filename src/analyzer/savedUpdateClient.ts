import { spawn } from 'node:child_process';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { TextDecoder } from 'node:util';

export interface SavedUpdateSelection {
  root: string;
  target: string;
  configuration: string;
  platform?: string;
  project?: string;
  targetFramework?: string;
  timeoutSeconds: number;
}
export interface SavedUpdateResult {
  workspaceId: string;
  snapshotId: string;
  generation: number;
}
export class SavedUpdateError extends Error {
  constructor(readonly code: string) {
    super(
      'The saved index update could not be confirmed. Refresh the saved view and check the SharpDeps output.'
    );
  }
}

const OUTPUT_CHARS = 256 * 1024;
const plain = (value: string) =>
  // eslint-disable-next-line no-control-regex
  value.length > 0 && value.length <= 128 && !/[\u0000-\u001f]/.test(value);

/** Fixed host arguments; no shell, automatic restore, owner takeover or arbitrary argv. */
export function savedUpdateArguments(selection: SavedUpdateSelection, requestId: string): string[] {
  if (
    !plain(requestId) ||
    requestId.length > 64 ||
    !plain(selection.configuration) ||
    (selection.platform !== undefined && !plain(selection.platform)) ||
    (selection.targetFramework !== undefined &&
      (!plain(selection.targetFramework) || !selection.project)) ||
    !Number.isInteger(selection.timeoutSeconds) ||
    selection.timeoutSeconds < 1 ||
    selection.timeoutSeconds > 300
  )
    throw new SavedUpdateError('SAVED_UPDATE_SELECTION_INVALID');
  selectedPath(selection.root, selection.target, ['.csproj', '.sln', '.slnx']);
  if (selection.project) selectedPath(selection.root, selection.project, ['.csproj']);
  const args = [
    '--vscode-mutation-helper',
    'update',
    '--root',
    selection.root,
    '--target',
    selection.target,
    '--configuration',
    selection.configuration,
    '--trusted',
    '--quiet',
    '--request-id',
    requestId,
    '--timeout-seconds',
    String(selection.timeoutSeconds)
  ];
  if (selection.platform) args.push('--platform', selection.platform);
  if (selection.project) args.push('--project', selection.project);
  if (selection.targetFramework) args.push('--tfm', selection.targetFramework);
  return args;
}

/** Must run before even an SDK probe can evaluate the selected target directory. */
export async function verifySavedUpdateSelection(selection: SavedUpdateSelection): Promise<void> {
  savedUpdateArguments(selection, 'selection');
  for (const file of [
    selection.root,
    selection.target,
    ...(selection.project ? [selection.project] : [])
  ]) {
    for (let current = file; ; current = path.dirname(current)) {
      if ((await fs.lstat(current)).isSymbolicLink())
        throw new SavedUpdateError('SAVED_UPDATE_LINK');
      if (path.dirname(current) === current) break;
    }
    const stat = await fs.stat(file);
    if (file === selection.root ? !stat.isDirectory() : !stat.isFile())
      throw new SavedUpdateError('SAVED_UPDATE_SELECTION_INVALID');
  }
}

function selectedPath(root: string, file: string, extensions: string[]): void {
  const relative = path.relative(root, file);
  if (
    !path.isAbsolute(root) ||
    root.startsWith('\\\\') ||
    root.startsWith('//') ||
    !path.isAbsolute(file) ||
    !relative ||
    path.isAbsolute(relative) ||
    relative === '..' ||
    relative.startsWith(`..${path.sep}`) ||
    !extensions.includes(path.extname(file).toLowerCase())
  )
    throw new SavedUpdateError('SAVED_UPDATE_SELECTION_INVALID');
}

/** Consumes a single identified CLI reply; noisy output never supplies a different request. */
export function savedUpdateReply(
  text: string,
  requestId: string,
  exit: number | null
): SavedUpdateResult {
  const matching: Record<string, unknown>[] = [];
  for (const line of text.split('\n')) {
    try {
      const value: unknown = JSON.parse(line);
      if (typeof value !== 'object' || !value) continue;
      const reply = value as Record<string, unknown>;
      if (reply.apiVersion === '1' && reply.command === 'update' && reply.requestId === requestId)
        matching.push(reply);
    } catch {
      /* SDK/generator noise is retained but is not a reply. */
    }
  }
  if (matching.length !== 1) throw new SavedUpdateError('SAVED_UPDATE_REPLY_INVALID');
  const reply = matching[0];
  if (Array.isArray(reply.errors) && reply.errors.length) {
    const first = reply.errors[0] as { code?: unknown } | null;
    if (first && typeof first.code === 'string' && /^[A-Z0-9_]{1,128}$/.test(first.code))
      throw new SavedUpdateError(first.code);
  }
  if (!Array.isArray(reply.errors) || reply.errors.length || exit !== 0)
    throw new SavedUpdateError('SAVED_UPDATE_FAILED_CHECK_STATUS');
  const result = reply.result as Record<string, unknown> | undefined;
  if (
    !result ||
    typeof result.workspaceId !== 'string' ||
    !plain(result.workspaceId) ||
    typeof result.snapshotId !== 'string' ||
    !plain(result.snapshotId) ||
    !Number.isSafeInteger(result.generation) ||
    Number(result.generation) < 1
  )
    throw new SavedUpdateError('SAVED_UPDATE_REPLY_INVALID');
  return {
    workspaceId: result.workspaceId,
    snapshotId: result.snapshotId,
    generation: Number(result.generation)
  };
}

export async function runSavedUpdate(options: {
  dotnetPath: string;
  cliPath: string;
  selection: SavedUpdateSelection;
  requestId: string;
  signal: AbortSignal;
  current: () => boolean;
}): Promise<SavedUpdateResult> {
  const args = savedUpdateArguments(options.selection, options.requestId);
  if (
    !path.isAbsolute(options.dotnetPath) ||
    !path.isAbsolute(options.cliPath) ||
    path.extname(options.cliPath).toLowerCase() !== '.dll'
  )
    throw new SavedUpdateError('SAVED_UPDATE_CLI_REQUIRED');
  await verifySavedUpdateSelection(options.selection);
  if (!options.current() || options.signal.aborted)
    throw new SavedUpdateError('SAVED_UPDATE_CANCELLED_CHECK_STATUS');
  if (!(await fs.stat(options.cliPath)).isFile())
    throw new SavedUpdateError('SAVED_UPDATE_CLI_REQUIRED');
  if (!options.current() || options.signal.aborted)
    throw new SavedUpdateError('SAVED_UPDATE_CANCELLED_CHECK_STATUS');
  const child = spawn(options.dotnetPath, [options.cliPath, ...args], {
    cwd: options.selection.root,
    shell: false,
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe']
  });
  let invalid = false;
  const cancel = () => {
    child.stdin.end();
  };
  child.stdin.on('error', () => {
    invalid = true;
  });
  options.signal.addEventListener('abort', cancel, { once: true });
  if (options.signal.aborted) cancel();
  const capture = (stream: NodeJS.ReadableStream) => {
    const decoder = new TextDecoder('utf-8', { fatal: true });
    let text = '';
    stream.on('data', (bytes: Buffer) => {
      if (invalid) return;
      try {
        const value = decoder.decode(bytes, { stream: true });
        if (text.length + value.length > OUTPUT_CHARS) {
          invalid = true;
          cancel();
          return;
        }
        text += value;
      } catch {
        invalid = true;
        cancel();
      }
    });
    stream.on('error', () => {
      invalid = true;
      cancel();
    });
    stream.on('end', () => {
      try {
        text += decoder.decode();
      } catch {
        invalid = true;
        cancel();
      }
    });
    return () => text;
  };
  const stdout = capture(child.stdout);
  capture(child.stderr);
  // Continue consuming discarded output and wait for CLI DrainAsync, including on cancel.
  const exit = await new Promise<number | null>((resolve) => {
    child.on('error', () => {
      invalid = true;
    });
    child.on('close', resolve);
  });
  options.signal.removeEventListener('abort', cancel);
  if (!options.current() || options.signal.aborted)
    throw new SavedUpdateError('SAVED_UPDATE_CANCELLED_CHECK_STATUS');
  if (invalid) throw new SavedUpdateError('SAVED_UPDATE_OUTPUT_INVALID_CHECK_STATUS');
  return savedUpdateReply(stdout(), options.requestId, exit);
}
