// Shell: the DOM skeleton of the new viewer (SD-015).
//
// The shell builds elements and wires user gestures to callbacks. It never reads the
// state from the DOM and never decides what to show: rendering is driven by the state
// in `media/app/app.ts`. Panes and the toolbar stay reachable at 360 CSS px.

import type { Filters, ProfileRequest } from '../../src/view/protocolV2';
import { shellTranslations, type Language } from '../app/i18n';
export type NavTab = 'structure' | 'cycles' | 'analysis';

export interface ShellHandlers {
  onLanguageToggled: () => void;
  onAnalyze: () => void;
  onStop: () => void;
  onCancelLayout: () => void;
  onRetryLayout: () => void;
  onZoom: (zoom: number | 'in' | 'out' | 'fit') => void;
  onLayout: (layout: {
    direction: 'RIGHT' | 'DOWN';
    nodeSpacing: number;
    rankSpacing: number;
  }) => void;
  onImageOptions: (options: { profile: boolean; omissions: boolean; legend: boolean }) => void;
  onMode: (mode: 'quick' | 'semantic') => void;
  onProfile: (profile: ProfileRequest) => void;
  onFilters: (filters: Filters) => void;
  onBack: () => void;
  onDepth: (depth: number) => void;
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
  languageToggle: HTMLButtonElement;
  setLanguage: (language: Language) => void;
  root: HTMLElement;
  targetName: HTMLElement;
  targetPath: HTMLElement;
  modeSelect: HTMLSelectElement;
  configuration: HTMLInputElement;
  platform: HTMLInputElement;
  backButton: HTMLButtonElement;
  depthSelect: HTMLSelectElement;
  zoom: HTMLInputElement;
  nodeSpacing: HTMLInputElement;
  rankSpacing: HTMLInputElement;
  layoutDirection: HTMLSelectElement;
  legend: HTMLElement;
  imageOptions: {
    profile: HTMLInputElement;
    omissions: HTMLInputElement;
    legend: HTMLInputElement;
  };
  filterControls: {
    tests: HTMLInputElement;
    external: HTMLInputElement;
    generated: HTMLInputElement;
    basis: HTMLFieldSetElement;
    kinds: HTMLFieldSetElement;
    projectKinds: HTMLFieldSetElement;
    relations: HTMLFieldSetElement;
  };
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
  graphControls: HTMLElement;
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
  modeSelect.setAttribute('aria-label', 'Analysis mode');

  const analyzeButton = button('sd-analyze', 'Analyze', 'primary');
  const { trigger: profileSummary, panel: profilePicker } = toolbarPopover('profile', 'Profile');
  const configuration = document.createElement('input');
  configuration.value = 'Debug';
  configuration.maxLength = 64;
  const platform = document.createElement('input');
  platform.placeholder = 'Default';
  platform.maxLength = 64;
  for (const [name, input] of [
    ['Configuration', configuration],
    ['Platform', platform]
  ] as const) {
    const label = document.createElement('label');
    label.textContent = name;
    label.append(input);
    profilePicker.append(label);
    input.setAttribute('aria-label', name);
    input.addEventListener('change', () =>
      handlers.onProfile({
        configuration: configuration.value || 'Debug',
        platform: platform.value || null
      })
    );
  }
  const stopButton = button('sd-stop', 'Stop', 'danger');
  stopButton.disabled = true;

  const { trigger: exportButton, panel: exportMenu } = toolbarPopover('export', 'Export ▾');
  exportMenu.classList.add('sd-menu');
  for (const [format, label] of [
    ['mermaid', 'Mermaid'],
    ['svg', 'SVG'],
    ['png', 'PNG'],
    ['json', 'JSON']
  ] as const) {
    const item = button(`sd-export-${format}`, label);
    item.addEventListener('click', () => {
      exportMenu.hidePopover();
      handlers.onExport(format);
    });
    exportMenu.append(item);
  }

  const copyButton = button('sd-copy', 'Copy for agent');
  const languageToggle = button('sd-language', '日本語');
  languageToggle.addEventListener('click', handlers.onLanguageToggled);

  const overflow = element('div', 'sd-overflow');
  overflow.append(exportButton, exportMenu, copyButton);

  const topBarMeta = element('div', 'sd-topbar-meta');
  topBarMeta.append(breadcrumbElement(), statusTextPlaceholder());
  const statusText = topBarMeta.querySelector('.sd-status-text') as HTMLElement;
  statusText.setAttribute('role', 'status');
  const breadcrumbs = topBarMeta.querySelector('.sd-breadcrumbs') as HTMLElement;

  topBar.append(
    targetBlock,
    modeSelect,
    profileSummary,
    profilePicker,
    analyzeButton,
    stopButton,
    overflow,
    languageToggle,
    topBarMeta
  );

  const body = element('div', 'sd-body');
  const navPane = element('aside', 'sd-nav');
  const navTabs = element('div', 'sd-nav-tabs');
  navTabs.setAttribute('role', 'tablist');
  const navPaneBody = element('div', 'sd-nav-body');
  navPaneBody.id = 'sd-navigation-panel';
  navPaneBody.setAttribute('role', 'tabpanel');
  for (const [tab, label] of [
    ['structure', 'Structure'],
    ['cycles', 'Cycles'],
    ['analysis', 'Analysis']
  ] as const) {
    const tabButton = button(`sd-tab-${tab}`, label, 'tab');
    tabButton.dataset.tab = tab;
    tabButton.setAttribute('role', 'tab');
    tabButton.setAttribute('aria-controls', navPaneBody.id);
    tabButton.tabIndex = tab === 'structure' ? 0 : -1;
    tabButton.addEventListener('click', () => handlers.onNavTab(tab));
    navTabs.append(tabButton);
  }
  navTabs.addEventListener('keydown', (event) => {
    const tabs = [...navTabs.querySelectorAll<HTMLButtonElement>('button')];
    const index = tabs.indexOf(document.activeElement as HTMLButtonElement);
    if (index < 0 || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    const next =
      event.key === 'Home'
        ? 0
        : event.key === 'End'
          ? tabs.length - 1
          : (index + (event.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length;
    event.preventDefault();
    tabs[next].click();
    tabs[next].focus();
  });

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
  granularitySelect.setAttribute('aria-label', 'Graph level');
  const viewKindButtons = element('div', 'sd-viewkind');
  viewKindButtons.setAttribute('role', 'group');
  viewKindButtons.setAttribute('aria-label', 'Display mode');
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
  const backButton = button('sd-back', 'Back');
  backButton.addEventListener('click', handlers.onBack);
  const depthSelect = select('sd-depth', [
    ['1', 'Depth 1'],
    ['2', 'Depth 2'],
    ['3', 'Depth 3']
  ]);
  depthSelect.addEventListener('change', () => handlers.onDepth(Number(depthSelect.value)));
  depthSelect.setAttribute('aria-label', 'Exploration depth');
  const { trigger: filterSummary, panel: filterPicker } = toolbarPopover('filters', 'Filters');
  const checkbox = (name: string): HTMLInputElement => {
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.checked = true;
    const label = document.createElement('label');
    label.append(input, document.createTextNode(name));
    filterPicker.append(label);
    return input;
  };
  const multiple = (name: string, choices: string[]): HTMLFieldSetElement => {
    const category = document.createElement('details');
    category.className = 'sd-filter-category';
    const summary = document.createElement('summary');
    summary.textContent = name;
    const group = document.createElement('fieldset');
    group.setAttribute('aria-label', name);
    for (const value of choices) {
      const input = document.createElement('input');
      input.type = 'checkbox';
      input.value = value;
      const label = document.createElement('label');
      label.append(input, document.createTextNode(value));
      group.append(label);
    }
    category.append(summary, group);
    filterPicker.append(category);
    return group;
  };
  const filterControls = {
    tests: checkbox('Include tests'),
    external: checkbox('Include external'),
    generated: checkbox('Include generated'),
    basis: multiple('Basis', [
      'projectDeclared',
      'projectEvaluated',
      'usingInferred',
      'symbolResolved'
    ]),
    projectKinds: multiple('Project kinds', ['app', 'web', 'library', 'test', 'desktop']),
    kinds: multiple('Entity kinds', [
      'app',
      'web',
      'library',
      'test',
      'desktop',
      'class',
      'interface',
      'struct',
      'record',
      'enum',
      'delegate'
    ]),
    relations: multiple('Relations', [
      'inherits',
      'implements',
      'signature',
      'constraint',
      'constructs',
      'calls',
      'memberAccess',
      'attribute',
      'typeUse',
      'compileTimeName'
    ])
  };
  const values = (group: HTMLFieldSetElement) =>
    [...group.querySelectorAll<HTMLInputElement>('input:checked')].map((input) => input.value);
  for (const input of Object.values(filterControls))
    input.addEventListener('change', () =>
      handlers.onFilters({
        includeTests: filterControls.tests.checked,
        includeExternal: filterControls.external.checked,
        includeGenerated: filterControls.generated.checked,
        basis: values(filterControls.basis),
        kinds: values(filterControls.kinds),
        projectKinds: values(filterControls.projectKinds),
        relationKinds: values(filterControls.relations) as Filters['relationKinds']
      })
    );
  mapToolbar.append(backButton, depthSelect, filterSummary, filterPicker);

  const graphControls = element('div', 'sd-graph-controls');
  graphControls.setAttribute('role', 'group');
  graphControls.setAttribute('aria-label', 'Graph controls');
  const { trigger: spacingSummary, panel: graphTools } = toolbarPopover(
    'spacing',
    'Spacing',
    'above'
  );
  const group = (name: string): HTMLFieldSetElement => {
    const fieldset = document.createElement('fieldset');
    fieldset.className = 'sd-control-group';
    const legend = document.createElement('legend');
    legend.textContent = name;
    fieldset.append(legend);
    return fieldset;
  };
  const viewTools = group('View');
  viewTools.classList.add('sd-graph-bar');
  const spacingTools = group('Spacing');
  const imageTools = group('Image export');
  const layoutActions = element('div', 'sd-control-actions');
  const layoutDirection = select('sd-layout-direction', [
    ['RIGHT', 'Horizontal'],
    ['DOWN', 'Vertical']
  ]);
  const directionLabel = document.createElement('label');
  directionLabel.textContent = 'Direction';
  directionLabel.htmlFor = layoutDirection.id;
  directionLabel.append(layoutDirection);
  viewTools.append(directionLabel);
  const cancelLayout = button('sd-cancel-layout', 'Cancel layout');
  cancelLayout.addEventListener('click', handlers.onCancelLayout);
  const retryLayout = button('sd-retry-layout', 'Retry layout');
  retryLayout.addEventListener('click', handlers.onRetryLayout);
  layoutActions.append(cancelLayout, retryLayout);
  const zoomActions = element('div', 'sd-control-actions');
  for (const [label, action] of [
    ['Zoom in', 'in'],
    ['Zoom out', 'out'],
    ['Fit', 'fit']
  ] as const) {
    const control = button(`sd-${action}`, label);
    if (action !== 'fit') {
      control.textContent = action === 'in' ? '+' : '−';
      control.setAttribute('aria-label', label);
      control.title = label;
    }
    control.addEventListener('click', () => handlers.onZoom(action));
    zoomActions.append(control);
  }
  viewTools.append(zoomActions);
  const range = (name: string, min: number, max: number, value: number) => {
    const input = document.createElement('input');
    input.type = 'range';
    input.min = String(min);
    input.max = String(max);
    input.value = String(value);
    input.id = `sd-${name.toLowerCase().replaceAll(' ', '-')}`;
    input.setAttribute('aria-label', name);
    const label = document.createElement('label');
    label.textContent = name;
    label.className = 'sd-range-field';
    label.htmlFor = input.id;
    const output = document.createElement('output');
    output.htmlFor = input.id;
    output.value = String(value);
    label.append(input, output);
    (name === 'Zoom percent' ? viewTools : spacingTools).append(label);
    return input;
  };
  const zoom = range('Zoom percent', 20, 600, 100);
  zoom.addEventListener('input', () => handlers.onZoom(Number(zoom.value) / 100));
  const nodeSpacing = range('Node spacing', 10, 160, 40);
  const rankSpacing = range('Rank spacing', 20, 240, 80);
  for (const input of [nodeSpacing, rankSpacing, layoutDirection])
    input.addEventListener('change', () =>
      handlers.onLayout({
        direction: layoutDirection.value === 'DOWN' ? 'DOWN' : 'RIGHT',
        nodeSpacing: Number(nodeSpacing.value),
        rankSpacing: Number(rankSpacing.value)
      })
    );
  const imageOptions = {
    profile: document.createElement('input'),
    omissions: document.createElement('input'),
    legend: document.createElement('input')
  };
  for (const [key, labelText] of [
    ['profile', 'Image: target and profile'],
    ['omissions', 'Image: scope and omissions'],
    ['legend', 'Image: legend']
  ] as const) {
    const input = imageOptions[key];
    input.type = 'checkbox';
    input.checked = true;
    const label = document.createElement('label');
    label.append(input, document.createTextNode(labelText));
    imageTools.append(label);
    input.addEventListener('change', () =>
      handlers.onImageOptions({
        profile: imageOptions.profile.checked,
        omissions: imageOptions.omissions.checked,
        legend: imageOptions.legend.checked
      })
    );
  }
  graphTools.append(spacingTools, layoutActions);
  viewTools.append(spacingSummary);
  graphControls.append(viewTools, graphTools);
  center.append(graphControls);
  exportMenu.append(imageTools);
  const legend = element('div', 'sd-legend');
  legend.setAttribute('aria-label', 'Graph legend');
  center.insertBefore(legend, mapHost);

  const inspectorSplitter = element('div', 'sd-splitter sd-splitter-inspector');
  inspectorSplitter.setAttribute('role', 'separator');
  inspectorSplitter.setAttribute('aria-orientation', 'vertical');
  inspectorSplitter.setAttribute('aria-label', 'Resize details pane');
  inspectorSplitter.tabIndex = 0;

  const inspectorPane = element('aside', 'sd-inspector');
  const inspectorHeader = element('div', 'sd-inspector-header');
  const inspectorTitle = element('h2', 'sd-inspector-title', 'Details');
  inspectorTitle.id = 'sd-inspector-title';
  inspectorPane.setAttribute('aria-labelledby', inspectorTitle.id);
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
  modeSelect.addEventListener('change', () =>
    handlers.onMode(modeSelect.value as 'quick' | 'semantic')
  );
  copyButton.addEventListener('click', () => handlers.onCopyContext());
  granularitySelect.addEventListener('change', () =>
    handlers.onGranularity(granularitySelect.value as 'project' | 'namespace' | 'type')
  );
  inspectorToggle.addEventListener('click', () => handlers.onInspectorToggled());
  wireSplitter(navSplitter, 'navigation', handlers);
  wireSplitter(inspectorSplitter, 'inspector', handlers);

  const elements: ShellElements = {
    languageToggle,
    setLanguage: shellTranslations(root),
    zoom,
    nodeSpacing,
    rankSpacing,
    layoutDirection,
    imageOptions,
    legend,
    root,
    targetName,
    targetPath,
    modeSelect,
    configuration,
    platform,
    backButton,
    depthSelect,
    filterControls,
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
    graphControls,
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
  const paneElement = (
    pane === 'navigation' ? splitter.previousElementSibling : splitter.nextElementSibling
  ) as HTMLElement | null;
  let startX = 0;
  let startWidth = 0;
  const applyWith = (event: PointerEvent): void => {
    const delta = event.clientX - startX;
    const width = startWidth + (pane === 'navigation' ? delta : -delta);
    handlers.onPaneResized(pane, width);
  };

  splitter.addEventListener('pointerdown', (event) => {
    if (event.button !== 0 || !paneElement) return;
    event.preventDefault();
    startX = event.clientX;
    startWidth = paneElement.getBoundingClientRect().width;
    splitter.setPointerCapture(event.pointerId);
    splitter.dataset.dragging = 'true';
  });
  splitter.addEventListener('pointermove', (event) => {
    if (splitter.dataset.dragging === 'true') {
      applyWith(event);
    }
  });
  const stopDragging = (): void => {
    delete splitter.dataset.dragging;
  };
  splitter.addEventListener('pointerup', (event) => {
    if (splitter.hasPointerCapture(event.pointerId)) {
      splitter.releasePointerCapture(event.pointerId);
    }
    stopDragging();
  });
  splitter.addEventListener('pointercancel', stopDragging);
  splitter.addEventListener('lostpointercapture', stopDragging);
  splitter.addEventListener('keydown', (event) => {
    if (!paneElement) {
      return;
    }

    const current = paneElement.getBoundingClientRect().width;
    const step = pane === 'navigation' ? 16 : -16;
    if (event.key === 'ArrowLeft') {
      handlers.onPaneResized(pane, current - step);
      event.preventDefault();
    } else if (event.key === 'ArrowRight') {
      handlers.onPaneResized(pane, current + step);
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

/** Native top-layer panels do not resize the toolbar or get clipped by the map. */
function toolbarPopover(
  id: string,
  label: string,
  placement: 'above' | 'below' = 'below'
): {
  trigger: HTMLButtonElement;
  panel: HTMLDivElement;
} {
  const trigger = button(`sd-${id}-toggle`, label, 'toggle');
  const panel = element('div', 'sd-toolbar-popover');
  panel.id = `sd-${id}-panel`;
  panel.setAttribute('popover', 'auto');
  panel.setAttribute('role', 'group');
  panel.setAttribute('aria-label', label);
  trigger.setAttribute('popovertarget', panel.id);
  trigger.setAttribute('aria-expanded', 'false');
  panel.addEventListener('beforetoggle', (event) => {
    const open = (event as ToggleEvent).newState === 'open';
    trigger.setAttribute('aria-expanded', String(open));
    if (!open) return;
    const anchor = trigger.getBoundingClientRect();
    const width = Math.min(320, window.innerWidth - 16);
    const top = Math.min(anchor.bottom + 4, window.innerHeight - 80);
    panel.style.width = `${width}px`;
    panel.style.left = `${Math.max(8, Math.min(anchor.left, window.innerWidth - width - 8))}px`;
    panel.style.top = placement === 'above' ? 'auto' : `${Math.max(8, top)}px`;
    panel.style.bottom =
      placement === 'above' ? `${window.innerHeight - anchor.top + 4}px` : 'auto';
    panel.style.maxHeight = `${Math.max(0, placement === 'above' ? anchor.top - 12 : window.innerHeight - top - 8)}px`;
  });
  window.addEventListener('resize', () => panel.hidePopover());
  return { trigger, panel };
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
