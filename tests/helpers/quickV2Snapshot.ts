// Builds a machine-independent structural view of a Quick v2 snapshot.
//
// Absolute paths, ids, hashes of the root path, and timestamps differ per machine
// by design (ids are scoped to the workspace root), so the regression snapshot
// uses entity *labels* instead of ids. That still verifies the graph structure
// (which relation connects which pair), the evidence positions, the cycle groups,
// and the recorded limitations.

import type { AnalysisSnapshot, EvidenceRecord } from '../../src/analyzer/reportV2';

export const V2_PLACEHOLDERS = {
  analysisId: '<ANALYSIS_ID>',
  createdAt: '<TIMESTAMP>',
  rootId: '<ROOT_ID>',
  profileHash: '<PROFILE_HASH>'
} as const;

export function buildStructureSnapshot(
  snapshot: AnalysisSnapshot,
  evidence: readonly EvidenceRecord[]
): unknown {
  const projectNameById = new Map(snapshot.projects.map((project) => [project.id, project.name]));
  const projectNameByVariant = new Map(
    snapshot.projects.map((project) => [project.variantId, project.name])
  );

  const label = (id: string): string => {
    const project = projectNameById.get(id);
    if (project) {
      return `project:${project}`;
    }
    const namespace = snapshot.namespaces.find((node) => node.id === id);
    if (namespace) {
      const owner =
        projectNameByVariant.get(namespace.projectVariantId) ?? namespace.projectVariantId;
      return `namespace:${namespace.name}@${owner}`;
    }
    const type = snapshot.types.find((node) => node.id === id);
    if (type) {
      return `type:${type.fullName}`;
    }
    return id;
  };

  const relationLabel = (id: string): string => {
    const relation = snapshot.relations.find((entry) => entry.id === id);
    return relation ? `${label(relation.sourceEntityId)} -> ${label(relation.targetEntityId)}` : id;
  };

  const documentPathById = new Map(
    snapshot.sourceManifest.map((document) => [document.id, document.relativePath])
  );

  const relations = snapshot.relations
    .map((relation) => ({
      source: label(relation.sourceEntityId),
      target: label(relation.targetEntityId),
      basis: relation.basis,
      kinds: [...relation.kinds].sort(),
      evidenceCount: relation.evidenceCount,
      distinctSourceDocumentCount: relation.distinctSourceDocumentCount,
      confidence: relation.confidence ?? null,
      ambiguousCandidates: relation.ambiguousCandidates ?? null
    }))
    .sort(compareRecords);

  const cycles = snapshot.cycleGroups
    .map((group) => ({
      scope: group.scope,
      basis: group.basis,
      witness: group.witness ?? null,
      members: group.memberIds.map(label).sort(),
      internalRelations: group.internalRelationIds.map(relationLabel).sort()
    }))
    .sort(compareRecords);

  const evidenceRecords = evidence
    .map((record) => ({
      relation: relationLabel(record.relationId),
      kind: record.kind,
      document: documentPathById.get(record.documentId) ?? record.documentId,
      span:
        record.physicalSpan === null || record.physicalSpan === undefined
          ? null
          : [
              record.physicalSpan.startLine,
              record.physicalSpan.startCharacter,
              record.physicalSpan.length
            ],
      confidence: record.confidence,
      publicSurface: record.publicSurface,
      origin: record.origin
    }))
    .sort(compareRecords);

  return {
    target: { kind: snapshot.target.kind, relativePath: snapshot.target.relativePath },
    mode: snapshot.mode,
    completeness: snapshot.completeness,
    capabilities: snapshot.capabilities,
    coverage: snapshot.coverage,
    profile: {
      configuration: snapshot.profile.configuration,
      variants: snapshot.profile.projectVariants
        .map((variant) => ({
          project: projectNameByVariant.get(variant.variantId) ?? variant.projectLogicalId,
          targetFramework: variant.targetFramework,
          targetFrameworkSource: variant.targetFrameworkSource
        }))
        .sort(compareRecords)
    },
    projects: snapshot.projects
      .map((project) => ({
        name: project.name,
        relativePath: project.relativePath,
        groupPath: project.groupPath,
        kind: project.kind,
        targetFramework: project.targetFramework,
        loadState: project.loadState
      }))
      .sort(compareRecords),
    namespaces: snapshot.namespaces
      .map((node) => ({
        name: node.name,
        project: projectNameByVariant.get(node.projectVariantId) ?? node.projectVariantId,
        typeCount: node.typeCount
      }))
      .sort(compareRecords),
    types: snapshot.types.map((type) => type.fullName).sort(),
    relations,
    cycleGroups: cycles,
    diagnostics: snapshot.diagnostics
      .map((diagnostic) => ({
        severity: diagnostic.severity,
        code: diagnostic.code,
        message: diagnostic.message
      }))
      .sort(compareRecords),
    evidenceIndex: (snapshot.evidenceIndex?.relations ?? [])
      .map((entry) => ({ relation: relationLabel(entry.relationId), count: entry.count }))
      .sort(compareRecords),
    documents: snapshot.sourceManifest
      .map((document) => ({
        relativePath: document.relativePath,
        origin: document.origin,
        contentHash: document.contentHash
      }))
      .sort(compareRecords),
    evidence: evidenceRecords,
    limitations: snapshot.limitations
      .map((limitation) => ({
        code: limitation.code,
        count: limitation.count ?? null,
        message: limitation.message
      }))
      .sort(compareRecords)
  };
}

function compareRecords(left: unknown, right: unknown): number {
  return JSON.stringify(left).localeCompare(JSON.stringify(right));
}
