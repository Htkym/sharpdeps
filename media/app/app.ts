// Viewer application (SD-015): renders the shell from the view state.
//
// Rendering is one-directional: the state is the source of truth, the DOM is derived
// from it, and user gestures only dispatch actions. Every state the plan requires
// (no target, analyzing, complete, partial, failed, stale, filtered-to-empty) has an
// explicit rendering, so none of them looks like "no dependencies".

import { buildShell, type NavTab, type ShellElements } from '../components/shell';
import { renderEntityTable } from '../components/entityTable';
import type { SortState } from './query';
import { buildNavigationTree, renderNavigationTree } from '../components/navigationPane';
import {
  INITIAL_STATE,
  selectBreadcrumbs,
  selectSearchPresentation,
  selectStatusFooter,
  selectTableRows,
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

export function createViewerApp(root: HTMLElement, options: ViewerAppOptions = {}): ViewerApp {
  let state: ViewState = INITIAL_STATE;
  let activeTab: NavTab = 'structure';
  /** Tree expansion is a transient UI detail; it is not part of the persisted state. */
  const expandedTreeNodes = new Set<string>();

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
    onSearch: (search) => {
      dispatch({ type: 'searchChanged', search });
      // The full analysis index is searched host-side; results arrive as
      // `searchResultsReceived` (SD-013 bridge).
      options.onHostAction?.({ type: 'searchStarted', query: search });
    },
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

  elements.navPaneBody.addEventListener('sd-show-temporary', (event) => {
    const detail = (event as CustomEvent<{ entityId: string }>).detail;
    if (detail?.entityId) {
      dispatch({ type: 'temporaryDisplayAdded', entityId: detail.entityId });
    }
  });

  elements.navPaneBody.addEventListener('sd-hide-temporary', () =>
    dispatch({ type: 'temporaryDisplayCleared' })
  );

  elements.mapHost.addEventListener('sd-sort', (event) => {
    const detail = (event as CustomEvent<{ key: SortState['key'] }>).detail;
    if (detail?.key) {
      dispatch({ type: 'tableSortChanged', key: detail.key });
    }
  });

  elements.mapHost.addEventListener('sd-page', (event) => {
    const detail = (event as CustomEvent<{ page: number }>).detail;
    if (typeof detail?.page === 'number') {
      dispatch({ type: 'tablePageChanged', page: detail.page });
    }
  });

  elements.mapHost.addEventListener('sd-activate', (event) => {
    const detail = (event as CustomEvent<{ entityId: string }>).detail;
    if (detail?.entityId) {
      dispatch({ type: 'entitySelected', entityId: detail.entityId });
      dispatch({ type: 'inspectorToggled' });
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
    renderNavigation(elements, state, activeTab, { expanded: expandedTreeNodes, rerender: render });
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

interface StructureContext {
  expanded: Set<string>;
  rerender: () => void;
}

function renderNavigation(
  elements: ShellElements,
  state: ViewState,
  activeTab: NavTab,
  context: StructureContext
): void {
  for (const tabButton of Array.from(
    elements.navTabs.querySelectorAll<HTMLButtonElement>('button')
  )) {
    const isActive = tabButton.dataset.tab === activeTab;
    tabButton.classList.toggle('active', isActive);
    tabButton.setAttribute('aria-selected', isActive ? 'true' : 'false');
  }

  elements.navPaneBody.replaceChildren();
  if (activeTab === 'structure') {
    renderStructureTab(elements, state, context);
  } else if (activeTab === 'cycles') {
    renderCyclesTab(elements, state);
  } else {
    renderAnalysisTab(elements, state);
  }
}

function renderStructureTab(
  elements: ShellElements,
  state: ViewState,
  context: StructureContext
): void {
  renderSearchResults(elements, state);

  const visible = selectVisibleData(state);
  const tree = buildNavigationTree(visible.nodes);
  const container = document.createElement('div');
  elements.navPaneBody.append(container);
  renderNavigationTree(container, {
    nodes: tree,
    selectedId: state.selection.entityId,
    expanded: context.expanded,
    onSelect: (entityId) =>
      elements.navPaneBody.dispatchEvent(
        new CustomEvent('sd-select', { detail: { entityId }, bubbles: true })
      ),
    onToggle: (entityId) => {
      if (context.expanded.has(entityId)) {
        context.expanded.delete(entityId);
      } else {
        context.expanded.add(entityId);
      }

      context.rerender();
    }
  });
}

/** Search hits from the whole index, including entities the view does not show. */
function renderSearchResults(elements: ShellElements, state: ViewState): void {
  if (state.search.trim().length === 0) {
    return;
  }

  const results = selectSearchPresentation(state);
  const block = document.createElement('section');
  block.className = 'sd-search-results';
  const heading = document.createElement('h3');
  heading.textContent =
    results.length === 0 && !state.searchResults.pending
      ? `No match for "${state.search}"`
      : `Search results (${results.length}${state.searchResults.total > results.length ? ` of ${state.searchResults.total}` : ''})`;
  block.append(heading);

  if (state.searchResults.pending && results.length === 0) {
    block.append(message('Searching the analyzed index...', 'sd-note'));
  }

  const list = document.createElement('ul');
  for (const result of results.slice(0, 20)) {
    const item = document.createElement('li');
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'sd-node-item';
    button.textContent = result.entity.name;
    button.addEventListener('click', () =>
      elements.navPaneBody.dispatchEvent(
        new CustomEvent('sd-select', { detail: { entityId: result.entity.id }, bubbles: true })
      )
    );
    item.append(button);

    if (result.visibility !== 'visible') {
      item.append(
        badge(result.visibility === 'outsideBudget' ? 'outside view' : 'outside filters')
      );
      const show = document.createElement('button');
      show.type = 'button';
      show.className = 'sd-button sd-button-small';
      show.textContent = 'Show';
      show.addEventListener('click', () =>
        elements.navPaneBody.dispatchEvent(
          new CustomEvent('sd-show-temporary', {
            detail: { entityId: result.entity.id },
            bubbles: true
          })
        )
      );
      item.append(show);
    }

    list.append(item);
  }

  block.append(list);

  if (state.temporaryDisplayIds.length > 0) {
    const reset = document.createElement('button');
    reset.type = 'button';
    reset.className = 'sd-button sd-button-small';
    reset.textContent = `Hide ${state.temporaryDisplayIds.length} row(s) outside filters`;
    reset.addEventListener('click', () =>
      elements.navPaneBody.dispatchEvent(new CustomEvent('sd-hide-temporary', { bubbles: true }))
    );
    block.append(reset);
  }

  elements.navPaneBody.append(block);
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

  const tableOptions = {
    rows: selectTableRows(state).rows,
    sort: state.tableSort,
    page: selectTableRows(state).page,
    pageCount: selectTableRows(state).pageCount,
    totalItems: selectTableRows(state).totalItems,
    selectedId: state.selection.entityId,
    onSort: (key: SortState['key']) =>
      elements.mapHost.dispatchEvent(
        new CustomEvent('sd-sort', { detail: { key }, bubbles: true })
      ),
    onPage: (page: number) =>
      elements.mapHost.dispatchEvent(
        new CustomEvent('sd-page', { detail: { page }, bubbles: true })
      ),
    onSelect: (entityId: string) =>
      elements.mapHost.dispatchEvent(
        new CustomEvent('sd-select', { detail: { entityId }, bubbles: true })
      ),
    onActivate: (entityId: string) =>
      elements.mapHost.dispatchEvent(
        new CustomEvent('sd-activate', { detail: { entityId }, bubbles: true })
      )
  };

  if (state.viewKind === 'graph') {
    elements.mapHost.append(
      message('The interactive SVG graph replaces this table in SD-017.', 'sd-note')
    );
  }

  renderEntityTable(elements.mapHost, tableOptions);
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
