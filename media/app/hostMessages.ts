// Host -> webview message interpretation (SD-017).
//
// The webview never trusts the host payload shape: every field is read defensively and
// an unknown or malformed message becomes no action at all, so a protocol change cannot
// leave the shell in a half-updated state.

import type { AnalysisStage } from './state';
import type {
  EntitySummary,
  ProjectionCycleGroup,
  QueryResultMetadata,
  Scope
} from '../../src/view/protocolV2';
import type { Granularity } from '../../src/analyzer/reportV2';
import type { ViewAction } from './state';
import type { ViewState } from './state';
import { restoreViewState } from './serializer';

export interface RequestContext {
  treeParentId?: string;
  appendTree?: boolean;
  /** The search query a request carried, so the answer can be matched to it. */
  query?: string;
  /** True when the request asked for the next evidence page. */
  appendEvidence?: boolean;
  analysisId?: string;
  type?: string;
}

export function toViewActions(
  message: unknown,
  requestContext: ReadonlyMap<string, RequestContext>,
  currentState?: ViewState
): ViewAction[] {
  if (!isRecord(message) || typeof message.type !== 'string') {
    return [];
  }
  const metadataValue =
    message.type === 'projection' && isRecord(message.projection)
      ? message.projection.queryMetadata
      : message.queryMetadata;
  const queryMetadata = readQueryMetadata(metadataValue);
  // A malformed saved result must not silently acquire the legacy display semantics.
  if (metadataValue !== undefined && !queryMetadata) return [];
  if (currentState) {
    if (
      ['projection', 'details', 'evidencePage', 'searchResults', 'stale', 'reveal'].includes(
        message.type
      ) &&
      message.analysisId !== currentState.analysisId
    )
      return [];
    if (
      ['analysisComplete', 'analysisFailed', 'analysisProgress'].includes(message.type) &&
      currentState.runningAnalysisId &&
      message.analysisId !== currentState.runningAnalysisId
    )
      return [];
    if (
      message.type === 'details' &&
      isRecord(message.entity) &&
      message.entity.id !== currentState.selection.entityId
    )
      return [];
    if (message.type === 'evidencePage' && message.relationId !== currentState.selection.relationId)
      return [];
    if (
      ['projection', 'details', 'searchResults', 'evidencePage'].includes(message.type) &&
      currentState.queryMetadata &&
      (!queryMetadata ||
        queryMetadata.workspaceId !== currentState.queryMetadata.workspaceId ||
        queryMetadata.snapshotId !== currentState.queryMetadata.snapshotId ||
        queryMetadata.generation !== currentState.queryMetadata.generation ||
        JSON.stringify([...queryMetadata.variantIds].sort()) !==
          JSON.stringify([...currentState.queryMetadata.variantIds].sort()))
    )
      return [];
  }

  switch (message.type) {
    case 'analysisStarted':
      return typeof message.analysisId === 'string'
        ? [
            ...(!currentState?.analysisId &&
            isRecord(message.target) &&
            typeof message.target.name === 'string' &&
            typeof message.target.relativePath === 'string'
              ? [
                  {
                    type: 'targetSelected' as const,
                    name: message.target.name,
                    relativePath: message.target.relativePath
                  }
                ]
              : []),
            {
              type: 'analyzeStarted',
              analysisId: message.analysisId,
              mode: message.mode === 'semantic' ? 'semantic' : 'quick'
            }
          ]
        : [];
    case 'capabilities':
      return isRecord(message.capabilities)
        ? [
            {
              type: 'capabilitiesReceived',
              capabilities: {
                typeGraph: message.capabilities.typeGraph === true,
                evidence: message.capabilities.evidence === true,
                generatedDocuments: message.capabilities.generatedDocuments === true,
                cycleWitness: message.capabilities.cycleWitness === true,
                search: message.capabilities.search === true
              }
            }
          ]
        : [];
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
        ...(isRecord(message.target) &&
        typeof message.target.name === 'string' &&
        typeof message.target.relativePath === 'string'
          ? [
              {
                type: 'targetSelected' as const,
                name: message.target.name,
                relativePath: message.target.relativePath
              }
            ]
          : []),
        {
          type: 'analysisComplete',
          analysisId: message.analysisId,
          completeness: queryMetadata
            ? queryMetadata.coverage === 'Failed'
              ? 'failed'
              : 'partial'
            : completeness === 'partial' || completeness === 'failed'
              ? completeness
              : 'completeWithinScope',
          ...(queryMetadata ? { queryMetadata } : {}),
          coverage: readCoverage(message.coverage),
          limitations: readLimitations(message.limitations),
          mode:
            message.mode === 'semantic'
              ? 'semantic'
              : message.mode === 'quick'
                ? 'quick'
                : undefined,
          profile: isRecord(message.profile) ? message.profile : undefined,
          capabilities: isRecord(message.capabilities)
            ? (message.capabilities as unknown as ViewState['capabilities'])
            : undefined,
          variantOptions: Array.isArray(message.variantOptions)
            ? message.variantOptions.filter(
                (item) =>
                  isRecord(item) &&
                  typeof item.projectLogicalId === 'string' &&
                  typeof item.targetFramework === 'string' &&
                  typeof item.projectPath === 'string'
              )
            : undefined
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
          message: 'This result is out of date. Analyze again to refresh.'
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
            entity: readEntities([message.entity])[0],
            dependencies: readEntities(message.dependencies),
            dependents: readEntities(message.dependents),
            ...(queryMetadata ? { queryMetadata } : {})
          }
        ];
      }

      return [
        {
          type: 'detailsReceived',
          entityId: message.entityId,
          entity: readEntities([message.entity])[0],
          dependencies: readEntities(message.dependencies),
          dependents: readEntities(message.dependents),
          ...(queryMetadata ? { queryMetadata } : {})
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
          append: context?.appendEvidence === true,
          ...(queryMetadata ? { queryMetadata } : {})
        }
      ];
    }

    case 'searchResults': {
      const context =
        typeof message.requestId === 'string' ? requestContext.get(message.requestId) : undefined;
      if (context?.treeParentId)
        return [
          {
            type: 'treeReceived',
            parentId: context.treeParentId,
            items: readEntities(message.items),
            total: numberOrUndefined(message.total) ?? 0,
            nextCursor: typeof message.nextCursor === 'string' ? message.nextCursor : undefined,
            append: context.appendTree,
            ...(queryMetadata ? { queryMetadata } : {})
          }
        ];
      const query =
        typeof message.requestId === 'string'
          ? (requestContext.get(message.requestId)?.query ?? '')
          : '';
      return [
        {
          type: 'searchResultsReceived',
          query,
          total: numberOrUndefined(message.total) ?? 0,
          items: readEntities(message.items),
          ...(queryMetadata ? { queryMetadata } : {})
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

    case 'reveal': {
      if (typeof message.entityId !== 'string') {
        return [];
      }

      return [
        {
          type: 'revealRequested',
          entityId: message.entityId,
          scope: message.scope === undefined ? undefined : readScope(message.scope),
          granularity: readGranularity(message.granularity)
        }
      ];
    }

    case 'viewState': {
      // The host keeps the last small state in workspace storage. Restoring it never
      // starts an analysis: the state has no analysis status.
      const restored = restoreViewState(message.state);
      return Object.keys(restored.state).length > 0
        ? [{ type: 'stateRestored', state: restored.state }]
        : [];
    }

    default:
      // capabilities / cycleWitness and anything unknown: no state change.
      return [];
  }
}

function readGranularity(value: unknown): Granularity | undefined {
  return value === 'project' || value === 'namespace' || value === 'type' ? value : undefined;
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
  const queryMetadata = readQueryMetadata(value.queryMetadata);
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
      certainty: readCertainty(edge.certainty),
      sourceOccurrenceId:
        edge.sourceOccurrenceId === null
          ? null
          : typeof edge.sourceOccurrenceId === 'string'
            ? edge.sourceOccurrenceId
            : undefined,
      targetOccurrenceId:
        edge.targetOccurrenceId === null
          ? null
          : typeof edge.targetOccurrenceId === 'string'
            ? edge.targetOccurrenceId
            : undefined,
      variantId:
        edge.variantId === null
          ? null
          : typeof edge.variantId === 'string'
            ? edge.variantId
            : undefined,
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
    truncated: value.truncated === true,
    cycleGroups: readCycleGroups(value.cycleGroups),
    ...(queryMetadata ? { queryMetadata } : {})
  };
}

function readCycleGroups(value: unknown): ProjectionCycleGroup[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }

  return value
    .filter(isRecord)
    .filter((group) => typeof group.id === 'string' && Array.isArray(group.memberIds))
    .map((group) => ({
      id: group.id as string,
      scope: typeof group.scope === 'string' ? group.scope : 'type',
      basis: typeof group.basis === 'string' ? group.basis : 'unknown',
      memberIds: (group.memberIds as unknown[]).filter(
        (id): id is string => typeof id === 'string'
      ),
      internalRelationIds: Array.isArray(group.internalRelationIds)
        ? group.internalRelationIds.filter((id): id is string => typeof id === 'string')
        : [],
      witness: readWitness(group.witness),
      truncated: group.truncated === true
    }));
}

function readWitness(value: unknown): { memberIds: string[]; relationIds: string[] } | null {
  if (!isRecord(value)) {
    return null;
  }

  return {
    memberIds: Array.isArray(value.memberIds)
      ? value.memberIds.filter((id): id is string => typeof id === 'string')
      : [],
    relationIds: Array.isArray(value.relationIds)
      ? value.relationIds.filter((id): id is string => typeof id === 'string')
      : []
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
    certainty?: EntitySummary['certainty'];
    sourceOccurrenceId?: string | null;
    targetOccurrenceId?: string | null;
    variantId?: string | null;
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
  cycleGroups?: Array<{
    id: string;
    scope: string;
    basis: string;
    memberIds: string[];
    internalRelationIds: string[];
    witness: { memberIds: string[]; relationIds: string[] } | null;
    truncated?: boolean;
  }>;
  queryMetadata?: QueryResultMetadata;
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
      fullName: typeof entity.fullName === 'string' ? entity.fullName : undefined,
      projectId: typeof entity.projectId === 'string' ? entity.projectId : undefined,
      projectPath: typeof entity.projectPath === 'string' ? entity.projectPath : undefined,
      projectKind: typeof entity.projectKind === 'string' ? entity.projectKind : undefined,
      namespaceId: typeof entity.namespaceId === 'string' ? entity.namespaceId : undefined,
      namespaceName: typeof entity.namespaceName === 'string' ? entity.namespaceName : undefined,
      targetFramework:
        typeof entity.targetFramework === 'string' ? entity.targetFramework : undefined,
      analysisStatus: ['complete', 'partial', 'failed', 'skipped'].includes(
        String(entity.analysisStatus)
      )
        ? (entity.analysisStatus as EntitySummary['analysisStatus'])
        : undefined,
      analysisLimitations: Array.isArray(entity.analysisLimitations)
        ? entity.analysisLimitations.filter((item): item is string => typeof item === 'string')
        : [],
      dependencyCount: numberOrUndefined(entity.dependencyCount),
      dependentCount: numberOrUndefined(entity.dependentCount),
      id: entity.id as string,
      name: entity.name as string,
      granularity:
        entity.granularity === 'project' ||
        entity.granularity === 'namespace' ||
        entity.granularity === 'type'
          ? entity.granularity
          : 'type',
      kind: typeof entity.kind === 'string' ? entity.kind : undefined,
      certainty: readCertainty(entity.certainty),
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

function readCertainty(value: unknown): EntitySummary['certainty'] {
  return value === 'Resolved' || value === 'Candidate' || value === 'Unresolved'
    ? value
    : undefined;
}

function readQueryMetadata(value: unknown): QueryResultMetadata | undefined {
  const text = (item: unknown, max = 4096): item is string =>
    typeof item === 'string' && item.length > 0 && item.length <= max;
  const count = (item: unknown): item is number =>
    typeof item === 'number' && Number.isSafeInteger(item) && item >= 0;
  const strings = (items: unknown, maxItems: number, maxLength: number): items is string[] =>
    Array.isArray(items) &&
    items.length <= maxItems &&
    items.every((item) => text(item, maxLength));
  if (
    !isRecord(value) ||
    value.provider !== 'savedIndex' ||
    value.totalKind !== 'returned' ||
    !text(value.workspaceId) ||
    !text(value.snapshotId) ||
    !count(value.generation) ||
    !strings(value.variantIds, 64, 4096) ||
    new Set(value.variantIds).size !== value.variantIds.length ||
    (value.coverage !== 'CompleteWithinScope' &&
      value.coverage !== 'Partial' &&
      value.coverage !== 'Failed') ||
    !text(value.freshness, 64) ||
    typeof value.truncated !== 'boolean' ||
    !strings(value.truncationReasons, 64, 128) ||
    !count(value.returnedCount) ||
    !count(value.candidateCount) ||
    !count(value.unresolvedCount) ||
    !Array.isArray(value.diagnostics) ||
    value.diagnostics.length > 64 ||
    !value.diagnostics.every((item) => isRecord(item) && text(item.code, 128) && text(item.message))
  )
    return undefined;
  return {
    provider: 'savedIndex',
    workspaceId: value.workspaceId,
    snapshotId: value.snapshotId,
    generation: value.generation,
    variantIds: [...value.variantIds],
    coverage: value.coverage as QueryResultMetadata['coverage'],
    freshness: value.freshness,
    truncated: value.truncated,
    truncationReasons: [...value.truncationReasons],
    diagnostics: value.diagnostics.map((item) => ({
      code: item.code as string,
      message: item.message as string
    })),
    returnedCount: value.returnedCount,
    totalKind: 'returned',
    candidateCount: value.candidateCount,
    unresolvedCount: value.unresolvedCount
  };
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
