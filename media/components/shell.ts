// Shell: the DOM skeleton of the new viewer (SD-015).
//
// The shell builds elements and wires user gestures to callbacks. It never reads the
// state from the DOM and never decides what to show: rendering is driven by the state
// in `media/app/app.ts`. Panes and the toolbar stay reachable at 360 CSS px.

export type NavTab = 'structure' | 'cycles' | 'analysis';

export interface ShellHandlers {
  onAnalyze: () => void;
  onStop: () => void;
  onGranularity: (granularity: 'project' | 'namespace' | 'type') => void;
  onViewKind: (viewKind: 'graph' | 'table') => void;
  onSearch: (query: string) => void;
  onExport: (format: 'mermaid' | 'svg' | 'png' | 'json') => void;
  onCopyContext: () => void;
  onNavTab: (tab: NavTab) => void;
  onSelectionCleared: () => void;
  onInspectorToggled: () => void;
  onPaneResized: (pane: 'navigation' | 'inspector', width: number) => void;
}

export interface ShellElements {
  root: HTMLElement;
  targetName: HTMLElement;
  targetPath: HTMLElement;
  modeSelect: HTMLSelectElement;
  analyzeButton: HTMLButtonElement;
  stopButton: HTMLButtonElement;
  exportButton: HTMLButtonElement;
  exportMenu: HTMLElement;
  copyButton: HTMLButtonElement;
  breadcrumbs: HTMLElement;
  navTabs: HTMLElement;
  navPane: HTMLElement;
  navPaneBody: HTMLElement;
  granularitySelect: HTMLSelectElement;
  viewKindButtons: HTMLElement;
  searchInput: HTMLInputElement;
  mapHost: HTMLElement;
  /** Persistent host for the SVG graph; hidden outside the graph view. */
  graphHost: HTMLElement;
  /** Per-render area for the table and state messages. */
  mapContent: HTMLElement;
  mapSummary: HTMLElement;
  statusText: HTMLElement;
  errorBar: HTMLElement;
  footer: HTMLElement;
  inspectorPane: HTMLElement;
  inspectorTitle: HTMLElement;
  inspectorBody: HTMLElement;
  inspectorToggle: HTMLButtonElement;
  inspectorClose: HTMLButtonElement;
  navSplitter: HTMLElement;
  inspectorSplitter: HTMLElement;
}

export function buildShell(root: HTMLElement, handlers: ShellHandlers): ShellElements {
  root.classList.add('sd-shell');
  root.replaceChildren();

  const topBar = element('header', 'sd-topbar');
  const targetBlock = element('div', 'sd-target');
  const targetName = element('strong', 'sd-target-name', 'No target');
  const targetPath = element('span', 'sd-target-path', '');
  targetBlock.append(targetName, targetPath);

  const modeSelect = select('sd-mode', [
    ['quick', 'Quick'],
    ['semantic', 'Semantic']
  ]);

  const analyzeButton = button('sd-analyze', 'Analyze', 'primary');
  const stopButton = button('sd-stop', 'Stop', 'danger');
  stopButton.disabled = true;

  const exportButton = button('sd-export', 'Export ▾');
  const exportMenu = element('div', 'sd-menu');
  exportMenu.hidden = true;
  for (const [format, label] of [
    ['mermaid', 'Mermaid'],
    ['svg', 'SVG'],
    ['png', 'PNG'],
    ['json', 'JSON']
  ] as const) {
    const item = button(`sd-export-${format}`, label);
    item.addEventListener('click', () => {
      exportMenu.hidden = true;
      handlers.onExport(format);
    });
    exportMenu.append(item);
  }

  const copyButton = button('sd-copy', 'Copy for agent');

  const overflow = element('div', 'sd-overflow');
  overflow.append(exportButton, exportMenu, copyButton);

  const topBarMeta = element('div', 'sd-topbar-meta');
  topBarMeta.append(breadcrumbElement(), statusTextPlaceholder());
  const statusText = topBarMeta.querySelector('.sd-status-text') as HTMLElement;
  const breadcrumbs = topBarMeta.querySelector('.sd-breadcrumbs') as HTMLElement;

  topBar.append(targetBlock, modeSelect, analyzeButton, stopButton, overflow, topBarMeta);

  const body = element('div', 'sd-body');
  const navPane = element('aside', 'sd-nav');
  const navTabs = element('div', 'sd-nav-tabs');
  navTabs.setAttribute('role', 'tablist');
  const navPaneBody = element('div', 'sd-nav-body');
  for (const [tab, label] of [
    ['structure', 'Structure'],
    ['cycles', 'Cycles'],
    ['analysis', 'Analysis']
  ] as const) {
    const tabButton = button(`sd-tab-${tab}`, label, 'tab');
    tabButton.dataset.tab = tab;
    tabButton.setAttribute('role', 'tab');
    tabButton.addEventListener('click', () => handlers.onNavTab(tab));
    navTabs.append(tabButton);
  }

  navPane.append(navTabs, navPaneBody);

  const navSplitter = element('div', 'sd-splitter sd-splitter-nav');
  navSplitter.setAttribute('role', 'separator');
  navSplitter.setAttribute('aria-orientation', 'vertical');
  navSplitter.setAttribute('aria-label', 'Resize navigation pane');
  navSplitter.tabIndex = 0;

  const center = element('main', 'sd-center');
  const mapToolbar = element('div', 'sd-map-toolbar');
  const granularitySelect = select('sd-granularity', [
    ['project', 'Projects'],
    ['namespace', 'Namespaces'],
    ['type', 'Types']
  ]);
  const viewKindButtons = element('div', 'sd-viewkind');
  for (const [kind, label] of [
    ['graph', 'Graph'],
    ['table', 'Table']
  ] as const) {
    const kindButton = button(`sd-view-${kind}`, label, 'toggle');
    kindButton.dataset.viewKind = kind;
    kindButton.addEventListener('click', () => handlers.onViewKind(kind));
    viewKindButtons.append(kindButton);
  }

  const searchInput = document.createElement('input');
  searchInput.type = 'search';
  searchInput.className = 'sd-search';
  searchInput.placeholder = 'Search analyzed entities…';
  searchInput.setAttribute('aria-label', 'Search analyzed entities');
  searchInput.addEventListener('input', () => handlers.onSearch(searchInput.value));

  const mapSummary = element('p', 'sd-map-summary', '');
  const mapHost = element('div', 'sd-map-host');
  mapHost.tabIndex = 0;
  // The graph owns a persistent host so a redraw never destroys the SVG (and therefore
  // never resets zoom); the table is rendered into a separate area each time.
  const graphHost = element('div', 'sd-graph-host');
  graphHost.hidden = true;
  const mapContent = element('div', 'sd-map-content');
  mapHost.append(graphHost, mapContent);
  center.append(mapToolbar, mapSummary, mapHost);
  mapToolbar.append(granularitySelect, viewKindButtons, searchInput);

  const inspectorSplitter = element('div', 'sd-splitter sd-splitter-inspector');
  inspectorSplitter.setAttribute('role', 'separator');
  inspectorSplitter.setAttribute('aria-orientation', 'vertical');
  inspectorSplitter.setAttribute('aria-label', 'Resize details pane');
  inspectorSplitter.tabIndex = 0;

  const inspectorPane = element('aside', 'sd-inspector');
  const inspectorHeader = element('div', 'sd-inspector-header');
  const inspectorTitle = element('h2', 'sd-inspector-title', 'Details');
  const inspectorClose = button('sd-inspector-close', 'Close');
  inspectorClose.addEventListener('click', () => handlers.onInspectorToggled());
  inspectorHeader.append(inspectorTitle, inspectorClose);
  const inspectorBody = element('div', 'sd-inspector-body');
  inspectorPane.append(inspectorHeader, inspectorBody);

  body.append(navPane, navSplitter, center, inspectorSplitter, inspectorPane);

  const errorBar = element('div', 'sd-error-bar');
  errorBar.setAttribute('role', 'alert');
  errorBar.hidden = true;

  const footer = element('footer', 'sd-footer');

  const inspectorToggle = button('sd-inspector-toggle', 'Details');
  root.append(topBar, errorBar, body, footer);
  topBar.append(inspectorToggle);

  analyzeButton.addEventListener('click', () => handlers.onAnalyze());
  stopButton.addEventListener('click', () => handlers.onStop());
  modeSelect.addEventListener('change', () => undefined);
  exportButton.addEventListener('click', () => {
    exportMenu.hidden = !exportMenu.hidden;
  });
  copyButton.addEventListener('click', () => handlers.onCopyContext());
  granularitySelect.addEventListener('change', () =>
    handlers.onGranularity(granularitySelect.value as 'project' | 'namespace' | 'type')
  );
  inspectorToggle.addEventListener('click', () => handlers.onInspectorToggled());
  wireSplitter(navSplitter, 'navigation', handlers);
  wireSplitter(inspectorSplitter, 'inspector', handlers);

  const elements: ShellElements = {
    root,
    targetName,
    targetPath,
    modeSelect,
    analyzeButton,
    stopButton,
    exportButton,
    exportMenu,
    copyButton,
    breadcrumbs,
    navTabs,
    navPane,
    navPaneBody,
    granularitySelect,
    viewKindButtons,
    searchInput,
    mapHost,
    graphHost,
    mapContent,
    mapSummary,
    statusText,
    errorBar,
    footer,
    inspectorPane,
    inspectorTitle,
    inspectorBody,
    inspectorToggle,
    inspectorClose,
    navSplitter,
    inspectorSplitter
  };

  return elements;
}

/** Drag and keyboard resizing; widths are reported to the state, not kept locally. */
function wireSplitter(
  splitter: HTMLElement,
  pane: 'navigation' | 'inspector',
  handlers: ShellHandlers
): void {
  const applyWith = (event: PointerEvent): void => {
    const paneElement = splitter.previousElementSibling as HTMLElement | null;
    if (!paneElement) {
      return;
    }

    const rect = paneElement.getBoundingClientRect();
    const width = pane === 'navigation' ? event.clientX - rect.left : rect.right - event.clientX;
    handlers.onPaneResized(pane, width);
  };

  splitter.addEventListener('pointerdown', (event) => {
    splitter.setPointerCapture(event.pointerId);
    splitter.dataset.dragging = 'true';
  });
  splitter.addEventListener('pointermove', (event) => {
    if (splitter.dataset.dragging === 'true') {
      applyWith(event);
    }
  });
  splitter.addEventListener('pointerup', (event) => {
    splitter.releasePointerCapture(event.pointerId);
    delete splitter.dataset.dragging;
  });
  splitter.addEventListener('keydown', (event) => {
    const paneElement = splitter.previousElementSibling as HTMLElement | null;
    if (!paneElement) {
      return;
    }

    const current = paneElement.getBoundingClientRect().width;
    if (event.key === 'ArrowLeft') {
      handlers.onPaneResized(pane, current - 16);
      event.preventDefault();
    } else if (event.key === 'ArrowRight') {
      handlers.onPaneResized(pane, current + 16);
      event.preventDefault();
    }
  });
}

function element<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  className?: string,
  text?: string
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }

  if (text !== undefined) {
    node.textContent = text;
  }

  return node;
}

function button(id: string, label: string, variant?: string): HTMLButtonElement {
  const node = element('button', `sd-button${variant ? ` sd-button-${variant}` : ''}`, label);
  node.type = 'button';
  node.id = id;
  return node;
}

function select(id: string, options: Array<[string, string]>): HTMLSelectElement {
  const node = element('select', 'sd-select');
  node.id = id;
  for (const [value, label] of options) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = label;
    node.append(option);
  }

  return node;
}

function breadcrumbElement(): HTMLElement {
  const node = element('nav', 'sd-breadcrumbs');
  node.setAttribute('aria-label', 'Scope');
  return node;
}

function statusTextPlaceholder(): HTMLElement {
  return element('span', 'sd-status-text', '');
}
