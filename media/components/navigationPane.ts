// Navigation pane (SD-016): Project → Namespace → Type hierarchy.
//
// Children are rendered the first time a node is expanded (lazy rendering), and the
// expansion set is kept in the component rather than derived from the DOM. The
// hierarchy is grouped from the current projection; fetching children for scopes that
// the display budget left out arrives with the graph projection work (SD-017).

import type { EntitySummary } from '../../src/view/protocolV2';

export interface NavigationTreeNode {
  id: string;
  label: string;
  granularity: 'project' | 'namespace' | 'type';
  kind?: string;
  inCycle?: boolean;
  isExternal?: boolean;
  children: NavigationTreeNode[];
  canExpand?: boolean;
  moreCursor?: string;
}

export interface NavigationPaneOptions {
  nodes: NavigationTreeNode[];
  selectedId?: string;
  onSelect: (entityId: string) => void;
  onToggle: (entityId: string) => void;
  onLoadMore?: (entityId: string, cursor: string) => void;
  expanded: ReadonlySet<string>;
}

/** Groups projected entities into Project → Namespace → Type. */
export function buildNavigationTree(entities: readonly EntitySummary[]): NavigationTreeNode[] {
  const projects = new Map<string, NavigationTreeNode>();
  const namespaces = new Map<string, NavigationTreeNode>();

  for (const entity of entities) {
    if (entity.granularity === 'project') {
      const node = projectNode(entity);
      projects.set(entity.id, node);
      continue;
    }

    const projectKey = entity.projectId ?? entity.projectName ?? '(unknown project)';
    const project = projects.get(projectKey) ?? {
      id: entity.projectId ?? `project:${projectKey}`,
      label: entity.projectName ?? projectKey,
      granularity: 'project' as const,
      children: []
    };
    projects.set(projectKey, project);

    if (entity.granularity === 'namespace') {
      const node = namespaceNode(entity);
      namespaces.set(entity.id, node);
      project.children.push(node);
      continue;
    }

    const namespaceName = entity.namespaceName ?? namespaceOf(entity.fullName ?? entity.name);
    const namespaceKey = `${projectKey}|${entity.namespaceId ?? namespaceName}`;
    const namespace = namespaces.get(namespaceKey) ?? {
      id: entity.namespaceId ?? `namespace:${projectKey}:${namespaceName}`,
      label: namespaceName,
      granularity: 'namespace' as const,
      children: []
    };
    if (!namespaces.has(namespaceKey)) {
      namespaces.set(namespaceKey, namespace);
      project.children.push(namespace);
    }

    namespace.children.push({
      id: entity.id,
      label: entity.name,
      granularity: 'type',
      kind: entity.kind,
      inCycle: entity.inCycle,
      isExternal: entity.isExternal,
      children: []
    });
  }

  return [...projects.values()].sort((left, right) => left.label.localeCompare(right.label));
}

function projectNode(entity: EntitySummary): NavigationTreeNode {
  return {
    id: entity.id,
    label: entity.projectName ?? entity.name,
    granularity: 'project',
    kind: entity.kind,
    inCycle: entity.inCycle,
    children: []
  };
}

function namespaceNode(entity: EntitySummary): NavigationTreeNode {
  return {
    id: entity.id,
    label: entity.name,
    granularity: 'namespace',
    kind: entity.kind,
    inCycle: entity.inCycle,
    children: []
  };
}

function namespaceOf(fullName: string): string {
  const separator = fullName.lastIndexOf('.');
  return separator > 0 ? fullName.slice(0, separator) : '(global namespace)';
}

export function renderNavigationTree(container: HTMLElement, options: NavigationPaneOptions): void {
  container.replaceChildren();
  container.dataset.role = 'navigation-tree';

  if (options.nodes.length === 0) {
    const empty = document.createElement('p');
    empty.className = 'sd-empty';
    empty.textContent = 'The structure tree is empty for this scope.';
    container.append(empty);
    return;
  }

  container.append(renderLevel(options.nodes, options, 0));
}

function renderLevel(
  nodes: readonly NavigationTreeNode[],
  options: NavigationPaneOptions,
  depth: number
): HTMLUListElement {
  const list = document.createElement('ul');
  list.className = 'sd-tree';
  list.dataset.depth = String(depth);

  for (const node of nodes) {
    const item = document.createElement('li');
    const row = document.createElement('div');
    row.className = 'sd-tree-row';

    const expandable = node.canExpand || node.children.length > 0;
    if (expandable) {
      const toggle = document.createElement('button');
      toggle.type = 'button';
      toggle.className = 'sd-tree-toggle';
      toggle.textContent = options.expanded.has(node.id) ? '▾' : '▸';
      toggle.setAttribute('aria-expanded', options.expanded.has(node.id) ? 'true' : 'false');
      toggle.setAttribute(
        'aria-label',
        `${options.expanded.has(node.id) ? 'Collapse' : 'Expand'} ${node.label}`
      );
      toggle.addEventListener('click', () => options.onToggle(node.id));
      row.append(toggle);
    } else {
      const spacer = document.createElement('span');
      spacer.className = 'sd-tree-spacer';
      row.append(spacer);
    }

    const label = document.createElement('button');
    label.type = 'button';
    label.className = 'sd-node-item';
    label.textContent = node.label;
    label.dataset.entityId = node.id;
    label.classList.toggle('sd-node-selected', node.id === options.selectedId);
    label.addEventListener('click', () => {
      if (/^(prj|ns|ty)_[0-9a-f]{16}$/.test(node.id)) options.onSelect(node.id);
      else if (expandable) options.onToggle(node.id);
    });
    row.append(label);

    if (node.inCycle) {
      const badge = document.createElement('span');
      badge.className = 'sd-badge';
      badge.textContent = 'cycle';
      row.append(badge);
    }

    if (node.isExternal) {
      const badge = document.createElement('span');
      badge.className = 'sd-badge';
      badge.textContent = 'external';
      row.append(badge);
    }

    item.append(row);

    // Children are only built when the node is expanded.
    if (expandable && options.expanded.has(node.id)) {
      item.append(renderLevel(node.children, options, depth + 1));
      if (node.moreCursor) {
        const more = document.createElement('button');
        more.type = 'button';
        more.textContent = 'Load more';
        more.addEventListener('click', () => options.onLoadMore?.(node.id, node.moreCursor!));
        item.append(more);
      }
    }

    list.append(item);
  }

  return list;
}
