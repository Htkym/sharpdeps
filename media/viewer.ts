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
  copyButton: HTMLButtonElement;
  exportSvgButton: HTMLButtonElement;
  exportPngButton: HTMLButtonElement;
  status: HTMLElement;
  viewport: HTMLElement;
  source: HTMLElement;
  cycleList: HTMLElement;
  cycleEmpty: HTMLElement;
  graphSummary: HTMLElement;
  warningsList: HTMLElement;
  warningsEmpty: HTMLElement;
  notesList: HTMLElement;
  notesEmpty: HTMLElement;
};

const CYCLE_COLOR = '#e5484d';
const SELECTION_COLOR = '#3b82f6';
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

function postMessage(message: WebviewToHostMessage): void {
  vscode.postMessage(message);
}

function main(): void {
  elements = buildShell();
  wireUiEvents();
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

  const copyButton = createButton('viewer-copy-mermaid', undefined, 'Copy Mermaid');
  const exportSvgButton = createButton('viewer-export-svg', undefined, 'Export SVG');
  const exportPngButton = createButton('viewer-export-png', undefined, 'Export PNG');
  actions.append(granularity, copyButton, exportSvgButton, exportPngButton);
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
  graphPanel.append(status, legend, scroll);

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
  mainArea.append(graphPanel, sidebar);
  app.append(header, namespaceNote, mainArea);

  return {
    title,
    subtitle,
    projectsButton,
    namespacesButton,
    namespaceNote,
    copyButton,
    exportSvgButton,
    exportPngButton,
    status,
    viewport,
    source,
    cycleList,
    cycleEmpty,
    graphSummary,
    warningsList: warnings.list,
    warningsEmpty: warnings.empty,
    notesList: notes.list,
    notesEmpty: notes.empty
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
  elements.copyButton.addEventListener('click', copyCurrentMermaid);
  elements.exportSvgButton.addEventListener('click', () => {
    void exportCurrentGraph('svg');
  });
  elements.exportPngButton.addEventListener('click', () => {
    void exportCurrentGraph('png');
  });
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
      htmlLabels: false
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
  const mermaidSource = graph.mermaid || `flowchart LR\n  Empty["No ${graphLabel(graph)} data"]`;
  elements.source.textContent = mermaidSource;
  bindCycleList(graph.cycles);
  setGraphSummary(graph);

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

function renderFailure(error: unknown, mermaidSource: string): void {
  elements.viewport.replaceChildren();
  elements.viewport.append(
    createElement('pre', 'render-fallback', mermaidSource || 'Mermaid source is unavailable.')
  );
  const message = getMessage(error, 'Graph rendering failed. Showing Mermaid source.');
  setStatus(message, 'warning');
  postMessage({ type: 'log', level: 'error', message });
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
        ? '4px'
        : edge.inCycle || isCycleHighlighted
          ? '2.5px'
          : '';

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

      const baseStroke = node.inCycle || isCycleHighlighted ? CYCLE_COLOR : '';
      const baseWidth = node.inCycle || isCycleHighlighted ? '2px' : '';
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
    if (!node.inCycle) {
      continue;
    }
    const shapes =
      findCodeMapNodeElement(svg, node.nodeId)?.querySelectorAll<SVGElement>(
        'rect, circle, ellipse, polygon, path'
      ) ?? [];
    for (const shape of shapes) {
      shape.style.stroke = CYCLE_COLOR;
      shape.style.strokeWidth = '2px';
    }
  }

  for (const edge of edges) {
    if (!edge.inCycle) {
      continue;
    }
    const element = svg.querySelector<SVGElement>(
      `[data-edge="true"][data-id="${cssEscape(edge.edgeId)}"]`
    );
    if (element) {
      element.style.stroke = CYCLE_COLOR;
      element.style.strokeWidth = '2.5px';
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

async function exportCurrentGraph(format: ExportFormat): Promise<void> {
  try {
    const svg = elements.viewport.querySelector<SVGSVGElement>('svg');
    if (!svg) {
      throw new Error('No rendered SVG is available to export.');
    }

    const serialized = serializeSvg(svg);
    if (format === 'svg') {
      postMessage({ type: 'export', format, data: serialized, granularity: state.granularity });
      setStatus('SVG export sent to VS Code.', 'ready');
      return;
    }

    const png = await svgToPng(serialized, svg);
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

function cssEscape(value: string): string {
  if (typeof CSS !== 'undefined' && typeof CSS.escape === 'function') {
    return CSS.escape(value);
  }
  return value.replace(/["\\]/g, '\\$&');
}

main();
