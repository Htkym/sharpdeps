// TypeScript contract for the v2 analysis model (report v2).
//
// The normative definition is schemas/report-v2.schema.json; these interfaces
// mirror it for compile-time use. `src/analyzer/types.ts` still holds the legacy
// v1 report shape read by the adapter in `legacyAdapter.ts`.

export type AnalysisMode = 'quick' | 'semantic';

export type Completeness = 'completeWithinScope' | 'partial' | 'failed';

export type RelationBasis =
  'projectDeclared' | 'projectEvaluated' | 'usingInferred' | 'symbolResolved';

export type UsageRelationKind =
  | 'inherits'
  | 'implements'
  | 'signature'
  | 'constraint'
  | 'constructs'
  | 'calls'
  | 'memberAccess'
  | 'attribute'
  | 'typeUse'
  | 'compileTimeName';

/**
 * Declaration-level kinds. They describe a declared reference rather than a
 * resolved usage and are only produced with the matching basis.
 */
export type DeclaredRelationKind = 'projectDeclared' | 'projectEvaluated' | 'usingInferred';

export type RelationKind = UsageRelationKind | DeclaredRelationKind;

/** Evidence kinds match relation kinds; evidence is never more precise than its relation. */
export type EvidenceKind = RelationKind;

export type EvidenceOrigin = 'userSource' | 'generatedSource';

export type Confidence = 'resolved' | 'inferred';

export type Granularity = 'project' | 'namespace' | 'type';

export interface TargetDescriptor {
  kind: 'solution' | 'slnx' | 'project';
  rootId: string;
  relativePath: string;
}

export interface ProjectVariant {
  variantId: string;
  projectLogicalId: string;
  targetFramework: string;
  configuration: string;
  platform?: string | null;
  targetFrameworkSource: 'targetFramework' | 'targetFrameworks' | 'inferred' | 'notSpecified';
}

export interface AnalysisProfile {
  configuration: string;
  platform?: string | null;
  projectVariants: ProjectVariant[];
  profileHash: string;
}

export interface AnalysisCapabilities {
  typeGraph: boolean;
  evidence: boolean;
  generatedDocuments: boolean;
  cycleWitness: boolean;
  search: boolean;
}

export interface AnalysisCoverage {
  discovered: number;
  loaded: number;
  analyzed: number;
  failed: number;
  skipped: number;
  unresolved: number;
}

export interface Limitation {
  code: string;
  message: string;
  scope?: string | null;
  count?: number | null;
}

export interface AnalysisProject {
  id: string;
  variantId: string;
  name: string;
  relativePath: string;
  groupPath: string;
  kind: 'app' | 'web' | 'library' | 'test' | 'desktop' | 'native' | 'unknown';
  targetFramework: string;
  configuration?: string;
  platform?: string | null;
  packageReferences?: string[];
  loadState: 'loaded' | 'failed' | 'skipped';
  limitations?: Limitation[];
}

export interface AnalysisNamespace {
  id: string;
  projectVariantId: string;
  name: string;
  typeCount: number;
  representativeDocumentId?: string | null;
}

export interface AnalysisType {
  id: string;
  projectVariantId: string;
  namespaceId?: string | null;
  name: string;
  fullName: string;
  documentationId?: string | null;
  kind: 'class' | 'struct' | 'interface' | 'enum' | 'delegate' | 'record' | 'unknown';
  accessibility: 'public' | 'internal' | 'protected' | 'private' | 'file' | 'unknown';
  isPartial: boolean;
  declarationCount: number;
  memberCount?: number;
  isExternal?: boolean;
}

export interface AnalysisRelation {
  id: string;
  sourceEntityId: string;
  targetEntityId: string;
  basis: RelationBasis;
  kinds: RelationKind[];
  evidenceCount: number;
  distinctSourceMemberCount: number;
  distinctSourceDocumentCount: number;
  generatedEvidenceCount: number;
  publicSurfaceEvidenceCount: number;
  confidence?: Confidence;
}

export interface CycleWitness {
  memberIds: string[];
  relationIds: string[];
}

export interface CycleGroup {
  id: string;
  scope: 'type' | 'namespace' | 'project';
  basis: RelationBasis;
  memberIds: string[];
  internalRelationIds: string[];
  witness?: CycleWitness | null;
  truncated?: boolean;
}

export interface AnalysisDiagnostic {
  id: string;
  severity: 'error' | 'warning' | 'info';
  code: string;
  message: string;
  targetId?: string | null;
  evidenceId?: string | null;
  analysisId?: string;
}

export interface EvidenceIndexEntry {
  relationId: string;
  startByte: number;
  count: number;
}

export interface EvidenceIndex {
  format: 'ndjson';
  fileName: string;
  byteLength: number;
  relations: EvidenceIndexEntry[];
}
export interface SourceDocument {
  id: string;
  relativePath: string;
  origin: EvidenceOrigin;
  contentHash: string;
  byteLength?: number;
  mappedFromDocumentId?: string | null;
}

export interface EvidenceRecord {
  id: string;
  relationId: string;
  sourceEntityId: string;
  targetEntityId: string;
  sourceTypeId?: string | null;
  sourceMemberId?: string | null;
  targetTypeId?: string | null;
  targetMemberId?: string | null;
  kind: EvidenceKind;
  origin: EvidenceOrigin;
  documentId: string;
  physicalSpan?: PhysicalSpan | null;
  mappedLocation?: MappedLocation | null;
  sourceContentHash: string;
  confidence: Confidence;
  publicSurface: boolean;
  snippet?: string | null;
}

export interface PhysicalSpan {
  start: number;
  length: number;
  startLine: number;
  startCharacter: number;
  endLine: number;
  endCharacter: number;
}

export interface MappedLocation {
  relativePath: string;
  line: number;
  character: number;
}

/** Canonical analysis model. Renderings (SVG, Mermaid, tables) are derived from it. */
export interface AnalysisSnapshot {
  schemaVersion: 2;
  analyzerVersion: string;
  analysisId: string;
  createdAt: string;
  target: TargetDescriptor;
  mode: AnalysisMode;
  profile: AnalysisProfile;
  capabilities: AnalysisCapabilities;
  completeness: Completeness;
  coverage: AnalysisCoverage;
  projects: AnalysisProject[];
  namespaces: AnalysisNamespace[];
  types: AnalysisType[];
  relations: AnalysisRelation[];
  cycleGroups: CycleGroup[];
  diagnostics: AnalysisDiagnostic[];
  /**
   * Index into the evidence NDJSON side file. Null when the producer keeps
   * evidence in memory (the legacy adapter) or cannot provide evidence.
   */
  evidenceIndex: EvidenceIndex | null;
  sourceManifest: SourceDocument[];
  limitations: Limitation[];
}
