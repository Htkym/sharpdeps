// Shared builders for v2 model tests. Kept in tests/helpers so both the unit
// tests under src/ and the contract tests can use them.

import type {
  AnalysisSnapshot,
  EvidenceRecord,
  PhysicalSpan,
  RelationBasis,
  RelationKind
} from '../../src/analyzer/reportV2';

export const IDS = {
  root: 'wrk_0123456789abcdef',
  projectA: 'prj_1111111111111111',
  projectB: 'prj_2222222222222222',
  variantA: 'var_1111111111111111',
  variantB: 'var_2222222222222222',
  namespaceA: 'ns_1111111111111111',
  namespaceB: 'ns_2222222222222222',
  typeA: 'ty_1111111111111111',
  typeB: 'ty_2222222222222222',
  relation: 'rel_1111111111111111',
  evidence: 'ev_1111111111111111',
  cycle: 'cyc_1111111111111111',
  document: 'doc_1111111111111111',
  diagnostic: 'dg_1111111111111111'
} as const;

export const SPAN: PhysicalSpan = {
  start: 120,
  length: 12,
  startLine: 9,
  startCharacter: 8,
  endLine: 9,
  endCharacter: 20
};

export function makeSnapshot(overrides: Partial<AnalysisSnapshot> = {}): AnalysisSnapshot {
  const base: AnalysisSnapshot = {
    schemaVersion: 2,
    analyzerVersion: '0.1.0-test',
    analysisId: 'an_0123456789abcdef',
    createdAt: '2026-09-23T12:00:00.000Z',
    target: { kind: 'solution', rootId: IDS.root, relativePath: 'Sample.sln' },
    mode: 'semantic',
    profile: {
      configuration: 'Debug',
      platform: null,
      projectVariants: [
        {
          variantId: IDS.variantA,
          projectLogicalId: IDS.projectA,
          targetFramework: 'net10.0',
          configuration: 'Debug',
          platform: null,
          targetFrameworkSource: 'targetFramework'
        },
        {
          variantId: IDS.variantB,
          projectLogicalId: IDS.projectB,
          targetFramework: 'net10.0',
          configuration: 'Debug',
          platform: null,
          targetFrameworkSource: 'targetFramework'
        }
      ],
      profileHash: '0123456789abcdef'
    },
    capabilities: {
      typeGraph: true,
      evidence: true,
      generatedDocuments: true,
      cycleWitness: true,
      search: true
    },
    completeness: 'completeWithinScope',
    coverage: {
      discovered: 2,
      loaded: 2,
      analyzed: 2,
      failed: 0,
      skipped: 0,
      unresolved: 0
    },
    projects: [
      {
        id: IDS.projectA,
        variantId: IDS.variantA,
        name: 'App',
        relativePath: 'src/App/App.csproj',
        groupPath: 'src',
        kind: 'app',
        targetFramework: 'net10.0',
        configuration: 'Debug',
        platform: null,
        packageReferences: [],
        loadState: 'loaded',
        limitations: []
      },
      {
        id: IDS.projectB,
        variantId: IDS.variantB,
        name: 'Core',
        relativePath: 'src/Core/Core.csproj',
        groupPath: 'src',
        kind: 'library',
        targetFramework: 'net10.0',
        configuration: 'Debug',
        platform: null,
        packageReferences: [],
        loadState: 'loaded',
        limitations: []
      }
    ],
    namespaces: [
      {
        id: IDS.namespaceA,
        projectVariantId: IDS.variantA,
        name: 'App',
        typeCount: 1,
        representativeDocumentId: IDS.document
      },
      {
        id: IDS.namespaceB,
        projectVariantId: IDS.variantB,
        name: 'Core',
        typeCount: 1,
        representativeDocumentId: IDS.document
      }
    ],
    types: [
      {
        id: IDS.typeA,
        projectVariantId: IDS.variantA,
        namespaceId: IDS.namespaceA,
        name: 'Program',
        fullName: 'App.Program',
        documentationId: 'T:App.Program',
        kind: 'class',
        accessibility: 'internal',
        isPartial: false,
        declarationCount: 1,
        memberCount: 1,
        isExternal: false
      },
      {
        id: IDS.typeB,
        projectVariantId: IDS.variantB,
        namespaceId: IDS.namespaceB,
        name: 'Order',
        fullName: 'Core.Order',
        documentationId: 'T:Core.Order',
        kind: 'class',
        accessibility: 'public',
        isPartial: false,
        declarationCount: 1,
        memberCount: 2,
        isExternal: false
      }
    ],
    relations: [
      {
        id: IDS.relation,
        sourceEntityId: IDS.typeA,
        targetEntityId: IDS.typeB,
        basis: 'symbolResolved',
        kinds: ['constructs'],
        evidenceCount: 1,
        distinctSourceMemberCount: 1,
        distinctSourceDocumentCount: 1,
        generatedEvidenceCount: 0,
        publicSurfaceEvidenceCount: 0,
        confidence: 'resolved'
      }
    ],
    cycleGroups: [],
    diagnostics: [],
    evidenceIndex: {
      format: 'ndjson',
      fileName: 'evidence.ndjson',
      byteLength: 512,
      relations: [{ relationId: IDS.relation, startByte: 0, count: 1 }]
    },
    sourceManifest: [
      {
        id: IDS.document,
        relativePath: 'src/App/Program.cs',
        origin: 'userSource',
        contentHash: 'sha256-abc',
        byteLength: 256
      }
    ],
    limitations: []
  };

  return { ...base, ...overrides };
}

export function makeEvidence(overrides: Partial<EvidenceRecord> = {}): EvidenceRecord {
  const base: EvidenceRecord = {
    id: IDS.evidence,
    relationId: IDS.relation,
    sourceEntityId: IDS.typeA,
    targetEntityId: IDS.typeB,
    sourceTypeId: IDS.typeA,
    sourceMemberId: null,
    targetTypeId: IDS.typeB,
    targetMemberId: null,
    kind: 'constructs',
    origin: 'userSource',
    documentId: IDS.document,
    physicalSpan: SPAN,
    mappedLocation: null,
    sourceContentHash: 'sha256-abc',
    confidence: 'resolved',
    publicSurface: false
  };

  return { ...base, ...overrides };
}

export function makeRelation(
  basis: RelationBasis,
  kind: RelationKind,
  sourceEntityId: string,
  targetEntityId: string,
  id: string
) {
  return {
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
    confidence: 'inferred' as const
  };
}
