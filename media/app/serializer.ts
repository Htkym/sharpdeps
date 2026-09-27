// View state serialization (SD-021).
//
// Only small, path-free fields are persisted: the target name, selection, filters,
// scope, panes, table page, and the graph camera. Nothing here can start an analysis,
// and an unknown schema version or a malformed value is ignored instead of trusted.
// Pane widths are clamped so a state saved on a wide screen cannot break a narrow one.

import type { Filters, Scope } from '../../src/view/protocolV2';
import type { Granularity } from '../../src/analyzer/reportV2';
import type { ViewState, ViewKind } from './state';

export const VIEW_STATE_VERSION = 1;

export interface PersistedViewState {
  version: number;
  targetName?: string;
  targetRelativePath?: string;
  selection?: { entityId?: string; relationId?: string };
  filters?: Record<string, unknown>;
  granularity?: Granularity;
  viewKind?: ViewKind;
  scope?: Scope;
  search?: string;
  paneWidths?: { navigation: number; inspector: number };
  tablePage?: number;
  camera?: { zoom: number; scrollLeft: number; scrollTop: number };
}

export interface RestoredViewState {
  state: Partial<ViewState>;
  /** True when the persisted state was written by a different schema version. */
  versionMismatch: boolean;
}

const MIN_NAV_WIDTH = 140;
const MAX_NAV_WIDTH = 480;
const MIN_INSPECTOR_WIDTH = 200;
const MAX_INSPECTOR_WIDTH = 720;
const MIN_ZOOM = 0.2;
const MAX_ZOOM = 6;
const MAX_SEARCH_LENGTH = 200;

export function serializeViewState(
  state: ViewState,
  camera?: { zoom: number; scrollLeft: number; scrollTop: number }
): PersistedViewState {
  return {
    version: VIEW_STATE_VERSION,
    targetName: state.target?.name,
    targetRelativePath: state.target?.relativePath,
    selection: { ...state.selection },
    filters: { ...state.filters } as Record<string, unknown>,
    granularity: state.granularity,
    viewKind: state.viewKind,
    scope: state.scope,
    search: state.search,
    paneWidths: { ...state.paneWidths },
    tablePage: state.tablePage,
    camera
  };
}

export function deserializeViewState(raw: unknown): RestoredViewState {
  if (typeof raw !== 'object' || raw === null) {
    return { state: {}, versionMismatch: false };
  }

  const value = raw as Record<string, unknown>;
  if (value.version !== VIEW_STATE_VERSION) {
    // An older or newer schema is not guessed at; the view simply starts fresh.
    return { state: {}, versionMismatch: typeof value.version === 'number' };
  }

  const state: Partial<ViewState> = {};

  const targetName = stringOf(value.targetName, 200);
  const targetRelativePath = stringOf(value.targetRelativePath, 500);
  if (targetName && targetRelativePath) {
    state.target = { name: targetName, relativePath: targetRelativePath };
  }

  const selection = recordOf(value.selection);
  if (selection) {
    const entityId = stringOf(selection.entityId, 64);
    const relationId = stringOf(selection.relationId, 64);
    if (entityId || relationId) {
      state.selection = { entityId, relationId };
    }
  }

  const filters = recordOf(value.filters);
  if (filters) {
    state.filters = sanitizeFilters(filters);
  }

  const granularity = value.granularity;
  if (granularity === 'project' || granularity === 'namespace' || granularity === 'type') {
    state.granularity = granularity;
  }

  const viewKind = value.viewKind;
  if (viewKind === 'graph' || viewKind === 'table') {
    state.viewKind = viewKind;
  }

  const scope = sanitizeScope(value.scope);
  if (scope) {
    state.scope = scope;
  }

  const search = stringOf(value.search, MAX_SEARCH_LENGTH);
  if (search !== undefined) {
    state.search = search;
  }

  const paneWidths = recordOf(value.paneWidths);
  if (paneWidths) {
    state.paneWidths = {
      navigation: clampNumber(paneWidths.navigation, MIN_NAV_WIDTH, MAX_NAV_WIDTH, 220),
      inspector: clampNumber(paneWidths.inspector, MIN_INSPECTOR_WIDTH, MAX_INSPECTOR_WIDTH, 320)
    };
  }

  const tablePage = numberOf(value.tablePage);
  if (tablePage !== undefined && tablePage >= 0) {
    state.tablePage = Math.floor(tablePage);
  }

  return { state, versionMismatch: false };
}

export function restoreViewState(raw: unknown): RestoredViewState {
  const restored = deserializeViewState(raw);
  const camera = deserializeCamera((raw as { camera?: unknown } | null)?.camera);
  if (camera) {
    restored.state.camera = camera;
  }

  return restored;
}

/** The camera part is restored separately: it belongs to the graph view, not the shell. */
export function deserializeCamera(
  raw: unknown
): { zoom: number; scrollLeft: number; scrollTop: number } | undefined {
  const value = recordOf(raw);
  if (!value) {
    return undefined;
  }

  const zoom = numberOf(value.zoom);
  const scrollLeft = numberOf(value.scrollLeft);
  const scrollTop = numberOf(value.scrollTop);
  if (zoom === undefined || scrollLeft === undefined || scrollTop === undefined) {
    return undefined;
  }

  return {
    // Zoom keeps its fraction: only pane widths are rounded to whole pixels.
    zoom: Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, zoom)),
    scrollLeft: Math.max(0, scrollLeft),
    scrollTop: Math.max(0, scrollTop)
  };
}

function sanitizeFilters(filters: Record<string, unknown>): Filters {
  const sanitized: Filters = {};
  const kinds = stringArray(filters.kinds, 50);
  if (kinds) {
    sanitized.kinds = kinds;
  }

  const projectKinds = stringArray(filters.projectKinds, 20);
  if (projectKinds) {
    sanitized.projectKinds = projectKinds;
  }

  const basis = stringArray(filters.basis, 10);
  if (basis) {
    sanitized.basis = basis;
  }

  for (const key of ['includeGenerated', 'includeExternal', 'includeTests'] as const) {
    if (typeof filters[key] === 'boolean') {
      sanitized[key] = filters[key];
    }
  }

  return sanitized;
}

function sanitizeScope(value: unknown): Scope | undefined {
  const scope = recordOf(value);
  if (!scope) {
    return undefined;
  }

  const allowed: Scope['kind'][] = [
    'root',
    'project',
    'namespace',
    'type',
    'dependencies',
    'dependents',
    'cycle'
  ];
  const kind = allowed.find((entry) => entry === scope.kind);
  if (!kind) {
    return undefined;
  }

  const id = stringOf(scope.id, 64) ?? null;
  const depth = numberOf(scope.depth);

  // A local scope without an id would be meaningless (and would silently show the whole
  // graph), so it degrades to root instead.
  if (kind !== 'root' && !id) {
    return { kind: 'root', id: null, depth: null };
  }

  return { kind, id, depth: depth === undefined ? null : depth };
}

function stringArray(value: unknown, limit: number): string[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }

  return value.filter((entry): entry is string => typeof entry === 'string').slice(0, limit);
}

function clampNumber(value: unknown, min: number, max: number, fallback: number): number {
  const numeric = numberOf(value);
  if (numeric === undefined) {
    return fallback;
  }

  return Math.min(max, Math.max(min, Math.round(numeric)));
}

function stringOf(value: unknown, maxLength: number): string | undefined {
  return typeof value === 'string' && value.length > 0 && value.length <= maxLength
    ? value
    : undefined;
}

function numberOf(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function recordOf(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}
