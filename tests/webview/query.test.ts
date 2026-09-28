// Query layer tests (SD-016): filters, stable sorting, page boundaries, and how a
// search hit relates to what the view shows.

import { describe, expect, it } from 'vitest';
import type { EntitySummary } from '../../src/view/protocolV2';
import {
  DEFAULT_SORT,
  TABLE_PAGE_SIZE,
  classifySearchResult,
  matchesFilters,
  paginate,
  sortEntities,
  toggleSort
} from '../../media/app/query';

function entity(id: string, name: string, extra: Partial<EntitySummary> = {}): EntitySummary {
  return { id, name, granularity: 'type', kind: 'class', projectName: 'Core', ...extra };
}

const context = {
  dependencyCount: (id: string) => (id === 'ty_1' ? 3 : id === 'ty_2' ? 1 : 0),
  dependentCount: (id: string) => (id === 'ty_3' ? 2 : 0)
};

describe('filters', () => {
  it('uses OR within a category and AND across categories', () => {
    const node = entity('ty_1', 'Order', { kind: 'class' });

    // Within `kinds`, either value matches.
    expect(matchesFilters(node, { kinds: ['class', 'interface'] })).toBe(true);
    expect(matchesFilters(node, { kinds: ['interface'] })).toBe(false);

    // Across categories, all set categories must match.
    expect(matchesFilters(node, { kinds: ['class'], includeExternal: false })).toBe(true);
    expect(
      matchesFilters({ ...node, isExternal: true }, { kinds: ['class'], includeExternal: false })
    ).toBe(false);
  });

  it('treats relation kind filters as a set that must intersect', () => {
    expect(
      matchesFilters(entity('ty_1', 'A'), { relationKinds: ['calls'] }, ['calls', 'typeUse'])
    ).toBe(true);
    expect(matchesFilters(entity('ty_1', 'A'), { relationKinds: ['calls'] }, ['typeUse'])).toBe(
      false
    );
    // Without relation context the category is ignored rather than filtering everything out.
    expect(matchesFilters(entity('ty_1', 'A'), { relationKinds: ['calls'] })).toBe(true);
  });
});

describe('sorting and paging', () => {
  it('sorts by name and breaks ties by id, so the order is reproducible', () => {
    const nodes = [entity('ty_3', 'Beta'), entity('ty_1', 'Beta'), entity('ty_2', 'Alpha')];

    const sorted = sortEntities(nodes, DEFAULT_SORT, context);

    expect(sorted.map((node) => node.id)).toEqual(['ty_2', 'ty_1', 'ty_3']);
    expect(sortEntities(nodes, DEFAULT_SORT, context).map((node) => node.id)).toEqual(
      sorted.map((node) => node.id)
    );
  });

  it('toggles the direction for the same key and resets it for another key', () => {
    expect(toggleSort(DEFAULT_SORT, 'name')).toEqual({ key: 'name', direction: 'desc' });
    expect(toggleSort({ key: 'name', direction: 'desc' }, 'kind')).toEqual({
      key: 'kind',
      direction: 'asc'
    });
  });

  it('sorts by counts and cycle flags', () => {
    const nodes = [entity('ty_1', 'A'), entity('ty_2', 'B'), entity('ty_3', 'C')];

    expect(
      sortEntities(nodes, { key: 'dependencies', direction: 'desc' }, context).map((n) => n.id)
    ).toEqual(['ty_1', 'ty_2', 'ty_3']);
    expect(
      sortEntities(nodes, { key: 'dependents', direction: 'desc' }, context).map((n) => n.id)
    ).toEqual(['ty_3', 'ty_1', 'ty_2']);
  });

  it('produces the same page boundaries for the same input', () => {
    const nodes = Array.from({ length: 250 }, (_, index) =>
      entity(`ty_${String(index).padStart(16, '0')}`, `Node${String(index).padStart(3, '0')}`)
    );

    const first = paginate(sortEntities(nodes, DEFAULT_SORT, context), 0);
    const second = paginate(sortEntities([...nodes].reverse(), DEFAULT_SORT, context), 0);

    expect(first.pageCount).toBe(3);
    expect(first.items).toHaveLength(TABLE_PAGE_SIZE);
    expect(first.items.map((node) => node.id)).toEqual(second.items.map((node) => node.id));

    const last = paginate(nodes, 2);
    expect(last.items).toHaveLength(50);
    expect(last.page).toBe(2);

    // Out-of-range pages clamp instead of returning nothing.
    expect(paginate(nodes, 99).page).toBe(2);
  });
});

describe('search result classification', () => {
  const options = {
    visibleIds: new Set(['ty_1']),
    filters: { includeExternal: false },
    search: 'order'
  };

  it('marks results already in the view, outside the budget, or outside the filters', () => {
    expect(classifySearchResult(entity('ty_1', 'OrderService'), options)).toBe('visible');
    expect(classifySearchResult(entity('ty_9', 'OrderService'), options)).toBe('outsideBudget');
    expect(classifySearchResult(entity('ty_8', 'Order', { isExternal: true }), options)).toBe(
      'outsideFilters'
    );
  });
});
