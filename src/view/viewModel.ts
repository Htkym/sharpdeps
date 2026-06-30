import type { CodeMapReport } from '../analyzer/types';
import type { CodeMapViewModel, GraphView } from './protocol';

/** Reshape the analyzer report into the view model the webview renders. */
export function buildViewModel(report: CodeMapReport): CodeMapViewModel {
  const projectGraph: GraphView = {
    granularity: 'projects',
    mermaid: report.mermaid ?? '',
    nodes: report.diagramProjects ?? [],
    edges: report.diagramEdges ?? [],
    cycles: report.projectCycles ?? []
  };

  const namespaces = report.namespaces;
  const namespaceGraph: GraphView = {
    granularity: 'namespaces',
    mermaid: namespaces?.mermaid ?? '',
    nodes: namespaces?.diagramNodes ?? [],
    edges: namespaces?.diagramEdges ?? [],
    cycles: namespaces?.cycles ?? []
  };

  return {
    solutionName: report.solutionName ?? '',
    solutionPath: report.solutionPath ?? '',
    projectGraph,
    namespaceGraph,
    meta: {
      projectCount: report.projectCount ?? 0,
      namespaceCount: namespaces?.namespaceCount ?? 0,
      projectCycleCount: (report.projectCycles ?? []).length,
      namespaceCycleCount: (namespaces?.cycles ?? []).length,
      warnings: report.warnings ?? [],
      notes: report.notes ?? []
    }
  };
}
