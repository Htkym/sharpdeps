// Viewer application (SD-015): renders the shell from the view state.
//
// Rendering is one-directional: the state is the source of truth, the DOM is derived
// from it, and user gestures only dispatch actions. Every state the plan requires
// (no target, analyzing, complete, partial, failed, stale, filtered-to-empty) has an
// explicit rendering, so none of them looks like "no dependencies".

import { buildShell, type NavTab, type ShellElements } from '../components/shell';
import { projectKindColor } from '../graph/projectionAdapter';
import { renderEntityTable } from '../components/entityTable';
import { createGraphView, type GraphView } from '../components/graphView';
import {
  renderInspector,
  type InspectorEdgeOptions,
  type InspectorEntityOptions
} from '../components/inspector';
import { resolveShortcut } from './shortcuts';
import type { SortState } from './query';
import {
  buildNavigationTree,
  renderNavigationTree,
  type NavigationTreeNode
} from '../components/navigationPane';
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
  export(format: 'mermaid' | 'svg' | 'png' | 'json', copy?: boolean): Promise<void>;
}

export interface ViewerAppOptions {
  /** Called for actions the host must perform (analyze, stop, export, copy). */
  onHostAction?: (action: ViewAction) => void;
  /** Called after each render so the host can mirror derived values (pane widths). */
  onStateChanged?: (state: ViewState) => void;
  /** Export of the current selection (SD-022). Image formats carry the rendered data. */
  onExport?: (format: 'mermaid' | 'svg' | 'png' | 'json', data?: string, copy?: boolean) => void;
  /** Copy of the evidence-backed context (SD-022). Nothing is sent anywhere. */
  onCopyContext?: () => void;
  /** Opens one evidence record in the editor (SD-019/SD-024). */
  onOpenEvidence?: (evidenceId: string) => void;
  onOpenDeclaration?: (entityId: string) => void;
  /**
   * Resource URI of the ELK layout worker. Without it (or when the worker fails) the
   * table stays available and the graph shows why it is missing.
   */
  workerUrl?: string;
}

export function createViewerApp(root: HTMLElement, options: ViewerAppOptions = {}): ViewerApp {
  let state: ViewState = INITIAL_STATE;
  let activeTab: NavTab = 'structure';
  /** Tree expansion is a transient UI detail; it is not part of the persisted state. */
  const expandedTreeNodes = new Set<string>();
  const graphRuntime: { view?: GraphView; error?: string } = {};
  /** Focus returns here when the inspector drawer closes (SD-024). */
  let inspectorReturnFocus: HTMLElement | undefined;
  let inspectorWasOpen = false;

  const elements = buildShell(root, {
    onCancelLayout: () => graphRuntime.view?.cancelLayout(),
    onRetryLayout: () => graphRuntime.view?.retryLayout(),
    onZoom: (zoom) => {
      const view = graphView();
      if (!view) return;
      if (zoom === 'fit') view.fit();
      else
        view.zoomBy(
          zoom === 'in' ? 1.2 : zoom === 'out' ? 1 / 1.2 : zoom / view.cameraState().zoom
        );
    },
    onLayout: (layout) => dispatch({ type: 'layoutChanged', layout }),
    onImageOptions: (options) => dispatch({ type: 'imageOptionsChanged', options }),
    onMode: (mode) => dispatch({ type: 'modeChanged', mode }),
    onProfile: (profile) =>
      dispatch({ type: 'profileChanged', profile: { ...state.profile, ...profile } }),
    onFilters: (filters) => dispatch({ type: 'filtersChanged', filters }),
    onBack: () => {
      dispatch({ type: 'historyBack' });
      if (state.camera) graphRuntime.view?.applyCamera(state.camera);
    },
    onDepth: (depth) => dispatch({ type: 'scopeChanged', scope: { ...state.scope, depth } }),
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
    onExport: (format) => void exportCurrent(format),
    onCopyContext: () => options.onCopyContext?.(),
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
      const entity = state.searchResults.items.find((item) => item.id === detail.entityId);
      if (entity && entity.granularity !== state.granularity)
        dispatch({ type: 'granularityChanged', granularity: entity.granularity });
      dispatch({ type: 'temporaryDisplayAdded', entityId: detail.entityId });
      dispatch({ type: 'entitySelected', entityId: detail.entityId });
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
      drillDown(detail.entityId);
    }
  });

  elements.mapHost.addEventListener('sd-reset-filters', () => {
    dispatch({ type: 'searchChanged', search: '' });
    dispatch({ type: 'filtersChanged', filters: {} });
  });
  elements.mapHost.addEventListener('sd-select', (event) => {
    const entityId = (event as CustomEvent<{ entityId: string }>).detail?.entityId;
    if (entityId) dispatch({ type: 'entitySelected', entityId });
  });

  function drillDown(entityId: string): void {
    const entity =
      state.projection?.nodes.find((node) => node.id === entityId) ??
      state.searchResults.items.find((node) => node.id === entityId) ??
      (state.details?.entityId === entityId ? state.details.entity : undefined);
    if (!entity) return;
    dispatch({
      type: 'revealRequested',
      entityId,
      granularity: entity.granularity === 'project' ? 'namespace' : 'type',
      scope: {
        kind: entity.granularity === 'type' ? 'dependencies' : entity.granularity,
        id: entityId,
        depth: 1
      }
    });
  }

  async function exportCurrent(
    format: 'mermaid' | 'svg' | 'png' | 'json',
    copy?: boolean
  ): Promise<void> {
    try {
      if (format === 'svg' || format === 'png') {
        const view = graphView();
        if (!view || !state.projection) throw new Error('No graph is available to export.');
        const visible = selectVisibleData(state);
        view.setSpacing(state.layout);
        await view.update(
          { ...state.projection, nodes: visible.nodes, edges: visible.edges },
          scopeLabel(state)
        );
        const notes = imageMetadata(state);
        const data = format === 'svg' ? view.exportSvg(notes) : await view.exportPng(notes);
        if (!data) throw new Error('The layout is not ready. Try again after the graph appears.');
        options.onExport?.(format, data);
      } else options.onExport?.(format, undefined, copy);
    } catch (error) {
      dispatch({ type: 'errorRaised', code: 'export.failed', message: String(error) });
    }
  }

  function dispatch(action: ViewAction): void {
    const next = viewReducer(state, action);
    if (next === state) {
      return;
    }

    state = next;
    render();
  }

  /**
   * Keyboard path through the whole flow (SD-024): search, view switch, selection,
   * inspector close, and graph zoom, all without a mouse.
   */
  window.addEventListener('keydown', (event) => {
    const target = event.target as HTMLElement | null;
    const typing =
      target instanceof HTMLInputElement ||
      target instanceof HTMLTextAreaElement ||
      target instanceof HTMLSelectElement ||
      target?.isContentEditable === true;

    const action = resolveShortcut(event, {
      typing,
      inspectorOpen: state.inspectorOpen,
      // The event target may be window/document for programmatic keys: only an Element
      // can be inside the graph.
      graphFocused: target instanceof Element && target.closest('.sd-graph-host') !== null
    });
    if (!action) {
      return;
    }

    switch (action.type) {
      case 'focusSearch':
        event.preventDefault();
        elements.searchInput.focus();
        elements.searchInput.select();
        return;
      case 'viewKind':
        event.preventDefault();
        dispatch({ type: 'viewKindChanged', viewKind: action.viewKind });
        return;
      case 'clearSelection':
        dispatch({ type: 'selectionCleared' });
        return;
      case 'closeInspector':
        event.preventDefault();
        dispatch({ type: 'inspectorClosed' });
        return;
      case 'zoom': {
        const view = graphRuntime.view;
        if (!view) {
          return;
        }

        event.preventDefault();
        if (action.direction === 'fit') {
          view.fit();
        } else {
          view.zoomBy(action.direction === 'in' ? 1.2 : 1 / 1.2);
        }
      }
    }
  });

  function render(): void {
    renderTopBar(elements, state);
    renderError(elements, state);
    renderNavigation(elements, state, activeTab, {
      expanded: expandedTreeNodes,
      rerender: render,
      dispatch,
      requestTree: (parentId, granularity, cursor) =>
        options.onHostAction?.({ type: 'treeRequested', parentId, granularity, cursor })
    });
    renderCenter(elements, state, graphView, () => graphRuntime.error);
    renderDetails();
    elements.footer.textContent = selectStatusFooter(state);
    options.onStateChanged?.(state);
  }

  function renderDetails(): void {
    // Opening remembers the trigger; closing gives the focus back to it, so a keyboard
    // user never lands at the top of the page (SD-024).
    if (state.inspectorOpen && !inspectorWasOpen) {
      inspectorReturnFocus =
        document.activeElement instanceof HTMLElement ? document.activeElement : undefined;
    } else if (!state.inspectorOpen && inspectorWasOpen) {
      inspectorReturnFocus?.focus();
      inspectorReturnFocus = undefined;
    }

    inspectorWasOpen = state.inspectorOpen;
    elements.inspectorPane.classList.toggle('open', state.inspectorOpen);
    elements.inspectorToggle.setAttribute('aria-expanded', state.inspectorOpen ? 'true' : 'false');

    const projection = state.projection;
    const selectedEdgeId = state.selection.relationId;
    const selectedEntityId = state.selection.entityId;

    let edge: InspectorEdgeOptions | undefined;
    if (selectedEdgeId) {
      const relation = projection?.edges.find(
        (entry) =>
          entry.id === selectedEdgeId || entry.underlyingRelationIds?.includes(selectedEdgeId)
      );
      const nameOf = (id: string | undefined): string =>
        projection?.nodes.find((node) => node.id === id)?.name ?? id ?? '?';
      edge = {
        id: selectedEdgeId,
        edge: relation,
        sourceName: nameOf(relation?.sourceId),
        targetName: nameOf(relation?.targetId),
        evidence: state.evidence?.relationId === selectedEdgeId ? state.evidence : null
      };
    }

    let entity: InspectorEntityOptions | undefined;
    if (selectedEntityId) {
      const details = state.details?.entityId === selectedEntityId ? state.details : undefined;
      entity = {
        id: selectedEntityId,
        summary: details?.entity ?? projection?.nodes.find((node) => node.id === selectedEntityId),
        dependencies: details?.dependencies,
        dependents: details?.dependents,
        edges: projection?.edges.filter(
          (entry) => entry.sourceId === selectedEntityId || entry.targetId === selectedEntityId
        ),
        pending: !details
      };
    }

    renderInspector(elements.inspectorTitle, elements.inspectorBody, {
      edge,
      entity,
      limitations: state.limitations,
      mode: state.resultMode ?? state.mode,
      onSelectEntity: (entityId) => dispatch({ type: 'entitySelected', entityId }),
      onLoadMoreEvidence: () => dispatch({ type: 'evidencePageRequested' }),
      onCopyReference: (reference) => void copyReference(reference),
      onOpenEvidence: (evidenceId) => options.onOpenEvidence?.(evidenceId),
      onOpenDeclaration: (entityId) => options.onOpenDeclaration?.(entityId),
      onSelectRelation: (relationId) => dispatch({ type: 'relationSelected', relationId }),
      onExplore: (kind, entityId) =>
        dispatch({ type: 'revealRequested', entityId, scope: { kind, id: entityId, depth: 1 } }),
      onDrillDown: drillDown
    });
  }

  async function copyReference(reference: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(reference);
      elements.inspectorBody.append(message(`Copied: ${reference}`, 'sd-note'));
    } catch {
      // A failed copy must not look like a success; the text is shown so it can be
      // selected manually.
      elements.inspectorBody.append(
        message(`Copy failed. ${reference}`, 'sd-note sd-note-warning')
      );
    }
  }

  const app: ViewerApp = { dispatch, getState: () => state, elements, export: exportCurrent };
  render();
  return app;

  /** The SVG graph view, created on first use and kept for the app's lifetime. */
  function graphView(): GraphView | undefined {
    if (graphRuntime.view) {
      return graphRuntime.view;
    }

    if (!options.workerUrl) {
      graphRuntime.error = 'The layout worker is not available in this build.';
      return undefined;
    }

    graphRuntime.view = createGraphView({
      workerUrl: options.workerUrl,
      onSelect: (selection) => {
        const entityId = selection.nodeIds[0];
        const relationId = selection.edgeIds[0];
        if (relationId) {
          // The edge id is the representative relation, so evidence can be paged.
          dispatch({ type: 'relationSelected', relationId });
        } else if (entityId) {
          dispatch({ type: 'entitySelected', entityId });
        }
      },
      onActivate: (selection) => {
        const entityId = selection.nodeIds[0];
        if (entityId) {
          drillDown(entityId);
        }
      },
      onError: (message) => {
        graphRuntime.error = message;
        render();
      },
      onCameraChanged: (camera) => dispatch({ type: 'cameraChanged', camera })
    });
    elements.graphHost.append(graphRuntime.view.element);
    // A camera from the persisted state is applied to the first projection instead of
    // fitting, so a restored view keeps the zoom and position.
    if (state.camera) {
      graphRuntime.view.applyCamera(state.camera);
    }

    return graphRuntime.view;
  }
}

function renderTopBar(elements: ShellElements, state: ViewState): void {
  elements.zoom.value = String(Math.round((state.camera?.zoom ?? 1) * 100));
  elements.nodeSpacing.value = String(state.layout.nodeSpacing);
  elements.rankSpacing.value = String(state.layout.rankSpacing);
  for (const key of ['profile', 'omissions', 'legend'] as const)
    elements.imageOptions[key].checked = state.imageOptions[key];
  elements.legend.replaceChildren();
  const kinds = [
    ...new Set(
      selectVisibleData(state)
        .nodes.map(
          (node) => node.projectKind ?? (node.granularity === 'project' ? node.kind : undefined)
        )
        .filter((kind): kind is string => !!kind)
    )
  ].sort();
  for (const kind of kinds) {
    const item = document.createElement('span');
    item.textContent = kind;
    item.style.borderLeft = `4px solid ${projectKindColor(kind)}`;
    elements.legend.append(item);
  }
  const meaning = document.createElement('span');
  meaning.textContent = 'Dashed: inferred · Red / ⟳: cycle · G: generated · ext: external';
  elements.legend.append(meaning);
  elements.targetName.textContent = state.target?.name ?? 'No target';
  elements.targetPath.textContent = state.target?.relativePath ?? '';
  elements.modeSelect.value = state.mode;
  elements.modeSelect.disabled = state.status === 'analyzing';
  elements.configuration.value = state.profile.configuration ?? 'Debug';
  elements.platform.value = state.profile.platform ?? '';
  elements.configuration.disabled = elements.platform.disabled = state.status === 'analyzing';
  elements.backButton.disabled = state.history.length === 0;
  elements.depthSelect.value = String(state.scope.depth ?? 1);
  elements.depthSelect.disabled =
    state.scope.kind !== 'dependencies' && state.scope.kind !== 'dependents';
  const typeOption =
    elements.granularitySelect.querySelector<HTMLOptionElement>('option[value="type"]');
  if (typeOption) {
    typeOption.disabled = !state.capabilities.typeGraph;
    typeOption.title = state.capabilities.typeGraph ? '' : 'Type analysis requires Semantic';
  }
  const controls = elements.filterControls;
  controls.tests.checked = state.filters.includeTests !== false;
  controls.external.checked = state.filters.includeExternal !== false;
  controls.generated.checked = state.filters.includeGenerated !== false;
  for (const [input, selected] of [
    [controls.basis, state.filters.basis],
    [controls.kinds, state.filters.kinds],
    [controls.projectKinds, state.filters.projectKinds],
    [controls.relations, state.filters.relationKinds]
  ] as const)
    for (const option of input.options)
      option.selected = selected?.includes(option.value as never) === true;

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
  requestTree: (
    parentId: string,
    granularity: 'project' | 'namespace' | 'type',
    cursor?: string
  ) => void;
  expanded: Set<string>;
  rerender: () => void;
  dispatch: (action: ViewAction) => void;
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
    renderCyclesTab(elements, state, context.dispatch);
  } else {
    renderAnalysisTab(elements, state, context.dispatch);
  }
}

function renderStructureTab(
  elements: ShellElements,
  state: ViewState,
  context: StructureContext
): void {
  renderSearchResults(elements, state);

  const visible = selectVisibleData(state);
  const entityById = new Map(
    Object.values(state.tree)
      .flatMap((page) => page.items)
      .map((entity) => [entity.id, entity])
  );
  const branch = (
    entity: import('../../src/view/protocolV2').EntitySummary
  ): NavigationTreeNode => ({
    id: entity.id,
    label: entity.name,
    granularity: entity.granularity,
    kind: entity.kind,
    inCycle: entity.inCycle,
    isExternal: entity.isExternal,
    canExpand: entity.granularity !== 'type',
    moreCursor: state.tree[entity.id]?.nextCursor,
    children: (state.tree[entity.id]?.items ?? []).map(branch)
  });
  const tree = state.tree.root
    ? state.tree.root.items.map(branch)
    : buildNavigationTree(visible.nodes);
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
        const entity = entityById.get(entityId);
        if (entity && !state.tree[entityId])
          context.requestTree(entityId, entity.granularity === 'project' ? 'namespace' : 'type');
      }

      context.rerender();
    },
    onLoadMore: (entityId, cursor) =>
      context.requestTree(
        entityId,
        entityById.get(entityId)?.granularity === 'project' ? 'namespace' : 'type',
        cursor
      )
  });
  if (state.tree.root?.nextCursor) {
    const more = document.createElement('button');
    more.type = 'button';
    more.textContent = 'Load more projects';
    more.addEventListener('click', () =>
      context.requestTree('root', 'project', state.tree.root.nextCursor)
    );
    container.append(more);
  }
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

function renderCyclesTab(
  elements: ShellElements,
  state: ViewState,
  dispatch: (action: ViewAction) => void
): void {
  const cycles = state.cycles;
  if (cycles.length === 0) {
    elements.navPaneBody.append(message('No dependency cycles in this analysis.', 'sd-empty'));
    return;
  }

  elements.navPaneBody.append(
    message(
      `${cycles.length} cycle group(s). A group is a set of mutually reachable types; only the witness is a real path.`,
      'sd-note'
    )
  );

  const nameOf = (id: string): string =>
    state.projection?.nodes.find((node) => node.id === id)?.name ?? id;

  const list = document.createElement('ul');
  list.className = 'sd-cycle-list';
  for (const group of cycles) {
    const item = document.createElement('li');
    item.className = 'sd-cycle-item';
    item.dataset.cycleId = group.id;

    const header = document.createElement('div');
    header.className = 'sd-cycle-header';
    const label = document.createElement('strong');
    label.textContent = `${group.memberIds.length} member(s) · ${group.internalRelationIds.length} edge(s)`;
    header.append(label);
    header.append(badge(group.witness ? '実在する閉路あり' : '閉路未確認'));
    item.append(header);

    const focus = document.createElement('button');
    focus.type = 'button';
    focus.className = 'sd-button sd-button-small';
    focus.textContent = 'この循環を表示';
    focus.addEventListener('click', () =>
      dispatch({
        type: 'scopeChanged',
        granularity:
          group.scope === 'project' || group.scope === 'namespace' ? group.scope : 'type',
        scope: { kind: 'cycle', id: group.id, depth: null }
      })
    );
    item.append(focus);

    // The member list is a sorted set and is labelled as such: it is never a route.
    const members = document.createElement('ul');
    members.className = 'sd-cycle-members';
    for (const memberId of [...group.memberIds].sort()) {
      const memberItem = document.createElement('li');
      const memberButton = document.createElement('button');
      memberButton.type = 'button';
      memberButton.className = 'sd-node-item';
      memberButton.textContent = nameOf(memberId);
      memberButton.title = memberId;
      memberButton.addEventListener('click', () =>
        dispatch({ type: 'entitySelected', entityId: memberId })
      );
      memberItem.append(memberButton);
      members.append(memberItem);
    }

    item.append(
      members,
      message('メンバーの並びは経路ではありません（集合を名前順に表示）。', 'sd-note')
    );

    if (group.witness && group.witness.relationIds.length > 0) {
      const pathHeading = document.createElement('h4');
      pathHeading.textContent = `実在する閉路（${group.witness.relationIds.length} 辺）`;
      item.append(pathHeading);
      const path = document.createElement('ol');
      path.className = 'sd-cycle-path';
      group.witness.relationIds.forEach((relationId, index) => {
        const pathItem = document.createElement('li');
        const edge = document.createElement('button');
        edge.type = 'button';
        edge.className = 'sd-node-item';
        edge.textContent = `${nameOf(group.witness!.memberIds[index])} → ${nameOf(
          group.witness!.memberIds[index + 1] ?? group.witness!.memberIds[0]
        )}`;
        edge.title = `${relationId}（この辺の根拠を表示）`;
        edge.addEventListener('click', () => dispatch({ type: 'relationSelected', relationId }));
        pathItem.append(edge);
        path.append(pathItem);
      });
      item.append(path);
    } else {
      item.append(message('実在する閉路は確認できていません（相互到達のみ）。', 'sd-note'));
    }

    list.append(item);
  }

  elements.navPaneBody.append(list);
}

function renderAnalysisTab(
  elements: ShellElements,
  state: ViewState,
  dispatch: (action: ViewAction) => void
): void {
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
  if (state.variantOptions.length) {
    elements.navPaneBody.append(
      message(
        'Target frameworks: Automatic keeps evaluated reference variants separate. A change requires Analyze.',
        'sd-note'
      )
    );
    const projects = new Map<string, ViewState['variantOptions']>();
    for (const variant of state.variantOptions)
      projects.set(variant.projectLogicalId, [
        ...(projects.get(variant.projectLogicalId) ?? []),
        variant
      ]);
    for (const [projectLogicalId, variants] of projects) {
      const label = document.createElement('label');
      label.textContent = variants[0].projectPath;
      const picker = document.createElement('select');
      picker.setAttribute('aria-label', `Target framework: ${variants[0].projectPath}`);
      for (const tfm of ['', ...new Set(variants.map((variant) => variant.targetFramework))]) {
        const option = document.createElement('option');
        option.value = tfm;
        option.textContent = tfm || 'Automatic';
        picker.append(option);
      }
      picker.value =
        state.profile.projectVariants?.find(
          (variant) => variant.projectLogicalId === projectLogicalId
        )?.targetFramework ?? '';
      picker.addEventListener('change', () =>
        dispatch({
          type: 'profileChanged',
          profile: {
            ...state.profile,
            projectVariants: [
              ...(state.profile.projectVariants ?? []).filter(
                (variant) => variant.projectLogicalId !== projectLogicalId
              ),
              ...(picker.value ? [{ projectLogicalId, targetFramework: picker.value }] : [])
            ]
          }
        })
      );
      label.append(picker);
      elements.navPaneBody.append(label);
    }
  }

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

function renderCenter(
  elements: ShellElements,
  state: ViewState,
  graphView: () => GraphView | undefined,
  graphError: () => string | undefined
): void {
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
  elements.mapContent.replaceChildren();
  elements.mapHost.dataset.viewKind = state.viewKind;

  if (!state.projection) {
    elements.mapSummary.textContent = '';
    elements.graphHost.hidden = true;
    elements.mapContent.hidden = false;
    elements.mapContent.append(emptyStateMessage(state));
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
    elements.graphHost.hidden = true;
    elements.mapContent.hidden = false;
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
    elements.mapContent.append(empty);
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

  let graphShown = false;
  if (state.viewKind === 'graph') {
    const view = graphView();
    if (view && !graphError()) {
      view.setSpacing(state.layout);
      graphShown = true;
      elements.graphHost.hidden = false;
      elements.mapContent.hidden = true;
      // The graph shows exactly what the search and filters select, so the summary and
      // the picture can never disagree; the host's totals stay in the summary.
      view.update(
        { ...state.projection, nodes: visible.nodes, edges: visible.edges },
        scopeLabel(state)
      );
      // Selection and inspector state never change the layout; only the highlight.
      view.setSelection(
        state.selection.entityId ? [state.selection.entityId] : [],
        state.selection.relationId ? [state.selection.relationId] : []
      );
    } else {
      const reason = graphError() ?? 'the layout worker is unavailable';
      elements.mapContent.append(
        message(
          `The interactive graph is unavailable (${reason}). The table below shows the same analysis.`,
          'sd-note'
        )
      );
    }
  }

  elements.graphHost.hidden = !graphShown;
  elements.mapContent.hidden = graphShown;

  // The table is rendered even while the graph is shown, so switching views never
  // depends on the graph having succeeded.
  renderEntityTable(elements.mapContent, tableOptions);
  if (state.viewKind === 'graph' && !graphShown) {
    elements.mapContent.prepend(
      message(
        `The interactive graph is unavailable (${graphError() ?? 'the layout worker is unavailable'}). The table below shows the same analysis.`,
        'sd-note'
      )
    );
  }
}

/** Human-readable scope label for the graph header/exports. */
export function imageMetadata(state: ViewState): string[] {
  const notes: string[] = [];
  const visible = selectVisibleData(state);
  if (state.imageOptions.profile) {
    const profile = state.resultProfile ?? state.profile;
    notes.push(
      `SharpDeps: ${state.target?.relativePath ?? 'Unknown target'} · ${state.resultMode ?? state.mode} · ${state.status}`
    );
    notes.push(`Profile: ${profile.configuration ?? 'Debug'} / ${profile.platform ?? 'Default'}`);
    for (const variant of state.variantOptions)
      notes.push(`TFM: ${variant.projectPath} — ${variant.targetFramework}`);
  }
  if (state.imageOptions.omissions) {
    notes.push(`Scope: ${scopeLabel(state)} · Search: ${state.search || '(none)'}`);
    notes.push(
      `Shown ${visible.nodes.length}/${visible.totalNodeCount} nodes; ${visible.edges.length}/${visible.totalEdgeCount} relations; truncated: ${state.projectionTruncated}`
    );
    notes.push(`Filters: ${JSON.stringify(state.filters)}`);
    for (const limitation of state.limitations) notes.push(`Limitation: ${limitation.message}`);
  }
  if (state.imageOptions.legend) {
    notes.push(
      'Legend: dashed = inferred; solid = declared/evaluated/resolved (see relation kind); red / ⟳ = cycle; G = generated; ext = external'
    );
    notes.push(
      `Project kinds: ${[...new Set(visible.nodes.map((node) => node.projectKind ?? node.kind).filter(Boolean))].sort().join(', ') || '(none)'}`
    );
  }
  return notes;
}

function scopeLabel(state: ViewState): string {
  const scope = state.scope;
  if (!scope || scope.kind === 'root') {
    return `all ${state.granularity}`;
  }

  const origin = state.projection?.nodes.find((node) => node.id === scope.id);
  const name = origin?.name ?? scope.id ?? '';
  const depth =
    scope.kind === 'dependencies' || scope.kind === 'dependents'
      ? ` (depth ${scope.depth ?? 1})`
      : '';
  return `${scope.kind}: ${name}${depth}`;
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
