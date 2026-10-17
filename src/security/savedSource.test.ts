import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import {
  readSavedSource,
  savedEditorSpan,
  SavedSourceError,
  type SavedSourceLocation
} from './savedSource';

const roots: string[] = [];
function fixture(bytes: Buffer): SavedSourceLocation {
  const root = fs.mkdtempSync(path.join(fs.realpathSync(os.tmpdir()), 'sharpdeps-saved-source-'));
  roots.push(root);
  fs.writeFileSync(path.join(root, 'Input.cs'), bytes);
  return {
    root,
    relativePath: 'Input.cs',
    contentHash: createHash('sha256').update(bytes).digest('hex'),
    span: { start: 0, length: 0, end: 0 }
  };
}
afterEach(() => {
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

describe('saved physical source boundary', () => {
  it('keeps raw BOM, astral and CRLF coordinates and converts only a proven editor BOM', async () => {
    const text = '\ufeff// 🐈\r\nclass Input {}\r\n';
    const location = fixture(Buffer.from(text));
    const start = text.indexOf('Input');
    location.span = { start, length: 5, end: start + 5 };
    const source = await readSavedSource(location);
    expect(source.text).toBe(text);
    expect(savedEditorSpan(source.text, text.slice(1), location.span)).toEqual({
      start: start - 1,
      end: start + 4
    });
    const documentSpan = { start: 0, length: text.length, end: text.length };
    await readSavedSource({ ...location, span: documentSpan });
    expect(savedEditorSpan(text, text.slice(1), documentSpan)).toEqual({
      start: 0,
      end: text.length - 1
    });
    for (const start of [0, 1])
      expect(savedEditorSpan(text, text.slice(1), { start, length: 0, end: start })).toEqual({
        start: 0,
        end: 0
      });
    expect(() => savedEditorSpan(text, text.replace('\r\n', '\n'), location.span)).toThrow(
      SavedSourceError
    );
    expect(() => savedEditorSpan('abc', 'bc', { start: 1, length: 1, end: 2 })).toThrow(
      SavedSourceError
    );
    for (const start of [text.indexOf('\n'), text.indexOf('🐈') + 1])
      await expect(
        readSavedSource({ ...location, span: { start, length: 0, end: start } })
      ).rejects.toMatchObject({
        code: 'QUERY_SOURCE_SPAN'
      });
  });

  it('rejects changed bytes, invalid UTF-8, NUL and out-of-range raw positions', async () => {
    const changed = fixture(Buffer.from('class Input {}'));
    fs.writeFileSync(path.join(changed.root, changed.relativePath), 'class Other {}');
    await expect(readSavedSource(changed)).rejects.toMatchObject({ code: 'QUERY_SOURCE_CHANGED' });
    for (const bytes of [Buffer.from([0xc0, 0xaf]), Buffer.from('a\0b')])
      await expect(readSavedSource(fixture(bytes))).rejects.toMatchObject({
        code: 'QUERY_SOURCE_ENCODING'
      });
    const outside = fixture(Buffer.from('abc'));
    outside.span = { start: 2, length: 2, end: 4 };
    await expect(readSavedSource(outside)).rejects.toMatchObject({ code: 'QUERY_SOURCE_SPAN' });
    const huge = fixture(Buffer.from('small'));
    fs.truncateSync(path.join(huge.root, huge.relativePath), 64 * 1024 * 1024 + 1);
    await expect(readSavedSource(huge)).rejects.toMatchObject({ code: 'QUERY_SOURCE_LIMIT' });
  });

  it('rejects traversal, absolute paths and linked parents even when content hashes match', async () => {
    const input = fixture(Buffer.from('class Input {}'));
    const outside = fixture(Buffer.from('class Input {}'));
    for (const relativePath of [
      '../Input.cs',
      '/Input.cs',
      'C:/Input.cs',
      'folder/../Input.cs',
      'Input.cs:ads'
    ])
      await expect(readSavedSource({ ...input, relativePath })).rejects.toMatchObject({
        code: 'QUERY_SOURCE_UNAVAILABLE'
      });
    fs.symlinkSync(
      outside.root,
      path.join(input.root, 'link'),
      process.platform === 'win32' ? 'junction' : 'dir'
    );
    await expect(
      readSavedSource({ ...input, relativePath: 'link/Input.cs' })
    ).rejects.toMatchObject({
      code: 'QUERY_SOURCE_LINK'
    });
    // A linked root must not make the same outside file eligible either.
    await expect(
      readSavedSource({ ...input, root: path.join(input.root, 'link') })
    ).rejects.toMatchObject({
      code: 'QUERY_SOURCE_LINK'
    });
  });
});
