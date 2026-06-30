import * as path from 'path';
import type { CodeMapReport, DependencyCycle } from '../analyzer/types';

/** A set of cycle messages anchored to a single source file. */
export interface AnchoredCycleDiagnostics {
  file: string;
  messages: string[];
}

/** The pure result of mapping report cycles onto source files. */
export interface CycleDiagnosticsResult {
  anchored: AnchoredCycleDiagnostics[];
  unanchored: string[];
}

/**
 * Maps the circular-dependency findings in a report onto source files, without
 * touching the VS Code API. Project cycles anchor to their `.csproj` (relative
 * paths are joined to the solution directory); namespace cycles anchor to the
 * node's `representativeFile`. Namespace cycles without an anchor are returned
 * in `unanchored`.
 */
export function computeCycleDiagnostics(report: CodeMapReport): CycleDiagnosticsResult {
  const byFile = new Map<string, string[]>();
  const solutionDir = path.dirname(report.solutionPath ?? '');

  const projectByName = new Map((report.projects ?? []).map((project) => [project.name, project]));
  for (const cycle of report.projectCycles ?? []) {
    const description = formatCycle(cycle);
    for (const nodeName of cycle.nodes) {
      const project = projectByName.get(nodeName);
      if (!project) {
        continue;
      }
      const file = path.isAbsolute(project.relativePath)
        ? project.relativePath
        : path.join(solutionDir, project.relativePath);
      addMessage(
        byFile,
        file,
        `Project '${nodeName}' participates in a dependency cycle: ${description}`
      );
    }
  }

  const representativeByNamespace = new Map<string, string | null | undefined>();
  for (const node of report.namespaces?.diagramNodes ?? []) {
    representativeByNamespace.set(node.name, node.representativeFile);
  }

  const unanchored: string[] = [];
  for (const cycle of report.namespaces?.cycles ?? []) {
    const description = formatCycle(cycle);
    for (const nodeName of cycle.nodes) {
      const file = representativeByNamespace.get(nodeName);
      if (file) {
        addMessage(
          byFile,
          file,
          `Namespace '${nodeName}' participates in a dependency cycle: ${description}`
        );
      } else {
        unanchored.push(`${nodeName}: ${description}`);
      }
    }
  }

  const anchored = Array.from(byFile, ([file, messages]) => ({ file, messages }));
  return { anchored, unanchored };
}

function addMessage(byFile: Map<string, string[]>, file: string, message: string): void {
  const list = byFile.get(file) ?? [];
  list.push(message);
  byFile.set(file, list);
}

function formatCycle(cycle: DependencyCycle): string {
  if (cycle.nodes.length === 0) {
    return '(empty)';
  }
  return [...cycle.nodes, cycle.nodes[0]].join(' → ');
}
