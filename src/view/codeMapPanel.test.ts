import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as vscode from 'vscode';
import { CodeMapPanel, type CodeMapPanelHost } from './codeMapPanel';
import { SavedQueryProvider } from '../analyzer/queryProvider';
import type { QueryWireReply, SavedQueryClient } from '../analyzer/queryClient';
import type { ReportStore } from '../analyzer/reportStore';
import type { SourceReturnUi } from './protocolV2';
import { makeSnapshot, IDS } from '../../tests/helpers/reportV2Fixtures';

const api = vi.hoisted(() => ({
  post: vi.fn(),
  clipboard: vi.fn(),
  writeFile: vi.fn(),
  dialog: vi.fn(),
  info: vi.fn(),
  createPanel: vi.fn(),
  getReport: vi.fn(),
  legacyEvidence: vi.fn(),
  legacyDeclaration: vi.fn()
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
vi.mock('../commands/openLocation', () => ({
  openEvidenceLocation: api.legacyEvidence,
  openDeclarationLocation: api.legacyDeclaration
}));

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
  api.legacyEvidence.mockReset().mockResolvedValue(true);
  api.legacyDeclaration.mockReset().mockResolvedValue(true);
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

it('returns a Quick source through the common queue only when the editor opened it', async () => {
  const physical = physicalPanel();
  api.createPanel.mockReturnValue(physical);
  const report = makeSnapshot({ mode: 'quick', completeness: 'partial' });
  const host: CodeMapPanelHost = {
    store: {
      currentAnalysisId: report.analysisId,
      getReport: () => report,
      isStale: () => false
    } as unknown as ReportStore,
    bridge: {
      handle: vi.fn().mockResolvedValue({
        type: 'capabilities',
        protocolVersion: 2,
        capabilities: report.capabilities
      })
    },
    output: { appendLine: vi.fn() } as never,
    rootDirectory: () => 'D:/fixture',
    targetName: () => 'fixture',
    onAnalyze: vi.fn(),
    onCancel: vi.fn(),
    saveViewState: vi.fn(),
    loadViewState: () => undefined
  };
  CodeMapPanel.show(vscode.Uri.file('D:/extension'), host);
  physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  physical.dispatch({
    type: 'persistViewState',
    viewState: { version: 1, granularity: 'type', selection: { entityId: IDS.typeA } }
  });
  const ui: SourceReturnUi = {
    navTab: 'structure',
    focus: 'declaration',
    id: IDS.typeA,
    scroll: { navigation: 10, inspector: 20, map: 0 }
  };
  physical.dispatch({
    type: 'openDeclaration',
    requestId,
    analysisId: report.analysisId,
    entityId: IDS.typeA,
    returnUi: ui
  });
  await vi.waitFor(() => expect(api.legacyDeclaration).toHaveBeenCalledOnce());
  await Promise.resolve();
  const current = api.legacyDeclaration.mock.calls[0][0].isCurrent as () => boolean;
  expect(current()).toBe(true);
  api.post.mockClear();
  physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  expect(api.post).toHaveBeenCalledWith(
    expect.objectContaining({
      type: 'sourceReturn',
      analysisId: report.analysisId,
      ui,
      state: expect.objectContaining({ granularity: 'type', selection: { entityId: IDS.typeA } })
    })
  );
  expect(api.legacyEvidence).not.toHaveBeenCalled();
  physical.dispatch({ type: 'sourceReturnDismissed', analysisId: report.analysisId });
  expect(current()).toBe(false);
  api.legacyEvidence.mockResolvedValue(false);
  physical.dispatch({
    type: 'openEvidence',
    requestId,
    analysisId: report.analysisId,
    evidenceId: IDS.evidence,
    returnUi: { ...ui, focus: 'evidence', id: IDS.evidence }
  });
  await vi.waitFor(() => expect(api.legacyEvidence).toHaveBeenCalledOnce());
  await Promise.resolve();
  expect(current()).toBe(false);
  api.post.mockClear();
  physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  expect(api.post.mock.calls.some(([message]) => message.type === 'sourceReturn')).toBe(false);
  let finish!: (opened: boolean) => void;
  api.legacyDeclaration.mockReturnValueOnce(
    new Promise<boolean>((resolve) => {
      finish = resolve;
    })
  );
  physical.dispatch({
    type: 'openDeclaration',
    requestId: 'req_3333333333333333',
    analysisId: report.analysisId,
    entityId: IDS.typeA,
    returnUi: ui
  });
  await vi.waitFor(() => expect(api.legacyDeclaration).toHaveBeenCalledTimes(2));
  const pendingCurrent = api.legacyDeclaration.mock.calls[1][0].isCurrent as () => boolean;
  expect(pendingCurrent()).toBe(true);
  physical.dispatch({ type: 'sourceReturnDismissed', analysisId: report.analysisId });
  expect(pendingCurrent()).toBe(false);
  finish(true);
  await Promise.resolve();
  api.post.mockClear();
  physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  expect(
    api.post.mock.calls.some(([message]) => ['sourceReturn', 'reveal'].includes(message.type))
  ).toBe(false);
});

it('restores a saved source selection only on return to the same live analysis', async () => {
  const fixture = await setup('show');
  fixture.host.openSavedSource = vi.fn().mockResolvedValue(undefined);
  const result = await fixture.saved.handle(fixture.projection);
  if (result.type !== 'projection') throw new Error('projection failed');
  const entityId = result.projection.nodes[0].id;
  fixture.physical.dispatch({
    type: 'openDeclaration',
    requestId,
    analysisId: fixture.saved.analysisId,
    entityId
  });
  await vi.waitFor(() => expect(fixture.host.openSavedSource).toHaveBeenCalledOnce());
  await Promise.resolve();
  expect(api.post.mock.calls.some(([message]) => message.type === 'reveal')).toBe(false);
  fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  await vi.waitFor(() =>
    expect(
      api.post.mock.calls.some(
        ([message]) =>
          message.type === 'reveal' &&
          message.entityId === entityId &&
          message.analysisId === fixture.saved.analysisId
      )
    ).toBe(true)
  );
  const messages = api.post.mock.calls.map(([message]) => message);
  expect(messages.findIndex((message) => message.type === 'analysisComplete')).toBeLessThan(
    messages.findIndex((message) => message.type === 'reveal')
  );
  api.post.mockClear();
  fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  expect(api.post.mock.calls.some(([message]) => message.type === 'reveal')).toBe(false);

  fixture.physical.dispatch({
    type: 'openDeclaration',
    requestId,
    analysisId: fixture.saved.analysisId,
    entityId
  });
  await Promise.resolve();
  await Promise.resolve();
  fixture.switchCurrent('an_1111111111111111');
  api.post.mockClear();
  fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  expect(api.post.mock.calls.some(([message]) => message.type === 'reveal')).toBe(false);
});

it('does not queue a source return after a failed saved source open', async () => {
  const fixture = await setup('show');
  fixture.host.openSavedSource = vi.fn().mockRejectedValue(new Error('missing source'));
  const result = await fixture.saved.handle(fixture.projection);
  if (result.type !== 'projection') throw new Error('projection failed');
  fixture.physical.dispatch({
    type: 'openDeclaration',
    requestId,
    analysisId: fixture.saved.analysisId,
    entityId: result.projection.nodes[0].id
  });
  await vi.waitFor(() =>
    expect(
      api.post.mock.calls.some(([message]) => message.code === 'QUERY_SOURCE_OPEN_FAILED')
    ).toBe(true)
  );
  api.post.mockClear();
  fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  expect(api.post.mock.calls.some(([message]) => message.type === 'reveal')).toBe(false);
});

function deferredOpen() {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<void>((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
}

it('returns an evidence source to its saved relation context even when ready precedes completion', async () => {
  const fixture = await setup('show');
  const open = deferredOpen();
  fixture.host.openSavedSource = vi.fn().mockReturnValue(open.promise);
  const relationId = 'rel_1111111111111111';
  const evidenceId = 'ev_1111111111111111';
  const ui: SourceReturnUi = {
    navTab: 'cycles',
    focus: 'evidence',
    id: evidenceId,
    scroll: { navigation: 140, inspector: 680, map: 250 },
    searchCount: 40
  };
  fixture.physical.dispatch({
    type: 'persistViewState',
    viewState: {
      version: 1,
      scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 2 },
      granularity: 'type',
      selection: { relationId },
      search: 'Compute',
      filters: { resultKinds: ['code'], includeExternal: false },
      history: [{ scope: { kind: 'root' }, granularity: 'type', search: 'earlier' }],
      camera: { zoom: 1.5, scrollLeft: 55, scrollTop: 250 }
    }
  });
  fixture.physical.dispatch({
    type: 'openEvidence',
    requestId,
    analysisId: fixture.saved.analysisId,
    evidenceId,
    returnUi: ui
  });
  expect(fixture.host.openSavedSource).toHaveBeenCalledOnce();
  fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  expect(api.post.mock.calls.some(([message]) => message.type === 'sourceReturn')).toBe(false);
  open.resolve();
  await vi.waitFor(() =>
    expect(api.post).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'sourceReturn',
        requestId,
        analysisId: fixture.saved.analysisId,
        ui,
        state: expect.objectContaining({
          scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 2 },
          granularity: 'type',
          selection: { relationId },
          search: 'Compute',
          filters: { resultKinds: ['code'], includeExternal: false },
          history: [expect.objectContaining({ search: 'earlier' })],
          camera: { zoom: 1.5, scrollLeft: 55, scrollTop: 250 }
        })
      })
    )
  );
  const types = api.post.mock.calls.map(([message]) => message.type);
  expect(types.indexOf('analysisComplete')).toBeLessThan(types.indexOf('sourceReturn'));
  fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  expect(api.post.mock.calls.filter(([message]) => message.type === 'sourceReturn')).toHaveLength(
    1
  );
  expect(api.post.mock.calls.some(([message]) => message.type === 'reveal')).toBe(false);
});

it('serializes editor opens, drops the middle waiting request, and returns only the newest selection', async () => {
  const fixture = await setup('show');
  const first = deferredOpen(),
    latest = deferredOpen();
  const opener = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(latest.promise);
  fixture.host.openSavedSource = opener;
  const send = (digit: string) =>
    fixture.physical.dispatch({
      type: 'openDeclaration',
      requestId: `req_${digit.repeat(16)}`,
      analysisId: fixture.saved.analysisId,
      entityId: `ty_${digit.repeat(16)}`
    });
  send('1');
  const firstCurrent = opener.mock.calls[0][1] as () => boolean;
  expect(firstCurrent()).toBe(true);
  send('2');
  send('3');
  expect(firstCurrent()).toBe(false);
  expect(opener).toHaveBeenCalledOnce();
  fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  first.resolve();
  await vi.waitFor(() => expect(opener).toHaveBeenCalledTimes(2));
  expect(opener.mock.calls.map(([request]) => request.entityId)).toEqual([
    'ty_1111111111111111',
    'ty_3333333333333333'
  ]);
  expect(api.post.mock.calls.some(([message]) => message.type === 'reveal')).toBe(false);
  latest.resolve();
  await vi.waitFor(() =>
    expect(api.post).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'reveal',
        entityId: 'ty_3333333333333333'
      })
    )
  );
  expect(api.post.mock.calls.filter(([message]) => message.type === 'reveal')).toHaveLength(1);
});

it('does not resurrect an older successful open when the newest open fails', async () => {
  const fixture = await setup('show');
  const first = deferredOpen(),
    latest = deferredOpen();
  const opener = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(latest.promise);
  fixture.host.openSavedSource = opener;
  const send = (digit: string) =>
    fixture.physical.dispatch({
      type: 'openDeclaration',
      requestId: `req_${digit.repeat(16)}`,
      analysisId: fixture.saved.analysisId,
      entityId: `ty_${digit.repeat(16)}`
    });
  send('1');
  send('2');
  first.resolve();
  await vi.waitFor(() => expect(opener).toHaveBeenCalledTimes(2));
  latest.reject(new Error('missing source'));
  await vi.waitFor(() =>
    expect(api.post).toHaveBeenCalledWith(
      expect.objectContaining({
        code: 'QUERY_SOURCE_OPEN_FAILED',
        requestId: 'req_2222222222222222'
      })
    )
  );
  fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
  expect(
    api.post.mock.calls.some(([message]) => ['reveal', 'sourceReturn'].includes(message.type))
  ).toBe(false);
});

it.each(['analysis replacement', 'disposal', 'navigation dismissal'] as const)(
  'invalidates an in-flight source open on %s',
  async (change) => {
    const fixture = await setup('show');
    const open = deferredOpen();
    const opener = vi.fn().mockReturnValue(open.promise);
    fixture.host.openSavedSource = opener;
    fixture.physical.dispatch({
      type: 'openDeclaration',
      requestId,
      analysisId: fixture.saved.analysisId,
      entityId: 'ty_1111111111111111'
    });
    const current = opener.mock.calls[0][1] as () => boolean;
    if (change === 'analysis replacement') fixture.switchCurrent('an_1111111111111111');
    else if (change === 'navigation dismissal')
      fixture.physical.dispatch({
        type: 'sourceReturnDismissed',
        analysisId: fixture.saved.analysisId
      });
    else fixture.panel.dispose();
    expect(current()).toBe(false);
    api.post.mockClear();
    if (change === 'analysis replacement') open.reject(new Error('old request failed'));
    else open.resolve();
    await open.promise.catch(() => undefined);
    await Promise.resolve();
    fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
    expect(
      api.post.mock.calls.some(([message]) => ['reveal', 'sourceReturn'].includes(message.type))
    ).toBe(false);
    expect(
      api.post.mock.calls.some(([message]) => message.code === 'QUERY_SOURCE_OPEN_FAILED')
    ).toBe(false);
  }
);

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
  it('routes a candidate declaration through the current source queue and returns to the original mention', async () => {
    const fixture = await setup('show');
    const mentionId = 'ty_1111111111111111';
    const originId = 'ci_' + '1'.repeat(32);
    const capabilityId = 'cd_' + '2'.repeat(32);
    const ui: SourceReturnUi = {
      navTab: 'structure',
      focus: 'declaration',
      id: mentionId,
      scroll: { navigation: 10, inspector: 20, map: 30 },
      searchCount: 0
    };
    fixture.physical.dispatch({
      type: 'persistViewState',
      viewState: {
        version: 1,
        selection: { entityId: mentionId },
        scope: { kind: 'root' },
        granularity: 'type'
      }
    });
    let finish!: () => void;
    let isCurrent!: () => boolean;
    fixture.host.openSavedSource = vi.fn(async (_request, current) => {
      isCurrent = current!;
      await new Promise<void>((resolve) => {
        finish = resolve;
      });
    });
    const send = (id: string) =>
      fixture.physical.dispatch({
        type: 'openCandidateDeclaration',
        requestId: id,
        analysisId: fixture.saved.analysisId,
        originId,
        capabilityId,
        returnUi: ui
      });
    send(requestId);
    await vi.waitFor(() => expect(fixture.host.openSavedSource).toHaveBeenCalledOnce());
    expect(isCurrent()).toBe(true);
    fixture.physical.dispatch({
      type: 'sourceReturnDismissed',
      analysisId: fixture.saved.analysisId
    });
    expect(isCurrent()).toBe(false);
    finish();
    await Promise.resolve();
    await Promise.resolve();
    fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
    expect(api.post.mock.calls.some(([message]) => message.type === 'sourceReturn')).toBe(false);
    const openAgain = vi.fn().mockResolvedValue(undefined);
    fixture.host.openSavedSource = openAgain;
    const nextId = 'req_3333333333333333';
    send(nextId);
    await vi.waitFor(() => expect(fixture.host.openSavedSource).toHaveBeenCalledOnce());
    await Promise.resolve();
    fixture.physical.dispatch({ type: 'ready', protocolVersion: 2, webviewVersion: '1' });
    await vi.waitFor(() =>
      expect(
        api.post.mock.calls.some(
          ([message]) =>
            message.type === 'sourceReturn' &&
            message.requestId === nextId &&
            message.ui.id === mentionId
        )
      ).toBe(true)
    );
    expect(openAgain.mock.calls[0][0]).toMatchObject({
      type: 'openCandidateDeclaration',
      originId,
      capabilityId
    });
    expect(api.post.mock.calls.some(([message]) => message.type === 'reveal')).toBe(false);
    expect(api.getReport).not.toHaveBeenCalled();
  });

  it('dismisses only a candidate investigation in the current saved analysis', async () => {
    const fixture = await setup('show');
    fixture.host.dismissCandidateInvestigation = vi.fn();
    const request = {
      type: 'dismissCandidateInvestigation',
      requestId,
      analysisId: fixture.saved.analysisId,
      originId: 'ci_' + '1'.repeat(32)
    };
    fixture.physical.dispatch(request);
    expect(fixture.host.dismissCandidateInvestigation).toHaveBeenCalledWith(request);
    fixture.switchCurrent('an_1111111111111111');
    fixture.physical.dispatch(request);
    fixture.physical.dispatch({ ...request, originId: 'ty_1111111111111111' });
    expect(fixture.host.dismissCandidateInvestigation).toHaveBeenCalledOnce();
    expect(api.getReport).not.toHaveBeenCalled();
  });

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
