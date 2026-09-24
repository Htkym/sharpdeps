// Browser entry for the new shell (SD-015).
//
// Used by the webview panel (SD-017 wires it into the panel) and by the fixture page
// for verification. It creates the app, keeps the CSS pane variables in sync, and
// exposes the app for fixtures and tests.

import { createViewerApp, type ViewerApp } from './app';
import type { ViewAction, ViewState } from './state';

declare global {
  interface Window {
    sharpdepsApp?: ViewerApp;
  }
}

const root = document.getElementById('app');
if (root) {
  const app = createViewerApp(root, {
    onStateChanged: (state) => applyPaneWidths(state),
    onHostAction: (action) => postToHost(action)
  });

  window.sharpdepsApp = app;

  const fixture = root.dataset.fixture;
  if (fixture === 'true') {
    root.dataset.ready = 'true';
  }
}

function applyPaneWidths(state: ViewState): void {
  root?.style.setProperty('--sd-nav-width', `${state.paneWidths.navigation}px`);
  root?.style.setProperty('--sd-inspector-width', `${state.paneWidths.inspector}px`);
}

function postToHost(action: ViewAction): void {
  // The real protocol messages are sent by the panel wiring (SD-017); the fixture has
  // no host, so the action is recorded for inspection instead of being lost.
  const target = document.getElementById('app');
  if (!target) {
    return;
  }

  const log = target.dataset.hostActions ? `${target.dataset.hostActions},` : '';
  target.dataset.hostActions = `${log}${action.type}`;
}
