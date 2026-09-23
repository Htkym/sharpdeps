import * as vscode from 'vscode';
import { spawn } from 'child_process';
import * as os from 'os';
import * as path from 'path';
import * as fs from 'fs';
import type { CodeMapReport } from './types';

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
export function locateAnalyzer(context: vscode.ExtensionContext): AnalyzerLocation {
  const candidates = [
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

export interface RunAnalyzerOptions {
  dotnetPath: string;
  analyzer: AnalyzerLocation;
  analysisTargetPath: string;
  maxProjects: number;
  maxEdges: number;
  token?: vscode.CancellationToken;
}

/** Run the analyzer against a solution/project target and return the parsed report. */
export async function runAnalyzer(options: RunAnalyzerOptions): Promise<CodeMapReport> {
  const workDir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'sharpdeps-'));
  const outputPath = path.join(workDir, 'report.json');

  const flags = [
    '--solution',
    options.analysisTargetPath,
    '--output',
    outputPath,
    '--max-projects',
    String(options.maxProjects),
    '--max-edges',
    String(options.maxEdges)
  ];

  try {
    await runProcess(options.dotnetPath, [options.analyzer.path, ...flags], options.token);
    const json = await fs.promises.readFile(outputPath, 'utf8');
    return JSON.parse(json) as CodeMapReport;
  } finally {
    await fs.promises.rm(workDir, { recursive: true, force: true }).catch(() => undefined);
  }
}

function runProcess(
  command: string,
  args: string[],
  token?: vscode.CancellationToken
): Promise<void> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: path.dirname(args[0] ?? '.') });
    let stdout = '';
    let stderr = '';

    child.stdout.on('data', (chunk) => (stdout += chunk.toString()));
    child.stderr.on('data', (chunk) => (stderr += chunk.toString()));

    const cancellation = token?.onCancellationRequested(() => {
      child.kill();
      reject(new AnalyzerError('Analysis was cancelled.'));
    });

    child.on('error', (err) => {
      cancellation?.dispose();
      reject(new AnalyzerError(`Failed to start the analyzer: ${err.message}`));
    });

    child.on('close', (code) => {
      cancellation?.dispose();
      if (code === 0) {
        resolve();
      } else {
        reject(
          new AnalyzerError(
            `The analyzer exited with code ${code}.`,
            stderr.trim() || stdout.trim() || undefined
          )
        );
      }
    });
  });
}
