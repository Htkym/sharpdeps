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

/**
 * Locate the published Quick analyzer. The VSIX ships
 * `analyzer/bin/quick/code-map.dll`; `analyzer/bin/code-map.dll` is accepted so a
 * v0.0.4-era layout keeps working. There is no source fallback: the file-based
 * analyzer was replaced by the QuickHost project in SD-005.
 */
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
    throw new AnalyzerError(
      'The Quick analyzer was not found in this installation (analyzer/bin/quick/code-map.dll).',
      'Reinstall SharpDeps. When running from source, execute `npm run build:analyzer` first.'
    );
  }
  return { path: found };
}
