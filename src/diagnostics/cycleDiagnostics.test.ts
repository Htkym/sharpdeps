import { describe, it, expect } from 'vitest';
import * as path from 'path';
import type { CodeMapProject, CodeMapReport } from '../analyzer/types';
import { computeCycleDiagnostics } from './cycleAnchoring';

const solutionDir = path.resolve('repo');
const solutionPath = path.join(solutionDir, 'App.sln');

function project(name: string, relativePath: string): CodeMapProject {
  return {
    name,
    relativePath,
    groupPath: '',
    kind: 'lib',
    targetFramework: 'net10.0',
    outgoingDependencies: 0,
    incomingDependencies: 0,
    packageReferences: 0
  };
}

function makeReport(overrides: Partial<CodeMapReport> = {}): CodeMapReport {
  const base: CodeMapReport = {
    solutionPath,
    solutionName: 'App',
    projectCount: 0,
    totalDependencies: 0,
    totalPackageReferences: 0,
    testProjectCount: 0,
    projectKinds: [],
    dependencyHubs: [],
    notes: [],
    warnings: [],
    mermaid: '',
    projects: [],
    diagramProjects: [],
    diagramEdges: [],
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

describe('computeCycleDiagnostics', () => {
  it('returns nothing when there are no cycles', () => {
    const result = computeCycleDiagnostics(makeReport());
    expect(result.anchored).toEqual([]);
    expect(result.unanchored).toEqual([]);
  });

  it('anchors project cycles to each project csproj, joining relative paths to the solution dir', () => {
    const report = makeReport({
      projects: [
        project('A', path.join('src', 'A', 'A.csproj')),
        project('B', path.join('src', 'B', 'B.csproj'))
      ],
      projectCycles: [{ scope: 'project', nodes: ['A', 'B'], length: 2 }]
    });

    const result = computeCycleDiagnostics(report);

    expect(result.unanchored).toEqual([]);
    expect(result.anchored).toEqual([
      {
        file: path.join(solutionDir, 'src', 'A', 'A.csproj'),
        messages: ["Project 'A' participates in a dependency cycle: A → B → A"]
      },
      {
        file: path.join(solutionDir, 'src', 'B', 'B.csproj'),
        messages: ["Project 'B' participates in a dependency cycle: A → B → A"]
      }
    ]);
  });

  it('keeps absolute project paths as-is', () => {
    const absolute = path.join(solutionDir, 'libs', 'B', 'B.csproj');
    const report = makeReport({
      projects: [project('B', absolute)],
      projectCycles: [{ scope: 'project', nodes: ['B'], length: 1 }]
    });

    const result = computeCycleDiagnostics(report);

    expect(result.anchored).toHaveLength(1);
    expect(result.anchored[0].file).toBe(absolute);
  });

  it('skips cycle nodes that have no matching project', () => {
    const report = makeReport({
      projects: [project('A', path.join('src', 'A', 'A.csproj'))],
      projectCycles: [{ scope: 'project', nodes: ['A', 'Ghost'], length: 2 }]
    });

    const result = computeCycleDiagnostics(report);

    expect(result.anchored).toHaveLength(1);
    expect(result.anchored[0].file).toBe(path.join(solutionDir, 'src', 'A', 'A.csproj'));
  });

  it('anchors namespace cycles to representativeFile', () => {
    const report = makeReport({
      namespaces: {
        namespaceCount: 2,
        dependencyCount: 1,
        mermaid: '',
        diagramNodes: [
          {
            nodeId: 'm0',
            lookupKey: 'A.Ns',
            name: 'A.Ns',
            kind: 'ns',
            inCycle: true,
            representativeFile: path.join(solutionDir, 'A', 'Thing.cs')
          },
          {
            nodeId: 'm1',
            lookupKey: 'B.Ns',
            name: 'B.Ns',
            kind: 'ns',
            inCycle: true,
            representativeFile: path.join(solutionDir, 'B', 'Other.cs')
          }
        ],
        diagramEdges: [],
        cycles: [{ scope: 'namespace', nodes: ['A.Ns', 'B.Ns'], length: 2 }],
        notes: []
      }
    });

    const result = computeCycleDiagnostics(report);

    expect(result.unanchored).toEqual([]);
    expect(result.anchored).toEqual([
      {
        file: path.join(solutionDir, 'A', 'Thing.cs'),
        messages: ["Namespace 'A.Ns' participates in a dependency cycle: A.Ns → B.Ns → A.Ns"]
      },
      {
        file: path.join(solutionDir, 'B', 'Other.cs'),
        messages: ["Namespace 'B.Ns' participates in a dependency cycle: A.Ns → B.Ns → A.Ns"]
      }
    ]);
  });

  it('reports namespace cycles without an anchor in unanchored', () => {
    const report = makeReport({
      namespaces: {
        namespaceCount: 2,
        dependencyCount: 1,
        mermaid: '',
        diagramNodes: [
          {
            nodeId: 'm0',
            lookupKey: 'A.Ns',
            name: 'A.Ns',
            kind: 'ns',
            inCycle: true,
            representativeFile: null
          }
        ],
        diagramEdges: [],
        cycles: [{ scope: 'namespace', nodes: ['A.Ns', 'Missing.Ns'], length: 2 }],
        notes: []
      }
    });

    const result = computeCycleDiagnostics(report);

    expect(result.anchored).toEqual([]);
    expect(result.unanchored).toEqual([
      'A.Ns: A.Ns → Missing.Ns → A.Ns',
      'Missing.Ns: A.Ns → Missing.Ns → A.Ns'
    ]);
  });

  it('groups multiple messages for the same file', () => {
    const shared = path.join('src', 'Shared', 'Shared.csproj');
    const report = makeReport({
      projects: [project('Shared', shared)],
      projectCycles: [
        { scope: 'project', nodes: ['Shared', 'X'], length: 2 },
        { scope: 'project', nodes: ['Shared', 'Y'], length: 2 }
      ]
    });

    const result = computeCycleDiagnostics(report);

    expect(result.anchored).toHaveLength(1);
    expect(result.anchored[0].file).toBe(path.join(solutionDir, shared));
    expect(result.anchored[0].messages).toEqual([
      "Project 'Shared' participates in a dependency cycle: Shared → X → Shared",
      "Project 'Shared' participates in a dependency cycle: Shared → Y → Shared"
    ]);
  });

  it('formats an empty cycle as (empty)', () => {
    const report = makeReport({
      projects: [project('A', path.join('src', 'A', 'A.csproj'))],
      projectCycles: [{ scope: 'project', nodes: [], length: 0 }]
    });

    const result = computeCycleDiagnostics(report);
    expect(result.anchored).toEqual([]);
  });
});
