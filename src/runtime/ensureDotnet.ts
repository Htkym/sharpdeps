import * as vscode from 'vscode';
import { spawn } from 'child_process';
import * as fs from 'fs';

/** Runtime version that matches the analyzer's target framework (net10.0). */
export const ANALYZER_RUNTIME_VERSION = '10.0';

const DOTNET_DOWNLOAD_URL = 'https://dotnet.microsoft.com/download/dotnet/10.0';

export interface DotnetResolution {
  dotnetPath: string;
  source: 'config' | 'findPath' | 'path' | 'acquired';
}

export class DotnetNotAvailableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'DotnetNotAvailableError';
  }
}

interface AcquireResult {
  dotnetPath?: string;
}

/**
 * Resolve a usable `dotnet` executable, in priority order:
 *   1. The `sharpdeps.dotnetPath` setting.
 *   2. The .NET Install Tool (`dotnet.findPath`).
 *   3. `dotnet` on the PATH.
 *   4. Acquire a private .NET runtime via the .NET Install Tool (`dotnet.acquire`),
 *      which shows the standard download/progress UI.
 * If all of those fail, prompt the user to install .NET manually and throw.
 */
export async function ensureDotnet(context: vscode.ExtensionContext): Promise<DotnetResolution> {
  const configured = vscode.workspace
    .getConfiguration('sharpdeps')
    .get<string>('dotnetPath', '')
    .trim();
  if (configured) {
    if (await pathExists(configured)) {
      return { dotnetPath: configured, source: 'config' };
    }
    throw new DotnetNotAvailableError(
      `The configured sharpdeps.dotnetPath does not exist: ${configured}`
    );
  }

  const extensionId = context.extension.id;

  const found = await tryFindPath(extensionId);
  if (found) {
    return { dotnetPath: found, source: 'findPath' };
  }

  const onPath = await dotnetOnPath();
  if (onPath) {
    return { dotnetPath: onPath, source: 'path' };
  }

  try {
    const result = await vscode.commands.executeCommand<AcquireResult>('dotnet.acquire', {
      version: ANALYZER_RUNTIME_VERSION,
      requestingExtensionId: extensionId,
      mode: 'runtime'
    });
    if (result?.dotnetPath && (await pathExists(result.dotnetPath))) {
      return { dotnetPath: result.dotnetPath, source: 'acquired' };
    }
  } catch {
    // Fall through to the manual-install prompt.
  }

  await promptManualInstall();
  throw new DotnetNotAvailableError(
    'No usable .NET runtime was found and automatic acquisition was unsuccessful.'
  );
}

async function tryFindPath(extensionId: string): Promise<string | undefined> {
  // The .NET Install Tool API shape has changed across versions; try the newer
  // context object first, then the older flat form. Both are best-effort.
  const attempts: unknown[] = [
    {
      acquireContext: {
        version: ANALYZER_RUNTIME_VERSION,
        requestingExtensionId: extensionId,
        mode: 'runtime',
        architecture: process.arch
      },
      versionSpecRequirement: 'greater_than_or_equal'
    },
    { version: ANALYZER_RUNTIME_VERSION, requestingExtensionId: extensionId, mode: 'runtime' }
  ];

  for (const arg of attempts) {
    try {
      const res = await vscode.commands.executeCommand<
        { dotnetPath?: string } | string | undefined
      >('dotnet.findPath', arg);
      const candidate = typeof res === 'string' ? res : res?.dotnetPath;
      if (candidate && (await pathExists(candidate))) {
        return candidate;
      }
    } catch {
      // Try the next shape, or give up.
    }
  }
  return undefined;
}

function pathExists(target: string): Promise<boolean> {
  return fs.promises
    .access(target)
    .then(() => true)
    .catch(() => false);
}

function dotnetOnPath(): Promise<string | undefined> {
  return new Promise((resolve) => {
    const command = process.platform === 'win32' ? 'where' : 'which';
    const child = spawn(command, ['dotnet']);
    let stdout = '';
    child.stdout.on('data', (chunk) => (stdout += chunk.toString()));
    child.on('error', () => resolve(undefined));
    child.on('close', (code) => {
      if (code !== 0) {
        resolve(undefined);
        return;
      }
      const first = stdout
        .split(/\r?\n/)
        .map((line) => line.trim())
        .filter(Boolean)[0];
      resolve(first || 'dotnet');
    });
  });
}

async function promptManualInstall(): Promise<void> {
  const choice = await vscode.window.showWarningMessage(
    'SharpDeps needs the .NET runtime to analyze solutions, but none was found.',
    'Download .NET',
    'Open Settings'
  );
  if (choice === 'Download .NET') {
    await vscode.env.openExternal(vscode.Uri.parse(DOTNET_DOWNLOAD_URL));
  } else if (choice === 'Open Settings') {
    await vscode.commands.executeCommand('workbench.action.openSettings', 'sharpdeps.dotnetPath');
  }
}
