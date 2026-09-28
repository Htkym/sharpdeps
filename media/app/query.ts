// Query helpers for the table, tree, and search (SD-016).
//
// Pure functions only: filters, sorting, paging, and the classification of a search
// result against what the current view shows. Keeping them pure makes "same input →
// same sort and page boundaries" a testable property.

import type { Filters } from '../../src/view/protocolV2';
import type { EntitySummary } from '../../src/view/protocolV2';

export type SortKey = 'name' | 'kind' | 'project' | 'dependencies' | 'dependents' | 'cycle';

export interface SortState {
  key: SortKey;
  direction: 'asc' | 'desc';
}

export const DEFAULT_SORT: SortState = { key: 'name', direction: 'asc' };
export const TABLE_PAGE_SIZE = 100;

/**
 * Filters are OR within one category and AND across categories: a node must satisfy
 * every category the user set, and may match any value inside a category.
 */
export function matchesFilters(
  entity: EntitySummary,
  filters: Filters,
  relationKinds?: readonly string[]
): boolean {
  if (filters.kinds && filters.kinds.length > 0) {
    if (!entity.kind || !filters.kinds.includes(entity.kind)) {
      return false;
    }
  }

  if (filters.includeExternal === false && entity.isExternal === true) {
    return false;
  }

  if (filters.includeGenerated === false && entity.isGenerated === true) {
    return false;
  }

  if (filters.projectKinds && filters.projectKinds.length > 0) {
    // Project kind is only meaningful for project entities; other entities keep the
    // category-neutral behaviour instead of being filtered out silently.
    if (entity.granularity === 'project' && !filters.projectKinds.includes(entity.kind ?? '')) {
      return false;
    }
  }

  if (filters.relationKinds && filters.relationKinds.length > 0 && relationKinds !== undefined) {
    if (!filters.relationKinds.some((kind) => relationKinds.includes(kind))) {
      return false;
    }
  }

  return true;
}

export function matchesSearch(entity: EntitySummary, query: string): boolean {
  const needle = query.trim().toLowerCase();
  if (needle.length === 0) {
    return true;
  }

  return entity.name.toLowerCase().includes(needle);
}

export interface SortContext {
  dependencyCount: (entityId: string) => number;
  dependentCount: (entityId: string) => number;
}

/** Stable ordering: ties are broken by id so the same input yields the same order. */
export function sortEntities(
  entities: readonly EntitySummary[],
  sort: SortState,
  context: SortContext
): EntitySummary[] {
  const factor = sort.direction === 'asc' ? 1 : -1;
  return [...entities].sort((left, right) => {
    const primary = compareBy(left, right, sort.key, context);
    if (primary !== 0) {
      return primary * factor;
    }

    return left.id.localeCompare(right.id);
  });
}

function compareBy(
  left: EntitySummary,
  right: EntitySummary,
  key: SortKey,
  context: SortContext
): number {
  switch (key) {
    case 'kind':
      return (left.kind ?? '').localeCompare(right.kind ?? '');
    case 'project':
      return (left.projectName ?? '').localeCompare(right.projectName ?? '');
    case 'dependencies':
      return context.dependencyCount(left.id) - context.dependencyCount(right.id);
    case 'dependents':
      return context.dependentCount(left.id) - context.dependentCount(right.id);
    case 'cycle':
      return Number(left.inCycle === true) - Number(right.inCycle === true);
    default:
      return left.name.localeCompare(right.name);
  }
}

export interface Page<T> {
  items: T[];
  page: number;
  pageCount: number;
  totalItems: number;
}

export function paginate<T>(
  items: readonly T[],
  page: number,
  pageSize = TABLE_PAGE_SIZE
): Page<T> {
  const safePageSize = Math.max(1, Math.floor(pageSize));
  const pageCount = Math.max(1, Math.ceil(items.length / safePageSize));
  const safePage = Math.min(Math.max(0, Math.floor(page)), pageCount - 1);
  const start = safePage * safePageSize;
  return {
    items: items.slice(start, start + safePageSize),
    page: safePage,
    pageCount,
    totalItems: items.length
  };
}

export function toggleSort(current: SortState, key: SortKey): SortState {
  if (current.key !== key) {
    return { key, direction: 'asc' };
  }

  return { key, direction: current.direction === 'asc' ? 'desc' : 'asc' };
}

/**
 * How a search result relates to what the view currently shows. A hit outside the
 * projection or the filters must be shown as such instead of being dropped.
 */
export type SearchResultVisibility = 'visible' | 'outsideFilters' | 'outsideBudget';

export function classifySearchResult(
  entity: EntitySummary,
  options: {
    visibleIds: ReadonlySet<string>;
    filters: Filters;
    search: string;
  }
): SearchResultVisibility {
  if (options.visibleIds.has(entity.id)) {
    return 'visible';
  }

  return matchesFilters(entity, options.filters) && matchesSearch(entity, options.search)
    ? 'outsideBudget'
    : 'outsideFilters';
}

/** Filter values as chips, for the toolbar count and the reset control. */
export function describeFilters(filters: Filters): Array<{ category: string; values: string[] }> {
  const described: Array<{ category: string; values: string[] }> = [];
  for (const [category, value] of Object.entries(filters)) {
    if (Array.isArray(value) && value.length > 0) {
      described.push({ category, values: value });
    } else if (typeof value === 'boolean') {
      described.push({ category, values: [value ? 'on' : 'off'] });
    }
  }

  return described;
}
