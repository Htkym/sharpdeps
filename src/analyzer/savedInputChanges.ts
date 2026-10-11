import * as path from 'node:path';
import { isInsideRoot } from '../security/paths';

const CONFIGURATION_FILES = new Set(
  [
    'global.json',
    'Directory.Build.props',
    'Directory.Build.targets',
    'Directory.Packages.props',
    'NuGet.Config',
    'nuget.config'
  ].map((name) => (process.platform === 'win32' ? name.toLowerCase() : name))
);

/** An input hint, not a proof that a pinned saved generation is fresh. */
export function affectsSavedInput(root: string, file: string): boolean {
  const relative = path.relative(root, file);
  if (isInsideRoot(root, relative)) {
    // Match the harness output exclusions; additional files and Markdown are inputs too.
    return !relative
      .split(/[\\/]/)
      .some((part) =>
        ['.sharpdeps', '.git', 'bin', 'obj', 'node_modules'].includes(
          process.platform === 'win32' ? part.toLowerCase() : part
        )
      );
  }
  const directory = path.dirname(file);
  const name = path.basename(file);
  return (
    CONFIGURATION_FILES.has(process.platform === 'win32' ? name.toLowerCase() : name) &&
    isInsideRoot(directory, path.relative(directory, root))
  );
}
