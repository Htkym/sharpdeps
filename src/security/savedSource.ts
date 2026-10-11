import { createHash } from 'node:crypto';
import { constants } from 'node:fs';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { TextDecoder } from 'node:util';

/** Only the host's pinned Query session constructs these values. */
export interface SavedSourceLocation {
  root: string;
  relativePath: string;
  contentHash: string;
  span: { start: number; length: number; end: number };
}

export class SavedSourceError extends Error {
  constructor(readonly code: string) {
    const messages: Record<string, string> = {
      QUERY_ANALYSIS_EXPIRED: 'The saved view changed. Refresh it before opening this source.',
      QUERY_SOURCE_TRUST_REQUIRED: 'Trust this workspace before opening saved source locations.',
      QUERY_SOURCE_DIRTY: 'Save or discard the unsaved changes, then update the saved index.',
      QUERY_SOURCE_CHANGED: 'This source changed since the saved index. Update the index first.',
      QUERY_SOURCE_LINK: 'Saved source navigation does not open linked files or linked roots.',
      QUERY_SOURCE_OUTSIDE_ROOT: 'This saved source is outside the selected workspace root.',
      QUERY_SOURCE_LIMIT: 'This saved source exceeds the navigation size limit.',
      QUERY_SOURCE_ENCODING: 'This saved source requires valid UTF-8 text without NUL characters.',
      QUERY_SOURCE_SPAN: 'The saved source position is unavailable for this file.',
      QUERY_SOURCE_UNAVAILABLE: 'This saved source could not be verified inside the workspace.'
    };
    super(messages[code] ?? 'The saved source could not be opened.');
  }
}

const MAX_SOURCE_BYTES = 64 * 1024 * 1024;

/** Verifies physical, bounded, strict UTF-8 source; never follows a saved mapped path. */
export async function readSavedSource(location: SavedSourceLocation): Promise<{
  filePath: string;
  text: string;
}> {
  const { root, relativePath, contentHash, span } = location;
  const parts = relativePath.split('/');
  if (
    !path.isAbsolute(root) ||
    root.startsWith('\\\\') ||
    root.startsWith('//') ||
    !relativePath ||
    parts.some((part) => !part || part === '.' || part === '..') ||
    // eslint-disable-next-line no-control-regex
    /[\\:*?"<>|\u0000-\u001f]/.test(relativePath) ||
    !/^[0-9a-f]{64}$/.test(contentHash)
  )
    throw new SavedSourceError('QUERY_SOURCE_UNAVAILABLE');
  const filePath = path.resolve(root, ...parts);
  if (!inside(root, filePath)) throw new SavedSourceError('QUERY_SOURCE_OUTSIDE_ROOT');
  try {
    await rejectLinks(filePath);
    const [realRoot, realFile] = await Promise.all([fs.realpath(root), fs.realpath(filePath)]);
    if (!samePath(root, realRoot) || !samePath(filePath, realFile) || !inside(realRoot, realFile))
      throw new SavedSourceError('QUERY_SOURCE_LINK');
    const named = await fs.lstat(filePath);
    if (!named.isFile() || named.size > MAX_SOURCE_BYTES)
      throw new SavedSourceError('QUERY_SOURCE_LIMIT');
    const handle = await fs.open(
      filePath,
      constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0) | (constants.O_NONBLOCK ?? 0)
    );
    let bytes: Buffer;
    try {
      const before = await handle.stat();
      if (!before.isFile() || before.size > MAX_SOURCE_BYTES)
        throw new SavedSourceError('QUERY_SOURCE_LIMIT');
      bytes = Buffer.alloc(before.size);
      let offset = 0;
      while (offset < bytes.length) {
        const result = await handle.read(bytes, offset, bytes.length - offset, offset);
        if (!result.bytesRead) throw new SavedSourceError('QUERY_SOURCE_CHANGED');
        offset += result.bytesRead;
      }
      const extra = await handle.read(Buffer.alloc(1), 0, 1, offset);
      const after = await handle.stat();
      await rejectLinks(filePath);
      const current = await fs.stat(filePath);
      if (
        extra.bytesRead ||
        before.size !== after.size ||
        before.mtimeMs !== after.mtimeMs ||
        before.ctimeMs !== after.ctimeMs ||
        after.dev !== current.dev ||
        after.ino !== current.ino ||
        after.size !== current.size ||
        after.mtimeMs !== current.mtimeMs ||
        after.ctimeMs !== current.ctimeMs ||
        createHash('sha256').update(bytes).digest('hex') !== contentHash
      )
        throw new SavedSourceError('QUERY_SOURCE_CHANGED');
    } finally {
      await handle.close();
    }
    let text: string;
    try {
      // Saved raw spans include U+FEFF; ignoreBOM=true retains that code unit.
      text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
    } catch {
      throw new SavedSourceError('QUERY_SOURCE_ENCODING');
    }
    if (text.includes('\0')) throw new SavedSourceError('QUERY_SOURCE_ENCODING');
    if (
      !Number.isSafeInteger(span.start) ||
      !Number.isSafeInteger(span.length) ||
      !Number.isSafeInteger(span.end) ||
      span.start < 0 ||
      span.length < 0 ||
      span.start + span.length !== span.end ||
      span.end > text.length ||
      !textBoundary(text, span.start) ||
      !textBoundary(text, span.end)
    )
      throw new SavedSourceError('QUERY_SOURCE_SPAN');
    return { filePath, text };
  } catch (error) {
    if (error instanceof SavedSourceError) throw error;
    throw new SavedSourceError('QUERY_SOURCE_UNAVAILABLE');
  }
}

/** Editor text must equal the verified source, apart from its proven byte BOM. */
export function savedEditorSpan(
  sourceText: string,
  editorText: string,
  span: SavedSourceLocation['span']
): { start: number; end: number } {
  const offset =
    editorText === sourceText
      ? 0
      : sourceText.startsWith('\ufeff') && editorText === sourceText.slice(1)
        ? 1
        : -1;
  if (offset < 0) throw new SavedSourceError('QUERY_SOURCE_CHANGED');
  if (span.start < offset || span.end > sourceText.length)
    throw new SavedSourceError('QUERY_SOURCE_SPAN');
  return { start: span.start - offset, end: span.end - offset };
}

function samePath(left: string, right: string): boolean {
  const normalized = (value: string) => {
    const absolute = path.resolve(value);
    return process.platform === 'win32' ? absolute.toLowerCase() : absolute;
  };
  return normalized(left) === normalized(right);
}

function textBoundary(text: string, offset: number): boolean {
  const previous = text.charCodeAt(offset - 1);
  const next = text.charCodeAt(offset);
  return !(
    (previous === 13 && next === 10) ||
    (previous >= 0xd800 && previous <= 0xdbff && next >= 0xdc00 && next <= 0xdfff)
  );
}

function inside(root: string, file: string): boolean {
  const relative = path.relative(root, file);
  return (
    !!relative &&
    !path.isAbsolute(relative) &&
    relative !== '..' &&
    !relative.startsWith(`..${path.sep}`)
  );
}

async function rejectLinks(file: string): Promise<void> {
  for (let current = file; ; current = path.dirname(current)) {
    if ((await fs.lstat(current)).isSymbolicLink()) throw new SavedSourceError('QUERY_SOURCE_LINK');
    const parent = path.dirname(current);
    if (parent === current) return;
  }
}
