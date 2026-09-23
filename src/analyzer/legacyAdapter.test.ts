import * as fs from 'node:fs';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import type { CodeMapReport } from './types';
import { adaptLegacyReport } from './legacyAdapter';
import { validateEvidenceRecord, validateSnapshot } from './reportV2Validation';

const root = 'C:/repo/sample';

function makeLegacyReport(): CodeMapReport {
  return {
    solutionPath: path.join(root, 'Baseline.sln'),
    solutionName: 'Baseline',
    projectCount: 3,
    totalDependencies: 2,
    totalPackageReferences: 0,
    testProjectCount: 1,
    projectKinds: [
      { name: 'library', count: 2 },
      { name: 'test', count: 1 }
    ],
    dependencyHubs: [],
    notes: [],
    warnings: [
      '1 conditional ProjectReference item(s) were detected and may vary by configuration.'
    ],
    mermaid: 'flowchart LR\n  P0 --> P1',
    projects: [
      {
        name: 'Core',
        relativePath: 'src\\Core\\Core.csproj',
        groupPath: 'src',
        kind: 'library',
        targetFramework: 'net10.0',
        outgoingDependencies: 1,
        incomingDependencies: 1,
        packageReferences: 0
      },
      {
        name: 'Util',
        relativePath: 'src\\Util\\Util.csproj',
        groupPath: 'src',
        kind: 'library',
        targetFramework: 'net10.0',
        outgoingDependencies: 1,
        incomingDependencies: 1,
        packageReferences: 0
      },
      {
        name: 'Core.Tests',
        relativePath: 'tests\\Core.Tests\\Core.Tests.csproj',
        groupPath: 'tests',
        kind: 'test',
        targetFramework: 'net10.0',
        outgoingDependencies: 0,
        incomingDependencies: 0,
        packageReferences: 0
      }
    ],
    diagramProjects: [
      { nodeId: 'P0', lookupKey: 'core', name: 'Core', kind: 'library', inCycle: true },
      { nodeId: 'P1', lookupKey: 'util', name: 'Util', kind: 'library', inCycle: true },
      { nodeId: 'P2', lookupKey: 'core-tests', name: 'Core.Tests', kind: 'test', inCycle: false }
    ],
    diagramEdges: [
      {
        edgeId: 'E0',
        sourceKey: 'core',
        targetKey: 'util',
        sourceNodeId: 'P0',
        targetNodeId: 'P1',
        sourceName: 'Core',
        targetName: 'Util',
        count: 1,
        inCycle: true
      },
      {
        edgeId: 'E1',
        sourceKey: 'util',
        targetKey: 'core',
        sourceNodeId: 'P1',
        targetNodeId: 'P0',
        sourceName: 'Util',
        targetName: 'Core',
        count: 1,
        inCycle: true
      }
    ],
    projectCycles: [{ scope: 'project', nodes: ['Core', 'Util'], length: 2 }],
    namespaces: {
      namespaceCount: 2,
      dependencyCount: 1,
      mermaid: 'flowchart LR\n  P0 --> P1',
      diagramNodes: [
        {
          nodeId: 'P0',
          lookupKey: 'Core',
          name: 'Core',
          kind: 'library',
          inCycle: false,
          representativeFile: path.join(root, 'src', 'Core', 'Order.cs')
        },
        {
          nodeId: 'P1',
          lookupKey: 'Util',
          name: 'Util',
          kind: 'library',
          inCycle: false,
          representativeFile: path.join(root, 'src', 'Util', 'Helpers.cs')
        }
      ],
      diagramEdges: [
        {
          edgeId: 'E0',
          sourceKey: 'Core',
          targetKey: 'Util',
          sourceNodeId: 'P0',
          targetNodeId: 'P1',
          sourceName: 'Core',
          targetName: 'Util',
          count: 2,
          inCycle: false
        }
      ],
      cycles: [],
      notes: ['Namespace edges are derived from `using` directives.']
    }
  };
}

describe('adaptLegacyReport', () => {
  const adapted = adaptLegacyReport({
    report: makeLegacyReport(),
    createdAt: '2026-09-23T00:00:00.000Z'
  });

  it('produces a snapshot that satisfies the v2 contract', () => {
    const result = validateSnapshot(adapted.snapshot);

    expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
  });

  it('produces evidence records that satisfy the v2 contract', () => {
    expect(adapted.evidence.length).toBeGreaterThan(0);
    for (const record of adapted.evidence) {
      const result = validateEvidenceRecord(record);
      expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
    }
  });

  it('never claims resolved symbols or type-level knowledge', () => {
    expect(adapted.snapshot.capabilities.typeGraph).toBe(false);
    expect(adapted.snapshot.capabilities.cycleWitness).toBe(false);
    expect(adapted.snapshot.capabilities.generatedDocuments).toBe(false);
    expect(adapted.snapshot.relations.every((relation) => relation.confidence === 'inferred')).toBe(
      true
    );
    expect(adapted.snapshot.relations.some((relation) => relation.basis === 'symbolResolved')).toBe(
      false
    );
    expect(adapted.snapshot.types).toHaveLength(0);
  });

  it('reports partial completeness with explicit limitations', () => {
    expect(adapted.snapshot.completeness).toBe('partial');
    const codes = adapted.snapshot.limitations.map((limitation) => limitation.code);

    expect(codes).toContain('quick.usingInferred');
    expect(codes).toContain('legacy.evidenceLocationsUnavailable');
    expect(codes).toContain('legacy.witnessUnavailable');
    expect(codes).toContain('quick.conditionNotEvaluated');
  });

  it('maps project edges to projectDeclared and namespace edges to usingInferred', () => {
    const projectRelations = adapted.snapshot.relations.filter(
      (relation) => relation.basis === 'projectDeclared'
    );
    const namespaceRelations = adapted.snapshot.relations.filter(
      (relation) => relation.basis === 'usingInferred'
    );

    expect(projectRelations).toHaveLength(2);
    expect(namespaceRelations).toHaveLength(1);
    expect(projectRelations.every((relation) => relation.kinds.includes('projectDeclared'))).toBe(
      true
    );
    expect(namespaceRelations.every((relation) => relation.kinds.includes('usingInferred'))).toBe(
      true
    );
  });

  it('keeps evidence document-level without inventing positions', () => {
    expect(adapted.evidence.every((record) => record.physicalSpan === null)).toBe(true);
    expect(adapted.snapshot.evidenceIndex).toBeNull();
  });

  it('keeps cycle groups without a witness and links the internal relations', () => {
    const projectCycle = adapted.snapshot.cycleGroups.find((group) => group.scope === 'project');

    expect(projectCycle?.witness).toBeNull();
    expect(projectCycle?.memberIds).toHaveLength(2);
    expect(projectCycle?.internalRelationIds).toHaveLength(2);
  });

  it('records documents for projects and namespace representatives', () => {
    const paths = adapted.snapshot.sourceManifest.map((document) => document.relativePath);

    expect(paths).toContain('src/Core/Core.csproj');
    expect(paths).toContain('src/Core/Order.cs');
    expect(paths.every((relativePath) => !relativePath.includes('\\'))).toBe(true);
    expect(paths.every((relativePath) => !path.isAbsolute(relativePath))).toBe(true);
  });

  it('is deterministic for the same report and timestamp', () => {
    const second = adaptLegacyReport({
      report: makeLegacyReport(),
      createdAt: '2026-09-23T00:00:00.000Z'
    });

    expect(second.snapshot.relations).toEqual(adapted.snapshot.relations);
    expect(second.snapshot.analysisId).toBe(adapted.snapshot.analysisId);
  });

  it('adapts the committed quick baseline snapshot', () => {
    const fixturePath = path.join(
      process.cwd(),
      'tests',
      'fixtures',
      'quick-baseline',
      'expected',
      'quick-report.json'
    );
    const raw = fs.readFileSync(fixturePath, 'utf8').replaceAll('<FIXTURE_ROOT>', root);
    const report = JSON.parse(raw) as CodeMapReport;

    const result = adaptLegacyReport({ report, createdAt: '2026-09-23T00:00:00.000Z' });

    expect(validateSnapshot(result.snapshot).ok).toBe(true);
    expect(result.snapshot.relations.length).toBeGreaterThan(0);
    expect(result.snapshot.cycleGroups.length).toBeGreaterThan(0);
  });
});
