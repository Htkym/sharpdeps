import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { affectsSavedInput } from './savedInputChanges';

describe('saved input notifications', () => {
  const root = path.resolve('fixture-workspace');
  it('includes Markdown and additional files, while excluding index and build outputs', () => {
    for (const file of [
      'guide.md',
      'guide.markdown',
      'view.mdx',
      'Service.cs',
      'data/additional.dat'
    ])
      expect(affectsSavedInput(root, path.join(root, file))).toBe(true);
    for (const file of [
      '.sharpdeps/index.sqlite-wal',
      'bin/out.md',
      'obj/Generated.cs',
      '.git/index',
      'node_modules/pkg/file.md'
    ])
      expect(affectsSavedInput(root, path.join(root, file))).toBe(false);
    expect(affectsSavedInput(root, root)).toBe(true);
  });
  it('accepts only ancestor configuration outside the selected root', () => {
    expect(affectsSavedInput(root, path.join(path.dirname(root), 'global.json'))).toBe(true);
    expect(affectsSavedInput(root, path.join(path.dirname(root), 'nuget.config'))).toBe(true);
    expect(affectsSavedInput(root, path.join(path.dirname(root), 'directory.build.props'))).toBe(
      process.platform === 'win32'
    );
    for (const file of [
      path.join(path.dirname(root), 'other.md'),
      path.join(root + '-sibling', 'Directory.Build.props')
    ])
      expect(affectsSavedInput(root, file)).toBe(false);
  });
});
