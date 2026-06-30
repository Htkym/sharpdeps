// Message protocol and view-model shared between the extension host and the webview client.
import type { CodeMapDiagramEdge, CodeMapDiagramProject, DependencyCycle } from '../analyzer/types';

export type Granularity = 'projects' | 'namespaces';

export type ThemeKind = 'dark' | 'light';

export type ExportFormat = 'svg' | 'png';

/** A single renderable graph (project-level or namespace-level). */
export interface GraphView {
  granularity: Granularity;
  mermaid: string;
  nodes: CodeMapDiagramProject[];
  edges: CodeMapDiagramEdge[];
  cycles: DependencyCycle[];
}

export interface ViewModelMeta {
  projectCount: number;
  namespaceCount: number;
  projectCycleCount: number;
  namespaceCycleCount: number;
  warnings: string[];
  notes: string[];
}

/** The complete payload posted to the webview for rendering. */
export interface CodeMapViewModel {
  solutionName: string;
  solutionPath: string;
  projectGraph: GraphView;
  namespaceGraph: GraphView;
  meta: ViewModelMeta;
}

// Extension host -> webview
export type HostToWebviewMessage =
  | { type: 'render'; model: CodeMapViewModel; theme: ThemeKind }
  | { type: 'theme'; theme: ThemeKind }
  | { type: 'doExport'; format: ExportFormat }
  | { type: 'doCopyMermaid' }
  | { type: 'setGranularity'; granularity: Granularity };

// Webview -> extension host
export type WebviewToHostMessage =
  | { type: 'ready' }
  | { type: 'copyMermaid'; text: string }
  | { type: 'export'; format: ExportFormat; data: string; granularity: Granularity }
  | { type: 'exportError'; message: string }
  | { type: 'log'; level: 'info' | 'warn' | 'error'; message: string };
