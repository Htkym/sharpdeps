// Host -> webview message interpretation (SD-017).
//
// The webview never trusts the host payload shape: every field is read defensively and
// an unknown or malformed message becomes no action at all, so a protocol change cannot
// leave the shell in a half-updated state.

import type { AnalysisStage } from './state';
import type { EntitySummary, Scope } from '../../src/view/protocolV2';
import type { Granularity } from '../../src/analyzer/reportV2';
import type { ViewAction } from './state';

export interface RequestContext {
  /** The search query a request carried, so the answer can be matched to it. */
  query?: string;
  /** True when the request asked for the next evidence page. */
  appendEvidence?: boolean;
}

export function toViewActions(
  message: unknown,
  requestContext: ReadonlyMap<string, RequestContext>
): ViewAction[] {
  if (!isRecord(message) || typeof message.type !== 'string') {
    return [];
  }

  switch (message.type) {
    case 'analysisProgress':
      return [
        {
          type: 'analysisProgress',
          stage: readStage(message.stage),
          loaded: numberOrUndefined(message.loaded),
          analyzed: numberOrUndefined(message.analyzed),
          elapsedMs: numberOrUndefined(message.elapsedMs) ?? 0
        }
      ];

    case 'analysisComplete': {
      if (typeof message.analysisId !== 'string') {
        return [];
      }

      const completeness = message.completeness;
      return [
        {
          type: 'analysisComplete',
          analysisId: message.analysisId,
          completeness:
            completeness === 'partial' || completeness === 'failed'
              ? completeness
              : 'completeWithinScope',
          coverage: readCoverage(message.coverage),
          limitations: readLimitations(message.limitations)
        }
      ];
    }

    case 'analysisFailed':
      return [
        {
          type: 'analysisFailed',
          analysisId: typeof message.analysisId === 'string' ? message.analysisId : undefined,
          message: typeof message.message === 'string' ? message.message : 'The analysis failed.',
          cancelled: message.cancelled === true
        }
      ];

    case 'stale':
      return [
        {
          type: 'analysisStale',
          message: 'The analysis result is out of date. Analyze again to refresh.'
        }
      ];

    case 'projection': {
      const projection = readProjection(message.projection);
      return projection ? [{ type: 'projectionReceived', projection }] : [];
    }

    case 'details': {
      if (typeof message.entityId !== 'string') {
        // The host sends the entity inside `entity`; the details action is keyed by id.
        const entity = isRecord(message.entity) ? message.entity : undefined;
        const entityId = entity && typeof entity.id === 'string' ? entity.id : undefined;
        if (!entityId) {
          return [];
        }

        return [
          {
            type: 'detailsReceived',
            entityId,
            dependencies: readEntities(message.dependencies),
            dependents: readEntities(message.dependents)
          }
        ];
      }

      return [
        {
          type: 'detailsReceived',
          entityId: message.entityId,
          dependencies: readEntities(message.dependencies),
          dependents: readEntities(message.dependents)
        }
      ];
    }

    case 'evidencePage': {
      if (typeof message.relationId !== 'string') {
        return [];
      }

      const context =
        typeof message.requestId === 'string' ? requestContext.get(message.requestId) : undefined;
      return [
        {
          type: 'evidenceReceived',
          relationId: message.relationId,
          total: numberOrUndefined(message.total) ?? 0,
          items: Array.isArray(message.items) ? (message.items as Record<string, unknown>[]) : [],
          nextCursor: typeof message.nextCursor === 'string' ? message.nextCursor : null,
          append: context?.appendEvidence === true
        }
      ];
    }

    case 'searchResults': {
      const query =
        typeof message.requestId === 'string'
          ? (requestContext.get(message.requestId)?.query ?? '')
          : '';
      return [
        {
          type: 'searchResultsReceived',
          query,
          total: numberOrUndefined(message.total) ?? 0,
          items: readEntities(message.items)
        }
      ];
    }

    case 'error':
      return [
        {
          type: 'errorRaised',
          code: typeof message.code === 'string' ? message.code : 'host.error',
          message:
            typeof message.message === 'string' ? message.message : 'The host reported an error.'
        }
      ];

    default:
      // capabilities / cycleWitness and anything unknown: no state change.
      return [];
  }
}

function readProjection(value: unknown): ProjectionLike | undefined {
  if (!isRecord(value) || !Array.isArray(value.nodes) || !Array.isArray(value.edges)) {
    return undefined;
  }

  const granularity = value.granularity;
  if (granularity !== 'project' && granularity !== 'namespace' && granularity !== 'type') {
    return undefined;
  }

  const nodes = readEntities(value.nodes);
  const edges = value.edges
    .filter(isRecord)
    .filter(
      (edge) =>
        typeof edge.id === 'string' &&
        typeof edge.sourceId === 'string' &&
        typeof edge.targetId === 'string'
    )
    .map((edge) => ({
      id: edge.id as string,
      sourceId: edge.sourceId as string,
      targetId: edge.targetId as string,
      basis: typeof edge.basis === 'string' ? edge.basis : 'unknown',
      kinds: Array.isArray(edge.kinds) ? edge.kinds.filter((kind) => typeof kind === 'string') : [],
      evidenceCount: numberOrUndefined(edge.evidenceCount) ?? 0,
      inCycle: edge.inCycle === true,
      generatedEvidenceCount: numberOrUndefined(edge.generatedEvidenceCount),
      publicSurfaceEvidenceCount: numberOrUndefined(edge.publicSurfaceEvidenceCount),
      underlyingRelationIds: Array.isArray(edge.underlyingRelationIds)
        ? edge.underlyingRelationIds.filter((id): id is string => typeof id === 'string')
        : undefined,
      underlyingRelations: Array.isArray(edge.underlyingRelations)
        ? edge.underlyingRelations
            .filter(isRecord)
            .filter(
              (relation) =>
                typeof relation.id === 'string' && typeof relation.evidenceCount === 'number'
            )
            .map((relation) => ({
              id: relation.id as string,
              basis: typeof relation.basis === 'string' ? relation.basis : 'unknown',
              kinds: Array.isArray(relation.kinds)
                ? relation.kinds.filter((kind): kind is string => typeof kind === 'string')
                : [],
              evidenceCount: relation.evidenceCount as number,
              confidence: typeof relation.confidence === 'string' ? relation.confidence : null
            }))
        : undefined
    }));

  return {
    scope: readScope(value.scope),
    granularity: granularity as Granularity,
    nodes,
    edges,
    totalNodeCount: numberOrUndefined(value.totalNodeCount) ?? nodes.length,
    totalEdgeCount: numberOrUndefined(value.totalEdgeCount) ?? edges.length,
    truncated: value.truncated === true
  };
}

interface ProjectionLike {
  scope: Scope;
  granularity: Granularity;
  nodes: EntitySummary[];
  edges: Array<{
    id: string;
    sourceId: string;
    targetId: string;
    basis: string;
    kinds: string[];
    evidenceCount: number;
    inCycle: boolean;
    generatedEvidenceCount?: number;
    publicSurfaceEvidenceCount?: number;
    underlyingRelationIds?: string[];
    underlyingRelations?: Array<{
      id: string;
      basis: string;
      kinds: string[];
      evidenceCount: number;
      confidence: string | null;
    }>;
  }>;
  totalNodeCount: number;
  totalEdgeCount: number;
  truncated: boolean;
}

function readScope(value: unknown): Scope {
  if (!isRecord(value) || typeof value.kind !== 'string') {
    return { kind: 'root' };
  }

  const kind = value.kind;
  const allowed = [
    'root',
    'project',
    'namespace',
    'type',
    'dependencies',
    'dependents',
    'cycle'
  ] as const;
  const resolved = allowed.find((entry) => entry === kind) ?? 'root';
  return {
    kind: resolved,
    id: typeof value.id === 'string' ? value.id : null,
    depth: numberOrUndefined(value.depth) ?? null
  };
}

function readEntities(value: unknown): EntitySummary[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .filter(isRecord)
    .filter((entity) => typeof entity.id === 'string' && typeof entity.name === 'string')
    .map((entity) => ({
      id: entity.id as string,
      name: entity.name as string,
      granularity:
        entity.granularity === 'project' ||
        entity.granularity === 'namespace' ||
        entity.granularity === 'type'
          ? entity.granularity
          : 'type',
      kind: typeof entity.kind === 'string' ? entity.kind : undefined,
      projectName: typeof entity.projectName === 'string' ? entity.projectName : undefined,
      inCycle: entity.inCycle === true,
      isExternal: entity.isExternal === true,
      isGenerated: entity.isGenerated === true
    }));
}

function readCoverage(value: unknown): {
  discovered: number;
  loaded: number;
  analyzed: number;
  failed: number;
  skipped: number;
} {
  const coverage = isRecord(value) ? value : {};
  return {
    discovered: numberOrUndefined(coverage.discovered) ?? 0,
    loaded: numberOrUndefined(coverage.loaded) ?? 0,
    analyzed: numberOrUndefined(coverage.analyzed) ?? 0,
    failed: numberOrUndefined(coverage.failed) ?? 0,
    skipped: numberOrUndefined(coverage.skipped) ?? 0
  };
}

function readLimitations(value: unknown): Array<{ code: string; message: string }> | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }

  return value
    .filter(isRecord)
    .filter((limitation) => typeof limitation.code === 'string')
    .map((limitation) => ({
      code: limitation.code as string,
      message:
        typeof limitation.message === 'string' ? limitation.message : (limitation.code as string)
    }));
}

function numberOrUndefined(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

const STAGES: readonly AnalysisStage[] = [
  'discover',
  'load',
  'compile',
  'extract',
  'aggregate',
  'write'
];

function readStage(value: unknown): AnalysisStage {
  return STAGES.find((stage) => stage === value) ?? 'load';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}
