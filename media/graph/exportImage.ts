// SVG/PNG export for the graph (SD-004).
//
// Exports exactly what is displayed: the current projection and layout, with the
// theme styles inlined so the file opens standalone. The SVG contains no script,
// no event attributes, and no external links.

export interface SvgExportOptions {
  /** Content size in CSS pixels (the layout bounds, not the viewport). */
  width: number;
  height: number;
  /** Style text to inline (theme tokens resolved to concrete colors). */
  styles: string;
  /** Optional caption shown in the exported image only. */
  caption?: string;
  metadata?: string[];
}

export function serializeSvg(content: SVGGElement, options: SvgExportOptions): string {
  const width = Math.max(options.width, options.metadata?.length ? 640 : 0);
  const characters = Math.max(30, Math.floor((width - 32) / 8));
  const notes = (options.metadata ?? []).flatMap((line) => {
    const chars = Array.from(line);
    const result: string[] = [];
    for (let offset = 0; offset < chars.length; offset += characters)
      result.push(chars.slice(offset, offset + characters).join(''));
    return result;
  });
  const headerHeight = notes.length ? notes.length * 20 + 24 : 0;
  const height = options.height + headerHeight;
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  svg.setAttribute('width', String(Math.ceil(width)));
  svg.setAttribute('height', String(Math.ceil(height)));
  svg.setAttribute('viewBox', `0 0 ${Math.ceil(width)} ${Math.ceil(height)}`);
  svg.setAttribute('role', 'img');
  if (options.caption) {
    svg.setAttribute('aria-label', options.caption);
  }

  const style = document.createElementNS('http://www.w3.org/2000/svg', 'style');
  style.textContent = options.styles;
  svg.append(style);
  const background = document.createElementNS(svg.namespaceURI, 'rect');
  background.setAttribute('width', '100%');
  background.setAttribute('height', '100%');
  background.setAttribute('fill', 'var(--vscode-editor-background, #1f1f1f)');
  svg.append(background);
  notes.forEach((line, index) => {
    const text = document.createElementNS(svg.namespaceURI, 'text');
    text.setAttribute('x', '16');
    text.setAttribute('y', String(24 + index * 20));
    text.setAttribute('fill', 'var(--vscode-editor-foreground, #e6e6e6)');
    text.setAttribute('font-family', 'sans-serif');
    text.setAttribute('font-size', '13');
    text.textContent = line;
    svg.append(text);
  });

  const clone = content.cloneNode(true) as SVGGElement;
  // The exported image is static: focus and camera transforms do not apply.
  clone.removeAttribute('transform');
  if (headerHeight) clone.setAttribute('transform', `translate(0 ${headerHeight})`);
  svg.append(clone);

  stripInteractiveAttributes(svg);
  return `<?xml version="1.0" encoding="UTF-8"?>\n${new XMLSerializer().serializeToString(svg)}\n`;
}

/** Removes focus/interaction attributes so the export cannot receive input. */
function stripInteractiveAttributes(root: Element): void {
  const selector = '[tabindex], [role="button"], [aria-selected], [aria-label]';
  for (const element of Array.from(root.querySelectorAll(selector))) {
    element.removeAttribute('tabindex');
    element.removeAttribute('role');
    element.removeAttribute('aria-selected');
  }
  root.removeAttribute('tabindex');
}

export async function svgToPngDataUrl(
  svgText: string,
  width: number,
  height: number,
  scale = 2
): Promise<string> {
  const image = new Image();
  const url = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svgText)}`;
  await new Promise<void>((resolve, reject) => {
    image.onload = () => resolve();
    image.onerror = () => reject(new Error('The exported SVG could not be rasterized.'));
    image.src = url;
  });

  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.ceil(width * scale));
  canvas.height = Math.max(1, Math.ceil(height * scale));
  const context = canvas.getContext('2d');
  if (!context) {
    throw new Error('A 2D canvas context is required for PNG export.');
  }
  context.drawImage(image, 0, 0, canvas.width, canvas.height);
  return canvas.toDataURL('image/png');
}

/**
 * Reads the theme values the exported image needs. Computed styles are resolved
 * here so the file does not depend on VS Code theme variables.
 */
export function readThemeStyles(host: Element): string {
  const styles = getComputedStyle(host);
  const variables = [
    '--vscode-editor-background',
    '--vscode-editor-foreground',
    '--vscode-panel-border',
    '--vscode-focusBorder',
    '--vscode-charts-red',
    '--vscode-charts-blue',
    '--vscode-charts-purple',
    '--vscode-charts-green',
    '--vscode-charts-orange',
    '--vscode-charts-yellow',
    '--vscode-descriptionForeground',
    '--vscode-editor-font-family',
    '--vscode-font-size'
  ];
  const declarations = variables
    .map((name) => {
      const value = styles.getPropertyValue(name).trim();
      return value ? `${name}: ${value};` : '';
    })
    .filter(Boolean)
    .join(' ');

  return `:root { ${declarations} } ${GRAPH_EXPORT_STYLES}`;
}

/**
 * Fallback values are provided for every theme variable so an exported file is
 * readable even outside VS Code.
 */
export const GRAPH_EXPORT_STYLES = `
  .node rect { fill: var(--vscode-editor-background, #1f1f1f); stroke: var(--sd-kind-color, var(--vscode-panel-border, #6b6b6b)); }
  .node text { fill: var(--vscode-editor-foreground, #e6e6e6); font-family: var(--vscode-editor-font-family, sans-serif); font-size: 12px; }
  .node-sublabel, .node-badge { fill: var(--vscode-descriptionForeground, #a0a0a0); font-size: 10px; }
  .node.in-cycle rect { stroke: var(--vscode-charts-red, #e5484d); stroke-width: 2; }
  .node.selected rect { stroke: var(--vscode-focusBorder, #4daafc); stroke-width: 2; }
  .node.inferred rect { stroke-dasharray: 4 3; }
  .edge-line { fill: none; stroke: var(--vscode-panel-border, #6b6b6b); stroke-width: 1.5; }
  .edge-hit { fill: none; stroke: transparent; stroke-width: 14; }
  .edge-arrow { fill: var(--vscode-panel-border, #6b6b6b); }
  .edge.in-cycle .edge-line { stroke: var(--vscode-charts-red, #e5484d); stroke-width: 2.5; }
  .edge.in-cycle .edge-arrow { fill: var(--vscode-charts-red, #e5484d); }
  .edge.selected .edge-line { stroke: var(--vscode-focusBorder, #4daafc); stroke-width: 2.5; }
  .edge.selected .edge-arrow { fill: var(--vscode-focusBorder, #4daafc); }
  .edge-inferred .edge-line { stroke-dasharray: 5 4; }
`;
