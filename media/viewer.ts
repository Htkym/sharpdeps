import mermaid from 'mermaid';
import type {
  CodeMapViewModel,
  ExportFormat,
  Granularity,
  GraphView,
  HostToWebviewMessage,
  ThemeKind,
  WebviewToHostMessage
} from '../src/view/protocol';
import type {
  CodeMapDiagramEdge,
  CodeMapDiagramProject,
  DependencyCycle
} from '../src/analyzer/types';

declare function acquireVsCodeApi(): {
  postMessage(msg: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
};

type StatusState = 'ready' | 'warning';
type NodeEntry = CodeMapDiagramProject & { element: SVGGElement };
type EdgeEntry = CodeMapDiagramEdge & { element: SVGElement };
type Binding = {
  highlightCycle(nodes: readonly string[]): void;
  clear(): void;
};

type Elements = {
  title: HTMLElement;
  subtitle: HTMLElement;
  projectsButton: HTMLButtonElement;
  namespacesButton: HTMLButtonElement;
  namespaceNote: HTMLElement;
  refreshButton: HTMLButtonElement;
  copyButton: HTMLButtonElement;
  copyForAgentButton: HTMLButtonElement;
  exportSvgButton: HTMLButtonElement;
  exportPngButton: HTMLButtonElement;
  status: HTMLElement;
  viewport: HTMLElement;
  scroll: HTMLElement;
  zoomInButton: HTMLButtonElement;
  zoomOutButton: HTMLButtonElement;
  zoomFitButton: HTMLButtonElement;
  zoomLevel: HTMLElement;
  zoomSlider: HTMLInputElement;
  nodeSpacingSlider: HTMLInputElement;
  rankSpacingSlider: HTMLInputElement;
  testToggleButton: HTMLButtonElement;
  source: HTMLElement;
  cycleList: HTMLElement;
  cycleEmpty: HTMLElement;
  graphSummary: HTMLElement;
  legend: HTMLElement;
  warningsList: HTMLElement;
  warningsEmpty: HTMLElement;
  notesList: HTMLElement;
  notesEmpty: HTMLElement;
  mainArea: HTMLElement;
  panelSplitter: HTMLElement;
};

const CYCLE_COLOR = '#e5484d';
const SELECTION_COLOR = '#3b82f6';
const KIND_COLORS: Record<string, string> = {
  web: '#f59e0b',
  library: '#10b981',
  test: '#a855f7',
  desktop: '#06b6d4',
  app: '#eab308'
};
const DEFAULT_KIND_COLOR = '#8b8f98';

function getKindColor(kind: string): string {
  return KIND_COLORS[kind.toLowerCase()] ?? DEFAULT_KIND_COLOR;
}

const vscode = acquireVsCodeApi();

const state: {
  model: CodeMapViewModel | null;
  theme: ThemeKind;
  granularity: Granularity;
  renderSequence: number;
} = {
  model: null,
  theme: 'light',
  granularity: 'projects',
  renderSequence: 0
};

let elements: Elements;
let currentGraph: GraphView | null = null;
let currentBinding: Binding | null = null;
let mermaidInitialized = false;
let hideTestProjects = false;

const MIN_ZOOM = 0.1;
const MAX_ZOOM = 4;
const ZOOM_STEP = 1.2;
const ZOOM_WHEEL_SENSITIVITY = 0.0015;
const FIT_PADDING = 16;
const PAN_THRESHOLD = 4;
// The zoom slider uses a logarithmic scale so that dragging feels equally
// precise across the whole 10%-400% range (a linear scale made small drags
// change the zoom level far too much, especially in the common 20%-100% band).
const ZOOM_SLIDER_RESOLUTION = 1000;

const zoomState: {
  svg: SVGSVGElement | null;
  intrinsicWidth: number;
  intrinsicHeight: number;
  zoom: number;
  fitZoom: number;
} = { svg: null, intrinsicWidth: 0, intrinsicHeight: 0, zoom: 1, fitZoom: 1 };

const layoutSpacing = { nodeSpacing: 50, rankSpacing: 50 };
let spacingRenderTimer: ReturnType<typeof setTimeout> | undefined;

// When set, the next setupZoom() call keeps the current zoom level instead of
// re-fitting the diagram to the viewport. Used for re-renders that are not a
// user-initiated navigation (spacing changes, test-project visibility toggle)
// so the zoom level the user chose is not silently reset.
let keepZoomOnNextRender = false;

let suppressNextClick = false;

function postMessage(message: WebviewToHostMessage): void {
  vscode.postMessage(message);
}

function main(): void {
  elements = buildShell();
  wireUiEvents();
  wireZoomEvents();
  wireSpacingEvents();
  wirePanelSplitter();
  window.addEventListener('message', (event: MessageEvent<HostToWebviewMessage>) => {
    void handleHostMessage(event.data);
  });
  setStatus('Waiting for dependency graph data...', 'ready');
  postMessage({ type: 'ready' });
}

function buildShell(): Elements {
  const app = document.getElementById('app');
  if (!app) {
    throw new Error('Missing #app host element.');
  }

  app.className = 'sharpdeps-app';
  app.replaceChildren();

  const header = createElement('header', 'viewer-toolbar');
  const titleBlock = createElement('div', 'title-block');
  const eyebrow = createElement('p', 'eyebrow', 'SharpDeps dependency map');
  const title = createElement('h1', undefined, 'Loading dependency graph...');
  title.id = 'viewer-title';
  const subtitle = createElement('p', 'subtitle');
  subtitle.id = 'viewer-subtitle';
  titleBlock.append(eyebrow, title, subtitle);

  const actions = createElement('div', 'toolbar-actions');
  const granularity = createElement('div', 'granularity-toggle');
  granularity.setAttribute('role', 'group');
  granularity.setAttribute('aria-label', 'Graph granularity');
  const projectsButton = createButton('viewer-projects', 'toggle active', 'Projects');
  projectsButton.setAttribute('aria-pressed', 'true');
  const namespacesButton = createButton('viewer-namespaces', 'toggle', 'Namespaces');
  namespacesButton.setAttribute('aria-pressed', 'false');
  granularity.append(projectsButton, namespacesButton);

  const refreshButton = createIconButton('viewer-refresh', 'Refresh dependency map', ICONS.refresh);
  const copyButton = createIconButton('viewer-copy-mermaid', 'Copy Mermaid source', ICONS.copy);
  const copyForAgentButton = createIconButton(
    'viewer-copy-agent',
    'Copy analysis and Coding Agent prompt',
    ICONS.copyForAgent
  );
  const exportSvgButton = createIconButton(
    'viewer-export-svg',
    'Export as SVG',
    ICONS.download,
    'SVG'
  );
  const exportPngButton = createIconButton(
    'viewer-export-png',
    'Export as PNG',
    ICONS.download,
    'PNG'
  );
  actions.append(
    granularity,
    refreshButton,
    copyButton,
    copyForAgentButton,
    exportSvgButton,
    exportPngButton
  );
  header.append(titleBlock, actions);

  const namespaceNote = createElement(
    'div',
    'namespace-note',
    'Namespace graph is unavailable for this solution.'
  );
  namespaceNote.id = 'viewer-namespace-note';
  namespaceNote.hidden = true;

  const mainArea = createElement('main', 'viewer-main');
  const graphPanel = createElement('section', 'graph-panel panel');
  graphPanel.setAttribute('aria-label', 'Dependency graph');
  const status = createElement('div', 'status', 'Loading dependency graph...');
  status.id = 'viewer-status';
  status.setAttribute('role', 'status');
  status.setAttribute('aria-live', 'polite');

  const legend = createElement('div', 'legend');
  legend.setAttribute('aria-label', 'Graph legend');
  legend.append(
    legendItem('cycle-swatch', 'Circular dependency'),
    legendItem('selected-swatch', 'Selected / connected')
  );

  const scroll = createElement('div', 'diagram-scroll');
  const stage = createElement('div', 'diagram-stage');
  const viewport = createElement('div', 'diagram-viewport');
  viewport.id = 'viewer-graph';
  stage.append(viewport);
  scroll.append(stage);

  const spacingControls = createElement('div', 'spacing-controls');
  spacingControls.setAttribute('role', 'group');
  spacingControls.setAttribute('aria-label', 'Diagram spacing controls');
  const nodeSpacingLabel = createElement('label', 'spacing-label', 'Node spacing');
  nodeSpacingLabel.htmlFor = 'viewer-node-spacing';
  const nodeSpacingSlider = document.createElement('input');
  nodeSpacingSlider.type = 'range';
  nodeSpacingSlider.id = 'viewer-node-spacing';
  nodeSpacingSlider.className = 'spacing-slider';
  nodeSpacingSlider.min = '10';
  nodeSpacingSlider.max = '300';
  nodeSpacingSlider.step = '10';
  nodeSpacingSlider.value = String(layoutSpacing.nodeSpacing);
  nodeSpacingSlider.setAttribute('aria-label', 'Node spacing');
  const rankSpacingLabel = createElement('label', 'spacing-label', 'Rank spacing');
  rankSpacingLabel.htmlFor = 'viewer-rank-spacing';
  const rankSpacingSlider = document.createElement('input');
  rankSpacingSlider.type = 'range';
  rankSpacingSlider.id = 'viewer-rank-spacing';
  rankSpacingSlider.className = 'spacing-slider';
  rankSpacingSlider.min = '10';
  rankSpacingSlider.max = '300';
  rankSpacingSlider.step = '10';
  rankSpacingSlider.value = String(layoutSpacing.rankSpacing);
  rankSpacingSlider.setAttribute('aria-label', 'Rank spacing');
  spacingControls.append(nodeSpacingLabel, nodeSpacingSlider, rankSpacingLabel, rankSpacingSlider);

  const zoomControls = createElement('div', 'zoom-controls');
  zoomControls.setAttribute('role', 'group');
  zoomControls.setAttribute('aria-label', 'Zoom controls');
  const zoomOutButton = createIconButton('viewer-zoom-out', 'Zoom out', ICONS.zoomOut);
  const zoomSlider = document.createElement('input');
  zoomSlider.type = 'range';
  zoomSlider.id = 'viewer-zoom-slider';
  zoomSlider.className = 'zoom-slider';
  zoomSlider.min = '0';
  zoomSlider.max = String(ZOOM_SLIDER_RESOLUTION);
  zoomSlider.step = '1';
  zoomSlider.setAttribute('aria-label', 'Zoom level');
  const zoomLevel = createElement('span', 'zoom-level', '—');
  zoomLevel.id = 'viewer-zoom-level';
  zoomLevel.setAttribute('aria-live', 'polite');
  const zoomInButton = createIconButton('viewer-zoom-in', 'Zoom in', ICONS.zoomIn);
  const zoomDivider = createElement('span', 'zoom-divider');
  zoomDivider.setAttribute('aria-hidden', 'true');
  const zoomFitButton = createIconButton('viewer-zoom-fit', 'Fit to view', ICONS.fit);
  const testDivider = createElement('span', 'zoom-divider');
  testDivider.setAttribute('aria-hidden', 'true');
  const testToggleButton = createIconButton(
    'viewer-toggle-test',
    'Hide test projects',
    ICONS.flask
  );
  testToggleButton.setAttribute('aria-pressed', 'false');
  zoomOutButton.disabled = true;
  zoomInButton.disabled = true;
  zoomFitButton.disabled = true;
  zoomSlider.disabled = true;
  zoomControls.append(
    zoomOutButton,
    zoomSlider,
    zoomLevel,
    zoomInButton,
    zoomDivider,
    zoomFitButton,
    testDivider,
    testToggleButton
  );

  const controlsPanel = createElement('div', 'controls-panel');
  controlsPanel.append(spacingControls, zoomControls);

  graphPanel.append(status, legend, scroll, controlsPanel);

  const sidebar = createElement('aside', 'cycle-sidebar panel');
  sidebar.setAttribute('aria-label', 'Graph details');
  const sidebarTitle = createElement('h2', undefined, 'Circular dependencies');
  const graphSummary = createElement('p', 'graph-summary');
  graphSummary.id = 'viewer-graph-summary';
  const cycleList = document.createElement('ul');
  cycleList.id = 'viewer-cycles';
  cycleList.className = 'cycle-list';
  const cycleEmpty = createElement('p', 'empty', 'No cycles detected.');
  cycleEmpty.id = 'viewer-cycles-empty';

  const sourceDetails = createElement('details', 'source-details');
  const sourceSummary = document.createElement('summary');
  sourceSummary.textContent = 'Mermaid source';
  const source = createElement('pre');
  source.id = 'viewer-mermaid-source';
  sourceDetails.append(sourceSummary, source);

  const warnings = detailsList(
    'Warnings',
    'viewer-warnings',
    'viewer-warnings-empty',
    'No warnings.'
  );
  const notes = detailsList('Notes', 'viewer-notes', 'viewer-notes-empty', 'No notes.');

  sidebar.append(
    sidebarTitle,
    graphSummary,
    cycleList,
    cycleEmpty,
    warnings.container,
    notes.container,
    sourceDetails
  );

  const panelSplitter = createElement('div', 'panel-splitter');
  panelSplitter.setAttribute('role', 'separator');
  panelSplitter.setAttribute('aria-orientation', 'vertical');
  panelSplitter.setAttribute('aria-label', 'Resize graph panel');
  panelSplitter.tabIndex = 0;

  mainArea.append(graphPanel, panelSplitter, sidebar);
  app.append(header, namespaceNote, mainArea);

  return {
    title,
    subtitle,
    projectsButton,
    namespacesButton,
    namespaceNote,
    refreshButton,
    copyButton,
    copyForAgentButton,
    exportSvgButton,
    exportPngButton,
    status,
    viewport,
    scroll,
    zoomInButton,
    zoomOutButton,
    zoomFitButton,
    zoomLevel,
    zoomSlider,
    nodeSpacingSlider,
    rankSpacingSlider,
    testToggleButton,
    source,
    cycleList,
    cycleEmpty,
    graphSummary,
    legend,
    warningsList: warnings.list,
    warningsEmpty: warnings.empty,
    notesList: notes.list,
    notesEmpty: notes.empty,
    mainArea,
    panelSplitter
  };
}

function createElement<K extends keyof HTMLElementTagNameMap>(
  tagName: K,
  className?: string,
  textContent?: string
): HTMLElementTagNameMap[K] {
  const element = document.createElement(tagName);
  if (className) {
    element.className = className;
  }
  if (textContent !== undefined) {
    element.textContent = textContent;
  }
  return element;
}

function createButton(id: string, className: string | undefined, text: string): HTMLButtonElement {
  const button = document.createElement('button');
  button.id = id;
  button.type = 'button';
  if (className) {
    button.className = className;
  }
  button.textContent = text;
  return button;
}

type IconShape = { tag: string; attrs: Record<string, string>; text?: string };

const SVG_NS = 'http://www.w3.org/2000/svg';

const ICONS: Record<
  | 'refresh'
  | 'copy'
  | 'copyForAgent'
  | 'download'
  | 'zoomIn'
  | 'zoomOut'
  | 'fit'
  | 'flask'
  | 'flaskOff',
  IconShape[]
> = {
  refresh: [
    { tag: 'polyline', attrs: { points: '23 4 23 10 17 10' } },
    { tag: 'polyline', attrs: { points: '1 20 1 14 7 14' } },
    {
      tag: 'path',
      attrs: { d: 'M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15' }
    }
  ],
  copy: [
    { tag: 'rect', attrs: { x: '9', y: '9', width: '13', height: '13', rx: '2', ry: '2' } },
    { tag: 'path', attrs: { d: 'M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1' } }
  ],
  // Original flat icon: a chat bubble containing "AI" plus a sparkle badge,
  // used for "copy analysis + agent prompt". Not derived from any
  // third-party artwork/logo (e.g. not the Copilot mark or any stock icon).
  copyForAgent: [
    { tag: 'rect', attrs: { x: '3', y: '4', width: '15', height: '11', rx: '2', ry: '2' } },
    { tag: 'path', attrs: { d: 'M7 15v4l5-4' } },
    {
      tag: 'text',
      attrs: {
        x: '10.5',
        y: '11.4',
        'font-size': '6.5',
        'font-weight': '700',
        'font-family': 'sans-serif',
        'text-anchor': 'middle',
        fill: 'currentColor',
        stroke: 'none'
      },
      text: 'AI'
    },
    {
      tag: 'path',
      attrs: {
        d: 'M19.5 1 20.4 3.1 22.5 4 20.4 4.9 19.5 7 18.6 4.9 16.5 4 18.6 3.1Z',
        fill: 'currentColor',
        stroke: 'none'
      }
    }
  ],
  download: [
    { tag: 'path', attrs: { d: 'M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4' } },
    { tag: 'polyline', attrs: { points: '7 10 12 15 17 10' } },
    { tag: 'line', attrs: { x1: '12', y1: '15', x2: '12', y2: '3' } }
  ],
  zoomIn: [
    { tag: 'circle', attrs: { cx: '11', cy: '11', r: '8' } },
    { tag: 'line', attrs: { x1: '21', y1: '21', x2: '16.65', y2: '16.65' } },
    { tag: 'line', attrs: { x1: '11', y1: '8', x2: '11', y2: '14' } },
    { tag: 'line', attrs: { x1: '8', y1: '11', x2: '14', y2: '11' } }
  ],
  zoomOut: [
    { tag: 'circle', attrs: { cx: '11', cy: '11', r: '8' } },
    { tag: 'line', attrs: { x1: '21', y1: '21', x2: '16.65', y2: '16.65' } },
    { tag: 'line', attrs: { x1: '8', y1: '11', x2: '14', y2: '11' } }
  ],
  fit: [
    { tag: 'path', attrs: { d: 'M8 3H5a2 2 0 0 0-2 2v3' } },
    { tag: 'path', attrs: { d: 'M21 8V5a2 2 0 0 0-2-2h-3' } },
    { tag: 'path', attrs: { d: 'M3 16v3a2 2 0 0 0 2 2h3' } },
    { tag: 'path', attrs: { d: 'M16 21h3a2 2 0 0 0 2-2v-3' } }
  ],
  // Conical lab flask — the universal "test" symbol, used for the
  // show/hide test-projects toggle.
  flask: [
    {
      tag: 'path',
      attrs: {
        d: 'M10 2v8.5a2.5 2.5 0 0 1-.34 1.26L4.5 21a1 1 0 0 0 .87 1.5h13.26a1 1 0 0 0 .87-1.5l-5.16-9.24a2.5 2.5 0 0 1-.34-1.26V2'
      }
    },
    { tag: 'path', attrs: { d: 'M8.5 2h7' } },
    { tag: 'path', attrs: { d: 'M7 16h10' } }
  ],
  // The same flask crossed out with a slash — shown while test projects are hidden.
  flaskOff: [
    {
      tag: 'path',
      attrs: {
        d: 'M10 2v8.5a2.5 2.5 0 0 1-.34 1.26L4.5 21a1 1 0 0 0 .87 1.5h13.26a1 1 0 0 0 .87-1.5l-5.16-9.24a2.5 2.5 0 0 1-.34-1.26V2'
      }
    },
    { tag: 'path', attrs: { d: 'M8.5 2h7' } },
    { tag: 'path', attrs: { d: 'M7 16h10' } },
    { tag: 'line', attrs: { x1: '3', y1: '3', x2: '21', y2: '21' } }
  ]
};

function createIcon(shapes: IconShape[]): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', '16');
  svg.setAttribute('height', '16');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '2');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');
  for (const shape of shapes) {
    const element = document.createElementNS(SVG_NS, shape.tag);
    for (const [name, value] of Object.entries(shape.attrs)) {
      element.setAttribute(name, value);
    }
    if (shape.text) {
      element.textContent = shape.text;
    }
    svg.append(element);
  }
  return svg;
}

function createIconButton(
  id: string,
  label: string,
  shapes: IconShape[],
  textLabel?: string
): HTMLButtonElement {
  const button = document.createElement('button');
  button.id = id;
  button.type = 'button';
  button.className = textLabel ? 'icon-button has-label' : 'icon-button';
  button.title = label;
  button.setAttribute('aria-label', label);
  button.append(createIcon(shapes));
  if (textLabel) {
    button.append(createElement('span', 'icon-button-text', textLabel));
  }
  return button;
}

function legendItem(swatchClass: string, label: string): HTMLElement {
  const item = createElement('span', 'legend-item');
  const swatch = createElement('span', `swatch ${swatchClass}`);
  swatch.setAttribute('aria-hidden', 'true');
  item.append(swatch, document.createTextNode(label));
  return item;
}

function detailsList(
  title: string,
  listId: string,
  emptyId: string,
  emptyText: string
): {
  container: HTMLDetailsElement;
  list: HTMLUListElement;
  empty: HTMLElement;
} {
  const container = document.createElement('details');
  container.className = 'detail-panel';
  const summary = document.createElement('summary');
  summary.textContent = title;
  const list = document.createElement('ul');
  list.id = listId;
  const empty = createElement('p', 'empty', emptyText);
  empty.id = emptyId;
  container.append(summary, list, empty);
  return { container, list, empty };
}

function wireUiEvents(): void {
  elements.projectsButton.addEventListener('click', () => {
    void selectGraph('projects');
  });
  elements.namespacesButton.addEventListener('click', () => {
    void selectGraph('namespaces');
  });
  elements.refreshButton.addEventListener('click', () => {
    postMessage({ type: 'refresh' });
    setStatus('Refreshing dependency map…', 'ready');
  });
  elements.copyButton.addEventListener('click', copyCurrentMermaid);
  elements.copyForAgentButton.addEventListener('click', copyForAgent);
  elements.exportSvgButton.addEventListener('click', () => {
    void exportCurrentGraph('svg');
  });
  elements.exportPngButton.addEventListener('click', () => {
    void exportCurrentGraph('png');
  });
}

function wireSpacingEvents(): void {
  const handleSpacingChange = (): void => {
    layoutSpacing.nodeSpacing = Number(elements.nodeSpacingSlider.value);
    layoutSpacing.rankSpacing = Number(elements.rankSpacingSlider.value);
    if (spacingRenderTimer !== undefined) {
      clearTimeout(spacingRenderTimer);
    }
    spacingRenderTimer = setTimeout(() => {
      initializeMermaid(state.theme);
      keepZoomOnNextRender = true;
      void renderGraph(state.granularity);
    }, 120);
  };
  elements.nodeSpacingSlider.addEventListener('input', handleSpacingChange);
  elements.rankSpacingSlider.addEventListener('input', handleSpacingChange);
}

async function handleHostMessage(message: HostToWebviewMessage): Promise<void> {
  switch (message.type) {
    case 'render':
      state.model = message.model;
      state.theme = message.theme;
      state.granularity = 'projects';
      bindModelMetadata(message.model);
      initializeMermaid(message.theme);
      await selectGraph('projects');
      break;
    case 'theme':
      state.theme = message.theme;
      initializeMermaid(message.theme);
      if (state.model) {
        await renderGraph(state.granularity);
      }
      break;
    case 'setGranularity':
      await selectGraph(message.granularity);
      break;
    case 'doExport':
      await exportCurrentGraph(message.format);
      break;
    case 'doCopyMermaid':
      copyCurrentMermaid();
      break;
  }
}

function initializeMermaid(theme: ThemeKind): void {
  mermaid.initialize({
    startOnLoad: false,
    theme: theme === 'dark' ? 'dark' : 'default',
    securityLevel: 'loose',
    flowchart: {
      useMaxWidth: false,
      htmlLabels: false,
      nodeSpacing: layoutSpacing.nodeSpacing,
      rankSpacing: layoutSpacing.rankSpacing
    }
  });
  mermaidInitialized = true;
}

async function selectGraph(granularity: Granularity): Promise<void> {
  if (!state.model) {
    return;
  }
  if (granularity === 'namespaces' && !hasNamespaceGraph(state.model)) {
    return;
  }

  state.granularity = granularity;
  vscode.setState({ granularity });
  const isProjects = granularity === 'projects';
  elements.projectsButton.classList.toggle('active', isProjects);
  elements.projectsButton.setAttribute('aria-pressed', isProjects ? 'true' : 'false');
  elements.namespacesButton.classList.toggle('active', !isProjects);
  elements.namespacesButton.setAttribute('aria-pressed', isProjects ? 'false' : 'true');
  await renderGraph(granularity);
}

function getGraphData(granularity: Granularity): GraphView {
  if (!state.model) {
    return { granularity, mermaid: '', nodes: [], edges: [], cycles: [] };
  }
  return granularity === 'namespaces' ? state.model.namespaceGraph : state.model.projectGraph;
}

async function renderGraph(granularity: Granularity): Promise<void> {
  const graph = getGraphData(granularity);
  currentGraph = graph;
  currentBinding = null;
  const baseMermaidSource =
    graph.mermaid || `flowchart LR\n  Empty["No ${graphLabel(graph)} data"]`;
  const hiddenNodeIds = getHiddenTestNodeIds(graph);
  const mermaidSource = filterHiddenNodesFromMermaid(baseMermaidSource, hiddenNodeIds);
  elements.source.textContent = mermaidSource;
  bindCycleList(graph.cycles);
  setGraphSummary(graph);
  updateLegend(graph);

  if (!mermaidInitialized) {
    initializeMermaid(state.theme);
  }

  const renderId = nextRenderId();
  const sequence = ++state.renderSequence;
  try {
    const rendered = await mermaid.render(renderId, mermaidSource);
    if (sequence !== state.renderSequence) {
      return;
    }

    elements.viewport.innerHTML = rendered.svg;
    const svg = elements.viewport.querySelector('svg');
    if (svg) {
      svg.removeAttribute('width');
      svg.removeAttribute('height');
      svg.setAttribute('role', 'img');
      svg.setAttribute('aria-label', `${graphLabel(graph)} dependency graph`);
      scaleArrowMarkers(svg);
      setupZoom(svg);
      updateTestToggleButton();
    } else {
      disableZoom();
    }

    if (typeof rendered.bindFunctions === 'function') {
      rendered.bindFunctions(elements.viewport);
    }

    let statusMessage = baseStatusMessage(graph);
    if (svg && bindCodeMapInteractivity(svg, graph)) {
      statusMessage += ` Click a ${graphLabel(graph)} node to highlight connected references.`;
    }
    setStatus(statusMessage, 'ready');
  } catch (error) {
    renderFailure(error, mermaidSource);
  }
}

function bindModelMetadata(model: CodeMapViewModel): void {
  elements.title.textContent = model.solutionName || 'Dependency Map';
  elements.subtitle.textContent = model.solutionPath || '';
  const namespacesAvailable = hasNamespaceGraph(model);
  elements.namespacesButton.disabled = !namespacesAvailable;
  elements.namespacesButton.title = namespacesAvailable
    ? 'Show namespace-level dependencies'
    : 'No namespace graph available.';
  elements.namespaceNote.hidden = namespacesAvailable;
  bindTextList(elements.warningsList, elements.warningsEmpty, model.meta.warnings);
  bindTextList(elements.notesList, elements.notesEmpty, model.meta.notes);
}

function hasNamespaceGraph(model: CodeMapViewModel): boolean {
  return Array.isArray(model.namespaceGraph.nodes) && model.namespaceGraph.nodes.length > 0;
}

function baseStatusMessage(graph: GraphView): string {
  let message = `${capitalize(graphLabel(graph))} graph: ${formatNumber(graph.nodes.length)} node(s), ${formatNumber(graph.edges.length)} dependency edge(s).`;
  if (graph.cycles.length) {
    message += ` ${formatNumber(graph.cycles.length)} circular dependency group(s) highlighted in red.`;
  }
  return message;
}

function setGraphSummary(graph: GraphView): void {
  elements.graphSummary.textContent = `${formatNumber(graph.nodes.length)} node(s), ${formatNumber(graph.edges.length)} edge(s), ${formatNumber(graph.cycles.length)} cycle(s).`;
}

function updateLegend(graph: GraphView): void {
  const kinds = Array.from(new Set(graph.nodes.map((node) => node.kind))).sort();
  const kindItems = kinds.map((kind) => {
    const item = legendItem('', capitalize(kind));
    const swatch = item.querySelector<HTMLElement>('.swatch');
    if (swatch) {
      swatch.style.background = getKindColor(kind);
    }
    return item;
  });
  elements.legend.replaceChildren(
    legendItem('cycle-swatch', 'Circular dependency'),
    legendItem('selected-swatch', 'Selected / connected'),
    ...kindItems
  );
}

function renderFailure(error: unknown, mermaidSource: string): void {
  elements.viewport.replaceChildren();
  elements.viewport.append(
    createElement('pre', 'render-fallback', mermaidSource || 'Mermaid source is unavailable.')
  );
  disableZoom();
  const message = getMessage(error, 'Graph rendering failed. Showing Mermaid source.');
  setStatus(message, 'warning');
  postMessage({ type: 'log', level: 'error', message });
}

function setupZoom(svg: SVGSVGElement): void {
  const viewBox = svg.viewBox.baseVal;
  let intrinsicWidth = viewBox && viewBox.width > 0 ? viewBox.width : 0;
  let intrinsicHeight = viewBox && viewBox.height > 0 ? viewBox.height : 0;
  if (!intrinsicWidth || !intrinsicHeight) {
    try {
      const box = svg.getBBox();
      intrinsicWidth = intrinsicWidth || box.width;
      intrinsicHeight = intrinsicHeight || box.height;
    } catch {
      // getBBox can throw when the element is not yet rendered; fall back below.
    }
  }
  zoomState.svg = svg;
  zoomState.intrinsicWidth = intrinsicWidth || 1200;
  zoomState.intrinsicHeight = intrinsicHeight || 800;
  setZoomControlsEnabled(true);
  // Capture and consume the "preserve zoom" request synchronously so a later,
  // unrelated render doesn't accidentally inherit it.
  const preserveZoom = keepZoomOnNextRender;
  const previousZoom = zoomState.zoom;
  keepZoomOnNextRender = false;
  // When preserving zoom (e.g. spacing/test re-renders), size the freshly
  // inserted SVG synchronously in the same frame as the swap. Deferring this
  // to requestAnimationFrame lets the SVG paint once at its unscaled default
  // size and then jump to the target zoom, which reads as a flicker.
  if (preserveZoom) {
    zoomState.zoom = clampZoom(previousZoom);
    applyZoom();
  }
  // Defer the initial fit measurement until layout has settled — measuring
  // immediately after the SVG is inserted can read a stale/transient container
  // size (e.g. right after a VS Code webview panel becomes visible), producing
  // an incorrectly small fit percentage.
  requestAnimationFrame(() => {
    requestAnimationFrame(() => {
      if (zoomState.svg !== svg) {
        return;
      }
      zoomState.fitZoom = computeFitZoom();
      if (!preserveZoom) {
        zoomState.zoom = zoomState.fitZoom;
        applyZoom();
        centerScroll();
      }
    });
  });
}

function disableZoom(): void {
  zoomState.svg = null;
  setZoomControlsEnabled(false);
}

function clampZoom(zoom: number): number {
  if (!Number.isFinite(zoom)) {
    return MIN_ZOOM;
  }
  return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, zoom));
}

// Maps a zoom factor to a position on the (linear) slider track using a
// logarithmic scale, and back. This keeps the perceived drag sensitivity
// consistent across the whole MIN_ZOOM..MAX_ZOOM range.
function zoomToSliderValue(zoom: number): number {
  const ratio = Math.log(clampZoom(zoom) / MIN_ZOOM) / Math.log(MAX_ZOOM / MIN_ZOOM);
  return Math.round(ratio * ZOOM_SLIDER_RESOLUTION);
}

function sliderValueToZoom(value: number): number {
  const ratio = Math.min(1, Math.max(0, value / ZOOM_SLIDER_RESOLUTION));
  return clampZoom(MIN_ZOOM * Math.pow(MAX_ZOOM / MIN_ZOOM, ratio));
}

function computeFitZoom(): number {
  const { intrinsicWidth, intrinsicHeight } = zoomState;
  const availableWidth = elements.scroll.clientWidth - FIT_PADDING * 2;
  const availableHeight = elements.scroll.clientHeight - FIT_PADDING * 2;
  if (intrinsicWidth <= 0 || intrinsicHeight <= 0 || availableWidth <= 0 || availableHeight <= 0) {
    return 1;
  }
  const fit = Math.min(availableWidth / intrinsicWidth, availableHeight / intrinsicHeight);
  return clampZoom(Math.min(fit, 1));
}

function applyZoom(): void {
  const svg = zoomState.svg;
  if (!svg) {
    return;
  }
  const zoom = clampZoom(zoomState.zoom);
  zoomState.zoom = zoom;
  svg.style.width = `${zoomState.intrinsicWidth * zoom}px`;
  svg.style.height = `${zoomState.intrinsicHeight * zoom}px`;
  elements.zoomLevel.textContent = `${Math.round(zoom * 100)}%`;
  elements.zoomSlider.value = String(zoomToSliderValue(zoom));
  elements.zoomOutButton.disabled = zoom <= MIN_ZOOM + 1e-4;
  elements.zoomInButton.disabled = zoom >= MAX_ZOOM - 1e-4;
}

function zoomToPoint(targetZoom: number, clientX: number, clientY: number): void {
  if (!zoomState.svg) {
    return;
  }
  const previousZoom = zoomState.zoom;
  const nextZoom = clampZoom(targetZoom);
  if (Math.abs(nextZoom - previousZoom) < 1e-4) {
    return;
  }
  const scroll = elements.scroll;
  const rect = scroll.getBoundingClientRect();
  const offsetX = clientX - rect.left;
  const offsetY = clientY - rect.top;
  const contentX = scroll.scrollLeft + offsetX;
  const contentY = scroll.scrollTop + offsetY;
  const ratio = nextZoom / previousZoom;
  zoomState.zoom = nextZoom;
  applyZoom();
  scroll.scrollLeft = contentX * ratio - offsetX;
  scroll.scrollTop = contentY * ratio - offsetY;
}

function zoomByStep(factor: number): void {
  const rect = elements.scroll.getBoundingClientRect();
  zoomToPoint(zoomState.zoom * factor, rect.left + rect.width / 2, rect.top + rect.height / 2);
}

function fitToViewport(): void {
  if (!zoomState.svg) {
    return;
  }
  zoomState.fitZoom = computeFitZoom();
  zoomState.zoom = zoomState.fitZoom;
  applyZoom();
  centerScroll();
}

function centerScroll(): void {
  const scroll = elements.scroll;
  scroll.scrollLeft = Math.max(0, (scroll.scrollWidth - scroll.clientWidth) / 2);
  scroll.scrollTop = 0;
}

function setZoomControlsEnabled(enabled: boolean): void {
  elements.zoomInButton.disabled = !enabled;
  elements.zoomOutButton.disabled = !enabled;
  elements.zoomFitButton.disabled = !enabled;
  elements.zoomSlider.disabled = !enabled;
  if (!enabled) {
    elements.zoomLevel.textContent = '—';
  }
}

function wheelZoomFactor(event: WheelEvent): number {
  let delta = event.deltaY;
  if (event.deltaMode === 1) {
    delta *= 16;
  } else if (event.deltaMode === 2) {
    delta *= 100;
  }
  const factor = Math.exp(-delta * ZOOM_WHEEL_SENSITIVITY);
  return Math.min(5, Math.max(0.2, factor));
}

function wireZoomEvents(): void {
  const scroll = elements.scroll;
  elements.zoomInButton.addEventListener('click', () => zoomByStep(ZOOM_STEP));
  elements.zoomOutButton.addEventListener('click', () => zoomByStep(1 / ZOOM_STEP));
  elements.zoomFitButton.addEventListener('click', () => fitToViewport());
  elements.testToggleButton.addEventListener('click', () => {
    hideTestProjects = !hideTestProjects;
    updateTestToggleButton();
    keepZoomOnNextRender = true;
    void renderGraph(state.granularity);
  });
  elements.zoomSlider.addEventListener('input', () => {
    const rect = elements.scroll.getBoundingClientRect();
    const targetZoom = sliderValueToZoom(Number(elements.zoomSlider.value));
    zoomToPoint(targetZoom, rect.left + rect.width / 2, rect.top + rect.height / 2);
  });

  scroll.addEventListener(
    'wheel',
    (event: WheelEvent) => {
      if (!zoomState.svg) {
        return;
      }
      // Trackpad pinch gestures are delivered as wheel events with ctrlKey set,
      // so this single handler covers Ctrl/Cmd + wheel and two-finger pinch zoom.
      // Plain wheel / two-finger swipe keeps the native scroll behaviour for panning.
      if (!event.ctrlKey && !event.metaKey) {
        return;
      }
      event.preventDefault();
      zoomToPoint(zoomState.zoom * wheelZoomFactor(event), event.clientX, event.clientY);
    },
    { passive: false }
  );

  let panning = false;
  let panMoved = false;
  let panPointerId = -1;
  let startClientX = 0;
  let startClientY = 0;
  let startScrollLeft = 0;
  let startScrollTop = 0;

  scroll.addEventListener('pointerdown', (event: PointerEvent) => {
    if (event.button !== 0 || !zoomState.svg) {
      return;
    }
    panning = true;
    panMoved = false;
    suppressNextClick = false;
    panPointerId = event.pointerId;
    startClientX = event.clientX;
    startClientY = event.clientY;
    startScrollLeft = scroll.scrollLeft;
    startScrollTop = scroll.scrollTop;
  });

  scroll.addEventListener('pointermove', (event: PointerEvent) => {
    if (!panning) {
      return;
    }
    const dx = event.clientX - startClientX;
    const dy = event.clientY - startClientY;
    if (!panMoved) {
      if (Math.abs(dx) + Math.abs(dy) < PAN_THRESHOLD) {
        return;
      }
      panMoved = true;
      suppressNextClick = true;
      scroll.classList.add('is-panning');
      try {
        scroll.setPointerCapture(panPointerId);
      } catch {
        // Pointer capture is best-effort; dragging still works without it.
      }
    }
    scroll.scrollLeft = startScrollLeft - dx;
    scroll.scrollTop = startScrollTop - dy;
  });

  const endPan = (): void => {
    if (!panning) {
      return;
    }
    panning = false;
    if (panMoved) {
      scroll.classList.remove('is-panning');
      try {
        scroll.releasePointerCapture(panPointerId);
      } catch {
        // Capture may already be released; ignore.
      }
    }
  };
  scroll.addEventListener('pointerup', endPan);
  scroll.addEventListener('pointercancel', endPan);

  // Suppress the click that follows a drag so panning never toggles node selection.
  scroll.addEventListener(
    'click',
    (event: MouseEvent) => {
      if (suppressNextClick) {
        suppressNextClick = false;
        event.stopPropagation();
        event.preventDefault();
      }
    },
    true
  );

  window.addEventListener('resize', () => {
    if (!zoomState.svg) {
      return;
    }
    if (Math.abs(zoomState.zoom - zoomState.fitZoom) < 1e-3) {
      fitToViewport();
    }
  });

  const scrollResizeObserver = new ResizeObserver(() => {
    if (!zoomState.svg) {
      return;
    }
    if (Math.abs(zoomState.zoom - zoomState.fitZoom) < 1e-3) {
      fitToViewport();
    }
  });
  scrollResizeObserver.observe(elements.scroll);
}

const SIDEBAR_MIN_WIDTH = 240;
const SIDEBAR_MAX_WIDTH = 480;

function wirePanelSplitter(): void {
  const splitter = elements.panelSplitter;
  const mainArea = elements.mainArea;
  let dragging = false;
  let dragPointerId = -1;

  function currentSidebarWidth(): number {
    const sidebarColumn = mainArea.style.gridTemplateColumns;
    const match = /\s(\d+(?:\.\d+)?)px$/.exec(sidebarColumn);
    if (match) {
      return Number(match[1]);
    }
    return elements.scroll.getBoundingClientRect() ? 320 : 320;
  }

  function applySidebarWidth(width: number): void {
    const clamped = Math.min(SIDEBAR_MAX_WIDTH, Math.max(SIDEBAR_MIN_WIDTH, width));
    mainArea.style.gridTemplateColumns = `minmax(0, 1fr) 6px ${clamped}px`;
  }

  splitter.addEventListener('pointerdown', (event: PointerEvent) => {
    if (event.button !== 0) {
      return;
    }
    dragging = true;
    dragPointerId = event.pointerId;
    try {
      splitter.setPointerCapture(dragPointerId);
    } catch {
      // Pointer capture is best-effort.
    }
    event.preventDefault();
  });

  splitter.addEventListener('pointermove', (event: PointerEvent) => {
    if (!dragging) {
      return;
    }
    const rect = mainArea.getBoundingClientRect();
    const newSidebarWidth = rect.right - event.clientX;
    applySidebarWidth(newSidebarWidth);
  });

  const endDrag = (): void => {
    if (!dragging) {
      return;
    }
    dragging = false;
    try {
      splitter.releasePointerCapture(dragPointerId);
    } catch {
      // Capture may already be released.
    }
  };
  splitter.addEventListener('pointerup', endDrag);
  splitter.addEventListener('pointercancel', endDrag);

  splitter.addEventListener('keydown', (event: KeyboardEvent) => {
    const step = 16;
    if (event.key === 'ArrowLeft') {
      event.preventDefault();
      applySidebarWidth(currentSidebarWidth() + step);
    } else if (event.key === 'ArrowRight') {
      event.preventDefault();
      applySidebarWidth(currentSidebarWidth() - step);
    }
  });
}

function bindCodeMapInteractivity(svg: SVGSVGElement, graph: GraphView): boolean {
  const diagramNodes = Array.isArray(graph.nodes) ? graph.nodes : [];
  const diagramEdges = Array.isArray(graph.edges) ? graph.edges : [];
  if (!diagramNodes.length || !diagramEdges.length) {
    applyCycleBaseStyles(svg, diagramNodes, diagramEdges);
    return false;
  }

  const nodeEntries = diagramNodes
    .map((node): NodeEntry | null => {
      const element = findCodeMapNodeElement(svg, node.nodeId);
      return element ? { ...node, element } : null;
    })
    .filter((node): node is NodeEntry => node !== null);
  const edgeEntries = diagramEdges
    .map((edge): EdgeEntry | null => {
      const element = svg.querySelector<SVGElement>(
        `[data-edge="true"][data-id="${cssEscape(edge.edgeId)}"]`
      );
      return element ? { ...edge, element } : null;
    })
    .filter((edge): edge is EdgeEntry => edge !== null);
  if (!nodeEntries.length || !edgeEntries.length) {
    applyCycleBaseStyles(svg, diagramNodes, diagramEdges);
    return false;
  }

  const nodeById = new Map<string, NodeEntry>(nodeEntries.map((entry) => [entry.nodeId, entry]));
  const relatedEdgeIdsByNodeId = new Map<string, Set<string>>(
    nodeEntries.map((entry) => [entry.nodeId, new Set<string>()])
  );
  let selectedNodeId: string | null = null;
  let highlightedCycleNodes = new Set<string>();

  for (const edge of edgeEntries) {
    relatedEdgeIdsByNodeId.get(edge.sourceNodeId)?.add(edge.edgeId);
    relatedEdgeIdsByNodeId.get(edge.targetNodeId)?.add(edge.edgeId);
  }

  for (const node of nodeEntries) {
    node.element.classList.add('interactive-node');
    node.element.style.cursor = 'pointer';
    node.element.style.transition = 'opacity 140ms ease';
    node.element.setAttribute('tabindex', '0');
    node.element.setAttribute('role', 'button');
    node.element.setAttribute('aria-label', `${node.name}: highlight connected references`);
    node.element.setAttribute('aria-pressed', 'false');
    node.element.addEventListener('click', (event) => {
      event.stopPropagation();
      highlightedCycleNodes = new Set<string>();
      setActiveCycleItem(null);
      toggleSelection(node.nodeId);
    });
    node.element.addEventListener('keydown', (event: KeyboardEvent) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        highlightedCycleNodes = new Set<string>();
        setActiveCycleItem(null);
        toggleSelection(node.nodeId);
      }
    });
  }

  svg.addEventListener('click', () => {
    selectedNodeId = null;
    highlightedCycleNodes = new Set<string>();
    setActiveCycleItem(null);
    applySelection();
  });

  currentBinding = {
    highlightCycle(nodes: readonly string[]): void {
      selectedNodeId = null;
      highlightedCycleNodes = new Set(nodes);
      applySelection();
      setStatus(`Highlighted circular dependency: ${nodes.join(' → ')}`, 'ready');
    },
    clear(): void {
      selectedNodeId = null;
      highlightedCycleNodes = new Set<string>();
      applySelection();
    }
  };

  function toggleSelection(nextNodeId: string): void {
    selectedNodeId = selectedNodeId === nextNodeId ? null : nextNodeId;
    applySelection();
  }

  function applySelection(): void {
    const hasSelection = Boolean(selectedNodeId);
    const hasCycleHighlight = highlightedCycleNodes.size > 0;
    svg.classList.toggle('has-selection', hasSelection);
    svg.classList.toggle('has-cycle-highlight', hasCycleHighlight);

    const connectedNodeIds = new Set<string>();
    if (selectedNodeId) {
      connectedNodeIds.add(selectedNodeId);
    }

    for (const edge of edgeEntries) {
      const isHighlighted =
        Boolean(selectedNodeId) &&
        (edge.sourceNodeId === selectedNodeId || edge.targetNodeId === selectedNodeId);
      const isCycleHighlighted =
        !hasSelection &&
        hasCycleHighlight &&
        highlightedCycleNodes.has(edge.sourceName) &&
        highlightedCycleNodes.has(edge.targetName);
      const isDimmed = hasSelection ? !isHighlighted : hasCycleHighlight && !isCycleHighlighted;

      edge.element.classList.toggle('is-highlighted', isHighlighted);
      edge.element.classList.toggle('is-cycle-highlighted', isCycleHighlighted);
      edge.element.classList.toggle('is-dimmed', isDimmed);
      edge.element.style.transition =
        'opacity 140ms ease, stroke-width 140ms ease, stroke 140ms ease';
      edge.element.style.opacity = isDimmed ? '0.2' : '1';
      edge.element.style.stroke = isHighlighted
        ? SELECTION_COLOR
        : edge.inCycle || isCycleHighlighted
          ? CYCLE_COLOR
          : '';
      edge.element.style.strokeWidth = isHighlighted
        ? '4.5px'
        : edge.inCycle || isCycleHighlighted
          ? '3px'
          : '2px';

      if (isHighlighted) {
        connectedNodeIds.add(edge.sourceNodeId);
        connectedNodeIds.add(edge.targetNodeId);
      }
    }

    for (const node of nodeEntries) {
      const isSelected = node.nodeId === selectedNodeId;
      const isRelated = !isSelected && connectedNodeIds.has(node.nodeId);
      const isCycleHighlighted = !hasSelection && highlightedCycleNodes.has(node.name);
      const isDimmed = hasSelection
        ? !connectedNodeIds.has(node.nodeId)
        : hasCycleHighlight && !isCycleHighlighted;

      node.element.classList.toggle('is-selected', isSelected);
      node.element.classList.toggle('is-related', isRelated);
      node.element.classList.toggle('is-cycle-highlighted', isCycleHighlighted);
      node.element.classList.toggle('is-dimmed', isDimmed);
      node.element.setAttribute('aria-pressed', isSelected ? 'true' : 'false');
      node.element.style.opacity = isDimmed ? '0.45' : '1';

      const baseStroke = node.inCycle || isCycleHighlighted ? CYCLE_COLOR : getKindColor(node.kind);
      const baseWidth = node.inCycle || isCycleHighlighted ? '2px' : '1.5px';
      const shapes = node.element.querySelectorAll<SVGElement>(
        'rect, circle, ellipse, polygon, path'
      );
      for (const shape of shapes) {
        shape.style.transition = 'stroke-width 140ms ease, stroke 140ms ease';
        shape.style.stroke = isSelected ? SELECTION_COLOR : baseStroke;
        shape.style.strokeWidth = isSelected ? '2.5px' : isRelated ? '2px' : baseWidth;
      }
    }

    if (!selectedNodeId && !hasCycleHighlight) {
      setStatus(
        baseStatusMessage(graph) +
          ` Click a ${graphLabel(graph)} node to highlight connected references.`,
        'ready'
      );
      return;
    }
    if (!selectedNodeId) {
      return;
    }

    const selected = nodeById.get(selectedNodeId);
    const relatedEdgeCount = relatedEdgeIdsByNodeId.get(selectedNodeId)?.size ?? 0;
    setStatus(
      `${String(selected?.name || 'Node')}: highlighted ${formatNumber(relatedEdgeCount)} connected reference(s). Click the node again or the background to clear.`,
      'ready'
    );
  }

  applySelection();
  return true;
}

function applyCycleBaseStyles(
  svg: SVGSVGElement,
  nodes: readonly CodeMapDiagramProject[],
  edges: readonly CodeMapDiagramEdge[]
): void {
  for (const node of nodes) {
    const shapes =
      findCodeMapNodeElement(svg, node.nodeId)?.querySelectorAll<SVGElement>(
        'rect, circle, ellipse, polygon, path'
      ) ?? [];
    const stroke = node.inCycle ? CYCLE_COLOR : getKindColor(node.kind);
    const strokeWidth = node.inCycle ? '2px' : '1.5px';
    for (const shape of shapes) {
      shape.style.stroke = stroke;
      shape.style.strokeWidth = strokeWidth;
    }
  }

  for (const edge of edges) {
    const element = svg.querySelector<SVGElement>(
      `[data-edge="true"][data-id="${cssEscape(edge.edgeId)}"]`
    );
    if (!element) {
      continue;
    }
    if (edge.inCycle) {
      element.style.stroke = CYCLE_COLOR;
      element.style.strokeWidth = '3px';
    } else {
      element.style.strokeWidth = '2px';
    }
  }
}

function findCodeMapNodeElement(svg: SVGSVGElement, nodeId: string): SVGGElement | null {
  for (const element of svg.querySelectorAll<SVGGElement>('g.node')) {
    if (typeof element.id === 'string' && element.id.includes(`-flowchart-${nodeId}-`)) {
      return element;
    }
  }
  return null;
}

function bindCycleList(cycles: readonly DependencyCycle[]): void {
  const items = Array.isArray(cycles) ? cycles : [];
  elements.cycleList.replaceChildren();
  currentBinding?.clear();
  if (!items.length) {
    elements.cycleEmpty.hidden = false;
    return;
  }

  elements.cycleEmpty.hidden = true;
  for (const item of items) {
    const nodes = Array.isArray(item.nodes) ? item.nodes : [];
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'cycle-button';
    const scope = item.scope ? `${item.scope}: ` : '';
    button.textContent = `${scope}(${formatNumber(item.length || nodes.length)}) ${nodes.join(' → ')}`;
    button.addEventListener('click', () => {
      setActiveCycleItem(button);
      currentBinding?.highlightCycle(nodes);
    });
    const li = document.createElement('li');
    li.append(button);
    elements.cycleList.append(li);
  }
}

function setActiveCycleItem(activeButton: HTMLButtonElement | null): void {
  for (const button of elements.cycleList.querySelectorAll<HTMLButtonElement>('.cycle-button')) {
    button.classList.toggle('active', button === activeButton);
  }
}

function bindTextList(
  listElement: HTMLElement,
  emptyElement: HTMLElement,
  items: readonly string[]
): void {
  listElement.replaceChildren();
  if (!items.length) {
    emptyElement.hidden = false;
    return;
  }
  emptyElement.hidden = true;
  for (const item of items) {
    const li = document.createElement('li');
    li.textContent = String(item || '');
    listElement.append(li);
  }
}

function copyCurrentMermaid(): void {
  postMessage({ type: 'copyMermaid', text: currentGraph?.mermaid ?? '' });
  setStatus('Mermaid source sent to VS Code for copying.', 'ready');
}

function copyForAgent(): void {
  postMessage({ type: 'copyForAgent', text: buildAgentClipboardText() });
  setStatus('Analysis summary and Coding Agent prompt sent to VS Code for copying.', 'ready');
}

const MAX_LISTED_CYCLES = 15;
const MAX_LISTED_PROJECTS = 60;
const MAX_LISTED_MESSAGES = 3;

/**
 * Builds a compact, no-mermaid clipboard payload for handoff to a coding
 * agent (e.g. GitHub Copilot, or any other AI coding assistant).
 *
 * This is meant to work for two use cases: reviewing a circular-dependency
 * finding, and getting a first bird's-eye view of an unfamiliar codebase.
 * Total-token economy is the goal, not just clipboard-payload size: a
 * moderately larger but self-contained snapshot (project map, dependency
 * hubs, cycle chains with exact files) lets the agent skip the many
 * grep/glob/view round-trips it would otherwise need to reconstruct the same
 * picture, which costs far more tokens overall. Raw Mermaid source and the
 * full edge list are still omitted since they add bulk without adding
 * anything grep couldn't already tell the agent.
 */
function buildAgentClipboardText(): string {
  const model = state.model;
  const graph = currentGraph;
  if (!model || !graph) {
    return [
      'SharpDeps analysis data is not ready yet.',
      '',
      'Instruction: Analysis data is unavailable. Please re-run the analysis, then review the dependencies.'
    ].join('\n');
  }

  const granularityLabel = graph.granularity === 'namespaces' ? 'namespace' : 'project';
  const fileByName = new Map(graph.nodes.map((node) => [node.name, node.representativeFile]));
  const cycleLines = formatCyclesWithFiles(graph.cycles, fileByName, model.solutionPath);
  const overviewLines = formatProjectOverview(model);
  const messageLines = formatMessages(model.meta.warnings, model.meta.notes);

  return [
    '# SharpDeps report (pre-analyzed architecture snapshot; no re-exploration needed)',
    `solution=${model.solutionPath || model.solutionName || 'Dependency Map'}`,
    `viewing=${granularityLabel} nodes=${formatNumber(graph.nodes.length)} edges=${formatNumber(graph.edges.length)} cycles=${formatNumber(graph.cycles.length)}`,
    `totals: projects=${formatNumber(model.meta.projectCount)} namespaces=${formatNumber(model.meta.namespaceCount)}`,
    '',
    ...overviewLines,
    '',
    ...cycleLines,
    ...(messageLines.length ? ['', ...messageLines] : []),
    '',
    'Instruction: The above is a pre-analyzed snapshot (project map, key dependencies, cycles). No further file exploration is needed; reference the listed files/modules directly. Summarize, in short priority-ordered points: (1) architecture overview, (2) how to resolve any cycles, (3) improvement suggestions.'
  ].join('\n');
}

/** Solution-wide project map: kind breakdown, top dependency hubs, then every project with its file. */
function formatProjectOverview(model: CodeMapViewModel): string[] {
  const nodes = model.projectGraph.nodes;
  if (!nodes.length) {
    return ['projects: (none)'];
  }

  const kindsLine = model.meta.projectKinds.length
    ? [
        `kinds: ${model.meta.projectKinds.map((kind) => `${kind.name}=${formatNumber(kind.count)}`).join(', ')}`
      ]
    : [];

  const hubLines = model.meta.dependencyHubs.length
    ? [
        'hubs (most connected, review these first):',
        ...model.meta.dependencyHubs.map(
          (hub) =>
            `- ${hub.name}(${hub.kind}) out=${formatNumber(hub.outgoingDependencies)} in=${formatNumber(hub.incomingDependencies)} pkg=${formatNumber(hub.packageReferences)}`
        )
      ]
    : [];

  const listedNodes = nodes.slice(0, MAX_LISTED_PROJECTS);
  const projectLines = listedNodes.map((node) => {
    const file = node.representativeFile
      ? toDisplayPath(node.representativeFile, model.solutionPath)
      : '';
    const cycleMark = node.inCycle ? ' [cycle]' : '';
    return `- ${node.name}(${node.kind})${cycleMark}${file ? ` ${file}` : ''}`;
  });
  const restCount = nodes.length - listedNodes.length;
  if (restCount > 0) {
    projectLines.push(`(+${formatNumber(restCount)} more projects omitted)`);
  }

  return ['projects (solution map):', ...kindsLine, ...hubLines, ...projectLines];
}

/** Surfaces a few raw warning/note messages verbatim (e.g. "diagram truncated to top N") as caveats. */
function formatMessages(warnings: readonly string[], notes: readonly string[]): string[] {
  const lines: string[] = [];
  if (warnings.length) {
    lines.push(
      'warnings:',
      ...warnings.slice(0, MAX_LISTED_MESSAGES).map((warning) => `- ${warning}`)
    );
    if (warnings.length > MAX_LISTED_MESSAGES) {
      lines.push(`(+${formatNumber(warnings.length - MAX_LISTED_MESSAGES)} more warnings omitted)`);
    }
  }
  if (notes.length) {
    lines.push('notes:', ...notes.slice(0, MAX_LISTED_MESSAGES).map((note) => `- ${note}`));
    if (notes.length > MAX_LISTED_MESSAGES) {
      lines.push(`(+${formatNumber(notes.length - MAX_LISTED_MESSAGES)} more notes omitted)`);
    }
  }
  return lines;
}

function formatCyclesWithFiles(
  cycles: readonly DependencyCycle[],
  fileByName: ReadonlyMap<string, string | null | undefined>,
  solutionPath: string
): string[] {
  if (!cycles.length) {
    return ['cycles: none'];
  }

  const lines = cycles.slice(0, MAX_LISTED_CYCLES).map((cycle, index) => {
    const nodes = Array.isArray(cycle.nodes) ? cycle.nodes : [];
    const chain = nodes
      .map((name) => {
        const file = fileByName.get(name);
        return file ? `${name}(${toDisplayPath(file, solutionPath)})` : name;
      })
      .join(' -> ');
    const closing = nodes.length ? ` -> ${nodes[0]}` : '';
    return `${index + 1}) ${chain}${closing}`;
  });

  const restCount = cycles.length - lines.length;
  if (restCount > 0) {
    lines.push(`(+${formatNumber(restCount)} more cycles omitted)`);
  }
  return ['cycles:', ...lines];
}

/**
 * Converts an absolute representativeFile path to a short path relative to
 * the solution directory. The webview sandbox has no Node 'path' module, so
 * this does plain string/prefix stripping instead.
 */
function toDisplayPath(file: string, solutionPath: string): string {
  const normalizedFile = file.replace(/\\/g, '/');
  const solutionDir = solutionPath.replace(/[\\/][^\\/]*$/, '').replace(/\\/g, '/');
  const solutionDirPrefix = solutionDir.endsWith('/') ? solutionDir : `${solutionDir}/`;
  if (
    solutionDir &&
    (normalizedFile === solutionDir || normalizedFile.startsWith(solutionDirPrefix))
  ) {
    return normalizedFile.slice(solutionDir.length).replace(/^\/+/, '');
  }
  return normalizedFile;
}

async function exportCurrentGraph(format: ExportFormat): Promise<void> {
  try {
    const svg = elements.viewport.querySelector<SVGSVGElement>('svg');
    if (!svg) {
      throw new Error('No rendered SVG is available to export.');
    }

    const exportSvg = svg.cloneNode(true) as SVGSVGElement;
    const { width, height } = getSvgPixelSize(svg);
    exportSvg.style.removeProperty('width');
    exportSvg.style.removeProperty('height');
    exportSvg.setAttribute('width', String(width));
    exportSvg.setAttribute('height', String(height));

    const serialized = serializeSvg(exportSvg);
    if (format === 'svg') {
      postMessage({ type: 'export', format, data: serialized, granularity: state.granularity });
      setStatus('SVG export sent to VS Code.', 'ready');
      return;
    }

    const png = await svgToPng(serialized, exportSvg);
    postMessage({ type: 'export', format, data: png, granularity: state.granularity });
    setStatus('PNG export sent to VS Code.', 'ready');
  } catch (error) {
    const message = getMessage(error, 'Export failed.');
    postMessage({ type: 'exportError', message });
    setStatus(message, 'warning');
  }
}

function serializeSvg(svg: SVGSVGElement): string {
  if (!svg.getAttribute('xmlns')) {
    svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  }
  if (!svg.getAttribute('xmlns:xlink')) {
    svg.setAttribute('xmlns:xlink', 'http://www.w3.org/1999/xlink');
  }
  return new XMLSerializer().serializeToString(svg);
}

async function svgToPng(svgText: string, svg: SVGSVGElement): Promise<string> {
  const { width, height } = getSvgPixelSize(svg);
  const scale = 2;
  const image = await loadImage(svgToDataUrl(svgText));
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.ceil(width * scale));
  canvas.height = Math.max(1, Math.ceil(height * scale));
  const context = canvas.getContext('2d');
  if (!context) {
    throw new Error('Canvas 2D context is unavailable.');
  }

  context.scale(scale, scale);
  context.drawImage(image, 0, 0, width, height);
  return canvas.toDataURL('image/png').replace(/^data:image\/png;base64,/, '');
}

function loadImage(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error('SVG could not be rasterized.'));
    image.src = url;
  });
}

function svgToDataUrl(svgText: string): string {
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svgText)}`;
}

function getSvgPixelSize(svg: SVGSVGElement): { width: number; height: number } {
  const widthAttr = parseSvgLength(svg.getAttribute('width'));
  const heightAttr = parseSvgLength(svg.getAttribute('height'));
  if (widthAttr && heightAttr) {
    return { width: widthAttr, height: heightAttr };
  }

  const viewBox = svg.viewBox.baseVal;
  if (viewBox && viewBox.width > 0 && viewBox.height > 0) {
    return { width: viewBox.width, height: viewBox.height };
  }

  const rect = svg.getBoundingClientRect();
  return { width: rect.width || 1200, height: rect.height || 800 };
}

function parseSvgLength(value: string | null): number | null {
  if (!value) {
    return null;
  }
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function setStatus(message: string, statusState: StatusState): void {
  elements.status.textContent = message;
  elements.status.dataset.state = statusState === 'warning' ? 'warning' : 'ready';
}

function formatNumber(value: unknown): string {
  const number = Number(value || 0);
  return Number.isFinite(number) ? number.toLocaleString() : '-';
}

function getMessage(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

function graphLabel(graph: GraphView): string {
  return graph.granularity === 'namespaces' ? 'namespace' : 'project';
}

function capitalize(value: string): string {
  return value.length ? value.charAt(0).toUpperCase() + value.slice(1) : value;
}

function nextRenderId(): string {
  if (typeof crypto.randomUUID === 'function') {
    return `sharpdeps-${crypto.randomUUID()}`;
  }
  return `sharpdeps-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function scaleArrowMarkers(svg: SVGSVGElement): void {
  const MARKER_SCALE = 1.3;
  const markers = svg.querySelectorAll<SVGMarkerElement>('marker');
  for (const marker of markers) {
    scaleMarkerAttribute(marker, 'markerWidth', MARKER_SCALE);
    scaleMarkerAttribute(marker, 'markerHeight', MARKER_SCALE);
    scaleMarkerAttribute(marker, 'refX', MARKER_SCALE);
    scaleMarkerAttribute(marker, 'refY', MARKER_SCALE);
  }
}

function scaleMarkerAttribute(marker: SVGMarkerElement, attribute: string, factor: number): void {
  const raw = marker.getAttribute(attribute);
  if (raw === null) {
    return;
  }
  const value = Number.parseFloat(raw);
  if (!Number.isFinite(value)) {
    return;
  }
  marker.setAttribute(attribute, String(value * factor));
}

function getHiddenTestNodeIds(graph: GraphView): Set<string> {
  if (!hideTestProjects) {
    return new Set();
  }
  return new Set(
    graph.nodes.filter((node) => node.kind.toLowerCase() === 'test').map((node) => node.nodeId)
  );
}

// Removes the Mermaid statements that declare hidden (test-kind) nodes and
// any edges touching them, so the layout engine lays out the remaining nodes
// compactly instead of just hiding elements after rendering (which would
// leave their reserved layout space empty).
function filterHiddenNodesFromMermaid(source: string, hiddenNodeIds: ReadonlySet<string>): string {
  if (hiddenNodeIds.size === 0) {
    return source;
  }
  const nodeDeclPattern = /^\s*([A-Za-z0-9_]+)\[/;
  const edgePattern = /^\s*(\S+)\s+\S+@-->\s*(\S+)\s*$/;
  const stylePattern = /^\s*style\s+(\S+)\s+/;
  const linkStylePattern = /^\s*linkStyle\s+/;

  const withoutHiddenStatements = source.split('\n').filter((line) => {
    // linkStyle references edges by their positional index in the diagram, which
    // shifts once edges are removed above; drop it rather than risk pointing at
    // the wrong edge. Cycle/selection coloring is reapplied by JS after render
    // (see applyCycleBaseStyles), so this Mermaid-native styling is redundant.
    if (linkStylePattern.test(line)) {
      return false;
    }
    const edgeMatch = line.match(edgePattern);
    if (edgeMatch) {
      const [, sourceId, targetId] = edgeMatch;
      return !hiddenNodeIds.has(sourceId) && !hiddenNodeIds.has(targetId);
    }
    const styleMatch = line.match(stylePattern);
    if (styleMatch) {
      return !hiddenNodeIds.has(styleMatch[1]);
    }
    const nodeMatch = line.match(nodeDeclPattern);
    if (nodeMatch) {
      return !hiddenNodeIds.has(nodeMatch[1]);
    }
    return true;
  });

  // Drop subgraph groups that ended up with no nodes left inside them.
  const result: string[] = [];
  for (let i = 0; i < withoutHiddenStatements.length; i++) {
    const line = withoutHiddenStatements[i];
    const next = withoutHiddenStatements[i + 1];
    if (/^\s*subgraph\s/.test(line) && next !== undefined && /^\s*end\s*$/.test(next)) {
      i++;
      continue;
    }
    result.push(line);
  }
  return result.join('\n');
}

function updateTestToggleButton(): void {
  const button = elements.testToggleButton;
  const label = hideTestProjects ? 'Show test projects' : 'Hide test projects';
  button.title = label;
  button.setAttribute('aria-label', label);
  button.setAttribute('aria-pressed', hideTestProjects ? 'true' : 'false');
  button.classList.toggle('is-active', hideTestProjects);
  button.replaceChildren(createIcon(hideTestProjects ? ICONS.flaskOff : ICONS.flask));
}

function cssEscape(value: string): string {
  if (typeof CSS !== 'undefined' && typeof CSS.escape === 'function') {
    return CSS.escape(value);
  }
  return value.replace(/["\\]/g, '\\$&');
}

main();
