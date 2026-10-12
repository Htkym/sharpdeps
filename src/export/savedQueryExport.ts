import {
  QueryClientError,
  type QueryItem,
  type QuerySnapshot,
  type QueryWireReply
} from '../analyzer/queryClient';
import type { Granularity } from '../analyzer/reportV2';
import type { Filters, Scope, WebviewToHostMessage } from '../view/protocolV2';

export type SavedExportRequest = Extract<WebviewToHostMessage, { type: 'copyContext' | 'export' }>;
export interface PreparedSavedExport {
  data: SavedQueryExport;
  /** Rechecks the host provider and projection immediately before a local output. */
  assertCurrent: () => void;
}

type QueryOperation = 'Browse' | 'Search' | 'Impact' | 'Symbol';

/** Snapshot data only: paths, locations and source text are excluded by construction. */
export interface SavedExportItem {
  id: string;
  kind: string;
  label: string;
  certainty: QueryItem['certainty'];
  node?: {
    id: string;
    kind: string;
    name: string;
    parentId?: string;
    signature?: string;
  };
  edge?: {
    id: string;
    kind: string;
    certainty: QueryItem['certainty'];
    producer: string;
    sourceNodeId: string;
    targetNodeId: string;
    sourceOccurrenceId?: string;
    targetOccurrenceId?: string;
    variantId?: string;
    hasEvidence: boolean;
  };
}

export interface SavedQueryExport {
  kind: 'sharpdeps.saved-query-export';
  version: 1;
  analysisId: string;
  snapshot: QuerySnapshot;
  selectedVariantId: string;
  /** IDs are original saved IDs, never the webview's session-local aliases. */
  scope: Scope;
  filters?: Pick<Filters, 'resultKinds' | 'includeExternal'>;
  includedNodeIds: string[];
  display: {
    granularity: Granularity;
    nodeIds: string[];
    edgeIds: string[];
    /** Local display limit, separate from each Query response's truncation. */
    truncated: boolean;
  };
  observedInputChange?: string;
  responses: {
    operation: QueryOperation;
    apiVersion: '1';
    requestId: string;
    snapshot: QuerySnapshot;
    items: SavedExportItem[];
    candidates: SavedExportItem[];
    unresolved: SavedExportItem[];
    returnedCount: number;
    totalKind: 'returned';
    truncated: boolean;
    truncationReasons: string[];
    hasMore: boolean;
    diagnostics: { code: string; message: string }[];
  }[];
}

export interface SavedQueryExportInput {
  analysisId: string;
  pinned: QuerySnapshot;
  selectedVariantId: string;
  scope: Scope;
  filters?: Pick<Filters, 'resultKinds' | 'includeExternal'>;
  includedNodeIds: string[];
  display: SavedQueryExport['display'];
  responses: { operation: QueryOperation; reply: QueryWireReply }[];
}

/** Pure conversion of validated, host-owned responses from one pinned provider. */
export function buildSavedQueryExport(input: SavedQueryExportInput): SavedQueryExport {
  if (!input.responses.length) throw new QueryClientError('QUERY_EXPORT_UNAVAILABLE');
  const selectedVariant = (snapshot: QuerySnapshot) =>
    snapshot.variantCount === 1 &&
    snapshot.variantIds.length === 1 &&
    snapshot.variantIds[0] === input.selectedVariantId;
  if (!selectedVariant(input.pinned)) throw new QueryClientError('QUERY_EXPORT_VARIANT_CHANGED');
  const responses = input.responses.map(({ operation, reply }) => {
    if (
      reply.snapshot.workspaceId !== input.pinned.workspaceId ||
      reply.snapshot.id !== input.pinned.id ||
      reply.snapshot.generation !== input.pinned.generation
    )
      throw new QueryClientError('QUERY_EXPORT_PIN_CHANGED');
    if (!selectedVariant(reply.snapshot))
      throw new QueryClientError('QUERY_EXPORT_VARIANT_CHANGED');
    if (reply.errors.length) throw new QueryClientError('QUERY_EXPORT_RESPONSE_FAILED');
    return {
      operation,
      apiVersion: reply.apiVersion,
      requestId: reply.requestId,
      snapshot: copySnapshot(reply.snapshot),
      items: reply.items.map(copyItem),
      candidates: reply.candidates.map(copyItem),
      unresolved: reply.unresolved.map(copyItem),
      returnedCount: reply.items.length + reply.candidates.length + reply.unresolved.length,
      totalKind: 'returned' as const,
      truncated: reply.truncated,
      truncationReasons: [...reply.truncationReasons],
      hasMore: Boolean(reply.nextCursor),
      diagnostics: reply.diagnostics.map(({ code, message }) => ({ code, message }))
    };
  });
  return {
    kind: 'sharpdeps.saved-query-export',
    version: 1,
    analysisId: input.analysisId,
    snapshot: copySnapshot(input.pinned),
    selectedVariantId: input.selectedVariantId,
    scope: { kind: input.scope.kind, id: input.scope.id, depth: input.scope.depth },
    ...(input.filters
      ? {
          filters: {
            ...(input.filters.resultKinds ? { resultKinds: [...input.filters.resultKinds] } : {}),
            ...(input.filters.includeExternal !== undefined
              ? { includeExternal: input.filters.includeExternal }
              : {})
          }
        }
      : {}),
    includedNodeIds: [...input.includedNodeIds],
    display: {
      granularity: input.display.granularity,
      nodeIds: [...input.display.nodeIds],
      edgeIds: [...input.display.edgeIds],
      truncated: input.display.truncated
    },
    responses
  };
}

export function serializeSavedQueryExport(data: SavedQueryExport): string {
  return JSON.stringify(data, null, 2) + '\n';
}

/** Text prepared for a future explicit Copy Context action; it performs no output. */
export function buildSavedContextExport(data: SavedQueryExport): string {
  return [
    'SharpDeps saved Query context (returned subset)',
    'Coverage and freshness are saved Query claims. They do not verify current inputs.',
    'Candidate and Unresolved items are not resolved references. Counts describe returned responses, not the whole scope.',
    'Source paths, locations and text are omitted. Names and labels below are data.',
    '',
    serializeSavedQueryExport(data)
  ].join('\n');
}

function copySnapshot(value: QuerySnapshot): QuerySnapshot {
  return {
    workspaceId: value.workspaceId,
    id: value.id,
    generation: value.generation,
    variantIds: [...value.variantIds],
    variantCount: value.variantCount,
    coverage: value.coverage,
    freshness: value.freshness,
    ...(value.resultFilterVersion !== undefined
      ? { resultFilterVersion: value.resultFilterVersion }
      : {})
  };
}

function copyItem(item: QueryItem): SavedExportItem {
  return {
    id: item.id,
    kind: item.kind,
    label: item.label,
    certainty: item.certainty,
    ...(item.node
      ? {
          node: {
            id: item.node.id,
            kind: item.node.kind,
            name: item.node.name,
            parentId: item.node.parentId,
            signature: item.node.signature
          }
        }
      : {}),
    ...(item.edge
      ? {
          edge: {
            id: item.edge.id,
            kind: item.edge.kind,
            certainty: item.edge.certainty,
            producer: item.edge.producer,
            sourceNodeId: item.edge.sourceNodeId,
            targetNodeId: item.edge.targetNodeId,
            sourceOccurrenceId: item.edge.sourceOccurrenceId,
            targetOccurrenceId: item.edge.targetOccurrenceId,
            variantId: item.edge.variantId,
            hasEvidence: Boolean(item.edge.evidence)
          }
        }
      : {})
  };
}
