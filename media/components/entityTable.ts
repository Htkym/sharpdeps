// Entity table (SD-016): 100 rows per page, stable sort, row selection, actions.
//
// The component renders what it is given and reports intent back; sorting, paging,
// and selection live in the view state so switching Graph/Table or re-rendering never
// loses them.

import type { EntitySummary } from '../../src/view/protocolV2';
import type { SortState } from '../app/query';

export interface TableRow {
  entity: EntitySummary;
  /** True when the row is shown although filters exclude it. */
  temporary: boolean;
}

export interface EntityTableOptions {
  rows: TableRow[];
  sort: SortState;
  page: number;
  pageCount: number;
  totalItems: number;
  selectedId?: string;
  onSort: (key: SortState['key']) => void;
  onPage: (page: number) => void;
  onSelect: (entityId: string) => void;
  onActivate: (entityId: string) => void;
}

const COLUMNS: Array<{ key: SortState['key'] | null; label: string }> = [
  { key: 'name', label: 'Name' },
  { key: 'kind', label: 'Kind' },
  { key: 'project', label: 'Project' },
  { key: 'dependencies', label: 'Dependencies' },
  { key: 'dependents', label: 'Dependents' },
  { key: 'cycle', label: 'Cycle' }
];

export function renderEntityTable(container: HTMLElement, options: EntityTableOptions): void {
  container.replaceChildren();
  container.dataset.role = 'entity-table';

  const table = document.createElement('table');
  table.className = 'sd-table';
  table.dataset.rowsPerPage = '100';

  const head = document.createElement('thead');
  const headRow = document.createElement('tr');
  for (const column of COLUMNS) {
    const cell = document.createElement('th');
    cell.scope = 'col';
    if (column.key) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'sd-sort';
      const active = options.sort.key === column.key;
      button.textContent = `${column.label}${active ? (options.sort.direction === 'asc' ? ' ▲' : ' ▼') : ''}`;
      button.setAttribute('aria-sort', active ? options.sort.direction : 'none');
      button.addEventListener('click', () => options.onSort(column.key as SortState['key']));
      cell.append(button);
    } else {
      cell.textContent = column.label;
    }

    headRow.append(cell);
  }

  head.append(headRow);
  table.append(head);

  const outgoing = new Map<string, number>();
  const incoming = new Map<string, number>();
  for (const row of options.rows) {
    outgoing.set(row.entity.id, 0);
    incoming.set(row.entity.id, 0);
  }

  const body = document.createElement('tbody');
  for (const row of options.rows) {
    const entity = row.entity;
    const element = document.createElement('tr');
    element.dataset.entityId = entity.id;
    element.tabIndex = 0;
    element.classList.toggle('sd-row-selected', entity.id === options.selectedId);
    element.classList.toggle('sd-row-temporary', row.temporary);
    element.addEventListener('click', () => options.onSelect(entity.id));
    element.addEventListener('dblclick', () => options.onActivate(entity.id));
    element.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        options.onActivate(entity.id);
        event.preventDefault();
      }
    });

    const values = [
      entity.name,
      entity.kind ?? '—',
      entity.projectName ?? '—',
      String(outgoing.get(entity.id) ?? 0),
      String(incoming.get(entity.id) ?? 0),
      entity.inCycle ? 'yes' : 'no'
    ];

    values.forEach((value, index) => {
      const cell = document.createElement('td');
      cell.textContent = value;
      if (index === 0 && row.temporary) {
        const badge = document.createElement('span');
        badge.className = 'sd-badge';
        badge.textContent = 'outside filters';
        cell.append(badge);
      }

      element.append(cell);
    });

    body.append(element);
  }

  table.append(body);
  container.append(table);

  const footer = document.createElement('div');
  footer.className = 'sd-table-footer';
  const summary = document.createElement('span');
  summary.textContent = `${options.totalItems} row(s) · page ${options.page + 1}/${options.pageCount} · 100 per page`;
  footer.append(summary);

  if (options.pageCount > 1) {
    const previous = pageButton('Previous', options.page > 0, () =>
      options.onPage(options.page - 1)
    );
    const next = pageButton('Next', options.page + 1 < options.pageCount, () =>
      options.onPage(options.page + 1)
    );
    footer.append(previous, next);
  }

  container.append(footer);
}

function pageButton(label: string, enabled: boolean, onClick: () => void): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'sd-button';
  button.textContent = label;
  button.disabled = !enabled;
  button.addEventListener('click', onClick);
  return button;
}
