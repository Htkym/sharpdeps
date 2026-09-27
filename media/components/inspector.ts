// Inspector: node overview / edge evidence (SD-018).
//
// The inspector is a pure renderer: the state is the source of truth, and every action
// (navigate to a related entity, load the next evidence page, copy a reference) is a
// callback. Evidence is never fetched to open the details pane, so selecting something
// never triggers an analysis.

import type { EntitySummary, ProjectionEdge } from '../../src/view/protocolV2';
import { translator, type Language } from '../app/i18n';
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
  language?: Language;
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
  onOpenDeclaration?: (entityId: string) => void;
  onSelectRelation?: (relationId: string) => void;
  onExplore?: (kind: 'dependencies' | 'dependents', entityId: string) => void;
  onDrillDown?: (entityId: string) => void;
}

export function renderInspector(
  title: HTMLElement,
  body: HTMLElement,
  options: InspectorOptions
): void {
  const tr = translator(options.language);
  body.replaceChildren();

  if (options.edge) {
    renderEdge(title, body, options, options.edge);
    return;
  }

  if (options.entity) {
    renderEntity(title, body, options, options.entity);
    return;
  }

  title.textContent = tr('Details');
  body.append(message(tr('Select a node or relation to inspect it.'), 'sd-empty'));
}

function renderEntity(
  title: HTMLElement,
  body: HTMLElement,
  options: InspectorOptions,
  entity: InspectorEntityOptions
): void {
  const tr = translator(options.language);
  const summary = entity.summary;
  title.textContent = summary?.name ?? entity.id;

  const facts: Array<[string, string]> = [
    ['Granularity', tr(summary?.granularity ?? 'unknown')],
    ['Kind', tr(summary?.kind ?? 'unknown')],
    ['Project', summary?.projectName ?? '—'],
    ['In cycle', tr(summary?.inCycle ? 'yes' : 'no')]
  ];
  if (summary?.analysisStatus) facts.push(['Analysis', tr(summary.analysisStatus)]);
  for (const limitation of summary?.analysisLimitations ?? [])
    body.append(message(limitation, 'sd-note'));
  if (summary?.isExternal) {
    facts.push(['External', tr('yes (type from an external assembly)')]);
  }

  if (summary?.isGenerated) {
    facts.push(['Generated', tr('yes (from generated code)')]);
  }

  const edges = entity.edges ?? [];
  const outgoing = edges.filter((edge) => edge.sourceId === entity.id);
  const incoming = edges.filter((edge) => edge.targetId === entity.id);
  facts.push(['Outgoing occurrences', tr('{0} ({1} relation(s))', sum(outgoing), outgoing.length)]);
  facts.push(['Incoming occurrences', tr('{0} ({1} relation(s))', sum(incoming), incoming.length)]);

  const actions = document.createElement('div');
  actions.className = 'sd-inspector-actions';
  if (summary?.granularity === 'type' && options.mode === 'semantic' && !summary.isExternal) {
    const open = button(tr('Open declaration'), 'sd-button');
    open.addEventListener('click', () => options.onOpenDeclaration?.(entity.id));
    actions.append(open);
  } else if (summary && summary.granularity !== 'type') {
    const drill = button(tr('Drill down'), 'sd-button');
    drill.addEventListener('click', () => options.onDrillDown?.(entity.id));
    actions.append(drill);
  }
  for (const kind of ['dependencies', 'dependents'] as const) {
    const explore = button(
      kind === 'dependencies' ? tr('Dependencies') : tr('Dependents'),
      'sd-button sd-button-toggle'
    );
    explore.setAttribute(
      'aria-label',
      tr(kind === 'dependencies' ? 'Explore dependencies' : 'Explore dependents')
    );
    explore.addEventListener('click', () => options.onExplore?.(kind, entity.id));
    actions.append(explore);
  }
  body.append(actions, factsList(facts, options.language));
  const relations = document.createElement('section');
  const heading = document.createElement('h3');
  heading.textContent = tr('Relations ({0})', edges.length);
  const relationItems = document.createElement('ul');
  relationItems.className = 'sd-inspector-relations';
  for (const edge of edges) {
    const item = document.createElement('li');
    const inspect = button(
      `${edge.sourceId === entity.id ? tr('Outgoing') : tr('Incoming')}: ${edge.kinds.map((kind) => tr(kind)).join(', ')} (${edge.evidenceCount})`,
      'sd-node-item'
    );
    inspect.addEventListener('click', () => options.onSelectRelation?.(edge.id));
    item.append(inspect);
    relationItems.append(item);
  }
  relations.append(heading, relationItems);
  if (edges.length > 0) body.append(relations);
  body.append(identifiersBlock([['ID', entity.id]], options.language));

  if (entity.pending && !entity.dependencies) {
    body.append(message(tr('Loading details…'), 'sd-note'));
    return;
  }

  body.append(
    entityList(
      tr('Dependencies'),
      entity.dependencies ?? [],
      options.onSelectEntity,
      options.language
    ),
    entityList(tr('Dependents'), entity.dependents ?? [], options.onSelectEntity, options.language)
  );

  const limitations = relevantLimitations(options.limitations, 'evidence');
  if (limitations.length > 0) {
    body.append(limitationsBlock(limitations, options.language));
  }
}

function renderEdge(
  title: HTMLElement,
  body: HTMLElement,
  options: InspectorOptions,
  edge: InspectorEdgeOptions
): void {
  const tr = translator(options.language);
  const relation = edge.edge;
  const selectedRelation = relation?.underlyingRelations?.find((entry) => entry.id === edge.id);
  const basis = describeBasis(
    selectedRelation?.basis ?? relation?.basis ?? 'unknown',
    options.language
  );
  const representativeSelected = edge.id === relation?.id;
  const summary = relation ? summarizeEdge(relation) : undefined;

  title.textContent = `${edge.sourceName} → ${edge.targetName}`;

  const facts: Array<[string, string]> = [['Basis', `${basis.label} — ${basis.detail}`]];
  if (relation) {
    facts.push([
      'Kinds',
      (selectedRelation?.kinds ?? relation.kinds).map((kind) => tr(kind)).join(', ') || '—'
    ]);
    facts.push([
      'Occurrences',
      summary
        ? `${summary.total}${summary.aggregated ? tr(' ({0} relation(s) aggregated)', summary.relationCount) : ''}`
        : String(relation.evidenceCount)
    ]);
    if (summary && summary.generated > 0) {
      facts.push(['Generated evidence', tr('{0} occurrence(s)', summary.generated)]);
    }
    if (summary && summary.publicSurface > 0) {
      facts.push(['Public surface', tr('{0} occurrence(s)', summary.publicSurface)]);
    }
    facts.push(['In cycle', tr(relation.inCycle ? 'yes' : 'no')]);
  }

  body.append(factsList(facts, options.language));
  if (relation) body.append(identifiersBlock([['Relation id', edge.id]], options.language));

  if (summary?.aggregated) {
    body.append(
      relationList(
        tr('Aggregated relations'),
        summary.relations,
        options.onCopyReference,
        options.onSelectRelation,
        options.language
      )
    );
  }

  const evidence = edge.evidence;
  if (!evidence) {
    body.append(message(tr('Loading evidence…'), 'sd-note'));
    return;
  }

  const heading = document.createElement('h3');
  heading.textContent =
    summary?.aggregated && representativeSelected
      ? tr(
          'Evidence — representative relation {0} ({1} of {2})',
          edge.id,
          evidence.items.length,
          evidence.total
        )
      : tr('Evidence ({0} of {1})', evidence.items.length, evidence.total);
  body.append(heading);

  if (summary?.aggregated && representativeSelected) {
    body.append(
      message(
        tr(
          'This edge aggregates {0} relation(s) with {1} occurrence(s) in total; the list below pages the representative relation. The per-relation counts are above.',
          summary.relationCount,
          summary.total
        ),
        'sd-note'
      )
    );
  }

  if (evidence.items.length === 0) {
    body.append(message(tr('No evidence records were returned for this relation.'), 'sd-empty'));
  } else {
    const list = document.createElement('ul');
    list.className = 'sd-evidence-list';
    for (const record of evidence.items) {
      list.append(
        evidenceItem(
          toEvidenceView(record),
          options.onCopyReference,
          options.onOpenEvidence,
          options.language
        )
      );
    }

    body.append(list);
  }

  if (evidence.nextCursor) {
    const loadMore = button(tr('Load more evidence'), 'sd-button sd-button-small');
    loadMore.disabled = evidence.pending;
    loadMore.addEventListener('click', () => options.onLoadMoreEvidence());
    body.append(loadMore);
  }

  if (evidence.pending) {
    body.append(message(tr('Loading the next page…'), 'sd-note'));
  }

  if (summary && !basis.resolved) {
    body.append(
      message(
        options.mode === 'quick'
          ? tr(
              'This is a Quick result: the occurrences are declared or inferred, not resolved references.'
            )
          : tr('The occurrences on this edge are not resolved symbol references.'),
        'sd-note sd-note-warning'
      )
    );
  }

  const limitations = relevantLimitations(options.limitations, 'evidence');
  if (limitations.length > 0) {
    body.append(limitationsBlock(limitations, options.language));
  }
}

function evidenceItem(
  evidence: ReturnType<typeof toEvidenceView>,
  onCopyReference: (reference: string) => void,
  onOpenEvidence: ((evidenceId: string) => void) | undefined,
  language: Language = 'en'
): HTMLElement {
  const tr = translator(language);
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
  kind.textContent = tr(evidence.kind);
  head.append(kind);

  if (evidence.confidence === 'inferred') {
    head.append(badge(tr('Inferred'), 'sd-badge sd-badge-inferred'));
  } else if (evidence.confidence === 'resolved') {
    head.append(badge(tr('Resolved'), 'sd-badge sd-badge-resolved'));
  }

  if (evidence.generated) {
    head.append(badge(tr('Generated'), 'sd-badge sd-badge-generated'));
  }

  if (evidence.publicSurface) {
    head.append(badge(tr('Public'), 'sd-badge sd-badge-public'));
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

  const actions = document.createElement('div');
  actions.className = 'sd-evidence-actions';
  const copy = button(tr('Copy reference'), 'sd-node-item');
  copy.addEventListener('click', () => onCopyReference(evidence.reference));

  if (onOpenEvidence) {
    const open = button(tr('Open in editor'), 'sd-button sd-button-small');
    open.title = evidence.reference;
    open.addEventListener('click', () => onOpenEvidence(evidence.id));
    actions.append(open);
  }
  actions.append(copy);
  item.append(actions);

  return item;
}

function relationList(
  label: string,
  relations: Array<{ id: string; evidenceCount: number; kinds: string[]; basis: string }>,
  onCopyReference: (reference: string) => void,
  onSelectRelation?: (relationId: string) => void,
  language: Language = 'en'
): HTMLElement {
  const tr = translator(language);
  const block = document.createElement('section');
  const heading = document.createElement('h3');
  heading.textContent = `${label} (${relations.length})`;
  block.append(heading);

  const list = document.createElement('ul');
  list.className = 'sd-relation-list';
  for (const relation of relations) {
    const item = document.createElement('li');
    const text = document.createElement('span');
    text.textContent = tr(
      '{0} — {1} occurrence(s) · {2}',
      relation.id,
      relation.evidenceCount,
      [...relation.kinds.map((kind) => tr(kind)), tr(relation.basis)].join(' · ')
    );
    item.append(text);

    const actions = document.createElement('div');
    actions.className = 'sd-evidence-actions';
    const copy = button(tr('Copy'), 'sd-node-item');
    copy.addEventListener('click', () => onCopyReference(`${relation.id}`));
    const inspect = button(tr('Evidence'), 'sd-button sd-button-small');
    inspect.addEventListener('click', () => onSelectRelation?.(relation.id));
    actions.append(inspect, copy);
    item.append(actions);
    list.append(item);
  }

  block.append(list);
  return block;
}

function entityList(
  label: string,
  entities: EntitySummary[],
  onSelectEntity: (entityId: string) => void,
  language: Language = 'en'
): HTMLElement {
  const tr = translator(language);
  const block = document.createElement('section');
  const heading = document.createElement('h3');
  heading.textContent = `${label} (${entities.length})`;
  block.append(heading);

  if (entities.length === 0) {
    block.append(message(tr('none'), 'sd-empty'));
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
      item.append(badge(tr('External'), 'sd-badge'));
    }

    list.append(item);
  }

  block.append(list);
  return block;
}

function factsList(facts: Array<[string, string]>, language: Language = 'en'): HTMLElement {
  const tr = translator(language);
  const list = document.createElement('dl');
  list.className = 'sd-facts';
  for (const [label, value] of facts) {
    const term = document.createElement('dt');
    term.textContent = tr(label);
    const definition = document.createElement('dd');
    definition.textContent = value;
    list.append(term, definition);
  }

  return list;
}

function identifiersBlock(facts: Array<[string, string]>, language: Language = 'en'): HTMLElement {
  const tr = translator(language);
  const block = document.createElement('details');
  block.className = 'sd-inspector-metadata';
  const summary = document.createElement('summary');
  summary.textContent = tr('Identifiers');
  block.append(summary, factsList(facts, language));
  return block;
}

function limitationsBlock(
  limitations: Array<{ code: string; message: string }>,
  language: Language = 'en'
): HTMLElement {
  const tr = translator(language);
  const block = document.createElement('section');
  const heading = document.createElement('h3');
  heading.textContent = tr('Notes');
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
