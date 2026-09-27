// Browser entry for the new shell (SD-015, host wiring in SD-017).
//
// Used by the webview panel and by the fixture page for verification. It creates the
// app, keeps the CSS pane variables in sync, and translates between protocol v2 messages
// and app actions. Requests are only sent for a registered analysis, and a request is
// only repeated when its input actually changed.

import { PROTOCOL_VERSION } from '../../src/view/protocolV2';
import { createViewerApp, type ViewerApp } from './app';
import { toViewActions, type RequestContext } from './hostMessages';
import { serializeViewState } from './serializer';
import type { ViewAction, ViewState } from './state';

declare function acquireVsCodeApi(): {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
};

declare global {
  interface Window {
    sharpdepsApp?: ViewerApp;
  }
}

const root = document.getElementById('app');
if (root) {
  const host = createWebviewLink(root);
  const requestContext = new Map<string, RequestContext>();
  const requested = { projection: '', details: '', evidence: '', evidencePage: '' };
  let persistTimer: number | undefined;
  let lastPersisted = '';

  function sendRequest(message: Record<string, unknown>, context: RequestContext = {}): void {
    const type = context.type ?? String(message.type);
    for (const [id, existing] of requestContext)
      if (existing.type === type) requestContext.delete(id);
    requestContext.set(String(message.requestId), {
      ...context,
      type,
      analysisId: String(message.analysisId)
    });
    host.post(message);
  }

  const app = createViewerApp(root, {
    workerUrl: root.dataset.workerUri,
    onStateChanged: (state) => {
      applyPaneWidths(state);
      requestForState(state);
      persistSoon(state);
    },
    onHostAction: (action) => {
      if (action.type === 'treeRequested') {
        sendRequest(
          {
            type: 'searchEntities',
            requestId: nextRequestId(),
            analysisId: app.getState().analysisId,
            query: '',
            granularity: action.granularity,
            parentId: action.parentId === 'root' ? undefined : action.parentId,
            cursor: action.cursor,
            limit: 100
          },
          {
            type: `tree:${action.parentId}`,
            treeParentId: action.parentId,
            appendTree: !!action.cursor
          }
        );
        return;
      }
      if (action.type === 'searchStarted') {
        const requestId = nextRequestId();
        sendRequest(
          {
            type: 'searchEntities',
            requestId,
            analysisId: app.getState().analysisId ?? '',
            query: action.query
          },
          { query: action.query }
        );
        return;
      }

      const message = toHostMessage(action, app.getState());
      if (message) {
        host.post(message);
      }
    },
    onExport: (format, data, copy) => {
      const state = app.getState();
      if (!state.analysisId) {
        return;
      }

      host.post({
        type: 'export',
        requestId: nextRequestId(),
        analysisId: state.analysisId,
        format,
        copy,
        scope: state.scope,
        granularity: state.granularity,
        filters: state.filters,
        search: state.search,
        includeIds: state.temporaryDisplayIds,
        data
      });
    },
    onCopyContext: () => {
      const state = app.getState();
      if (!state.analysisId) {
        return;
      }

      host.post({
        type: 'copyContext',
        requestId: nextRequestId(),
        analysisId: state.analysisId,
        scope: state.scope,
        granularity: state.granularity,
        filters: state.filters,
        search: state.search,
        includeIds: state.temporaryDisplayIds
      });
    },
    onOpenDeclaration: (entityId) => {
      const analysisId = app.getState().analysisId;
      if (analysisId)
        host.post({ type: 'openDeclaration', requestId: nextRequestId(), analysisId, entityId });
    },
    onOpenEvidence: (evidenceId) => {
      const state = app.getState();
      if (!state.analysisId) {
        return;
      }

      host.post({
        type: 'openEvidence',
        requestId: nextRequestId(),
        analysisId: state.analysisId,
        evidenceId
      });
    }
  });

  host.subscribe((message) => {
    const payload = message as {
      type?: string;
      requestId?: string;
      format?: 'mermaid' | 'svg' | 'png' | 'json';
      copy?: boolean;
    };
    if (payload?.type === 'requestExport' && payload.format) {
      void app.export(payload.format, payload.copy);
      return;
    }
    if (payload?.type === 'error' && payload.requestId && !requestContext.has(payload.requestId))
      return;
    if (
      payload &&
      ['projection', 'details', 'evidencePage', 'searchResults'].includes(payload.type ?? '') &&
      (!payload.requestId || !requestContext.has(payload.requestId))
    )
      return;
    for (const action of toViewActions(message, requestContext, app.getState())) {
      app.dispatch(action);
    }
    if (payload?.requestId) requestContext.delete(payload.requestId);
  });

  window.sharpdepsApp = app;
  // Restore before the handshake: a webview that hid and came back keeps its small state,
  // and the host's `viewState` message covers a window reload. Neither starts an analysis.
  const stored = host.getState();
  if (stored !== undefined && stored !== null) {
    for (const action of toViewActions({ type: 'viewState', state: stored }, requestContext)) {
      app.dispatch(action);
    }
  }

  // The handshake asks the host for the protocol version, capabilities, and whether an
  // analysis result is already registered.
  host.post({ type: 'ready', protocolVersion: PROTOCOL_VERSION, webviewVersion: '1' });

  function persistSoon(state: ViewState): void {
    if (persistTimer !== undefined) {
      window.clearTimeout(persistTimer);
    }

    persistTimer = window.setTimeout(() => {
      persistTimer = undefined;
      const snapshot = serializeViewState(state, state.camera ?? undefined);
      const serialized = JSON.stringify(snapshot);
      if (serialized === lastPersisted) return;
      lastPersisted = serialized;
      host.setState(snapshot);
      host.post({ type: 'persistViewState', viewState: snapshot });
    }, 300);
  }

  function requestForState(state: ViewState): void {
    const analysisId = state.analysisId;
    if (!analysisId) {
      return;
    }
    const treeKey = `tree:${analysisId}`;
    if (
      !state.tree.root &&
      ![...requestContext.values()].some((context) => context.type === treeKey)
    ) {
      sendRequest(
        {
          type: 'searchEntities',
          requestId: nextRequestId(),
          analysisId,
          query: '',
          granularity: 'project',
          limit: 100
        },
        { type: treeKey, treeParentId: 'root' }
      );
    }

    const projectionKey = [
      analysisId,
      state.granularity,
      state.scope.kind,
      state.scope.id ?? '',
      state.scope.depth ?? 1,
      JSON.stringify(state.filters),
      state.search,
      state.temporaryDisplayIds.join(',')
    ].join('|');
    if (requested.projection !== projectionKey) {
      requested.projection = projectionKey;
      sendRequest({
        type: 'getProjection',
        requestId: nextRequestId(),
        analysisId,
        scope: state.scope,
        granularity: state.granularity,
        filters: state.filters,
        search: state.search,
        includeIds: state.temporaryDisplayIds
      });
    }

    const entityId = state.selection.entityId ?? '';
    if (!entityId) requested.details = '';
    if (entityId && requested.details !== `${analysisId}|${entityId}`) {
      requested.details = `${analysisId}|${entityId}`;
      sendRequest({
        type: 'getEntityDetails',
        requestId: nextRequestId(),
        analysisId,
        entityId
      });
    }

    const relationId = state.selection.relationId ?? '';
    if (!relationId) {
      requested.evidence = '';
      requested.evidencePage = '';
    }
    if (relationId && requested.evidence !== `${analysisId}|${relationId}`) {
      requested.evidence = `${analysisId}|${relationId}`;
      requested.evidencePage = '';
      sendRequest({
        type: 'getEvidencePage',
        requestId: nextRequestId(),
        analysisId,
        relationId
      });
    }

    // Paging: only the explicit request action asks for the next page, so a re-render
    // never loads more evidence on its own.
    const cursor = state.evidence?.nextCursor;
    if (relationId && cursor && state.evidence?.pending) {
      const pageKey = `${analysisId}|${relationId}|${cursor}`;
      if (requested.evidencePage !== pageKey) {
        requested.evidencePage = pageKey;
        const requestId = nextRequestId();
        sendRequest(
          {
            type: 'getEvidencePage',
            requestId,
            analysisId,
            relationId,
            cursor
          },
          { appendEvidence: true }
        );
      }
    }
  }
}

function toHostMessage(action: ViewAction, state: ViewState): Record<string, unknown> | undefined {
  switch (action.type) {
    case 'analyzeStarted':
      return {
        type: 'analyze',
        requestId: nextRequestId(),
        mode: action.mode,
        profile: state.profile
      };
    case 'analysisFailed':
      return {
        type: 'cancelAnalysis',
        requestId: nextRequestId(),
        analysisId: state.runningAnalysisId ?? state.analysisId ?? ''
      };
    default:
      return undefined;
  }
}

let requestSequence = 0;

function nextRequestId(): string {
  requestSequence++;
  const random = Math.floor(Math.random() * 0xffffffff)
    .toString(16)
    .padStart(8, '0');
  return `req_${random}${requestSequence.toString(16).padStart(8, '0')}`;
}

function createWebviewLink(root_: HTMLElement): {
  post(message: unknown): void;
  subscribe(listener: (message: unknown) => void): void;
  getState(): unknown;
  setState(state: unknown): void;
} {
  const api = typeof acquireVsCodeApi === 'function' ? acquireVsCodeApi() : undefined;
  return {
    post(message) {
      if (api) {
        api.postMessage(message);
        return;
      }

      // Fixture without a host: keep the messages for inspection instead of dropping
      // them, so the browser verification can assert the protocol traffic.
      const log = root_.dataset.hostMessages ? `${root_.dataset.hostMessages}\n` : '';
      root_.dataset.hostMessages = `${log}${JSON.stringify(message)}`;
    },
    subscribe(listener) {
      window.addEventListener('message', (event) => listener(event.data));
    },
    getState() {
      if (api) {
        return api.getState();
      }

      try {
        const stored = window.localStorage.getItem('sharpdeps.fixture.viewState');
        return stored ? JSON.parse(stored) : undefined;
      } catch {
        return undefined;
      }
    },
    setState(state) {
      if (api) {
        api.setState(state);
        return;
      }

      try {
        window.localStorage.setItem('sharpdeps.fixture.viewState', JSON.stringify(state));
      } catch {
        // A fixture without storage simply does not persist.
      }
    }
  };
}

function applyPaneWidths(state: ViewState): void {
  root?.style.setProperty('--sd-nav-width', `${state.paneWidths.navigation}px`);
  root?.style.setProperty('--sd-inspector-width', `${state.paneWidths.inspector}px`);
}
