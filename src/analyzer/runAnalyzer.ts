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
  /** 'dll' runs a precompiled DLL with any runtime; 'source' runs the .cs via the SDK (dev fallback). */
  mode: 'dll' | 'source';
  path: string;
}

/**
 * Locate the analyzer. Prefer the precompiled DLL (analyzer/bin/code-map.dll, shipped in the VSIX);
 * fall back to the .cs source for development before `npm run build:analyzer` has been run.
 */
export function locateAnalyzer(context: vscode.ExtensionContext): AnalyzerLocation {
  const dll = path.join(context.extensionUri.fsPath, 'analyzer', 'bin', 'code-map.dll');
  if (fs.existsSync(dll)) {
    return { mode: 'dll', path: dll };
  }
  return {
    mode: 'source',
    path: path.join(context.extensionUri.fsPath, 'analyzer', 'code-map.cs')
  };
}

export interface RunAnalyzerOptions {
  dotnetPath: string;
  analyzer: AnalyzerLocation;
  solutionPath: string;
  maxProjects: number;
  maxEdges: number;
  token?: vscode.CancellationToken;
}

/** Run the analyzer against a solution and return the parsed report. */
export async function runAnalyzer(options: RunAnalyzerOptions): Promise<CodeMapReport> {
  const workDir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'sharpdeps-'));
  const outputPath = path.join(workDir, 'report.json');

  const flags = [
    '--solution',
    options.solutionPath,
    '--output',
    outputPath,
    '--max-projects',
    String(options.maxProjects),
    '--max-edges',
    String(options.maxEdges)
  ];

  const args =
    options.analyzer.mode === 'dll'
      ? [options.analyzer.path, ...flags]
      : ['run', options.analyzer.path, '--', ...flags];

  try {
    await runProcess(options.dotnetPath, args, path.dirname(options.analyzer.path), options.token);
    const json = await fs.promises.readFile(outputPath, 'utf8');
    return JSON.parse(json) as CodeMapReport;
  } finally {
    await fs.promises.rm(workDir, { recursive: true, force: true }).catch(() => undefined);
  }
}

function runProcess(
  command: string,
  args: string[],
  cwd: string,
  token?: vscode.CancellationToken
): Promise<void> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd });
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
