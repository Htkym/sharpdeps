// Webview protocol v2 (SD-002, plan section 5.5).
//
// The normative definition is schemas/protocol-v2.schema.json. This module holds
// the compile-time shapes plus the runtime validation the receivers use:
// unknown message types are rejected, ids and cursors are pattern-checked, and
// `protocolVersion` is only accepted on the negotiation messages.
//
// The webview never sends file paths or byte offsets: it sends opaque ids and
// cursors that the host resolves against the registered analysis result.

import type { Granularity, RelationKind } from '../analyzer/reportV2';

export const PROTOCOL_VERSION = 2;

export const SAVED_UPDATE_PHASES = [
  'idle',
  'selecting',
  'updating',
  'cancelling',
  'cancelled',
  'failed'
] as const;
export type SavedUpdatePhase = (typeof SAVED_UPDATE_PHASES)[number];

export type ExportFormat = 'mermaid' | 'svg' | 'png' | 'json';

export interface Scope {
  kind: 'root' | 'project' | 'namespace' | 'type' | 'dependencies' | 'dependents' | 'cycle';
  id?: string | null;
  depth?: number | null;
}

export interface Filters {
  /** Saved Query display classification; independent of legacy code kinds. */
  resultKinds?: ('code' | 'docs')[];
  kinds?: string[];
  projectKinds?: string[];
  relationKinds?: RelationKind[];
  basis?: string[];
  includeGenerated?: boolean;
  includeExternal?: boolean;
  includeTests?: boolean;
}

export interface ProfileRequest {
  configuration?: string;
  platform?: string | null;
  projectVariants?: { projectLogicalId: string; targetFramework: string }[];
}

export interface Capabilities {
  typeGraph: boolean;
  evidence: boolean;
  generatedDocuments: boolean;
  cycleWitness: boolean;
  search: boolean;
}

export interface Coverage {
  discovered: number;
  loaded: number;
  analyzed: number;
  failed: number;
  skipped: number;
  unresolved: number;
}

export interface EntitySummary {
  id: string;
  name: string;
  granularity: Granularity;
  kind?: string;
  projectName?: string;
  inCycle?: boolean;
  isExternal?: boolean;
  isGenerated?: boolean;
  fullName?: string;
  projectId?: string;
  projectPath?: string;
  projectKind?: string;
  namespaceId?: string;
  namespaceName?: string;
  targetFramework?: string;
  analysisStatus?: 'complete' | 'partial' | 'failed' | 'skipped';
  analysisLimitations?: string[];
  dependencyCount?: number;
  dependentCount?: number;
  certainty?: 'Resolved' | 'Candidate' | 'Unresolved';
  /** Display-only snapshot attributes; source requests still carry only opaque IDs. */
  path?: string;
  reason?: string;
  resultKind?: 'code' | 'docs' | 'other';
  sourceLocationAvailable?: boolean;
}

/** Saved Query bounds describe the returned page, never a complete legacy report. */
export interface QueryResultMetadata {
  provider: 'savedIndex';
  workspaceId: string;
  snapshotId: string;
  generation: number;
  variantIds: string[];
  coverage: 'CompleteWithinScope' | 'Partial' | 'Failed';
  freshness: string;
  truncated: boolean;
  truncationReasons: string[];
  diagnostics: { code: string; message: string }[];
  returnedCount: number;
  totalKind: 'returned';
  candidateCount: number;
  unresolvedCount: number;
  resultFilterVersion?: number;
}

/** An issued candidate relation action; independent of declaration availability. */
export interface CandidateOriginAction {
  originId: string;
  relationId: string;
  targetEntityId: string;
  targetName: string;
}

/** One provider-owned, returned relation; metadata on its reply owns the saved pin. */
export interface SavedRelation {
  id: string;
  sourceId: string;
  targetId: string;
  sourceName?: string;
  targetName?: string;
  kind: string;
  certainty: 'Resolved' | 'Candidate' | 'Unresolved';
  evidenceCount: number;
  sourceOccurrenceId?: string | null;
  targetOccurrenceId?: string | null;
  variantId?: string | null;
}

/** Groups describe the Impact traversal direction, not a complete incident-edge set. */
export interface SavedRelations {
  outgoing: SavedRelation[];
  incoming: SavedRelation[];
}

/** Display attributes of one exact occurrence; only capabilityId authorizes source lookup. */
export interface CandidateDeclarationSummary {
  capabilityId: string;
  occurrenceId: string;
  variantId: string;
  path: string;
}

export interface ProjectionEdge {
  id: string;
  sourceId: string;
  targetId: string;
  basis: string;
  kinds: string[];
  evidenceCount: number;
  inCycle: boolean;
  certainty?: 'Resolved' | 'Candidate' | 'Unresolved';
  sourceOccurrenceId?: string | null;
  targetOccurrenceId?: string | null;
  variantId?: string | null;
  generatedEvidenceCount?: number;
  publicSurfaceEvidenceCount?: number;
  /**
   * Relations this edge is derived from. A coarse-granularity edge aggregates several
   * type-level relations, and each can be paged for evidence.
   */
  underlyingRelationIds?: string[];
  /** Per-relation detail of an aggregated edge, representative first. */
  underlyingRelations?: Array<{
    id: string;
    basis: string;
    kinds: string[];
    evidenceCount: number;
    confidence?: string | null;
  }>;
}

export interface ProjectionCycleGroup {
  id: string;
  scope: string;
  basis: string;
  /** Members as a set: the order is not a path. */
  memberIds: string[];
  internalRelationIds: string[];
  /** The cycle the analyzer verified; the order of these ids is a real path. */
  witness: { memberIds: string[]; relationIds: string[] } | null;
  truncated?: boolean;
}

export interface Projection {
  scope: Scope;
  granularity: Granularity;
  nodes: EntitySummary[];
  edges: ProjectionEdge[];
  totalNodeCount: number;
  totalEdgeCount: number;
  truncated: boolean;
  queryMetadata?: QueryResultMetadata;
  includedOutOfFilterIds?: string[];
  /**
   * SCC groups and their verified cycles, sent with the projection so the cycle view has
   * both the member set and the real path in one round trip.
   */
  cycleGroups?: ProjectionCycleGroup[];
}

export interface AnalysisProgress {
  analysisId: string;
  stage: 'discover' | 'load' | 'compile' | 'extract' | 'aggregate' | 'write';
  discovered?: number;
  loaded?: number;
  analyzed?: number;
  elapsedMs?: number;
  message?: string;
}

// Webview -> extension host
export type WebviewToHostMessage =
  | { type: 'ready'; protocolVersion: number; webviewVersion?: string }
  | {
      type: 'analyze';
      requestId: string;
      mode: 'quick' | 'semantic';
      targetId?: string | null;
      profile?: ProfileRequest;
    }
  | { type: 'cancelAnalysis'; requestId: string; analysisId: string }
  | { type: 'updateSavedIndex'; requestId: string; analysisId: string }
  | {
      type: 'getProjection';
      requestId: string;
      analysisId: string;
      scope: Scope;
      granularity: Granularity;
      filters?: Filters;
      search?: string;
      includeIds?: string[];
    }
  | {
      type: 'searchEntities';
      requestId: string;
      analysisId: string;
      query: string;
      parentId?: string;
      granularity?: Granularity;
      filters?: Filters;
      limit?: number;
      cursor?: string;
    }
  | {
      type: 'getEntityDetails';
      requestId: string;
      analysisId: string;
      entityId: string;
      filters?: Filters;
    }
  | {
      type: 'getEvidencePage';
      requestId: string;
      analysisId: string;
      relationId: string;
      limit?: number;
      cursor?: string;
    }
  | { type: 'getCycleWitness'; requestId: string; analysisId: string; cycleGroupId: string }
  | { type: 'sourceReturnDismissed'; analysisId: string }
  | { type: 'investigateCandidate'; requestId: string; analysisId: string; originId: string }
  | {
      type: 'dismissCandidateInvestigation';
      requestId: string;
      analysisId: string;
      originId: string;
    }
  | {
      type: 'openCandidateDeclaration';
      requestId: string;
      analysisId: string;
      originId: string;
      capabilityId: string;
      returnUi?: SourceReturnUi;
    }
  | {
      type: 'openEvidence';
      requestId: string;
      analysisId: string;
      evidenceId: string;
      returnUi?: SourceReturnUi;
    }
  | {
      type: 'openDeclaration';
      requestId: string;
      analysisId: string;
      entityId: string;
      declarationIndex?: number;
      returnUi?: SourceReturnUi;
    }
  | {
      type: 'copyContext';
      requestId: string;
      analysisId: string;
      scope: Scope;
      /** Saved exports must identify the projection received by the webview. */
      projectionRequestId?: string;
      includeSnippets?: boolean;
      granularity?: Granularity;
      filters?: Filters;
      search?: string;
      includeIds?: string[];
    }
  | {
      type: 'export';
      requestId: string;
      analysisId: string;
      format: ExportFormat;
      copy?: boolean;
      scope: Scope;
      projectionRequestId?: string;
      /** Rendered SVG text or PNG data URL, when the webview produced it (SD-022). */
      data?: string;
      granularity?: Granularity;
      filters?: Filters;
      search?: string;
      includeIds?: string[];
    }
  | { type: 'persistViewState'; viewState: Record<string, unknown> };

// Extension host -> webview
export type HostToWebviewMessage =
  | {
      type: 'sourceReturn';
      requestId: string;
      analysisId: string;
      state: Record<string, unknown>;
      ui: SourceReturnUi;
    }
  | {
      type: 'capabilities';
      protocolVersion: number;
      capabilities: Capabilities;
      analysisId?: string | null;
    }
  | ({ type: 'analysisProgress' } & AnalysisProgress)
  | {
      type: 'analysisStarted';
      analysisId: string;
      mode: 'quick' | 'semantic';
      target: { name: string; relativePath: string };
    }
  | { type: 'requestExport'; format: ExportFormat; copy?: boolean }
  | {
      type: 'analysisComplete';
      analysisId: string;
      completeness: 'completeWithinScope' | 'partial' | 'failed';
      coverage: Coverage;
      mode?: 'quick' | 'semantic';
      limitations?: unknown[];
      capabilities?: Capabilities;
      target?: { name: string; relativePath: string };
      profile?: ProfileRequest;
      variantOptions?: { projectLogicalId: string; targetFramework: string; projectPath: string }[];
      queryMetadata?: QueryResultMetadata;
    }
  | {
      type: 'analysisFailed';
      analysisId: string;
      message: string;
      detail?: string;
      cancelled?: boolean;
    }
  | {
      type: 'savedUpdateState';
      analysisId: string;
      phase: SavedUpdatePhase;
      canUpdate: boolean;
    }
  | { type: 'projection'; requestId: string; analysisId: string; projection: Projection }
  | {
      /** Editor-driven reveal (SD-019): select an entity, optionally in a new scope. */
      type: 'reveal';
      analysisId: string;
      entityId: string;
      scope?: Scope;
      granularity?: Granularity;
    }
  | {
      /** Restored small view state from workspace storage (SD-021). */
      type: 'viewState';
      state: Record<string, unknown>;
    }
  | {
      type: 'searchResults';
      requestId: string;
      analysisId: string;
      total: number;
      items: EntitySummary[];
      nextCursor?: string | null;
      queryMetadata?: QueryResultMetadata;
    }
  | {
      type: 'details';
      requestId: string;
      analysisId: string;
      entity: Record<string, unknown>;
      dependencies?: EntitySummary[];
      dependents?: EntitySummary[];
      queryMetadata?: QueryResultMetadata;
      candidateOrigins?: CandidateOriginAction[];
      relations?: SavedRelations;
    }
  | {
      type: 'candidateInvestigation';
      requestId: string;
      analysisId: string;
      originId: string;
      relationId: string;
      targetEntityId: string;
      certainty: 'Candidate';
      relationKind: string;
      reason: string;
      message: string;
      queryMetadata: QueryResultMetadata;
      declarations: CandidateDeclarationSummary[];
      unavailableReason?: string;
    }
  | {
      type: 'evidencePage';
      requestId: string;
      analysisId: string;
      relationId: string;
      total: number;
      items: Record<string, unknown>[];
      nextCursor?: string | null;
      queryMetadata?: QueryResultMetadata;
      relation?: SavedRelation;
    }
  | {
      type: 'cycleWitness';
      requestId: string;
      analysisId: string;
      cycleGroupId: string;
      witness: Record<string, unknown> | null;
    }
  | {
      type: 'stale';
      analysisId: string;
      reason: 'unsavedChange' | 'savedChange' | 'profileChange' | 'unknown';
      relativePaths?: string[];
    }
  | {
      type: 'error';
      code: string;
      message: string;
      requestId?: string | null;
      analysisId?: string | null;
    };

export interface ProtocolValidationFailure {
  ok: false;
  code: 'unknownType' | 'invalidMessage';
  errors: string[];
}

export type ProtocolValidationResult<T> = { ok: true; value: T } | ProtocolValidationFailure;

/** Query text is echoed back to the UI; it must not be an unbounded payload. */
export const MAX_QUERY_LENGTH = 200;

/** One transient source-button anchor; never a path or an arbitrary DOM selector. */
export interface SourceReturnUi {
  navTab: 'structure' | 'cycles' | 'analysis';
  focus: 'declaration' | 'evidence';
  id: string;
  scroll: { navigation: number; inspector: number; map: number };
  searchCount?: number;
}

const PATTERNS = {
  requestId: /^req_[0-9a-f]{16}$/,
  analysisId: /^an_[0-9a-f]{16}$/,
  cursor: /^cur_[0-9a-f]{16}$/,
  entityId: /^(prj|var|ns|ty|mb)_[0-9a-f]{16}$/,
  relationId: /^rel_[0-9a-f]{16}$/,
  evidenceId: /^ev_[0-9a-f]{16}$/,
  candidateOriginId: /^ci_[0-9a-f]{32}$/,
  candidateDeclarationId: /^cd_[0-9a-f]{32}$/,
  candidateTargetId: /^ty_[0-9a-f]{16}$/,
  occurrenceId: /^hocc_[0-9a-f]{64}$/,
  variantId: /^hvar_[0-9a-f]{64}$/,
  cycleGroupId: /^cyc_[0-9a-f]{16}$/
} as const;

export function readSavedRelation(value: unknown): SavedRelation | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const item = value as Record<string, unknown>;
  if (
    typeof item.id !== 'string' ||
    !PATTERNS.relationId.test(item.id) ||
    typeof item.sourceId !== 'string' ||
    !PATTERNS.entityId.test(item.sourceId) ||
    typeof item.targetId !== 'string' ||
    !PATTERNS.entityId.test(item.targetId) ||
    typeof item.kind !== 'string' ||
    !item.kind.length ||
    item.kind.length > 128 ||
    (item.certainty !== 'Resolved' &&
      item.certainty !== 'Candidate' &&
      item.certainty !== 'Unresolved') ||
    (item.evidenceCount !== 0 && item.evidenceCount !== 1)
  )
    return undefined;
  for (const field of ['sourceName', 'targetName'])
    if (item[field] !== undefined && typeof item[field] !== 'string') return undefined;
  for (const field of ['sourceOccurrenceId', 'targetOccurrenceId', 'variantId']) {
    const value = item[field];
    if (value !== undefined && value !== null && (typeof value !== 'string' || value.length > 256))
      return undefined;
  }
  const nullableText = (field: string) => {
    const value = item[field];
    return value === null ? null : typeof value === 'string' ? value : undefined;
  };
  return {
    id: item.id,
    sourceId: item.sourceId,
    targetId: item.targetId,
    sourceName: typeof item.sourceName === 'string' ? item.sourceName : undefined,
    targetName: typeof item.targetName === 'string' ? item.targetName : undefined,
    kind: item.kind,
    certainty: item.certainty,
    evidenceCount: item.evidenceCount,
    sourceOccurrenceId: nullableText('sourceOccurrenceId'),
    targetOccurrenceId: nullableText('targetOccurrenceId'),
    variantId: nullableText('variantId')
  };
}

export function readSavedRelations(value: unknown): SavedRelations | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const groups = value as Record<string, unknown>;
  if (
    !Array.isArray(groups.outgoing) ||
    !Array.isArray(groups.incoming) ||
    groups.outgoing.length > 1000 ||
    groups.incoming.length > 1000
  )
    return undefined;
  const outgoing = groups.outgoing.map(readSavedRelation);
  const incoming = groups.incoming.map(readSavedRelation);
  if (outgoing.some((item) => !item) || incoming.some((item) => !item)) return undefined;
  return {
    outgoing: outgoing.filter((item): item is SavedRelation => !!item),
    incoming: incoming.filter((item): item is SavedRelation => !!item)
  };
}

type FieldKind =
  | keyof typeof PATTERNS
  | 'string'
  | 'object'
  | 'array'
  | 'entityIds'
  | 'boolean'
  | 'positiveInteger'
  | 'granularity'
  | 'scope'
  | 'filters'
  | 'profile'
  | 'exportFormat'
  | 'mode'
  | 'savedUpdatePhase'
  | 'viewState'
  | 'sourceReturnUi'
  | 'candidateOrigins'
  | 'candidateDeclarations'
  | 'candidateCertainty'
  | 'candidateQueryMetadata'
  | 'candidateText'
  | 'candidateRelationKind'
  | 'savedRelation'
  | 'savedRelations';

interface MessageRule {
  required: Record<string, FieldKind>;
  optional: Record<string, FieldKind>;
}

const MODES = ['quick', 'semantic'] as const;
const GRANULARITIES = ['project', 'namespace', 'type'] as const;
const EXPORT_FORMATS = ['mermaid', 'svg', 'png', 'json'] as const;
const SCOPE_KINDS = [
  'root',
  'project',
  'namespace',
  'type',
  'dependencies',
  'dependents',
  'cycle'
] as const;

const WEBVIEW_RULES: Record<string, MessageRule> = {
  ready: {
    required: {},
    optional: { protocolVersion: 'positiveInteger', webviewVersion: 'string' }
  },
  analyze: {
    required: { requestId: 'requestId', mode: 'mode' },
    optional: { targetId: 'string', profile: 'profile' }
  },
  cancelAnalysis: { required: { requestId: 'requestId', analysisId: 'analysisId' }, optional: {} },
  updateSavedIndex: {
    required: { requestId: 'requestId', analysisId: 'analysisId' },
    optional: {}
  },
  getProjection: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      scope: 'scope',
      granularity: 'granularity'
    },
    optional: { filters: 'filters', search: 'string', includeIds: 'entityIds' }
  },
  searchEntities: {
    required: { requestId: 'requestId', analysisId: 'analysisId', query: 'string' },
    optional: {
      parentId: 'entityId',
      granularity: 'granularity',
      filters: 'filters',
      limit: 'positiveInteger',
      cursor: 'cursor'
    }
  },
  getEntityDetails: {
    required: { requestId: 'requestId', analysisId: 'analysisId', entityId: 'entityId' },
    optional: { filters: 'filters' }
  },
  getEvidencePage: {
    required: { requestId: 'requestId', analysisId: 'analysisId', relationId: 'relationId' },
    optional: { limit: 'positiveInteger', cursor: 'cursor' }
  },
  getCycleWitness: {
    required: { requestId: 'requestId', analysisId: 'analysisId', cycleGroupId: 'cycleGroupId' },
    optional: {}
  },
  openEvidence: {
    required: { requestId: 'requestId', analysisId: 'analysisId', evidenceId: 'evidenceId' },
    optional: { returnUi: 'sourceReturnUi' }
  },
  sourceReturnDismissed: {
    required: { analysisId: 'analysisId' },
    optional: {}
  },
  investigateCandidate: {
    required: { requestId: 'requestId', analysisId: 'analysisId', originId: 'candidateOriginId' },
    optional: {}
  },
  dismissCandidateInvestigation: {
    required: { requestId: 'requestId', analysisId: 'analysisId', originId: 'candidateOriginId' },
    optional: {}
  },
  openCandidateDeclaration: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      originId: 'candidateOriginId',
      capabilityId: 'candidateDeclarationId'
    },
    optional: { returnUi: 'sourceReturnUi' }
  },
  openDeclaration: {
    required: { requestId: 'requestId', analysisId: 'analysisId', entityId: 'entityId' },
    optional: { declarationIndex: 'positiveInteger', returnUi: 'sourceReturnUi' }
  },
  copyContext: {
    required: { requestId: 'requestId', analysisId: 'analysisId', scope: 'scope' },
    optional: {
      projectionRequestId: 'requestId',
      includeSnippets: 'boolean',
      granularity: 'granularity',
      filters: 'filters',
      search: 'string',
      includeIds: 'entityIds'
    }
  },
  export: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      format: 'exportFormat',
      scope: 'scope'
    },
    optional: {
      projectionRequestId: 'requestId',
      data: 'string',
      copy: 'boolean',
      granularity: 'granularity',
      filters: 'filters',
      search: 'string',
      includeIds: 'entityIds'
    }
  },
  persistViewState: { required: { viewState: 'viewState' }, optional: {} }
};

const HOST_RULES: Record<string, MessageRule> = {
  savedUpdateState: {
    required: { analysisId: 'analysisId', phase: 'savedUpdatePhase', canUpdate: 'boolean' },
    optional: {}
  },
  analysisStarted: {
    required: { analysisId: 'analysisId', mode: 'mode', target: 'object' },
    optional: {}
  },
  requestExport: { required: { format: 'exportFormat' }, optional: { copy: 'boolean' } },
  capabilities: {
    required: { protocolVersion: 'positiveInteger', capabilities: 'object' },
    optional: { analysisId: 'analysisId' }
  },
  reveal: {
    required: { analysisId: 'analysisId', entityId: 'entityId' },
    optional: { scope: 'scope', granularity: 'granularity' }
  },
  viewState: {
    required: { state: 'object' },
    optional: {}
  },
  sourceReturn: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      state: 'viewState',
      ui: 'sourceReturnUi'
    },
    optional: {}
  },
  analysisProgress: {
    required: { analysisId: 'analysisId', stage: 'string' },
    optional: {
      discovered: 'positiveInteger',
      loaded: 'positiveInteger',
      analyzed: 'positiveInteger',
      elapsedMs: 'positiveInteger',
      message: 'string'
    }
  },
  analysisComplete: {
    required: { analysisId: 'analysisId', completeness: 'string', coverage: 'object' },
    optional: {
      mode: 'mode',
      limitations: 'array',
      capabilities: 'object',
      target: 'object',
      profile: 'profile',
      variantOptions: 'array',
      queryMetadata: 'object'
    }
  },
  analysisFailed: {
    required: { analysisId: 'analysisId', message: 'string' },
    optional: { detail: 'string', cancelled: 'boolean' }
  },
  projection: {
    required: { requestId: 'requestId', analysisId: 'analysisId', projection: 'object' },
    optional: {}
  },
  searchResults: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      total: 'positiveInteger',
      items: 'array'
    },
    optional: { nextCursor: 'cursor', queryMetadata: 'object' }
  },
  details: {
    required: { requestId: 'requestId', analysisId: 'analysisId', entity: 'object' },
    optional: {
      dependencies: 'array',
      dependents: 'array',
      queryMetadata: 'object',
      candidateOrigins: 'candidateOrigins',
      relations: 'savedRelations'
    }
  },
  candidateInvestigation: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      originId: 'candidateOriginId',
      relationId: 'relationId',
      targetEntityId: 'candidateTargetId',
      certainty: 'candidateCertainty',
      relationKind: 'candidateRelationKind',
      reason: 'candidateText',
      message: 'candidateText',
      queryMetadata: 'candidateQueryMetadata',
      declarations: 'candidateDeclarations'
    },
    optional: { unavailableReason: 'candidateText' }
  },
  evidencePage: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      relationId: 'relationId',
      total: 'positiveInteger',
      items: 'array'
    },
    optional: { nextCursor: 'cursor', queryMetadata: 'object', relation: 'savedRelation' }
  },
  cycleWitness: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      cycleGroupId: 'cycleGroupId',
      witness: 'object'
    },
    optional: {}
  },
  stale: {
    required: { analysisId: 'analysisId', reason: 'string' },
    optional: { relativePaths: 'array' }
  },
  error: {
    required: { code: 'string', message: 'string' },
    optional: { requestId: 'string', analysisId: 'analysisId' }
  }
};

/** Validates a message received by the extension host. */
export function validateWebviewMessage(
  input: unknown
): ProtocolValidationResult<WebviewToHostMessage> {
  const result = validateMessage<WebviewToHostMessage>(input, WEBVIEW_RULES, 'webview');
  if (!result.ok) {
    return result;
  }

  // Search text is echoed back to the UI and the host: keep it bounded (SD-023).
  if (result.value.type === 'searchEntities' && result.value.query.length > MAX_QUERY_LENGTH) {
    return {
      ok: false,
      code: 'invalidMessage',
      errors: [`$.query: longer than ${MAX_QUERY_LENGTH} characters`]
    };
  }
  if (
    'search' in result.value &&
    typeof result.value.search === 'string' &&
    result.value.search.length > MAX_QUERY_LENGTH
  )
    return {
      ok: false,
      code: 'invalidMessage',
      errors: [`$.search: longer than ${MAX_QUERY_LENGTH} characters`]
    };

  return result;
}

/** Validates a message received by the webview. */
export function validateHostMessage(
  input: unknown
): ProtocolValidationResult<HostToWebviewMessage> {
  return validateMessage(input, HOST_RULES, 'host');
}

function validateMessage<T>(
  input: unknown,
  rules: Record<string, MessageRule>,
  sender: string
): ProtocolValidationResult<T> {
  if (typeof input !== 'object' || input === null || Array.isArray(input)) {
    return { ok: false, code: 'invalidMessage', errors: ['$: expected an object'] };
  }
  const message = input as Record<string, unknown>;
  const type = message.type;
  if (typeof type !== 'string') {
    return { ok: false, code: 'invalidMessage', errors: ['$.type: expected a string'] };
  }
  const rule = rules[type];
  if (!rule) {
    return {
      ok: false,
      code: 'unknownType',
      errors: [`$.type: unknown ${sender} message type '${type}'`]
    };
  }

  const errors: string[] = [];
  for (const [field, kind] of Object.entries(rule.required)) {
    if (!checkField(message[field], kind, `$.${field}`, errors, true)) {
      continue;
    }
  }
  for (const [field, kind] of Object.entries(rule.optional)) {
    checkField(message[field], kind, `$.${field}`, errors, false);
  }

  return errors.length === 0
    ? { ok: true, value: input as T }
    : { ok: false, code: 'invalidMessage', errors };
}

function checkField(
  value: unknown,
  kind: FieldKind,
  path: string,
  errors: string[],
  required: boolean
): boolean {
  if (value === null && (kind === 'savedRelation' || kind === 'savedRelations')) {
    errors.push(`${path}: expected saved relation data, not null`);
    return false;
  }
  if (value === null && (kind === 'candidateOrigins' || kind === 'candidateText')) {
    errors.push(`${path}: expected a candidate field, not null`);
    return false;
  }
  if (value === undefined || value === null) {
    if (required) {
      errors.push(`${path}: missing required field`);
    }
    return !required;
  }

  const fail = (message: string): false => {
    errors.push(`${path}: ${message}`);
    return false;
  };

  switch (kind) {
    case 'savedRelation':
      return !!readSavedRelation(value) || fail('expected a bounded saved relation');
    case 'savedRelations':
      return !!readSavedRelations(value) || fail('expected bounded saved relation groups');
    case 'candidateOriginId':
    case 'candidateDeclarationId':
    case 'candidateTargetId':
    case 'occurrenceId':
    case 'variantId':
      return typeof value === 'string' && PATTERNS[kind].test(value)
        ? true
        : fail(`value does not match ${PATTERNS[kind]}`);
    case 'candidateText':
      return typeof value === 'string' && value.length <= 4096
        ? true
        : fail('expected at most 4096 characters');
    case 'candidateRelationKind':
      return typeof value === 'string' && value.length >= 1 && value.length <= 128
        ? true
        : fail('expected a nonempty relation kind of at most 128 characters');
    case 'candidateCertainty':
      return value === 'Candidate'
        ? true
        : fail('candidate relation certainty must remain Candidate');
    case 'candidateOrigins':
    case 'candidateDeclarations': {
      const origins = kind === 'candidateOrigins';
      const limit = origins ? 8 : 16;
      if (!Array.isArray(value) || value.length > limit)
        return fail(`expected at most ${limit} candidate entries`);
      const fields: Record<string, RegExp> = origins
        ? {
            originId: PATTERNS.candidateOriginId,
            relationId: PATTERNS.relationId,
            targetEntityId: PATTERNS.candidateTargetId
          }
        : {
            capabilityId: PATTERNS.candidateDeclarationId,
            occurrenceId: PATTERNS.occurrenceId,
            variantId: PATTERNS.variantId
          };
      const textKey = origins ? 'targetName' : 'path';
      return value.every(
        (item) =>
          item &&
          typeof item === 'object' &&
          !Array.isArray(item) &&
          Object.entries(fields).every(
            ([key, pattern]) => typeof item[key] === 'string' && pattern.test(item[key])
          ) &&
          typeof item[textKey] === 'string' &&
          item[textKey].length >= 1 &&
          item[textKey].length <= 4096
      )
        ? true
        : fail('expected candidate entries with exact role IDs and bounded display text');
    }
    case 'candidateQueryMetadata': {
      if (typeof value !== 'object' || Array.isArray(value))
        return fail('expected saved Query metadata');
      const metadata = value as Record<string, unknown>;
      const text = (item: unknown, max: number): item is string =>
        typeof item === 'string' && item.length >= 1 && item.length <= max;
      const count = (item: unknown): boolean =>
        typeof item === 'number' && Number.isSafeInteger(item) && item >= 0;
      return metadata.provider === 'savedIndex' &&
        metadata.totalKind === 'returned' &&
        text(metadata.workspaceId, 4096) &&
        text(metadata.snapshotId, 4096) &&
        count(metadata.generation) &&
        Array.isArray(metadata.variantIds) &&
        metadata.variantIds.length === 1 &&
        typeof metadata.variantIds[0] === 'string' &&
        PATTERNS.variantId.test(metadata.variantIds[0]) &&
        ['CompleteWithinScope', 'Partial', 'Failed'].includes(String(metadata.coverage)) &&
        ['unverified', 'dirty', 'verified-current'].includes(String(metadata.freshness)) &&
        typeof metadata.truncated === 'boolean' &&
        Array.isArray(metadata.truncationReasons) &&
        metadata.truncationReasons.length <= 64 &&
        metadata.truncationReasons.every((reason) => text(reason, 128)) &&
        Array.isArray(metadata.diagnostics) &&
        metadata.diagnostics.length <= 64 &&
        metadata.diagnostics.every(
          (issue) =>
            issue &&
            typeof issue === 'object' &&
            !Array.isArray(issue) &&
            text(issue.code, 128) &&
            text(issue.message, 4096)
        ) &&
        ['returnedCount', 'candidateCount', 'unresolvedCount'].every((key) =>
          count(metadata[key])
        ) &&
        (metadata.resultFilterVersion === undefined ||
          (count(metadata.resultFilterVersion) && (metadata.resultFilterVersion as number) >= 1))
        ? true
        : fail('expected bounded metadata for one saved variant');
    }
    case 'string':
      return typeof value === 'string' ? true : fail('expected a string');
    case 'boolean':
      return typeof value === 'boolean' ? true : fail('expected a boolean');
    case 'positiveInteger':
      return typeof value === 'number' && Number.isInteger(value) && value >= 0
        ? true
        : fail('expected a non-negative integer');
    case 'object':
      return typeof value === 'object' && !Array.isArray(value) ? true : fail('expected an object');
    case 'array':
      return Array.isArray(value) ? true : fail('expected an array');
    case 'entityIds':
      return Array.isArray(value) &&
        value.length <= 1000 &&
        value.every((id) => typeof id === 'string' && PATTERNS.entityId.test(id))
        ? true
        : fail('expected at most 1000 entity ids');
    case 'mode':
      return MODES.includes(value as (typeof MODES)[number])
        ? true
        : fail(`expected one of ${MODES.join(', ')}`);
    case 'savedUpdatePhase':
      return SAVED_UPDATE_PHASES.includes(value as SavedUpdatePhase)
        ? true
        : fail(`expected one of ${SAVED_UPDATE_PHASES.join(', ')}`);
    case 'granularity':
      return GRANULARITIES.includes(value as (typeof GRANULARITIES)[number])
        ? true
        : fail(`expected one of ${GRANULARITIES.join(', ')}`);
    case 'exportFormat':
      return EXPORT_FORMATS.includes(value as (typeof EXPORT_FORMATS)[number])
        ? true
        : fail(`expected one of ${EXPORT_FORMATS.join(', ')}`);
    case 'scope': {
      if (typeof value !== 'object' || Array.isArray(value)) return fail('expected a scope object');
      const scope = value as Record<string, unknown>;
      if (
        typeof scope.kind !== 'string' ||
        !SCOPE_KINDS.includes(scope.kind as (typeof SCOPE_KINDS)[number])
      ) {
        return fail(`scope.kind must be one of ${SCOPE_KINDS.join(', ')}`);
      }
      if (
        scope.id !== undefined &&
        scope.id !== null &&
        !(scope.kind === 'cycle' ? PATTERNS.cycleGroupId : PATTERNS.entityId).test(String(scope.id))
      ) {
        return fail('scope.id must be an entity id');
      }
      if (scope.depth !== undefined && scope.depth !== null) {
        const depth = Number(scope.depth);
        if (!Number.isInteger(depth) || depth < 1 || depth > 3) {
          return fail('scope.depth must be between 1 and 3');
        }
      }
      return true;
    }
    case 'filters': {
      if (typeof value !== 'object' || Array.isArray(value)) return fail('expected an object');
      const filters = value as Record<string, unknown>;
      if (
        filters.resultKinds !== undefined &&
        (!Array.isArray(filters.resultKinds) ||
          filters.resultKinds.length < 1 ||
          filters.resultKinds.length > 2 ||
          filters.resultKinds.some((item) => item !== 'code' && item !== 'docs'))
      )
        return fail('resultKinds must contain code and/or docs');
      for (const key of ['includeGenerated', 'includeExternal', 'includeTests'])
        if (filters[key] !== undefined && typeof filters[key] !== 'boolean')
          return fail(`${key} must be a boolean`);
      for (const key of ['kinds', 'projectKinds', 'relationKinds', 'basis']) {
        const items = filters[key];
        if (
          items !== undefined &&
          (!Array.isArray(items) ||
            items.length > 100 ||
            items.some((item) => typeof item !== 'string' || item.length > 100))
        )
          return fail(`${key} must be an array of at most 100 short strings`);
      }
      return true;
    }
    case 'profile': {
      if (typeof value !== 'object' || Array.isArray(value)) return fail('expected an object');
      const profile = value as Record<string, unknown>;
      for (const key of ['configuration', 'platform'])
        if (
          profile[key] != null &&
          (typeof profile[key] !== 'string' || (profile[key] as string).length > 64)
        )
          return fail(`${key} must be a short string`);
      const variants = profile.projectVariants;
      if (
        variants !== undefined &&
        (!Array.isArray(variants) ||
          variants.length > 10000 ||
          variants.some(
            (variant) =>
              !variant ||
              typeof variant !== 'object' ||
              !/^prj_[0-9a-f]{16}$/.test(variant.projectLogicalId) ||
              typeof variant.targetFramework !== 'string' ||
              !/^[a-zA-Z0-9.+_-]{1,100}$/.test(variant.targetFramework)
          ))
      )
        return fail('projectVariants must contain valid project ids and target frameworks');
      return true;
    }
    case 'viewState':
      return typeof value === 'object' && !Array.isArray(value) ? true : fail('expected an object');
    case 'sourceReturnUi': {
      if (typeof value !== 'object' || Array.isArray(value))
        return fail('expected source return UI');
      const ui = value as Record<string, unknown>;
      const scroll = ui.scroll as Record<string, unknown> | undefined;
      return ['structure', 'cycles', 'analysis'].includes(String(ui.navTab)) &&
        (ui.focus === 'declaration' || ui.focus === 'evidence') &&
        typeof ui.id === 'string' &&
        (ui.focus === 'declaration' ? PATTERNS.entityId : PATTERNS.evidenceId).test(ui.id) &&
        !!scroll &&
        typeof scroll === 'object' &&
        !Array.isArray(scroll) &&
        ['navigation', 'inspector', 'map'].every(
          (key) =>
            typeof scroll[key] === 'number' &&
            Number.isFinite(scroll[key]) &&
            (scroll[key] as number) >= 0 &&
            (scroll[key] as number) <= 10000000
        ) &&
        (ui.searchCount === undefined ||
          (Number.isSafeInteger(ui.searchCount) &&
            (ui.searchCount as number) >= 0 &&
            (ui.searchCount as number) <= 500))
        ? true
        : fail('expected a bounded source button anchor and scroll position');
    }
    default:
      return PATTERNS[kind].test(String(value))
        ? true
        : fail(`value does not match ${PATTERNS[kind]}`);
  }
}
