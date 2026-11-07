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
const sourceText = '// 日本語 🐈\r\nclass Input {}\r\n';
const encodings = [
  { name: 'UTF-8', bytes: Buffer.from(sourceText), bom: false },
  { name: 'UTF-8 BOM', bytes: Buffer.from('\ufeff' + sourceText), bom: true },
  { name: 'UTF-16 LE', bytes: Buffer.from('\ufeff' + sourceText, 'utf16le'), bom: true },
  { name: 'UTF-16 BE', bytes: Buffer.from('\ufeff' + sourceText, 'utf16le').swap16(), bom: true }
];

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
  it.each(encodings)(
    'keeps $name raw coordinates and converts only its editor BOM',
    async ({ bytes, bom }) => {
      const text = (bom ? '\ufeff' : '') + sourceText;
      const offset = bom ? 1 : 0;
      const location = fixture(bytes);
      const start = text.indexOf('Input');
      location.span = { start, length: 5, end: start + 5 };
      const source = await readSavedSource(location);
      expect(source.text).toBe(text);
      expect(savedEditorSpan(source.text, text, location.span)).toEqual({ start, end: start + 5 });
      expect(savedEditorSpan(source.text, sourceText, location.span)).toEqual({
        start: start - offset,
        end: start + 5 - offset
      });
      const documentSpan = { start: 0, length: text.length, end: text.length };
      await readSavedSource({ ...location, span: documentSpan });
      expect(savedEditorSpan(text, sourceText, documentSpan)).toEqual({
        start: 0,
        end: sourceText.length
      });
      if (bom)
        for (const start of [0, 1])
          expect(savedEditorSpan(text, sourceText, { start, length: 0, end: start })).toEqual({
            start: 0,
            end: 0
          });
      for (const editorText of [text.replace('\r\n', '\n'), '\ufeff' + text, sourceText.slice(1)])
        expect(() => savedEditorSpan(text, editorText, location.span)).toThrow(SavedSourceError);
      for (const start of [text.indexOf('\n'), text.indexOf('🐈') + 1])
        await expect(
          readSavedSource({ ...location, span: { start, length: 0, end: start } })
        ).rejects.toMatchObject({
          code: 'QUERY_SOURCE_SPAN'
        });
    }
  );

  it('rejects changed bytes before decoding, malformed text, NUL and out-of-range raw positions', async () => {
    const changed = fixture(Buffer.from('class Input {}'));
    fs.writeFileSync(
      path.join(changed.root, changed.relativePath),
      Buffer.from([0xff, 0xfe, 0x61])
    );
    await expect(readSavedSource(changed)).rejects.toMatchObject({ code: 'QUERY_SOURCE_CHANGED' });
    for (const bytes of [
      Buffer.from([0xc0, 0xaf]),
      Buffer.from([0xed, 0xa0, 0x80]),
      Buffer.from('a\0b'),
      Buffer.from([0xff, 0xfe, 0x61]),
      Buffer.from([0xfe, 0xff, 0x00]),
      Buffer.from([0xff, 0xfe, 0x00, 0xd8, 0x41, 0x00]),
      Buffer.from([0xfe, 0xff, 0xdc, 0x00]),
      Buffer.from('\ufeffa\0b', 'utf16le'),
      Buffer.from('\ufeffa\0b', 'utf16le').swap16()
    ])
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
    const bytes = Buffer.from('\ufeffclass 日本語 {}', 'utf16le');
    const input = fixture(bytes);
    const outside = fixture(bytes);
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
