import * as path from 'node:path';
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

  it('passes through project kind breakdown and dependency hubs for the overview', () => {
    const report = makeReport({
      projectKinds: [
        { name: 'lib', count: 3 },
        { name: 'test', count: 1 }
      ],
      dependencyHubs: [
        {
          name: 'Core',
          kind: 'lib',
          outgoingDependencies: 1,
          incomingDependencies: 4,
          packageReferences: 2
        }
      ]
    });

    const vm = buildViewModel(report);

    expect(vm.meta.projectKinds).toEqual([
      { name: 'lib', count: 3 },
      { name: 'test', count: 1 }
    ]);
    expect(vm.meta.dependencyHubs).toEqual([
      {
        name: 'Core',
        kind: 'lib',
        outgoingDependencies: 1,
        incomingDependencies: 4,
        packageReferences: 2
      }
    ]);
  });

  it('enriches project nodes with a representativeFile derived from relativePath', () => {
    const solutionDir = path.resolve('repo');
    const report = makeReport({
      solutionPath: path.join(solutionDir, 'App.sln'),
      projects: [
        {
          name: 'A',
          relativePath: path.join('src', 'A', 'A.csproj'),
          groupPath: '',
          kind: 'lib',
          targetFramework: 'net10.0',
          outgoingDependencies: 0,
          incomingDependencies: 0,
          packageReferences: 0
        },
        {
          name: 'B',
          relativePath: path.join('src', 'B', 'B.csproj'),
          groupPath: '',
          kind: 'lib',
          targetFramework: 'net10.0',
          outgoingDependencies: 0,
          incomingDependencies: 0,
          packageReferences: 0
        }
      ]
    });

    const vm = buildViewModel(report);

    expect(vm.projectGraph.nodes[0].representativeFile).toBe(
      path.join(solutionDir, 'src', 'A', 'A.csproj')
    );
    expect(vm.projectGraph.nodes[1].representativeFile).toBe(
      path.join(solutionDir, 'src', 'B', 'B.csproj')
    );
    // Enrichment must not mutate the original report data.
    expect(report.diagramProjects[0].representativeFile).toBeUndefined();
  });

  it('preserves project node identity when no enrichment is possible', () => {
    const solutionDir = path.resolve('repo');
    const existingFile = path.join(solutionDir, 'src', 'A', 'A.csproj');
    const report = makeReport({
      solutionPath: path.join(solutionDir, 'App.sln'),
      projects: [
        {
          name: 'A',
          relativePath: path.join('src', 'A', 'A.csproj'),
          groupPath: '',
          kind: 'lib',
          targetFramework: 'net10.0',
          outgoingDependencies: 0,
          incomingDependencies: 0,
          packageReferences: 0
        },
        {
          name: 'Missing',
          relativePath: path.join('src', 'Missing', 'Missing.csproj'),
          groupPath: '',
          kind: 'lib',
          targetFramework: 'net10.0',
          outgoingDependencies: 0,
          incomingDependencies: 0,
          packageReferences: 0
        }
      ],
      diagramProjects: [
        {
          nodeId: 'n0',
          lookupKey: 'A',
          name: 'A',
          kind: 'lib',
          inCycle: false,
          representativeFile: existingFile
        },
        { nodeId: 'n1', lookupKey: 'B', name: 'B', kind: 'lib', inCycle: false }
      ]
    });

    const vm = buildViewModel(report);

    expect(vm.projectGraph.nodes).toBe(report.diagramProjects);
    expect(vm.projectGraph.nodes[0].representativeFile).toBe(existingFile);
    expect(vm.projectGraph.nodes[1].representativeFile).toBeUndefined();
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
    expect(vm.meta.projectKinds).toEqual([]);
    expect(vm.meta.dependencyHubs).toEqual([]);
  });
});
