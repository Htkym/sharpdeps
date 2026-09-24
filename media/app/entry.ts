// Browser entry for the new shell (SD-015, host wiring in SD-017).
//
// Used by the webview panel and by the fixture page for verification. It creates the
// app, keeps the CSS pane variables in sync, and translates between protocol v2 messages
// and app actions. Requests are only sent for a registered analysis, and a request is
// only repeated when its input actually changed.

import { PROTOCOL_VERSION } from '../../src/view/protocolV2';
import { createViewerApp, type ViewerApp } from './app';
import { toViewActions } from './hostMessages';
import type { ViewAction, ViewState } from './state';

declare function acquireVsCodeApi(): { postMessage(message: unknown): void };

declare global {
  interface Window {
    sharpdepsApp?: ViewerApp;
  }
}

const root = document.getElementById('app');
if (root) {
  const host = createWebviewLink(root);
  const queryByRequestId = new Map<string, string>();
  const requested = { projection: '', details: '', evidence: '' };

  const app = createViewerApp(root, {
    workerUrl: root.dataset.workerUri,
    onStateChanged: (state) => {
      applyPaneWidths(state);
      requestForState(state);
    },
    onHostAction: (action) => {
      if (action.type === 'searchStarted') {
        const requestId = nextRequestId();
        queryByRequestId.set(requestId, action.query);
        host.post({
          type: 'searchEntities',
          requestId,
          analysisId: app.getState().analysisId ?? '',
          query: action.query
        });
        return;
      }

      const message = toHostMessage(action, app.getState());
      if (message) {
        host.post(message);
      }
    }
  });

  host.subscribe((message) => {
    for (const action of toViewActions(message, queryByRequestId)) {
      app.dispatch(action);
    }
  });

  window.sharpdepsApp = app;
  // The handshake asks the host for the protocol version, capabilities, and whether an
  // analysis result is already registered.
  host.post({ type: 'ready', protocolVersion: PROTOCOL_VERSION, webviewVersion: '1' });

  function requestForState(state: ViewState): void {
    const analysisId = state.analysisId;
    if (!analysisId) {
      return;
    }

    const projectionKey = [
      analysisId,
      state.granularity,
      state.scope.kind,
      state.scope.id ?? '',
      state.scope.depth ?? 1
    ].join('|');
    if (requested.projection !== projectionKey) {
      requested.projection = projectionKey;
      host.post({
        type: 'getProjection',
        requestId: nextRequestId(),
        analysisId,
        scope: state.scope,
        granularity: state.granularity
      });
    }

    const entityId = state.selection.entityId ?? '';
    if (entityId && requested.details !== `${analysisId}|${entityId}`) {
      requested.details = `${analysisId}|${entityId}`;
      host.post({
        type: 'getEntityDetails',
        requestId: nextRequestId(),
        analysisId,
        entityId
      });
    }

    const relationId = state.selection.relationId ?? '';
    if (relationId && requested.evidence !== `${analysisId}|${relationId}`) {
      requested.evidence = `${analysisId}|${relationId}`;
      host.post({
        type: 'getEvidencePage',
        requestId: nextRequestId(),
        analysisId,
        relationId
      });
    }
  }
}

function toHostMessage(action: ViewAction, state: ViewState): Record<string, unknown> | undefined {
  switch (action.type) {
    case 'analyzeStarted':
      return {
        type: 'analyze',
        requestId: nextRequestId(),
        mode: action.mode
      };
    case 'analysisFailed':
      return {
        type: 'cancelAnalysis',
        requestId: nextRequestId(),
        analysisId: state.analysisId ?? ''
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
    }
  };
}

function applyPaneWidths(state: ViewState): void {
  root?.style.setProperty('--sd-nav-width', `${state.paneWidths.navigation}px`);
  root?.style.setProperty('--sd-inspector-width', `${state.paneWidths.inspector}px`);
}
