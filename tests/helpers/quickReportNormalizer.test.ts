import { describe, expect, it } from 'vitest';
import type { CodeMapReport } from '../../src/analyzer/types';
import { FIXTURE_ROOT_PLACEHOLDER, normalizeQuickReport } from './quickReportNormalizer';

const root = 'C:\\repo\\tests\\fixtures\\quick-baseline';

function makeReport(overrides: Partial<CodeMapReport> = {}): CodeMapReport {
  return {
    solutionPath: 'C:\\repo\\tests\\fixtures\\quick-baseline\\Baseline.sln',
    solutionName: 'Baseline',
    projectCount: 1,
    totalDependencies: 1,
    totalPackageReferences: 0,
    testProjectCount: 0,
    projectKinds: [],
    dependencyHubs: [],
    notes: [],
    warnings: [],
    mermaid: 'flowchart LR\r\n  P0["Native\\nlibrary"]',
    projects: [
      {
        name: 'App',
        relativePath: 'src\\App\\App.csproj',
        groupPath: 'src',
        kind: 'app',
        targetFramework: 'net10.0',
        outgoingDependencies: 1,
        incomingDependencies: 0,
        packageReferences: 0
      }
    ],
    diagramProjects: [
      {
        nodeId: 'P0',
        lookupKey: 'c:\\repo\\tests\\fixtures\\quick-baseline\\src\\app\\app.csproj',
        name: 'App',
        kind: 'app',
        inCycle: false,
        representativeFile: 'C:\\repo\\tests\\fixtures\\quick-baseline\\src\\App\\Program.cs'
      }
    ],
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
    },
    ...overrides
  };
}

describe('normalizeQuickReport', () => {
  it('replaces the fixture root and normalizes separators', () => {
    const normalized = normalizeQuickReport(makeReport(), root);

    expect(normalized.solutionPath).toBe(`${FIXTURE_ROOT_PLACEHOLDER}/Baseline.sln`);
    expect(normalized.projects[0].relativePath).toBe('src/App/App.csproj');
    expect(normalized.diagramProjects[0].lookupKey).toBe(
      `${FIXTURE_ROOT_PLACEHOLDER}/src/app/app.csproj`
    );
    expect(normalized.diagramProjects[0].representativeFile).toBe(
      `${FIXTURE_ROOT_PLACEHOLDER}/src/App/Program.cs`
    );
  });

  it('keeps mermaid label escapes intact', () => {
    const normalized = normalizeQuickReport(makeReport(), root);

    expect(normalized.mermaid).toBe('flowchart LR\n  P0["Native\\nlibrary"]');
  });

  it('accepts forward-slash roots', () => {
    const normalized = normalizeQuickReport(makeReport(), 'C:/repo/tests/fixtures/quick-baseline');

    expect(normalized.solutionPath).toBe(`${FIXTURE_ROOT_PLACEHOLDER}/Baseline.sln`);
  });
});
