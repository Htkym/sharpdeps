import { describe, it, expect } from 'vitest';
import type { CodeMapReport } from '../analyzer/types';
import { buildViewModel } from './viewModel';

function makeReport(overrides: Partial<CodeMapReport> = {}): CodeMapReport {
  const base: CodeMapReport = {
    solutionPath: '/repo/App.sln',
    solutionName: 'App',
    projectCount: 2,
    totalDependencies: 1,
    totalPackageReferences: 0,
    testProjectCount: 0,
    projectKinds: [],
    dependencyHubs: [],
    notes: [],
    warnings: [],
    mermaid: 'flowchart LR\n  A --> B',
    projects: [],
    diagramProjects: [
      { nodeId: 'n0', lookupKey: 'A', name: 'A', kind: 'lib', inCycle: false },
      { nodeId: 'n1', lookupKey: 'B', name: 'B', kind: 'lib', inCycle: false }
    ],
    diagramEdges: [
      {
        edgeId: 'e0',
        sourceKey: 'A',
        targetKey: 'B',
        sourceNodeId: 'n0',
        targetNodeId: 'n1',
        sourceName: 'A',
        targetName: 'B',
        count: 1,
        inCycle: false
      }
    ],
    projectCycles: [],
    namespaces: {
      namespaceCount: 0,
      dependencyCount: 0,
      mermaid: '',
      diagramNodes: [],
      diagramEdges: [],
      cycles: [],
      notes: []
    }
  };
  return { ...base, ...overrides };
}

describe('buildViewModel', () => {
  it('maps the project graph from report fields', () => {
    const report = makeReport();
    const vm = buildViewModel(report);

    expect(vm.solutionName).toBe('App');
    expect(vm.solutionPath).toBe('/repo/App.sln');
    expect(vm.projectGraph.granularity).toBe('projects');
    expect(vm.projectGraph.mermaid).toBe(report.mermaid);
    expect(vm.projectGraph.nodes).toBe(report.diagramProjects);
    expect(vm.projectGraph.edges).toBe(report.diagramEdges);
    expect(vm.projectGraph.cycles).toEqual([]);
  });

  it('maps the namespace graph from the namespaces section', () => {
    const report = makeReport({
      namespaces: {
        namespaceCount: 3,
        dependencyCount: 2,
        mermaid: 'flowchart LR\n  X --> Y',
        diagramNodes: [
          {
            nodeId: 'm0',
            lookupKey: 'X',
            name: 'X',
            kind: 'ns',
            inCycle: true,
            representativeFile: '/repo/X.cs'
          }
        ],
        diagramEdges: [],
        cycles: [{ scope: 'namespace', nodes: ['X', 'Y'], length: 2 }],
        notes: ['ns note']
      }
    });

    const vm = buildViewModel(report);

    expect(vm.namespaceGraph.granularity).toBe('namespaces');
    expect(vm.namespaceGraph.mermaid).toBe('flowchart LR\n  X --> Y');
    expect(vm.namespaceGraph.nodes).toHaveLength(1);
    expect(vm.namespaceGraph.cycles).toHaveLength(1);
    expect(vm.meta.namespaceCount).toBe(3);
    expect(vm.meta.namespaceCycleCount).toBe(1);
  });

  it('computes meta counts including cycle counts', () => {
    const report = makeReport({
      projectCount: 5,
      projectCycles: [
        { scope: 'project', nodes: ['A', 'B'], length: 2 },
        { scope: 'project', nodes: ['C', 'D'], length: 2 }
      ],
      warnings: ['w1'],
      notes: ['n1', 'n2']
    });

    const vm = buildViewModel(report);

    expect(vm.meta.projectCount).toBe(5);
    expect(vm.meta.projectCycleCount).toBe(2);
    expect(vm.meta.warnings).toEqual(['w1']);
    expect(vm.meta.notes).toEqual(['n1', 'n2']);
  });

  it('falls back to safe defaults when optional fields are missing', () => {
    const sparse = {
      solutionPath: '/repo/Sparse.sln'
    } as unknown as CodeMapReport;

    const vm = buildViewModel(sparse);

    expect(vm.solutionName).toBe('');
    expect(vm.projectGraph.mermaid).toBe('');
    expect(vm.projectGraph.nodes).toEqual([]);
    expect(vm.projectGraph.edges).toEqual([]);
    expect(vm.namespaceGraph.nodes).toEqual([]);
    expect(vm.meta.projectCount).toBe(0);
    expect(vm.meta.namespaceCount).toBe(0);
    expect(vm.meta.projectCycleCount).toBe(0);
    expect(vm.meta.namespaceCycleCount).toBe(0);
    expect(vm.meta.warnings).toEqual([]);
    expect(vm.meta.notes).toEqual([]);
  });
});
