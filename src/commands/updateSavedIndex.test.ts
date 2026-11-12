import * as path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import * as vscode from 'vscode';
import { runSavedUpdate, verifySavedUpdateSelection } from '../analyzer/savedUpdateClient';
import { ensureSemanticDotnet } from '../runtime/ensureDotnet';
import { updateSavedIndex } from './updateSavedIndex';

vi.mock('vscode', () => ({
  Uri: { file: (fsPath: string) => ({ scheme: 'file', fsPath }) },
  ProgressLocation: { Notification: 1 },
  workspace: { isTrusted: true, getConfiguration: vi.fn() },
  window: {
    showOpenDialog: vi.fn(),
    showInputBox: vi.fn(),
    showQuickPick: vi.fn(),
    showInformationMessage: vi.fn(),
    withProgress: vi.fn()
  }
}));
vi.mock('../analyzer/savedUpdateClient', async (original) => ({
  ...(await original<typeof import('../analyzer/savedUpdateClient')>()),
  runSavedUpdate: vi.fn(),
  verifySavedUpdateSelection: vi.fn()
}));
vi.mock('../runtime/ensureDotnet', () => ({ ensureSemanticDotnet: vi.fn() }));

afterEach(() => vi.resetAllMocks());

describe('saved update timeout configuration', () => {
  it.each([undefined, 3600])(
    'passes the configured timeout %s through the host command',
    async (timeout) => {
      const root = path.resolve('workspace');
      const cliPath = path.join(root, 'SharpDeps.Cli.dll');
      const target = path.join(root, 'Input.csproj');
      vi.mocked(vscode.workspace.getConfiguration).mockReturnValue({
        get: (name: string, fallback: unknown) =>
          name === 'savedIndexCliPath' ? cliPath : (timeout ?? fallback)
      } as never);
      vi.mocked(vscode.window.showOpenDialog).mockResolvedValue([vscode.Uri.file(target)]);
      vi.mocked(vscode.window.showInputBox)
        .mockResolvedValueOnce('Debug')
        .mockResolvedValueOnce('');
      vi.mocked(vscode.window.showQuickPick).mockResolvedValue(
        'All projects and target frameworks' as never
      );
      vi.mocked(ensureSemanticDotnet).mockResolvedValue({
        dotnetPath: path.join(root, 'dotnet')
      } as never);
      vi.mocked(vscode.window.withProgress).mockImplementation(async (_options, action) =>
        action({ report: vi.fn() }, {
          isCancellationRequested: false,
          onCancellationRequested: () => ({ dispose: vi.fn() })
        } as never)
      );
      const result = { workspaceId: 'workspace', snapshotId: 'snapshot', generation: 1 };
      vi.mocked(runSavedUpdate).mockResolvedValue(result);
      expect(
        await updateSavedIndex({
          root,
          current: () => true,
          stop: new AbortController(),
          output: { appendLine: vi.fn() } as never
        })
      ).toEqual(result);
      expect(verifySavedUpdateSelection).toHaveBeenCalledWith(
        expect.objectContaining({
          timeoutSeconds: timeout ?? 1800
        })
      );
      expect(runSavedUpdate).toHaveBeenCalledWith(
        expect.objectContaining({
          selection: expect.objectContaining({ timeoutSeconds: timeout ?? 1800 })
        })
      );
    }
  );
});
