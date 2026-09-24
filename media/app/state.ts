// Webview view state, actions, and selectors (SD-015).
//
// The state is the single source of truth: the DOM is rendered from it and never
// read back to derive it. The reducer is pure so every UI state (initial, partial,
// failed, stale, filtered-to-empty) can be produced from a fixture in tests.

import type { Granularity } from '../../src/analyzer/reportV2';
import type { EntitySummary, Filters, Projection, Scope } from '../../src/view/protocolV2';
import {
  DEFAULT_SORT,
  toggleSort,
  TABLE_PAGE_SIZE,
  classifySearchResult,
  paginate,
  sortEntities,
  type SearchResultVisibility,
  type SortState
} from './query';

export type ViewKind = 'graph' | 'table';

export type AnalysisStage = 'discover' | 'load' | 'compile' | 'extract' | 'aggregate' | 'write';

/**
 * Analysis status as the UI shows it. `partial` and `stale` are distinct from
 * `complete` and from each other: an incomplete analysis is never presented as a
 * finished one.
 */
export type AnalysisStatus =
  'noTarget' | 'ready' | 'analyzing' | 'complete' | 'partial' | 'failed' | 'cancelled' | 'stale';

export interface HistoryEntry {
  scope: Scope;
  granularity: Granularity;
  selectionId?: string;
}

export interface ViewState {
  /** Target shown in the top bar. */
  target: { name: string; relativePath: string } | null;
  mode: 'quick' | 'semantic';
  granularity: Granularity;
  viewKind: ViewKind;
  scope: Scope;
  search: string;
  filters: Filters;
  selection: { entityId?: string; relationId?: string };
  status: AnalysisStatus;
  statusMessage: string;
  analysisId?: string;
  progress?: { stage: AnalysisStage; loaded?: number; analyzed?: number; elapsedMs: number };
  coverage?: {
    discovered: number;
    loaded: number;
    analyzed: number;
    failed: number;
    skipped: number;
  };
  projection: Projection | null;
  /** True when the projection only contains part of the analysis. */
  projectionTruncated: boolean;
  details: { entityId: string; dependencies: EntitySummary[]; dependents: EntitySummary[] } | null;
  evidence: {
    relationId: string;
    total: number;
    items: Record<string, unknown>[];
    nextCursor?: string | null;
  } | null;
  limitations: Array<{ code: string; message: string }>;
  error?: { code: string; message: string };
  paneWidths: { navigation: number; inspector: number };
  inspectorOpen: boolean;
  history: HistoryEntry[];
  /** Table view: sort and page. Both are state so the same input renders the same. */
  tableSort: SortState;
  tablePage: number;
  /** Full-index search results, kept separately from the display projection. */
  searchResults: { query: string; items: EntitySummary[]; total: number; pending: boolean };
  /** Entities the user chose to show even though filters exclude them. */
  temporaryDisplayIds: string[];
}

export const HISTORY_LIMIT = 20;

export const INITIAL_STATE: ViewState = {
  target: null,
  mode: 'quick',
  granularity: 'type',
  viewKind: 'graph',
  scope: { kind: 'root', id: null, depth: null },
  search: '',
  filters: {},
  selection: {},
  status: 'noTarget',
  statusMessage: 'No analysis target selected.',
  projection: null,
  projectionTruncated: false,
  details: null,
  evidence: null,
  limitations: [],
  paneWidths: { navigation: 220, inspector: 320 },
  inspectorOpen: false,
  history: [],
  tableSort: DEFAULT_SORT,
  tablePage: 0,
  searchResults: { query: '', items: [], total: 0, pending: false },
  temporaryDisplayIds: []
};

export type ViewAction =
  | { type: 'targetSelected'; name: string; relativePath: string }
  | { type: 'analyzeStarted'; analysisId: string; mode: 'quick' | 'semantic' }
  | {
      type: 'analysisProgress';
      stage: AnalysisStage;
      loaded?: number;
      analyzed?: number;
      elapsedMs: number;
    }
  | {
      type: 'analysisComplete';
      analysisId: string;
      completeness: 'completeWithinScope' | 'partial' | 'failed';
      coverage: ViewState['coverage'];
      limitations?: Array<{ code: string; message: string }>;
    }
  | { type: 'analysisFailed'; analysisId?: string; message: string; cancelled?: boolean }
  | { type: 'analysisStale'; message: string }
  | { type: 'projectionReceived'; projection: Projection }
  | {
      type: 'detailsReceived';
      entityId: string;
      dependencies: EntitySummary[];
      dependents: EntitySummary[];
    }
  | {
      type: 'evidenceReceived';
      relationId: string;
      total: number;
      items: Record<string, unknown>[];
      nextCursor?: string | null;
    }
  | { type: 'granularityChanged'; granularity: Granularity }
  | { type: 'viewKindChanged'; viewKind: ViewKind }
  | { type: 'scopeChanged'; scope: Scope }
  | { type: 'searchChanged'; search: string }
  | { type: 'searchStarted'; query: string }
  | { type: 'searchResultsReceived'; query: string; items: EntitySummary[]; total: number }
  | { type: 'searchCleared' }
  | { type: 'tableSortChanged'; key: SortState['key'] }
  | { type: 'tablePageChanged'; page: number }
  | { type: 'temporaryDisplayAdded'; entityId: string }
  | { type: 'temporaryDisplayCleared' }
  | { type: 'filtersChanged'; filters: Filters }
  | { type: 'entitySelected'; entityId: string }
  | { type: 'relationSelected'; relationId: string }
  | { type: 'selectionCleared' }
  | { type: 'inspectorToggled' }
  | { type: 'paneResized'; pane: 'navigation' | 'inspector'; width: number }
  | { type: 'historyBack' }
  | { type: 'errorRaised'; code: string; message: string }
  | { type: 'stateRestored'; state: Partial<ViewState> };

export function viewReducer(state: ViewState, action: ViewAction): ViewState {
  switch (action.type) {
    case 'targetSelected':
      return {
        ...state,
        target: { name: action.name, relativePath: action.relativePath },
        status: 'ready',
        statusMessage: 'Ready to analyze.',
        error: undefined
      };

    case 'analyzeStarted':
      return {
        ...state,
        mode: action.mode,
        analysisId: action.analysisId,
        status: 'analyzing',
        statusMessage: 'Analyzing…',
        progress: undefined,
        // The previous projection stays visible until a new one arrives; it is marked
        // as stale rather than cleared, so the map does not blink out.
        error: undefined
      };

    case 'analysisProgress':
      return {
        ...state,
        status: state.status === 'analyzing' ? 'analyzing' : state.status,
        progress: {
          stage: action.stage,
          loaded: action.loaded,
          analyzed: action.analyzed,
          elapsedMs: action.elapsedMs
        },
        statusMessage: progressMessage(action)
      };

    case 'analysisComplete':
      return {
        ...state,
        analysisId: action.analysisId,
        status: action.completeness === 'completeWithinScope' ? 'complete' : action.completeness,
        statusMessage: completionMessage(action),
        coverage: action.coverage,
        limitations: action.limitations ?? state.limitations,
        progress: undefined,
        error: undefined
      };

    case 'analysisFailed':
      return {
        ...state,
        status: action.cancelled ? 'cancelled' : 'failed',
        statusMessage: action.cancelled ? 'Analysis stopped.' : action.message,
        error: action.cancelled ? undefined : { code: 'analysis.failed', message: action.message },
        progress: undefined,
        analysisId: action.analysisId ?? state.analysisId
      };

    case 'analysisStale':
      return { ...state, status: 'stale', statusMessage: action.message };

    case 'projectionReceived':
      return {
        ...state,
        projection: action.projection,
        projectionTruncated: action.projection.truncated,
        details: null,
        evidence: null
      };

    case 'detailsReceived':
      return {
        ...state,
        details: {
          entityId: action.entityId,
          dependencies: action.dependencies,
          dependents: action.dependents
        },
        inspectorOpen: true
      };

    case 'evidenceReceived':
      return {
        ...state,
        evidence: {
          relationId: action.relationId,
          total: action.total,
          items: action.items,
          nextCursor: action.nextCursor
        },
        inspectorOpen: true
      };

    case 'granularityChanged':
      return {
        ...state,
        granularity: action.granularity,
        scope: { kind: 'root', id: null, depth: null },
        selection: {},
        projection: null,
        details: null,
        evidence: null,
        history: pushHistory(state, {
          scope: { kind: 'root', id: null, depth: null },
          granularity: action.granularity
        })
      };

    case 'viewKindChanged':
      // Switching between graph and table keeps scope, search, filters, and selection.
      return { ...state, viewKind: action.viewKind };

    case 'scopeChanged':
      return {
        ...state,
        scope: action.scope,
        selection: {},
        projection: null,
        details: null,
        evidence: null,
        history: pushHistory(state, {
          scope: action.scope,
          granularity: state.granularity,
          selectionId: state.selection.entityId
        })
      };

    case 'searchChanged':
      // The host query is asynchronous: mark it pending and reset the page so the
      // table always shows the first page of the new query.
      return {
        ...state,
        search: action.search,
        tablePage: 0,
        searchResults: { ...state.searchResults, query: action.search, pending: true }
      };

    case 'searchStarted':
      return {
        ...state,
        searchResults: { ...state.searchResults, query: action.query, pending: true }
      };

    case 'searchResultsReceived':
      return {
        ...state,
        searchResults: {
          query: action.query,
          items: action.items,
          total: action.total,
          pending: false
        }
      };

    case 'searchCleared':
      return {
        ...state,
        search: '',
        tablePage: 0,
        temporaryDisplayIds: [],
        searchResults: { query: '', items: [], total: 0, pending: false }
      };

    case 'tableSortChanged':
      return { ...state, tableSort: toggleSort(state.tableSort, action.key), tablePage: 0 };

    case 'tablePageChanged':
      return { ...state, tablePage: Math.max(0, action.page) };

    case 'temporaryDisplayAdded':
      return state.temporaryDisplayIds.includes(action.entityId)
        ? state
        : { ...state, temporaryDisplayIds: [...state.temporaryDisplayIds, action.entityId] };

    case 'temporaryDisplayCleared':
      return state.temporaryDisplayIds.length === 0 ? state : { ...state, temporaryDisplayIds: [] };

    case 'filtersChanged':
      return { ...state, filters: action.filters };

    case 'entitySelected':
      return { ...state, selection: { entityId: action.entityId } };

    case 'relationSelected':
      return { ...state, selection: { relationId: action.relationId } };

    case 'selectionCleared':
      return { ...state, selection: {}, details: null, evidence: null };

    case 'inspectorToggled':
      return { ...state, inspectorOpen: !state.inspectorOpen };

    case 'paneResized':
      return {
        ...state,
        paneWidths: { ...state.paneWidths, [action.pane]: Math.max(120, Math.round(action.width)) }
      };

    case 'historyBack':
      return back(state);

    case 'errorRaised':
      return { ...state, error: { code: action.code, message: action.message } };

    case 'stateRestored':
      return sanitizeRestoredState({ ...state, ...action.state });

    default:
      return state;
  }
}

function pushHistory(state: ViewState, entry: HistoryEntry): HistoryEntry[] {
  return [...state.history, entry].slice(-HISTORY_LIMIT);
}

function back(state: ViewState): ViewState {
  const previous = state.history.at(-2);
  if (!previous) {
    return state;
  }

  return {
    ...state,
    history: state.history.slice(0, -2),
    scope: previous.scope,
    granularity: previous.granularity,
    selection: previous.selectionId ? { entityId: previous.selectionId } : {},
    projection: null,
    details: null,
    evidence: null
  };
}

/** Restored state is untrusted: keep the safe defaults for anything unexpected. */
function sanitizeRestoredState(state: ViewState): ViewState {
  const granularity: Granularity = ['project', 'namespace', 'type'].includes(state.granularity)
    ? state.granularity
    : INITIAL_STATE.granularity;
  const viewKind: ViewKind = state.viewKind === 'table' ? 'table' : 'graph';

  return {
    ...state,
    granularity,
    viewKind,
    paneWidths: {
      navigation: clampWidth(state.paneWidths?.navigation, INITIAL_STATE.paneWidths.navigation),
      inspector: clampWidth(state.paneWidths?.inspector, INITIAL_STATE.paneWidths.inspector)
    },
    history: (state.history ?? []).slice(-HISTORY_LIMIT),
    // A restored view never pretends an analysis is running.
    status: state.status === 'analyzing' ? 'ready' : state.status,
    progress: undefined
  };
}

function clampWidth(value: number | undefined, fallback: number): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return fallback;
  }

  return Math.min(Math.max(Math.round(value), 120), 800);
}

function progressMessage(action: Extract<ViewAction, { type: 'analysisProgress' }>): string {
  const parts: string[] = [stageLabel(action.stage)];
  if (typeof action.loaded === 'number') {
    parts.push(`loaded ${action.loaded}`);
  }

  if (typeof action.analyzed === 'number') {
    parts.push(`analyzed ${action.analyzed}`);
  }

  return parts.join(' · ');
}

function completionMessage(action: Extract<ViewAction, { type: 'analysisComplete' }>): string {
  const coverage = action.coverage;
  const base =
    action.completeness === 'completeWithinScope'
      ? 'Analysis complete'
      : action.completeness === 'partial'
        ? 'Analysis complete (partial)'
        : 'Analysis failed';
  if (!coverage) {
    return base;
  }

  return `${base} · ${coverage.analyzed}/${coverage.discovered} project(s) analyzed`;
}

export function stageLabel(stage: AnalysisStage): string {
  switch (stage) {
    case 'discover':
      return 'Discovering projects';
    case 'load':
      return 'Loading projects';
    case 'compile':
      return 'Compiling';
    case 'extract':
      return 'Collecting evidence';
    case 'aggregate':
      return 'Aggregating';
    default:
      return 'Writing results';
  }
}

// ---- Selectors -----------------------------------------------------------------

export interface VisibleData {
  nodes: EntitySummary[];
  edges: Projection['edges'];
  /** Node count after search and filters, before the display budget. */
  matchedNodeCount: number;
  filterCount: number;
  isFilteredEmpty: boolean;
  totalNodeCount: number;
  totalEdgeCount: number;
}

/** Applies search and filters to the projection without touching the analysis. */
export function selectVisibleData(state: ViewState): VisibleData {
  const projection = state.projection;
  if (!projection) {
    return {
      nodes: [],
      edges: [],
      matchedNodeCount: 0,
      filterCount: countFilters(state.filters),
      isFilteredEmpty: false,
      totalNodeCount: 0,
      totalEdgeCount: 0
    };
  }

  const needle = state.search.trim().toLowerCase();
  const filters = state.filters;
  // Entities the user explicitly chose to show are kept even when they are outside
  // the current filters; the table marks those rows.
  const temporary = new Set(state.temporaryDisplayIds);
  const nodes = projection.nodes.filter((node) => {
    if (temporary.has(node.id)) {
      return true;
    }

    if (needle.length > 0 && !node.name.toLowerCase().includes(needle)) {
      return false;
    }

    if (
      filters.kinds &&
      filters.kinds.length > 0 &&
      node.kind &&
      !filters.kinds.includes(node.kind)
    ) {
      return false;
    }

    if (filters.includeExternal === false && node.isExternal) {
      return false;
    }

    if (filters.includeGenerated === false && node.isGenerated) {
      return false;
    }

    return true;
  });

  const nodeIds = new Set(nodes.map((node) => node.id));
  const edges = projection.edges.filter((edge) => {
    // Both ends must remain visible, otherwise the edge would dangle.
    if (!nodeIds.has(edge.sourceId) || !nodeIds.has(edge.targetId)) {
      return false;
    }

    if (filters.relationKinds && filters.relationKinds.length > 0) {
      return filters.relationKinds.some((kind) => edge.kinds.includes(kind));
    }

    return true;
  });

  return {
    nodes,
    edges,
    matchedNodeCount: nodes.length,
    filterCount: countFilters(filters),
    isFilteredEmpty: nodes.length === 0 && projection.nodes.length > 0,
    totalNodeCount: projection.totalNodeCount,
    totalEdgeCount: projection.totalEdgeCount
  };
}

/** Breadcrumb segments for the current scope. */
export function selectBreadcrumbs(state: ViewState): string[] {
  const crumbs: string[] = [state.target?.name ?? 'No target'];
  if (state.scope.kind !== 'root') {
    crumbs.push(state.scope.kind);
    if (state.selection.entityId && state.details?.entityId === state.selection.entityId) {
      const entity = state.details.dependencies.concat(state.details.dependents);
      void entity;
    }
  }

  return crumbs;
}

/** Footer text: what is shown out of the analysis, always including the mode. */
export function selectStatusFooter(state: ViewState): string {
  const visible = selectVisibleData(state);
  const parts = [`Showing ${visible.nodes.length}/${visible.totalNodeCount} node(s)`];
  parts.push(`${visible.edges.length}/${visible.totalEdgeCount} relation(s)`);
  parts.push(state.mode === 'quick' ? 'Quick' : 'Semantic');
  if (state.status === 'stale') {
    parts.push('stale result');
  } else if (state.status === 'partial') {
    parts.push('partial result');
  }

  if (state.projectionTruncated) {
    parts.push('display budget applied');
  }

  return parts.join(' · ');
}

/**
 * Table rows: filtered, sorted, then paged. Temporary rows are marked so the UI can
 * show that they are outside the current filters.
 */
export function selectTableRows(state: ViewState): {
  rows: Array<{ entity: EntitySummary; temporary: boolean }>;
  page: number;
  pageCount: number;
  totalItems: number;
  isFilteredEmpty: boolean;
} {
  const visible = selectVisibleData(state);
  const temporary = new Set(state.temporaryDisplayIds);
  const dependencyCount = dependencyCounter(state);
  const sorted = sortEntities(visible.nodes, state.tableSort, {
    dependencyCount: (id) => dependencyCount.outgoing.get(id) ?? 0,
    dependentCount: (id) => dependencyCount.incoming.get(id) ?? 0
  });
  const page = paginate(sorted, state.tablePage, TABLE_PAGE_SIZE);

  return {
    rows: page.items.map((entity) => ({ entity, temporary: temporary.has(entity.id) })),
    page: page.page,
    pageCount: page.pageCount,
    totalItems: page.totalItems,
    isFilteredEmpty: visible.isFilteredEmpty
  };
}

/** Search hits with their relation to the current view, so none look "not found". */
export function selectSearchPresentation(
  state: ViewState
): Array<{ entity: EntitySummary; visibility: SearchResultVisibility }> {
  const visibleIds = new Set(selectVisibleData(state).nodes.map((node) => node.id));
  return state.searchResults.items.map((entity) => ({
    entity,
    visibility: classifySearchResult(entity, {
      visibleIds,
      filters: state.filters,
      search: state.search
    })
  }));
}

function dependencyCounter(state: ViewState): {
  outgoing: Map<string, number>;
  incoming: Map<string, number>;
} {
  const outgoing = new Map<string, number>();
  const incoming = new Map<string, number>();
  for (const edge of state.projection?.edges ?? []) {
    outgoing.set(edge.sourceId, (outgoing.get(edge.sourceId) ?? 0) + 1);
    incoming.set(edge.targetId, (incoming.get(edge.targetId) ?? 0) + 1);
  }

  return { outgoing, incoming };
}

export function countFilters(filters: Filters): number {
  let count = 0;
  for (const value of Object.values(filters)) {
    if (Array.isArray(value)) {
      if (value.length > 0) {
        count++;
      }
    } else if (typeof value === 'boolean') {
      count++;
    }
  }

  return count;
}
