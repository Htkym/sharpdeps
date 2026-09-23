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

export type ExportFormat = 'mermaid' | 'svg' | 'png' | 'json';

export interface Scope {
  kind: 'root' | 'project' | 'namespace' | 'type' | 'dependencies' | 'dependents' | 'cycle';
  id?: string | null;
  depth?: number | null;
}

export interface Filters {
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
}

export interface ProjectionEdge {
  id: string;
  sourceId: string;
  targetId: string;
  basis: string;
  kinds: string[];
  evidenceCount: number;
  inCycle: boolean;
  generatedEvidenceCount?: number;
  publicSurfaceEvidenceCount?: number;
}

export interface Projection {
  scope: Scope;
  granularity: Granularity;
  nodes: EntitySummary[];
  edges: ProjectionEdge[];
  totalNodeCount: number;
  totalEdgeCount: number;
  truncated: boolean;
  includedOutOfFilterIds?: string[];
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
  | {
      type: 'getProjection';
      requestId: string;
      analysisId: string;
      scope: Scope;
      granularity: Granularity;
      filters?: Filters;
    }
  | {
      type: 'searchEntities';
      requestId: string;
      analysisId: string;
      query: string;
      granularity?: Granularity;
      filters?: Filters;
      limit?: number;
      cursor?: string;
    }
  | { type: 'getEntityDetails'; requestId: string; analysisId: string; entityId: string }
  | {
      type: 'getEvidencePage';
      requestId: string;
      analysisId: string;
      relationId: string;
      limit?: number;
      cursor?: string;
    }
  | { type: 'getCycleWitness'; requestId: string; analysisId: string; cycleGroupId: string }
  | { type: 'openEvidence'; requestId: string; analysisId: string; evidenceId: string }
  | {
      type: 'openDeclaration';
      requestId: string;
      analysisId: string;
      entityId: string;
      declarationIndex?: number;
    }
  | {
      type: 'copyContext';
      requestId: string;
      analysisId: string;
      scope: Scope;
      includeSnippets?: boolean;
    }
  | { type: 'export'; requestId: string; analysisId: string; format: ExportFormat; scope: Scope }
  | { type: 'persistViewState'; viewState: Record<string, unknown> };

// Extension host -> webview
export type HostToWebviewMessage =
  | {
      type: 'capabilities';
      protocolVersion: number;
      capabilities: Capabilities;
      analysisId?: string | null;
    }
  | ({ type: 'analysisProgress' } & AnalysisProgress)
  | {
      type: 'analysisComplete';
      analysisId: string;
      completeness: 'completeWithinScope' | 'partial' | 'failed';
      coverage: Coverage;
      mode?: 'quick' | 'semantic';
      limitations?: unknown[];
    }
  | {
      type: 'analysisFailed';
      analysisId: string;
      message: string;
      detail?: string;
      cancelled?: boolean;
    }
  | { type: 'projection'; requestId: string; analysisId: string; projection: Projection }
  | {
      type: 'searchResults';
      requestId: string;
      analysisId: string;
      total: number;
      items: EntitySummary[];
      nextCursor?: string | null;
    }
  | {
      type: 'details';
      requestId: string;
      analysisId: string;
      entity: Record<string, unknown>;
      dependencies?: EntitySummary[];
      dependents?: EntitySummary[];
    }
  | {
      type: 'evidencePage';
      requestId: string;
      analysisId: string;
      relationId: string;
      total: number;
      items: Record<string, unknown>[];
      nextCursor?: string | null;
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

const PATTERNS = {
  requestId: /^req_[0-9a-f]{16}$/,
  analysisId: /^an_[0-9a-f]{16}$/,
  cursor: /^cur_[0-9a-f]{16}$/,
  entityId: /^(prj|var|ns|ty|mb)_[0-9a-f]{16}$/,
  relationId: /^rel_[0-9a-f]{16}$/,
  evidenceId: /^ev_[0-9a-f]{16}$/,
  cycleGroupId: /^cyc_[0-9a-f]{16}$/
} as const;

type FieldKind =
  | keyof typeof PATTERNS
  | 'string'
  | 'object'
  | 'array'
  | 'boolean'
  | 'positiveInteger'
  | 'granularity'
  | 'scope'
  | 'filters'
  | 'profile'
  | 'exportFormat'
  | 'mode'
  | 'viewState';

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
  getProjection: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      scope: 'scope',
      granularity: 'granularity'
    },
    optional: { filters: 'filters' }
  },
  searchEntities: {
    required: { requestId: 'requestId', analysisId: 'analysisId', query: 'string' },
    optional: {
      granularity: 'granularity',
      filters: 'filters',
      limit: 'positiveInteger',
      cursor: 'cursor'
    }
  },
  getEntityDetails: {
    required: { requestId: 'requestId', analysisId: 'analysisId', entityId: 'entityId' },
    optional: {}
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
    optional: {}
  },
  openDeclaration: {
    required: { requestId: 'requestId', analysisId: 'analysisId', entityId: 'entityId' },
    optional: { declarationIndex: 'positiveInteger' }
  },
  copyContext: {
    required: { requestId: 'requestId', analysisId: 'analysisId', scope: 'scope' },
    optional: { includeSnippets: 'boolean' }
  },
  export: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      format: 'exportFormat',
      scope: 'scope'
    },
    optional: {}
  },
  persistViewState: { required: { viewState: 'viewState' }, optional: {} }
};

const HOST_RULES: Record<string, MessageRule> = {
  capabilities: {
    required: { protocolVersion: 'positiveInteger', capabilities: 'object' },
    optional: { analysisId: 'analysisId' }
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
    optional: { mode: 'mode', limitations: 'array' }
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
    optional: { nextCursor: 'cursor' }
  },
  details: {
    required: { requestId: 'requestId', analysisId: 'analysisId', entity: 'object' },
    optional: { dependencies: 'array', dependents: 'array' }
  },
  evidencePage: {
    required: {
      requestId: 'requestId',
      analysisId: 'analysisId',
      relationId: 'relationId',
      total: 'positiveInteger',
      items: 'array'
    },
    optional: { nextCursor: 'cursor' }
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
  return validateMessage(input, WEBVIEW_RULES, 'webview');
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
    case 'mode':
      return MODES.includes(value as (typeof MODES)[number])
        ? true
        : fail(`expected one of ${MODES.join(', ')}`);
    case 'granularity':
      return GRANULARITIES.includes(value as (typeof GRANULARITIES)[number])
        ? true
        : fail(`expected one of ${GRANULARITIES.join(', ')}`);
    case 'exportFormat':
      return EXPORT_FORMATS.includes(value as (typeof EXPORT_FORMATS)[number])
        ? true
        : fail(`expected one of ${EXPORT_FORMATS.join(', ')}`);
    case 'scope': {
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
        !PATTERNS.entityId.test(String(scope.id))
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
    case 'filters':
      return typeof value === 'object' && !Array.isArray(value) ? true : fail('expected an object');
    case 'profile':
      return typeof value === 'object' && !Array.isArray(value) ? true : fail('expected an object');
    case 'viewState':
      return typeof value === 'object' && !Array.isArray(value) ? true : fail('expected an object');
    default:
      return PATTERNS[kind].test(String(value))
        ? true
        : fail(`value does not match ${PATTERNS[kind]}`);
  }
}
