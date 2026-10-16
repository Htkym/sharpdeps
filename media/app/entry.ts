//
// Used by the webview panel and by the fixture page for verification. It creates the
// app, keeps the CSS pane variables in sync, and translates between protocol v2 messages
// and app actions. Requests are only sent for a registered analysis, and a request is
// only repeated when its input actually changed.

import { PROTOCOL_VERSION } from '../../src/view/protocolV2';
import { createViewerApp, type ViewerApp } from './app';
import {
  toViewActions,
  evidenceRequestContext,
  synchronizeSourceOperation,
  projectionRequestKey,
  searchScopeKey,
  type RequestContext,
  type RequestTracking
} from './hostMessages';
import { serializeViewState } from './serializer';
import type { ViewAction, ViewState } from './state';
import { candidateContextKey } from './state';

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
  const requested: RequestTracking = {
    projection: '',
    details: '',
    evidence: '',
    evidencePage: ''
  };
  let persistTimer: number | undefined;
  let lastPersisted = '';
  let latestSourceOperation: { analysisId: string; requestId: string } | undefined;

  function sendRequest(message: Record<string, unknown>, context: RequestContext = {}): void {
    const type = context.type ?? String(message.type);
    for (const [id, existing] of requestContext)
      if (existing.type === type) requestContext.delete(id);
    requestContext.set(String(message.requestId), {
      ...context,
      type,
      analysisId: String(message.analysisId),
      ...(app.getState().queryMetadata &&
      ((message.type === 'searchEntities' && !context.treeParentId) ||
        message.type === 'getProjection')
        ? { scopeKey: searchScopeKey(app.getState()) }
        : {}),
      ...(['searchEntities', 'getProjection', 'getEntityDetails'].includes(String(message.type))
        ? { filtersKey: JSON.stringify(message.filters ?? {}) }
        : {})
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
            limit: 100,
            filters: app.getState().filters
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
        if (app.getState().queryMetadata) return; // Saved lists follow the current navigation key.
        const requestId = nextRequestId();
        sendRequest(
          {
            type: 'searchEntities',
            requestId,
            analysisId: app.getState().analysisId ?? '',
            query: action.query,
            filters: app.getState().filters,
            ...(app.getState().queryMetadata ? { limit: 20 } : {})
          },
          { query: action.query }
        );
        return;
      }
      if (action.type === 'searchPageRequested') {
        const state = app.getState();
        if (state.queryMetadata && state.searchResults.nextCursor)
          sendRequest(
            {
              type: 'searchEntities',
              requestId: nextRequestId(),
              analysisId: state.analysisId,
              query: state.search,
              ...savedListScope(state),
              filters: state.filters,
              cursor: state.searchResults.nextCursor,
              limit: 20
            },
            { query: state.search, appendSearch: true }
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

      sendRequest({
        type: 'export',
        requestId: nextRequestId(),
        analysisId: state.analysisId,
        format,
        copy,
        scope: state.scope,
        projectionRequestId: state.projectionRequestId,
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

      sendRequest({
        type: 'copyContext',
        requestId: nextRequestId(),
        analysisId: state.analysisId,
        scope: state.scope,
        projectionRequestId: state.projectionRequestId,
        granularity: state.granularity,
        filters: state.filters,
        search: state.search,
        includeIds: state.temporaryDisplayIds
      });
    },
    onOpenDeclaration: (entityId) => {
      const analysisId = app.getState().analysisId;
      if (analysisId) {
        const returnUi = app.captureSourceReturn('declaration', entityId);
        const requestId = nextRequestId();
        latestSourceOperation = { analysisId, requestId };
        persistNow(app.getState());
        sendRequest({
          type: 'openDeclaration',
          requestId,
          analysisId,
          entityId,
          returnUi
        });
      }
    },
    onInvestigateCandidate: (originId) => {
      const state = app.getState();
      if (!state.analysisId || state.candidateInvestigation?.originId !== originId) return;
      sendRequest(
        {
          type: 'investigateCandidate',
          requestId: nextRequestId(),
          analysisId: state.analysisId,
          originId
        },
        {
          type: 'investigateCandidate',
          candidateOriginId: originId,
          candidateContextKey: candidateContextKey(state),
          filtersKey: JSON.stringify(state.filters)
        }
      );
    },
    onDismissCandidateInvestigation: (originId, analysisId) => {
      if (analysisId)
        sendRequest({
          type: 'dismissCandidateInvestigation',
          requestId: nextRequestId(),
          analysisId,
          originId
        });
    },
    onOpenCandidateDeclaration: (originId, capabilityId) => {
      const state = app.getState();
      const response = state.candidateInvestigation?.response;
      if (
        !state.analysisId ||
        !state.selection.entityId ||
        response?.originId !== originId ||
        !response.declarations.some((row) => row.capabilityId === capabilityId)
      )
        return;
      const returnUi = app.captureSourceReturn('declaration', state.selection.entityId);
      const requestId = nextRequestId();
      latestSourceOperation = { analysisId: state.analysisId, requestId };
      persistNow(state);
      sendRequest({
        type: 'openCandidateDeclaration',
        requestId,
        analysisId: state.analysisId,
        originId,
        capabilityId,
        returnUi
      });
    },
    onSourceReturnDismissed: () => {
      const state = app.getState();
      const previous = latestSourceOperation;
      latestSourceOperation = undefined;
      persistNow(state);
      const analysisId = state.queryMetadata ? state.analysisId : previous?.analysisId;
      if (analysisId) host.post({ type: 'sourceReturnDismissed', analysisId });
    },
    onOpenEvidence: (evidenceId) => {
      const state = app.getState();
      if (!state.analysisId) {
        return;
      }

      const returnUi = app.captureSourceReturn('evidence', evidenceId);
      const requestId = nextRequestId();
      latestSourceOperation = { analysisId: state.analysisId, requestId };
      persistNow(app.getState());

      sendRequest({
        type: 'openEvidence',
        requestId,
        analysisId: state.analysisId,
        evidenceId,
        returnUi
      });
    }
  });

  host.subscribe((message) => {
    const payload = message as {
      type?: string;
      requestId?: string;
      format?: 'mermaid' | 'svg' | 'png' | 'json';
      copy?: boolean;
      analysisId?: string;
    };
    if (
      payload.type === 'sourceReturn' &&
      (!latestSourceOperation ||
        payload.requestId !== latestSourceOperation.requestId ||
        payload.analysisId !== latestSourceOperation.analysisId)
    )
      return;
    if (payload?.type === 'requestExport' && payload.format) {
      void app.export(payload.format, payload.copy);
      return;
    }
    if (payload?.type === 'error' && payload.requestId && !requestContext.has(payload.requestId))
      return;
    if (
      payload &&
      ['projection', 'details', 'evidencePage', 'searchResults', 'candidateInvestigation'].includes(
        payload.type ?? ''
      ) &&
      (!payload.requestId || !requestContext.has(payload.requestId))
    )
      return;
    const actions = toViewActions(message, requestContext, app.getState());
    const sourceReturn = actions.some((action) => action.type === 'sourceReturned');
    if (sourceReturn) {
      if (!app.acceptsSourceReturn()) return;
      latestSourceOperation = undefined;
      requestContext.clear();
      requested.projection = requested.details = requested.evidence = requested.evidencePage = '';
      requested.search = '';
    }
    for (const action of actions) {
      app.dispatch(action);
    }
    if (sourceReturn) persistNow(app.getState());
    if (sourceReturn && app.getState().search && !app.getState().queryMetadata) {
      const state = app.getState();
      app.dispatch({ type: 'searchStarted', query: state.search });
      sendRequest(
        {
          type: 'searchEntities',
          requestId: nextRequestId(),
          analysisId: state.analysisId,
          query: state.search,
          filters: state.filters,
          limit: 20
        },
        { query: state.search }
      );
    }
    const state = app.getState(),
      target = app.searchRestoreTarget();
    if (
      payload.type === 'searchResults' &&
      target !== undefined &&
      state.searchResults.items.length < target &&
      !state.searchResults.pending &&
      state.searchResults.nextCursor
    ) {
      app.dispatch({ type: 'searchPageRequested' });
      sendRequest(
        {
          type: 'searchEntities',
          requestId: nextRequestId(),
          analysisId: state.analysisId,
          query: state.search,
          ...savedListScope(state),
          filters: state.filters,
          limit: 20,
          cursor: state.searchResults.nextCursor
        },
        { query: state.search, appendSearch: true }
      );
    }
    if (payload?.requestId) requestContext.delete(payload.requestId);
  });

  window.sharpdepsApp = app;
  // Restore before the handshake: a webview that hid and came back keeps its small state,
  // and the host's `viewState` message covers a window reload. Neither starts an analysis.
  const stored = host.getState();
  if (stored !== undefined && stored !== null) {
    const operation = (
      stored as { sourceOperation?: { analysisId?: unknown; requestId?: unknown } }
    ).sourceOperation;
    if (
      typeof operation?.analysisId === 'string' &&
      typeof operation.requestId === 'string' &&
      operation.analysisId.length <= 64 &&
      operation.requestId.length <= 64
    )
      latestSourceOperation = { analysisId: operation.analysisId, requestId: operation.requestId };
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
      const snapshot = navigationSnapshot(state);
      const serialized = JSON.stringify(snapshot);
      if (serialized === lastPersisted) return;
      lastPersisted = serialized;
      host.setState(snapshot);
      host.post({ type: 'persistViewState', viewState: snapshot });
    }, 300);
  }

  function persistNow(state: ViewState): void {
    if (persistTimer !== undefined) window.clearTimeout(persistTimer);
    persistTimer = undefined;
    const snapshot = navigationSnapshot(state);
    lastPersisted = JSON.stringify(snapshot);
    host.setState(snapshot);
    // This ordered message reaches the panel before its source-open request; the
    // in-memory capture does not wait for workspace storage's asynchronous write.
    host.post({ type: 'persistViewState', viewState: snapshot });
  }

  function navigationSnapshot(state: ViewState) {
    return {
      ...serializeViewState(state, state.camera ?? undefined),
      ...(latestSourceOperation ? { sourceOperation: latestSourceOperation } : {})
    };
  }

  function requestForState(state: ViewState): void {
    latestSourceOperation = synchronizeSourceOperation(
      state,
      requested,
      requestContext,
      latestSourceOperation
    );
    const analysisId = state.analysisId;
    if (!analysisId) {
      return;
    }
    const treeKey = `tree:${analysisId}`;
    if (
      !state.tree.root &&
      ![...requestContext.values()].some(
        (context) =>
          context.type === treeKey &&
          (!state.queryMetadata || context.filtersKey === JSON.stringify(state.filters))
      )
    ) {
      sendRequest(
        {
          type: 'searchEntities',
          requestId: nextRequestId(),
          analysisId,
          query: '',
          granularity: 'project',
          limit: 100,
          filters: state.filters
        },
        { type: treeKey, treeParentId: 'root' }
      );
    }

    const projectionKey = projectionRequestKey(state);
    if (state.queryMetadata) {
      const key = JSON.stringify([analysisId, searchScopeKey(state), state.searchRevision ?? 0]);
      if (requested.search !== key) {
        requested.search = key;
        if (state.search || ['project', 'namespace'].includes(state.scope.kind)) {
          app.dispatch({ type: 'searchStarted', query: state.search });
          sendRequest(
            {
              type: 'searchEntities',
              requestId: nextRequestId(),
              analysisId,
              query: state.search,
              filters: state.filters,
              ...savedListScope(state),
              limit: 20
            },
            { query: state.search }
          );
        }
      }
    }
    if (requested.projection !== projectionKey) {
      requested.projection = projectionKey;
      if (state.queryMetadata) app.dispatch({ type: 'projectionRequested' });
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
        entityId,
        ...(state.queryMetadata ? { filters: state.filters } : {})
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
      sendRequest(
        {
          type: 'getEvidencePage',
          requestId: nextRequestId(),
          analysisId,
          relationId
        },
        evidenceRequestContext(state, relationId)
      );
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
          evidenceRequestContext(state, relationId, true)
        );
      }
    }
  }
}

function savedListScope(state: ViewState): Record<string, unknown> {
  return state.queryMetadata && ['project', 'namespace'].includes(state.scope.kind)
    ? { parentId: state.scope.id, granularity: state.granularity }
    : {};
}

function toHostMessage(action: ViewAction, state: ViewState): Record<string, unknown> | undefined {
  switch (action.type) {
    case 'savedUpdateRequested':
      return state.queryMetadata && state.analysisId
        ? { type: 'updateSavedIndex', requestId: nextRequestId(), analysisId: state.analysisId }
        : undefined;
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
