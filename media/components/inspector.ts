// Inspector: node overview / edge evidence (SD-018).
//
// The inspector is a pure renderer: the state is the source of truth, and every action
// (navigate to a related entity, load the next evidence page, copy a reference) is a
// callback. Evidence is never fetched to open the details pane, so selecting something
// never triggers an analysis.

import type { EntitySummary, ProjectionEdge } from '../../src/view/protocolV2';
import {
  describeBasis,
  relevantLimitations,
  summarizeEdge,
  toEvidenceView
} from '../app/evidenceView';

export interface InspectorEntityOptions {
  id: string;
  summary?: EntitySummary;
  dependencies?: EntitySummary[];
  dependents?: EntitySummary[];
  /** Edges of the current projection that touch this entity. */
  edges?: ProjectionEdge[];
  /** True while the details request is in flight. */
  pending: boolean;
}

export interface InspectorEdgeOptions {
  id: string;
  edge?: ProjectionEdge;
  sourceName: string;
  targetName: string;
  evidence?: {
    total: number;
    items: Record<string, unknown>[];
    nextCursor?: string | null;
    pending: boolean;
  } | null;
}

export interface InspectorOptions {
  entity?: InspectorEntityOptions;
  edge?: InspectorEdgeOptions;
  limitations: Array<{ code: string; message: string }>;
  /** Which analyzer produced the result; the evidence list says so explicitly. */
  mode: 'quick' | 'semantic';
  onSelectEntity: (entityId: string) => void;
  onLoadMoreEvidence: () => void;
  onCopyReference: (reference: string) => void;
  /** Opens one evidence record in the editor (SD-019). */
  onOpenEvidence?: (evidenceId: string) => void;
}

export function renderInspector(
  title: HTMLElement,
  body: HTMLElement,
  options: InspectorOptions
): void {
  body.replaceChildren();

  if (options.edge) {
    renderEdge(title, body, options, options.edge);
    return;
  }

  if (options.entity) {
    renderEntity(title, body, options, options.entity);
    return;
  }

  title.textContent = 'Details';
  body.append(message('Select a node or relation to inspect it.', 'sd-empty'));
}

function renderEntity(
  title: HTMLElement,
  body: HTMLElement,
  options: InspectorOptions,
  entity: InspectorEntityOptions
): void {
  const summary = entity.summary;
  title.textContent = summary?.name ?? entity.id;

  const facts: Array<[string, string]> = [
    ['ID', entity.id],
    ['Granularity', summary?.granularity ?? 'unknown'],
    ['Kind', summary?.kind ?? 'unknown'],
    ['Project', summary?.projectName ?? '—'],
    ['In cycle', summary?.inCycle ? 'yes' : 'no']
  ];
  if (summary?.isExternal) {
    facts.push(['External', 'yes（外部アセンブリの型）']);
  }

  if (summary?.isGenerated) {
    facts.push(['Generated', 'yes（生成コード由来）']);
  }

  const edges = entity.edges ?? [];
  const outgoing = edges.filter((edge) => edge.sourceId === entity.id);
  const incoming = edges.filter((edge) => edge.targetId === entity.id);
  facts.push(['Outgoing occurrences', `${sum(outgoing)} (${outgoing.length} relation(s))`]);
  facts.push(['Incoming occurrences', `${sum(incoming)} (${incoming.length} relation(s))`]);

  body.append(factsList(facts));

  if (entity.pending && !entity.dependencies) {
    body.append(message('Loading details…', 'sd-note'));
    return;
  }

  body.append(
    entityList('Dependencies', entity.dependencies ?? [], options.onSelectEntity),
    entityList('Dependents', entity.dependents ?? [], options.onSelectEntity)
  );

  const limitations = relevantLimitations(options.limitations, 'evidence');
  if (limitations.length > 0) {
    body.append(limitationsBlock(limitations));
  }
}

function renderEdge(
  title: HTMLElement,
  body: HTMLElement,
  options: InspectorOptions,
  edge: InspectorEdgeOptions
): void {
  const relation = edge.edge;
  const basis = describeBasis(relation?.basis ?? 'unknown');
  const summary = relation ? summarizeEdge(relation) : undefined;

  title.textContent = `${edge.sourceName} → ${edge.targetName}`;

  const facts: Array<[string, string]> = [['Basis', `${basis.label} — ${basis.detail}`]];
  if (relation) {
    facts.push(['Kinds', relation.kinds.join(', ') || '—']);
    facts.push([
      'Occurrences',
      summary
        ? `${summary.total}${summary.aggregated ? ` (${summary.relationCount} relation(s) aggregated)` : ''}`
        : String(relation.evidenceCount)
    ]);
    if (summary && summary.generated > 0) {
      facts.push(['Generated evidence', `${summary.generated} occurrence(s)`]);
    }
    if (summary && summary.publicSurface > 0) {
      facts.push(['Public surface', `${summary.publicSurface} occurrence(s)`]);
    }
    facts.push(['In cycle', relation.inCycle ? 'yes' : 'no']);
    facts.push(['Relation id', relation.id]);
  }

  body.append(factsList(facts));

  if (summary?.aggregated) {
    body.append(relationList('Aggregated relations', summary.relations, options.onCopyReference));
  }

  const evidence = edge.evidence;
  if (!evidence) {
    body.append(message('Loading evidence…', 'sd-note'));
    return;
  }

  const heading = document.createElement('h3');
  heading.textContent = summary?.aggregated
    ? `Evidence — representative relation ${edge.id} (${evidence.items.length} of ${evidence.total})`
    : `Evidence (${evidence.items.length} of ${evidence.total})`;
  body.append(heading);

  if (summary?.aggregated) {
    body.append(
      message(
        `This edge aggregates ${summary.relationCount} relation(s) with ${summary.total} occurrence(s) in total; ` +
          'the list below pages the representative relation. The per-relation counts are above.',
        'sd-note'
      )
    );
  }

  if (evidence.items.length === 0) {
    body.append(message('No evidence records were returned for this relation.', 'sd-empty'));
  } else {
    const list = document.createElement('ul');
    list.className = 'sd-evidence-list';
    for (const record of evidence.items) {
      list.append(
        evidenceItem(toEvidenceView(record), options.onCopyReference, options.onOpenEvidence)
      );
    }

    body.append(list);
  }

  if (evidence.nextCursor) {
    const loadMore = button('Load more evidence', 'sd-button sd-button-small');
    loadMore.disabled = evidence.pending;
    loadMore.addEventListener('click', () => options.onLoadMoreEvidence());
    body.append(loadMore);
  }

  if (evidence.pending) {
    body.append(message('Loading the next page…', 'sd-note'));
  }

  if (summary && !basis.resolved) {
    body.append(
      message(
        options.mode === 'quick'
          ? 'This is a Quick result: the occurrences are declared or inferred, not resolved references.'
          : 'The occurrences on this edge are not resolved symbol references.',
        'sd-note sd-note-warning'
      )
    );
  }

  const limitations = relevantLimitations(options.limitations, 'evidence');
  if (limitations.length > 0) {
    body.append(limitationsBlock(limitations));
  }
}

function evidenceItem(
  evidence: ReturnType<typeof toEvidenceView>,
  onCopyReference: (reference: string) => void,
  onOpenEvidence: ((evidenceId: string) => void) | undefined
): HTMLElement {
  const item = document.createElement('li');
  item.className = 'sd-evidence-item';
  item.dataset.evidenceId = evidence.id;
  if (evidence.generated) {
    item.dataset.generated = 'true';
  }

  const head = document.createElement('div');
  head.className = 'sd-evidence-head';
  const kind = document.createElement('span');
  kind.className = 'sd-evidence-kind';
  kind.textContent = evidence.kind;
  head.append(kind);

  if (evidence.confidence === 'inferred') {
    head.append(badge('推定', 'sd-badge sd-badge-inferred'));
  } else if (evidence.confidence === 'resolved') {
    head.append(badge('解決', 'sd-badge sd-badge-resolved'));
  }

  if (evidence.generated) {
    head.append(badge('生成', 'sd-badge sd-badge-generated'));
  }

  if (evidence.publicSurface) {
    head.append(badge('公開', 'sd-badge sd-badge-public'));
  }

  item.append(head);

  const location = document.createElement('p');
  location.className = 'sd-evidence-location';
  location.textContent = evidence.reference;
  item.append(location);

  if (evidence.mappedPath) {
    const mapped = document.createElement('p');
    mapped.className = 'sd-evidence-mapped';
    mapped.textContent = `#line → ${evidence.mappedPath}:${evidence.mappedLine ?? '?'}:${evidence.mappedCharacter ?? '?'}`;
    item.append(mapped);
  }

  if (evidence.snippet) {
    const snippet = document.createElement('code');
    snippet.className = 'sd-evidence-snippet';
    snippet.textContent = evidence.snippet;
    item.append(snippet);
  }

  const copy = button('Copy reference', 'sd-button sd-button-small');
  copy.addEventListener('click', () => onCopyReference(evidence.reference));
  item.append(copy);

  if (onOpenEvidence) {
    const open = button('エディターで開く', 'sd-button sd-button-small');
    open.title = evidence.reference;
    open.addEventListener('click', () => onOpenEvidence(evidence.id));
    item.append(open);
  }

  return item;
}

function relationList(
  label: string,
  relations: Array<{ id: string; evidenceCount: number; kinds: string[]; basis: string }>,
  onCopyReference: (reference: string) => void
): HTMLElement {
  const block = document.createElement('section');
  const heading = document.createElement('h3');
  heading.textContent = `${label} (${relations.length})`;
  block.append(heading);

  const list = document.createElement('ul');
  list.className = 'sd-relation-list';
  for (const relation of relations) {
    const item = document.createElement('li');
    const text = document.createElement('span');
    text.textContent = `${relation.id} — ${relation.evidenceCount} occurrence(s) · ${relation.kinds.join(', ') || relation.basis}`;
    item.append(text);

    const copy = button('Copy', 'sd-button sd-button-small');
    copy.addEventListener('click', () => onCopyReference(`${relation.id}`));
    item.append(copy);
    list.append(item);
  }

  block.append(list);
  return block;
}

function entityList(
  label: string,
  entities: EntitySummary[],
  onSelectEntity: (entityId: string) => void
): HTMLElement {
  const block = document.createElement('section');
  const heading = document.createElement('h3');
  heading.textContent = `${label} (${entities.length})`;
  block.append(heading);

  if (entities.length === 0) {
    block.append(message('none', 'sd-empty'));
    return block;
  }

  const list = document.createElement('ul');
  list.className = 'sd-entity-list';
  for (const entity of entities.slice(0, 50)) {
    const item = document.createElement('li');
    const link = button(entity.name, 'sd-node-item');
    link.title = `${entity.id} (${entity.granularity})`;
    link.addEventListener('click', () => onSelectEntity(entity.id));
    item.append(link);
    if (entity.isExternal) {
      item.append(badge('外部', 'sd-badge'));
    }

    list.append(item);
  }

  block.append(list);
  return block;
}

function factsList(facts: Array<[string, string]>): HTMLElement {
  const list = document.createElement('dl');
  list.className = 'sd-facts';
  for (const [label, value] of facts) {
    const term = document.createElement('dt');
    term.textContent = label;
    const definition = document.createElement('dd');
    definition.textContent = value;
    list.append(term, definition);
  }

  return list;
}

function limitationsBlock(limitations: Array<{ code: string; message: string }>): HTMLElement {
  const block = document.createElement('section');
  const heading = document.createElement('h3');
  heading.textContent = 'Notes';
  block.append(heading);
  const list = document.createElement('ul');
  list.className = 'sd-limitations';
  for (const limitation of limitations) {
    const item = document.createElement('li');
    item.dataset.code = limitation.code;
    item.textContent = limitation.message;
    list.append(item);
  }

  block.append(list);
  return block;
}

function sum(edges: ProjectionEdge[]): number {
  return edges.reduce((total, edge) => total + edge.evidenceCount, 0);
}

function message(text: string, className: string): HTMLElement {
  const node = document.createElement('p');
  node.className = className;
  node.textContent = text;
  return node;
}

function badge(text: string, className: string): HTMLElement {
  const node = document.createElement('span');
  node.className = className;
  node.textContent = text;
  return node;
}

function button(label: string, className: string): HTMLButtonElement {
  const node = document.createElement('button');
  node.type = 'button';
  node.className = className;
  node.textContent = label;
  return node;
}
