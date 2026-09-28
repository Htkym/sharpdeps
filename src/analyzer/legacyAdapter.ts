// Adapter from the legacy v1 Quick report to the v2 analysis model (SD-002).
//
// A legacy report only knows declaration sites (ProjectReference, using) and
// never resolved symbol usage, and it does not record evidence positions. The
// adapter therefore:
//   - marks every relation as inferred (`projectDeclared` / `usingInferred`),
//   - keeps cycle groups without a witness (no edge-level proof exists),
//   - emits one document-level evidence record per relation with a null span,
//   - adds explicit limitations instead of pretending the data is complete.
// Evidence stays in memory (`evidenceIndex: null`) because no side file exists.
// The adapter exists so the transition period can render old results; it is not
// a substitute for the v2 analyzer output.

import * as path from 'node:path';
import type { CodeMapReport } from './types';
import type {
  AnalysisRelation,
  AnalysisSnapshot,
  CycleGroup,
  EvidenceRecord,
  Limitation,
  RelationBasis,
  RelationKind,
  SourceDocument,
  TargetDescriptor
} from './reportV2';
import {
  analysisId as buildAnalysisId,
  cycleGroupId,
  diagnosticId,
  documentId,
  evidenceId,
  namespaceId,
  profileHash,
  projectLogicalId,
  projectVariantId,
  relationId,
  workspaceRootId
} from './identity';

export const LEGACY_ANALYZER_VERSION = 'legacy-v1';

/** Content hash placeholder used when the legacy report has no hash information. */
export const HASH_UNAVAILABLE = 'unavailable';

export interface LegacyAdapterOptions {
  report: CodeMapReport;
  /** ISO timestamp recorded as the snapshot creation time. Defaults to now. */
  createdAt?: string;
  analyzerVersion?: string;
  /**
   * Optional content hash lookup. Legacy reports carry no hashes, so the adapter
   * writes `unavailable` unless the host can hash the document itself.
   */
  hashDocument?: (absolutePath: string) => string | undefined;
}

export interface LegacyAdaptation {
  snapshot: AnalysisSnapshot;
  /** In-memory evidence records; `snapshot.evidenceIndex` is null for adapted results. */
  evidence: EvidenceRecord[];
}

export function adaptLegacyReport(options: LegacyAdapterOptions): LegacyAdaptation {
  const { report } = options;
  const paths = /^[a-z]:[\\/]|^\\\\/i.test(report.solutionPath ?? '') ? path.win32 : path.posix;
  const createdAt = options.createdAt ?? new Date().toISOString();
  const solutionDirectory = paths.dirname(report.solutionPath ?? '');
  const rootId = workspaceRootId(solutionDirectory);

  const target: TargetDescriptor = {
    kind: targetKind(report.solutionPath ?? ''),
    rootId,
    relativePath: toRelative(solutionDirectory, report.solutionPath ?? '', paths)
  };

  const projects = report.projects ?? [];
  const projectIds = new Map<string, string>();
  const variantIds = new Map<string, string>();
  const projectDirectories = new Map<string, string>();
  for (const project of projects) {
    const relativePath = normalize(project.relativePath);
    const logicalId = projectLogicalId(rootId, relativePath);
    projectIds.set(project.name, logicalId);
    variantIds.set(
      project.name,
      projectVariantId(logicalId, project.targetFramework || '(not specified)', 'Debug', null)
    );
    projectDirectories.set(
      project.name,
      paths.dirname(paths.join(solutionDirectory, relativePath))
    );
  }

  const profile = profileHash({
    configuration: 'Debug',
    projectVariants: projects.map((project) => ({
      projectLogicalId: projectIds.get(project.name) as string,
      targetFramework: project.targetFramework || '(not specified)'
    }))
  });

  const documents = new Map<string, SourceDocument>();
  const documentFor = (absoluteOrRelative: string): SourceDocument => {
    const file = normalize(absoluteOrRelative);
    const absolute = paths.isAbsolute(file) ? file : paths.join(solutionDirectory, file);
    const relativePath = toRelative(solutionDirectory, absolute, paths);
    const id = documentId(rootId, relativePath, 'userSource');
    const existing = documents.get(id);
    if (existing) {
      return existing;
    }
    const document: SourceDocument = {
      id,
      relativePath,
      origin: 'userSource',
      contentHash: options.hashDocument?.(absolute) ?? HASH_UNAVAILABLE
    };
    documents.set(id, document);
    return document;
  };

  const ownerOfNamespace = (namespaceName: string): string | undefined => {
    const node = (report.namespaces?.diagramNodes ?? []).find(
      (entry) => entry.name === namespaceName
    );
    const file = node?.representativeFile;
    if (!file) {
      return undefined;
    }
    const normalizedFile = paths.normalize(normalize(file));
    let bestMatch: { name: string; length: number } | undefined;
    for (const [name, directory] of projectDirectories) {
      const prefix = directory.endsWith(paths.sep) ? directory : directory + paths.sep;
      if (
        normalizedFile.startsWith(prefix) &&
        (!bestMatch || directory.length > bestMatch.length)
      ) {
        bestMatch = { name, length: directory.length };
      }
    }
    return bestMatch?.name;
  };

  const namespaceIds = new Map<string, string>();
  let namespacesWithoutOwner = 0;
  for (const node of report.namespaces?.diagramNodes ?? []) {
    const owner = ownerOfNamespace(node.name);
    const variantId = owner ? variantIds.get(owner) : undefined;
    if (!variantId) {
      namespacesWithoutOwner++;
      continue;
    }
    namespaceIds.set(node.name, namespaceId(variantId, node.name));
  }

  const relations: AnalysisRelation[] = [];
  const evidence: EvidenceRecord[] = [];
  const seenRelations = new Set<string>();

  const addRelation = (
    sourceEntityId: string,
    targetEntityId: string,
    basis: RelationBasis,
    kind: RelationKind,
    document: SourceDocument | undefined
  ): void => {
    if (!document) {
      return;
    }
    const id = relationId({ basis, sourceEntityId, targetEntityId, profileHash: profile });
    if (seenRelations.has(id)) {
      return;
    }
    seenRelations.add(id);
    relations.push({
      id,
      sourceEntityId,
      targetEntityId,
      basis,
      kinds: [kind],
      evidenceCount: 1,
      distinctSourceMemberCount: 0,
      distinctSourceDocumentCount: 1,
      generatedEvidenceCount: 0,
      publicSurfaceEvidenceCount: 0,
      confidence: 'inferred'
    });
    evidence.push({
      id: evidenceId({ relationId: id, kind, documentId: document.id, spanKey: 'document' }),
      relationId: id,
      sourceEntityId,
      targetEntityId,
      sourceTypeId: null,
      sourceMemberId: null,
      targetTypeId: null,
      targetMemberId: null,
      kind,
      origin: document.origin,
      documentId: document.id,
      physicalSpan: null,
      mappedLocation: null,
      sourceContentHash: document.contentHash,
      confidence: 'inferred',
      publicSurface: false
    });
  };

  for (const edge of report.diagramEdges ?? []) {
    const source = projectIds.get(edge.sourceName);
    const targetProject = projectIds.get(edge.targetName);
    if (!source || !targetProject) {
      continue;
    }
    const project = projects.find((entry) => entry.name === edge.sourceName);
    addRelation(
      source,
      targetProject,
      'projectDeclared',
      'projectDeclared',
      project ? documentFor(project.relativePath) : undefined
    );
  }

  for (const edge of report.namespaces?.diagramEdges ?? []) {
    const source = namespaceIds.get(edge.sourceName);
    const targetNamespace = namespaceIds.get(edge.targetName);
    if (!source || !targetNamespace) {
      continue;
    }
    const representative = (report.namespaces?.diagramNodes ?? []).find(
      (node) => node.name === edge.sourceName
    )?.representativeFile;
    addRelation(
      source,
      targetNamespace,
      'usingInferred',
      'usingInferred',
      representative ? documentFor(representative) : undefined
    );
  }

  const cycleGroups: CycleGroup[] = [];
  for (const cycle of report.projectCycles ?? []) {
    const memberIds = cycle.nodes
      .map((name) => projectIds.get(name))
      .filter((id): id is string => Boolean(id));
    if (memberIds.length < 2) {
      continue;
    }
    cycleGroups.push({
      id: cycleGroupId('project', 'projectDeclared', memberIds),
      scope: 'project',
      basis: 'projectDeclared',
      memberIds,
      internalRelationIds: relations
        .filter(
          (relation) =>
            relation.basis === 'projectDeclared' &&
            memberIds.includes(relation.sourceEntityId) &&
            memberIds.includes(relation.targetEntityId)
        )
        .map((relation) => relation.id),
      witness: null
    });
  }

  for (const cycle of report.namespaces?.cycles ?? []) {
    const memberIds = cycle.nodes
      .map((name) => namespaceIds.get(name))
      .filter((id): id is string => Boolean(id));
    if (memberIds.length < 2) {
      continue;
    }
    cycleGroups.push({
      id: cycleGroupId('namespace', 'usingInferred', memberIds),
      scope: 'namespace',
      basis: 'usingInferred',
      memberIds,
      internalRelationIds: relations
        .filter(
          (relation) =>
            relation.basis === 'usingInferred' &&
            memberIds.includes(relation.sourceEntityId) &&
            memberIds.includes(relation.targetEntityId)
        )
        .map((relation) => relation.id),
      witness: null
    });
  }

  const limitations = buildLimitations(report, namespacesWithoutOwner);
  const analysis = buildAnalysisId({
    targetId: target.rootId,
    mode: 'quick',
    profileHash: profile,
    startedAt: createdAt
  });

  const diagnostics = (report.warnings ?? []).map((message, index) => ({
    id: diagnosticId('quick.warning', null, `${index}:${message}`),
    severity: 'warning' as const,
    code: 'quick.warning',
    message,
    targetId: null,
    evidenceId: null,
    analysisId: analysis
  }));

  const discovered = Math.max(report.projectCount ?? projects.length, projects.length);

  const snapshot: AnalysisSnapshot = {
    schemaVersion: 2,
    analyzerVersion: options.analyzerVersion ?? LEGACY_ANALYZER_VERSION,
    analysisId: analysis,
    createdAt,
    target,
    mode: 'quick',
    profile: {
      configuration: 'Debug',
      platform: null,
      projectVariants: projects.map((project) => ({
        variantId: variantIds.get(project.name) as string,
        projectLogicalId: projectIds.get(project.name) as string,
        targetFramework: project.targetFramework || '(not specified)',
        configuration: 'Debug',
        platform: null,
        targetFrameworkSource: project.targetFramework ? 'targetFramework' : 'notSpecified'
      })),
      profileHash: profile
    },
    capabilities: {
      typeGraph: false,
      evidence: true,
      generatedDocuments: false,
      cycleWitness: false,
      search: true
    },
    completeness: 'partial',
    coverage: {
      discovered,
      loaded: projects.length,
      analyzed: projects.length,
      failed: Math.max(0, discovered - projects.length),
      skipped: 0,
      unresolved: 0
    },
    projects: projects.map((project) => ({
      id: projectIds.get(project.name) as string,
      variantId: variantIds.get(project.name) as string,
      name: project.name,
      relativePath: normalize(project.relativePath),
      groupPath: project.groupPath,
      kind: toProjectKind(project.kind),
      targetFramework: project.targetFramework,
      configuration: 'Debug',
      platform: null,
      packageReferences: [],
      loadState: 'loaded',
      limitations: []
    })),
    namespaces: (report.namespaces?.diagramNodes ?? [])
      .map((node) => {
        const id = namespaceIds.get(node.name);
        const owner = ownerOfNamespace(node.name);
        const variantId = owner ? variantIds.get(owner) : undefined;
        if (!id || !variantId) {
          return undefined;
        }
        return {
          id,
          projectVariantId: variantId,
          name: node.name,
          typeCount: 0,
          representativeDocumentId: node.representativeFile
            ? documentFor(node.representativeFile).id
            : null
        };
      })
      .filter((entry): entry is NonNullable<typeof entry> => Boolean(entry)),
    types: [],
    relations,
    cycleGroups,
    diagnostics,
    evidenceIndex: null,
    sourceManifest: [...documents.values()],
    limitations
  };

  return { snapshot, evidence };
}

function buildLimitations(report: CodeMapReport, namespacesWithoutOwner: number): Limitation[] {
  const limitations: Limitation[] = [
    {
      code: 'quick.typeGraphUnavailable',
      message:
        'Quick analysis reads project files and C# syntax only; it cannot resolve symbol usage, so no type-level graph is available.'
    },
    {
      code: 'quick.usingInferred',
      message:
        'Namespace edges come from using directives. They are inferred dependencies, not usage counts.'
    },
    {
      code: 'legacy.evidenceLocationsUnavailable',
      message:
        'This result was adapted from a v1 report, which records documents but not evidence positions.'
    },
    {
      code: 'legacy.witnessUnavailable',
      message:
        'Cycle groups come from strongly connected components; no verified cycle path is available.'
    },
    {
      code: 'legacy.countsNotDistinct',
      message:
        'Reference counts are unavailable in a v1 report, so every adapted relation reports one document-level evidence record.'
    }
  ];

  if ((report.warnings ?? []).some((warning) => warning.includes('conditional'))) {
    limitations.push({
      code: 'quick.conditionNotEvaluated',
      message:
        'Conditional ProjectReference items were not evaluated against a configuration; edges may not exist in the selected build.'
    });
  }

  if (
    report.notes.some((note) => note.includes('truncated')) ||
    report.namespaces?.notes.some((note) => note.includes('truncated'))
  ) {
    limitations.push({
      code: 'quick.displayTruncated',
      message:
        'The v1 report applied display budgets while analyzing, so the adapted graph may omit projects or edges.'
    });
  }

  if (namespacesWithoutOwner > 0) {
    limitations.push({
      code: 'legacy.namespaceOwnerUnknown',
      message:
        'Some namespaces could not be attributed to a project variant and were left out of the adapted model.',
      count: namespacesWithoutOwner
    });
  }

  return limitations;
}

function toProjectKind(
  kind: string
): 'app' | 'web' | 'library' | 'test' | 'desktop' | 'native' | 'unknown' {
  switch (kind) {
    case 'app':
    case 'web':
    case 'library':
    case 'test':
    case 'desktop':
      return kind;
    default:
      return 'unknown';
  }
}

function targetKind(solutionPath: string): 'solution' | 'slnx' | 'project' {
  const extension = path.extname(solutionPath).toLowerCase();
  if (extension === '.slnx') {
    return 'slnx';
  }
  if (extension === '.sln') {
    return 'solution';
  }
  return 'project';
}

function toRelative(rootDirectory: string, target: string, paths: typeof path.posix): string {
  return normalize(paths.isAbsolute(target) ? paths.relative(rootDirectory, target) : target);
}

function normalize(value: string): string {
  return value.replace(/\\/g, '/');
}
