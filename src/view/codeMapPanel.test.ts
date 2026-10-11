import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as vscode from 'vscode';
import { CodeMapPanel, type CodeMapPanelHost } from './codeMapPanel';
import { SavedQueryProvider } from '../analyzer/queryProvider';
import type { QueryWireReply, SavedQueryClient } from '../analyzer/queryClient';
import type { ReportStore } from '../analyzer/reportStore';

const api = vi.hoisted(() => ({
  post: vi.fn(),
  clipboard: vi.fn(),
  writeFile: vi.fn(),
  dialog: vi.fn(),
  info: vi.fn(),
  createPanel: vi.fn(),
  getReport: vi.fn()
}));
vi.mock('vscode', () => ({
  Uri: {
    file: (fsPath: string) => ({ scheme: 'file', authority: '', fsPath }),
    joinPath: () => ({ scheme: 'file', authority: '', fsPath: 'D:/fixture/media' })
  },
  env: { clipboard: { writeText: api.clipboard } },
  workspace: { fs: { writeFile: api.writeFile } },
  ViewColumn: { One: 1 },
  window: {
    showSaveDialog: api.dialog,
    showInformationMessage: api.info,
    createWebviewPanel: api.createPanel
  },
  commands: { executeCommand: vi.fn() }
}));
vi.mock('./html', () => ({ getWebviewHtml: () => '<div id="app"></div>' }));

const projectionRequestId = 'req_1111111111111111';
const requestId = 'req_2222222222222222';
const text = '日本🧪\r\n"quoted"';
const snapshot = {
  workspaceId: 'workspace',
  id: 'snapshot',
  generation: 3,
  variantIds: ['variant'],
  variantCount: 1,
  coverage: 'Partial' as const,
  freshness: 'unverified'
};
const reply = (): QueryWireReply => ({
  apiVersion: '1',
  requestId: 'wire',
  snapshot: { ...snapshot },
  items: [
    {
      id: 'raw-member',
      kind: 'Member',
      label: text,
      certainty: 'Candidate',
      node: { id: 'raw-member', kind: 'Member', name: text }
    }
  ],
  candidates: [],
  unresolved: [],
  truncated: true,
  truncationReasons: ['PAGE_LIMIT'],
  diagnostics: [],
  errors: []
});
let provider: SavedQueryProvider | undefined;
beforeEach(() => {
  vi.clearAllMocks();
  api.dialog.mockReset();
  api.createPanel.mockReset();
  api.getReport.mockImplementation(() => {
    throw new Error('legacy store must not be read');
  });
});
afterEach(() => {
  CodeMapPanel.currentPanel?.dispose();
  provider?.dispose();
  provider = undefined;
});

function physicalPanel() {
  let disposed = false;
  let onDispose: (() => void) | undefined;
  let receive: ((message: unknown) => void) | undefined;
  const panel = {
    active: true,
    visible: true,
    reveal: vi.fn(),
    dispose: vi.fn(() => {
      if (disposed) return;
      disposed = true;
      onDispose?.();
    }),
    onDidDispose: (listener: () => void) => {
      onDispose = listener;
    },
    onDidChangeViewState: vi.fn(),
    webview: {
      postMessage: api.post,
      onDidReceiveMessage: (listener: (message: unknown) => void) => {
        receive = listener;
      }
    },
    dispatch: (message: unknown) => receive!(message)
  };
  return panel;
}

async function setup(first: 'show' | 'restore' = 'restore') {
  const requests: Record<string, unknown>[] = [];
  const wire: SavedQueryClient = {
    ready: Promise.resolve({ ...reply(), items: [] }),
    async request(request) {
      requests.push(request);
      return { ...reply(), requestId: String(request.requestId) };
    },
    dispose() {}
  };
  const saved = await SavedQueryProvider.create(wire, 'D:/fixture');
  provider = saved;
  const projection = {
    type: 'getProjection' as const,
    requestId: projectionRequestId,
    analysisId: saved.analysisId,
    scope: { kind: 'root' as const },
    granularity: 'type' as const,
    filters: {},
    search: '',
    includeIds: []
  };
  await saved.handle(projection);
  let currentId: string | undefined = saved.analysisId;
  const host: CodeMapPanelHost = {
    store: { getReport: api.getReport } as unknown as ReportStore,
    bridge: saved,
    output: { appendLine: vi.fn() } as never,
    rootDirectory: () => 'D:/fixture',
    targetName: () => 'fixture',
    saveViewState: vi.fn(),
    loadViewState: () => undefined,
    onAnalyze: vi.fn(),
    onCancel: vi.fn(),
    currentAnalysisId: () => currentId,
    savedAnalysisState: (id) =>
      id === saved.analysisId ? saved.analysisState('fixture') : undefined,
    prepareSavedExport: (request) => saved.prepareSavedExportFor(request),
    onDispose: vi.fn(() => saved.dispose())
  };
  const physical = physicalPanel();
  const uri = vscode.Uri.file('D:/fixture/extension');
  if (first === 'show') api.createPanel.mockReturnValueOnce(physical);
  const panel =
    first === 'show'
      ? CodeMapPanel.show(uri, host)
      : CodeMapPanel.restore(physical as unknown as vscode.WebviewPanel, uri, host);
  const send = (changes: Record<string, unknown> = {}) =>
    physical.dispatch({
      type: 'export',
      requestId,
      analysisId: saved.analysisId,
      format: 'json',
      scope: projection.scope,
      granularity: projection.granularity,
      filters: {},
      search: '',
      includeIds: [],
      projectionRequestId,
      ...changes
    });
  return {
    saved,
    projection,
    requests,
    panel,
    physical,
    host,
    send,
    switchCurrent: (id: string) => {
      currentId = id;
    }
  };
}

describe('saved output through validated panel messages (stubbed local APIs)', () => {
  it('copies context/JSON and writes UTF8 JSON from the host cache without another Query/store read', async () => {
    const { send, requests } = await setup();
    send({ type: 'copyContext', format: undefined });
    await vi.waitFor(() => expect(api.clipboard).toHaveBeenCalledTimes(1));
    expect(api.clipboard.mock.calls[0][0]).toContain('sharpdeps.saved-query-export');
    send({ copy: true });
    await vi.waitFor(() => expect(api.clipboard).toHaveBeenCalledTimes(2));
    const copied = JSON.parse(api.clipboard.mock.calls[1][0]);
    expect(copied.responses[0].items[0]).toMatchObject({
      id: 'raw-member',
      label: text,
      certainty: 'Candidate'
    });
    expect(copied.snapshot).toEqual(snapshot);
    expect(copied.responses[0]).toMatchObject({
      truncated: true,
      returnedCount: 1,
      totalKind: 'returned'
    });
    const uri = vscode.Uri.file('D:/fixture/output.json');
    api.dialog.mockResolvedValueOnce(uri);
    send();
    await vi.waitFor(() => expect(api.writeFile).toHaveBeenCalledOnce());
    expect(api.writeFile.mock.calls[0][0]).toBe(uri);
    expect(JSON.parse(Buffer.from(api.writeFile.mock.calls[0][1]).toString('utf8'))).toEqual(
      copied
    );
    expect(api.getReport).not.toHaveBeenCalled();
    expect(requests).toHaveLength(1);
  });

  it.each(['projection', 'input', 'currentSession', 'dispose'] as const)(
    'refuses output after %s changes while the save dialog is open',
    async (change) => {
      const { send, saved, projection, switchCurrent, panel } = await setup();
      let finish!: (uri: vscode.Uri) => void;
      api.dialog.mockReturnValueOnce(
        new Promise<vscode.Uri>((resolve) => {
          finish = resolve;
        })
      );
      send();
      expect(api.dialog).toHaveBeenCalledOnce();
      if (change === 'projection')
        await saved.handle({ ...projection, requestId: 'req_3333333333333333' });
      if (change === 'input') saved.markStale('savedChange');
      if (change === 'currentSession') switchCurrent('an_4444444444444444');
      if (change === 'dispose') panel.dispose();
      finish(vscode.Uri.file('D:/fixture/output.json'));
      await vi.waitFor(() =>
        expect(api.post).toHaveBeenCalledWith(
          expect.objectContaining({
            type: 'error',
            requestId,
            analysisId: saved.analysisId,
            code:
              change === 'projection'
                ? 'QUERY_EXPORT_SELECTION_CHANGED'
                : change === 'input'
                  ? 'QUERY_EXPORT_INPUT_CHANGED'
                  : 'QUERY_ANALYSIS_EXPIRED'
          })
        )
      );
      expect(api.writeFile).not.toHaveBeenCalled();
      expect(api.clipboard).not.toHaveBeenCalled();
    }
  );

  it('rejects foreign/unsupported/forged requests before any output or legacy fallback', async () => {
    const { send } = await setup();
    for (const changed of [
      { analysisId: 'an_4444444444444444' },
      { projectionRequestId: 'req_3333333333333333' },
      { search: 'different' },
      { format: 'svg', data: '<svg/>' },
      { data: '{}' },
      { type: 'copyContext', format: undefined, includeSnippets: true }
    ]) {
      api.post.mockClear();
      send(changed);
      expect(api.post).toHaveBeenCalledWith(expect.objectContaining({ type: 'error', requestId }));
    }
    expect(api.dialog).not.toHaveBeenCalled();
    expect(api.writeFile).not.toHaveBeenCalled();
    expect(api.clipboard).not.toHaveBeenCalled();
    expect(api.getReport).not.toHaveBeenCalled();
  });

  it('does not write on cancellation or to remote destinations; legacy SVG keeps its existing route', async () => {
    const { send, switchCurrent } = await setup();
    for (const uri of [
      undefined,
      { scheme: 'vscode-remote', authority: 'ssh-remote+host', fsPath: '/output.json' },
      { scheme: 'file', authority: 'server', fsPath: String.raw`\\server\share\output.json` }
    ]) {
      api.post.mockClear();
      api.dialog.mockResolvedValueOnce(uri);
      send();
      await vi.waitFor(() => expect(api.dialog).toHaveBeenCalled());
      await Promise.resolve();
      if (uri)
        expect(api.post).toHaveBeenCalledWith(
          expect.objectContaining({ code: 'QUERY_EXPORT_LOCAL_FILE_REQUIRED' })
        );
      expect(api.writeFile).not.toHaveBeenCalled();
      api.dialog.mockClear();
    }
    const legacyId = 'an_4444444444444444';
    switchCurrent(legacyId);
    api.dialog.mockResolvedValueOnce(vscode.Uri.file('D:/fixture/legacy.svg'));
    send({
      analysisId: legacyId,
      format: 'svg',
      data: '<svg>legacy</svg>',
      projectionRequestId: undefined
    });
    await vi.waitFor(() => expect(api.writeFile).toHaveBeenCalledOnce());
    expect(Buffer.from(api.writeFile.mock.calls[0][1]).toString('utf8')).toBe('<svg>legacy</svg>');
  });
});

describe('panel ownership with a delayed serializer (stubbed VSCode panel events)', () => {
  it.each(['show', 'restore'] as const)(
    'keeps one owner and a usable saved export when %s arrives first',
    async (first) => {
      const { panel, physical, host, send, requests } = await setup(first);
      const uri = vscode.Uri.file('D:/fixture/extension');
      if (first === 'show') {
        const oldRestoredTab = physicalPanel();
        CodeMapPanel.restore(oldRestoredTab as unknown as vscode.WebviewPanel, uri, host);
        // A tab restored late from the previous session must not own this live provider.
        expect(oldRestoredTab.dispose).toHaveBeenCalledOnce();
        oldRestoredTab.dispose();
      } else {
        expect(CodeMapPanel.show(uri, host)).toBe(panel);
      }
      expect(host.onDispose).not.toHaveBeenCalled();
      expect(CodeMapPanel.currentPanel).toBe(panel);
      expect(physical.dispose).not.toHaveBeenCalled();
      send({ type: 'copyContext', format: undefined });
      await vi.waitFor(() => expect(api.clipboard).toHaveBeenCalledOnce());
      expect(api.clipboard.mock.calls[0][0]).toContain('sharpdeps.saved-query-export');
      expect(requests).toHaveLength(1);
      expect(api.getReport).not.toHaveBeenCalled();
      panel.dispose();
      expect(host.onDispose).toHaveBeenCalledOnce();
      expect(CodeMapPanel.currentPanel).toBeUndefined();
    }
  );
});
