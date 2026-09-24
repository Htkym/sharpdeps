// Viewer application (SD-015): renders the shell from the view state.
//
// Rendering is one-directional: the state is the source of truth, the DOM is derived
// from it, and user gestures only dispatch actions. Every state the plan requires
// (no target, analyzing, complete, partial, failed, stale, filtered-to-empty) has an
// explicit rendering, so none of them looks like "no dependencies".

import { buildShell, type NavTab, type ShellElements } from '../components/shell';
import {
  INITIAL_STATE,
  selectBreadcrumbs,
  selectStatusFooter,
  selectVisibleData,
  viewReducer,
  type ViewAction,
  type ViewState
} from './state';

export interface ViewerApp {
  dispatch(action: ViewAction): void;
  getState(): ViewState;
  elements: ShellElements;
}

export interface ViewerAppOptions {
  /** Called for actions the host must perform (analyze, stop, export, copy). */
  onHostAction?: (action: ViewAction) => void;
  /** Called after each render so the host can mirror derived values (pane widths). */
  onStateChanged?: (state: ViewState) => void;
}

const MAX_TABLE_ROWS = 200;

export function createViewerApp(root: HTMLElement, options: ViewerAppOptions = {}): ViewerApp {
  let state: ViewState = INITIAL_STATE;
  let activeTab: NavTab = 'structure';

  const elements = buildShell(root, {
    onAnalyze: () =>
      options.onHostAction?.({ type: 'analyzeStarted', analysisId: '', mode: state.mode }),
    onStop: () =>
      options.onHostAction?.({
        type: 'analysisFailed',
        message: 'stop requested',
        cancelled: true
      }),
    onGranularity: (granularity) => dispatch({ type: 'granularityChanged', granularity }),
    onViewKind: (viewKind) => dispatch({ type: 'viewKindChanged', viewKind }),
    onSearch: (search) => dispatch({ type: 'searchChanged', search }),
    onExport: () => options.onHostAction?.({ type: 'selectionCleared' }),
    onCopyContext: () => options.onHostAction?.({ type: 'selectionCleared' }),
    onNavTab: (tab) => {
      activeTab = tab;
      render();
    },
    onSelectionCleared: () => dispatch({ type: 'selectionCleared' }),
    onInspectorToggled: () => dispatch({ type: 'inspectorToggled' }),
    onPaneResized: (pane, width) => dispatch({ type: 'paneResized', pane, width })
  });

  // Shell children emit intent as events; the app turns them into state changes.
  elements.navPaneBody.addEventListener('sd-select', (event) => {
    const detail = (event as CustomEvent<{ entityId: string }>).detail;
    if (detail?.entityId) {
      dispatch({ type: 'entitySelected', entityId: detail.entityId });
    }
  });

  elements.mapHost.addEventListener('sd-reset-filters', () => {
    dispatch({ type: 'searchChanged', search: '' });
    dispatch({ type: 'filtersChanged', filters: {} });
  });

  function dispatch(action: ViewAction): void {
    const next = viewReducer(state, action);
    if (next === state) {
      return;
    }

    state = next;
    render();
  }

  function render(): void {
    renderTopBar(elements, state);
    renderError(elements, state);
    renderNavigation(elements, state, activeTab);
    renderCenter(elements, state);
    renderInspector(elements, state);
    elements.footer.textContent = selectStatusFooter(state);
    options.onStateChanged?.(state);
  }

  const app: ViewerApp = { dispatch, getState: () => state, elements };
  render();
  return app;
}

function renderTopBar(elements: ShellElements, state: ViewState): void {
  elements.targetName.textContent = state.target?.name ?? 'No target';
  elements.targetPath.textContent = state.target?.relativePath ?? '';
  elements.modeSelect.value = state.mode;
  elements.modeSelect.disabled = state.status === 'analyzing';

  const analyzing = state.status === 'analyzing';
  elements.analyzeButton.disabled = analyzing;
  elements.stopButton.disabled = !analyzing;

  elements.breadcrumbs.replaceChildren(
    ...selectBreadcrumbs(state).map((crumb) => {
      const span = document.createElement('span');
      span.className = 'sd-crumb';
      span.textContent = crumb;
      return span;
    })
  );

  elements.statusText.textContent = state.statusMessage;
  elements.statusText.dataset.status = state.status;
}

function renderError(elements: ShellElements, state: ViewState): void {
  if (!state.error) {
    elements.errorBar.hidden = true;
    elements.errorBar.textContent = '';
    return;
  }

  elements.errorBar.hidden = false;
  elements.errorBar.textContent = `${state.error.code}: ${state.error.message}`;
}

function renderNavigation(elements: ShellElements, state: ViewState, activeTab: NavTab): void {
  for (const tabButton of Array.from(
    elements.navTabs.querySelectorAll<HTMLButtonElement>('button')
  )) {
    const isActive = tabButton.dataset.tab === activeTab;
    tabButton.classList.toggle('active', isActive);
    tabButton.setAttribute('aria-selected', isActive ? 'true' : 'false');
  }

  elements.navPaneBody.replaceChildren();
  if (activeTab === 'structure') {
    renderStructureTab(elements, state);
  } else if (activeTab === 'cycles') {
    renderCyclesTab(elements, state);
  } else {
    renderAnalysisTab(elements, state);
  }
}

function renderStructureTab(elements: ShellElements, state: ViewState): void {
  const visible = selectVisibleData(state);
  if (visible.nodes.length === 0) {
    elements.navPaneBody.append(message('The structure tree is empty for this scope.', 'sd-empty'));
    return;
  }

  const list = document.createElement('ul');
  list.className = 'sd-node-list';
  for (const node of visible.nodes.slice(0, 100)) {
    const item = document.createElement('li');
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'sd-node-item';
    button.textContent = node.name;
    button.dataset.entityId = node.id;
    if (node.inCycle) {
      button.dataset.inCycle = 'true';
      button.append(badge('cycle'));
    }

    if (node.isExternal) {
      button.append(badge('external'));
    }

    button.addEventListener('click', () => {
      // The app dispatches through the closure created in createViewerApp.
      elements.navPaneBody.dispatchEvent(
        new CustomEvent('sd-select', { detail: { entityId: node.id }, bubbles: true })
      );
    });
    item.append(button);
    list.append(item);
  }

  elements.navPaneBody.append(list);
}

function renderCyclesTab(elements: ShellElements, state: ViewState): void {
  const visible = selectVisibleData(state);
  const cycleNodes = visible.nodes.filter((node) => node.inCycle);
  if (cycleNodes.length === 0) {
    elements.navPaneBody.append(message('No dependency cycles in this scope.', 'sd-empty'));
    return;
  }

  elements.navPaneBody.append(
    message(`${cycleNodes.length} node(s) participate in a cycle.`, 'sd-note')
  );
  const list = document.createElement('ul');
  list.className = 'sd-node-list';
  for (const node of cycleNodes) {
    const item = document.createElement('li');
    item.textContent = node.name;
    list.append(item);
  }

  elements.navPaneBody.append(list);
}

function renderAnalysisTab(elements: ShellElements, state: ViewState): void {
  const rows: Array<[string, string]> = [
    ['Status', state.status],
    [
      'Mode',
      state.mode === 'quick' ? 'Quick (declared/inferred)' : 'Semantic (resolved references)'
    ],
    ['Analysis id', state.analysisId ?? '—']
  ];

  if (state.coverage) {
    rows.push([
      'Coverage',
      `discovered ${state.coverage.discovered} · loaded ${state.coverage.loaded} · analyzed ${state.coverage.analyzed}` +
        ` · failed ${state.coverage.failed} · skipped ${state.coverage.skipped}`
    ]);
  }

  if (state.progress) {
    rows.push(['Progress', `${state.progress.stage} · ${Math.round(state.progress.elapsedMs)} ms`]);
  }

  const list = document.createElement('dl');
  list.className = 'sd-facts';
  for (const [label, value] of rows) {
    const term = document.createElement('dt');
    term.textContent = label;
    const definition = document.createElement('dd');
    definition.textContent = value;
    list.append(term, definition);
  }

  elements.navPaneBody.append(list);

  if (state.limitations.length > 0) {
    const heading = document.createElement('h3');
    heading.textContent = 'Limitations';
    elements.navPaneBody.append(heading);
    const limitations = document.createElement('ul');
    limitations.className = 'sd-limitations';
    for (const limitation of state.limitations) {
      const item = document.createElement('li');
      item.textContent = limitation.message;
      item.dataset.code = limitation.code;
      limitations.append(item);
    }

    elements.navPaneBody.append(limitations);
  }
}

function renderCenter(elements: ShellElements, state: ViewState): void {
  elements.granularitySelect.value = state.granularity;
  for (const kindButton of Array.from(
    elements.viewKindButtons.querySelectorAll<HTMLButtonElement>('button')
  )) {
    const isActive = kindButton.dataset.viewKind === state.viewKind;
    kindButton.classList.toggle('active', isActive);
    kindButton.setAttribute('aria-pressed', isActive ? 'true' : 'false');
  }

  if (elements.searchInput.value !== state.search) {
    elements.searchInput.value = state.search;
  }

  const visible = selectVisibleData(state);
  elements.mapHost.replaceChildren();
  elements.mapHost.dataset.viewKind = state.viewKind;

  if (!state.projection) {
    elements.mapSummary.textContent = '';
    elements.mapHost.append(emptyStateMessage(state));
    return;
  }

  const summaryParts = [
    `${visible.nodes.length} node(s) shown of ${visible.totalNodeCount}`,
    `${visible.edges.length} relation(s) of ${visible.totalEdgeCount}`
  ];
  if (visible.filterCount > 0) {
    summaryParts.push(`${visible.filterCount} filter(s)`);
  }

  if (visible.isFilteredEmpty) {
    summaryParts.push('no match for the current search or filters');
  }

  elements.mapSummary.textContent = summaryParts.join(' · ');

  if (visible.isFilteredEmpty) {
    const empty = message('No match for the current search or filters.', 'sd-empty');
    const reset = document.createElement('button');
    reset.type = 'button';
    reset.className = 'sd-button';
    reset.textContent = 'Clear search and filters';
    reset.addEventListener('click', () => {
      elements.searchInput.value = '';
      elements.mapHost.dispatchEvent(new CustomEvent('sd-reset-filters', { bubbles: true }));
    });
    empty.append(reset);
    elements.mapHost.append(empty);
    return;
  }

  if (state.viewKind === 'table') {
    elements.mapHost.append(buildTable(visible.nodes, visible.edges));
    return;
  }

  // The interactive SVG graph and the paged table are SD-017/SD-016. Until then the
  // center shows the same projection as a readable list so no state looks empty.
  elements.mapHost.append(buildTable(visible.nodes, visible.edges));
  elements.mapHost.append(
    message('The interactive SVG graph replaces this list in SD-017.', 'sd-note')
  );
}

function buildTable(
  nodes: ReturnType<typeof selectVisibleData>['nodes'],
  edges: ReturnType<typeof selectVisibleData>['edges']
): HTMLTableElement {
  const table = document.createElement('table');
  table.className = 'sd-table';
  const head = document.createElement('thead');
  const headRow = document.createElement('tr');
  for (const label of ['Name', 'Kind', 'Project', 'Dependencies', 'Dependents', 'Cycle']) {
    const cell = document.createElement('th');
    cell.scope = 'col';
    cell.textContent = label;
    headRow.append(cell);
  }

  head.append(headRow);
  table.append(head);

  const outgoing = new Map<string, number>();
  const incoming = new Map<string, number>();
  for (const edge of edges) {
    outgoing.set(edge.sourceId, (outgoing.get(edge.sourceId) ?? 0) + 1);
    incoming.set(edge.targetId, (incoming.get(edge.targetId) ?? 0) + 1);
  }

  const body = document.createElement('tbody');
  for (const node of nodes.slice(0, MAX_TABLE_ROWS)) {
    const row = document.createElement('tr');
    row.dataset.entityId = node.id;
    row.tabIndex = 0;
    for (const value of [
      node.name,
      node.kind ?? '—',
      node.projectName ?? '—',
      String(outgoing.get(node.id) ?? 0),
      String(incoming.get(node.id) ?? 0),
      node.inCycle ? 'yes' : 'no'
    ]) {
      const cell = document.createElement('td');
      cell.textContent = value;
      row.append(cell);
    }

    body.append(row);
  }

  table.append(body);
  return table;
}

function emptyStateMessage(state: ViewState): HTMLElement {
  switch (state.status) {
    case 'noTarget':
      return message('Select a solution or project and choose Analyze.', 'sd-empty');
    case 'ready':
      return message(
        'Ready to analyze. The previous result is not shown until a new one arrives.',
        'sd-empty'
      );
    case 'analyzing':
      return message(state.statusMessage, 'sd-empty');
    case 'failed':
      return message(
        `The analysis failed: ${state.error?.message ?? 'unknown reason'}`,
        'sd-empty sd-empty-error'
      );
    case 'cancelled':
      return message('The analysis was stopped.', 'sd-empty');
    case 'stale':
      return message(
        'This result is out of date. Analyze again to refresh.',
        'sd-empty sd-empty-stale'
      );
    default:
      return message('No projection for this scope.', 'sd-empty');
  }
}

function renderInspector(elements: ShellElements, state: ViewState): void {
  elements.inspectorPane.classList.toggle('open', state.inspectorOpen);
  elements.inspectorToggle.setAttribute('aria-expanded', state.inspectorOpen ? 'true' : 'false');
  elements.inspectorBody.replaceChildren();

  if (state.selection.relationId && state.evidence?.relationId === state.selection.relationId) {
    elements.inspectorTitle.textContent = `Relation ${state.selection.relationId}`;
    elements.inspectorBody.append(
      message(`${state.evidence.total} evidence record(s)`, 'sd-note'),
      message(
        state.evidence.items.length > 0
          ? 'Evidence details open in the editor from the evidence list (SD-018).'
          : 'No evidence page loaded yet.',
        'sd-note'
      )
    );
    return;
  }

  if (state.details && state.selection.entityId === state.details.entityId) {
    elements.inspectorTitle.textContent = state.details.entityId;
    elements.inspectorBody.append(
      factList(
        'Dependencies',
        state.details.dependencies.map((entry) => entry.name)
      ),
      factList(
        'Dependents',
        state.details.dependents.map((entry) => entry.name)
      )
    );
    return;
  }

  elements.inspectorTitle.textContent = 'Details';
  elements.inspectorBody.append(message('Select a node or relation to inspect it.', 'sd-empty'));
}

function factList(label: string, values: string[]): HTMLElement {
  const block = document.createElement('section');
  const heading = document.createElement('h3');
  heading.textContent = `${label} (${values.length})`;
  block.append(heading);
  if (values.length === 0) {
    block.append(message('none', 'sd-empty'));
    return block;
  }

  const list = document.createElement('ul');
  for (const value of values.slice(0, 50)) {
    const item = document.createElement('li');
    item.textContent = value;
    list.append(item);
  }

  block.append(list);
  return block;
}

function message(text: string, className: string): HTMLElement {
  const node = document.createElement('p');
  node.className = className;
  node.textContent = text;
  return node;
}

function badge(text: string): HTMLElement {
  const node = document.createElement('span');
  node.className = 'sd-badge';
  node.textContent = text;
  return node;
}
