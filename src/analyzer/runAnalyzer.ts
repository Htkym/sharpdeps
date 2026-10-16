import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';

export class AnalyzerError extends Error {
  constructor(
    message: string,
    public readonly detail?: string
  ) {
    super(message);
    this.name = 'AnalyzerError';
  }
}

export interface AnalyzerLocation {
  path: string;
}

/** Accepts the legacy Quick binary layout; source execution is never a fallback. */
export function locateAnalyzer(
  context: vscode.ExtensionContext,
  mode: 'quick' | 'semantic' = 'quick'
): AnalyzerLocation {
  const candidates =
    mode === 'semantic'
      ? [
          path.join(
            context.extensionUri.fsPath,
            'analyzer',
            'bin',
            'semantic',
            'sharpdeps-semantic-host.dll'
          )
        ]
      : [
          path.join(context.extensionUri.fsPath, 'analyzer', 'bin', 'quick', 'code-map.dll'),
          path.join(context.extensionUri.fsPath, 'analyzer', 'bin', 'code-map.dll')
        ];
  const found = candidates.find((candidate) => fs.existsSync(candidate));
  if (!found) {
    const name = mode === 'semantic' ? 'Semantic' : 'Quick';
    const expectedPath = path
      .relative(context.extensionUri.fsPath, candidates[0])
      .replace(/\\/g, '/');
    throw new AnalyzerError(
      `The ${name} analyzer was not found in this installation (${expectedPath}).`,
      'Reinstall SharpDeps. When running from source, execute `npm run build:analyzer` first.'
    );
  }
  return { path: found };
}
