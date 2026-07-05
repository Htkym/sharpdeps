import * as path from 'node:path';
import type { CodeMapDiagramProject, CodeMapProject, CodeMapReport } from '../analyzer/types';
import type { CodeMapViewModel, GraphView } from './protocol';

/** Reshape the analyzer report into the view model the webview renders. */
export function buildViewModel(report: CodeMapReport): CodeMapViewModel {
  const projectGraph: GraphView = {
    granularity: 'projects',
    mermaid: report.mermaid ?? '',
    nodes: withProjectFilePaths(
      report.diagramProjects ?? [],
      report.projects ?? [],
      report.solutionPath ?? ''
    ),
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
      projectKinds: report.projectKinds ?? [],
      dependencyHubs: report.dependencyHubs ?? [],
      warnings: report.warnings ?? [],
      notes: report.notes ?? []
    }
  };
}

/**
 * Populates `representativeFile` on project-level nodes (it is only set by the
 * analyzer for namespace nodes). Mirrors the path resolution in
 * `diagnostics/cycleAnchoring.ts` so a project node's file always points at its
 * `.csproj`, letting the copy-for-agent payload reference exact files without
 * requiring the receiving Coding Agent to search the workspace.
 * Returns the original array unchanged (same reference) when there is nothing
 * to enrich, so callers relying on referential identity are unaffected.
 */
function withProjectFilePaths(
  nodes: CodeMapDiagramProject[],
  projects: CodeMapProject[],
  solutionPath: string
): CodeMapDiagramProject[] {
  if (!nodes.length || !projects.length) {
    return nodes;
  }

  const relativePathByName = new Map(
    projects.map((project) => [project.name, project.relativePath])
  );
  const solutionDir = path.dirname(solutionPath);
  let changed = false;
  const enrichedNodes = nodes.map((node) => {
    if (node.representativeFile) {
      return node;
    }
    const relativePath = relativePathByName.get(node.name);
    if (!relativePath) {
      return node;
    }
    const file = path.isAbsolute(relativePath)
      ? relativePath
      : path.join(solutionDir, relativePath);
    changed = true;
    return { ...node, representativeFile: file };
  });
  return changed ? enrichedNodes : nodes;
}
