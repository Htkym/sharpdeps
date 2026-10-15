import { createHash, randomBytes } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import type { Granularity } from './reportV2';
import type { ReportBridge } from './reportBridge';
import type { SavedSourceLocation } from '../security/savedSource';
import {
  buildSavedQueryExport,
  type SavedQueryExport,
  type SavedExportRequest,
  type PreparedSavedExport
} from '../export/savedQueryExport';
import {
  QueryClientError,
  validateQueryReply,
  type SavedQueryClient,
  type QueryWireReply,
  type QueryItem,
  type QueryNode,
  type QueryEdge,
  type QueryCertainty
} from './queryClient';
import { PROTOCOL_VERSION, validateWebviewMessage } from '../view/protocolV2';
import type {
  EntitySummary,
  HostToWebviewMessage,
  Projection,
  QueryResultMetadata,
  CandidateOriginAction,
  WebviewToHostMessage,
  Filters,
  SavedRelation
} from '../view/protocolV2';

const CAPABILITIES = {
  typeGraph: true,
  evidence: true,
  generatedDocuments: false,
  cycleWitness: false,
  search: true
};

interface CandidateOrigin {
  action: CandidateOriginAction;
  edge: QueryEdge;
  mention: QueryNode;
  target: QueryNode;
  originalCandidate: QueryItem;
  reason: string;
  snapshot: QueryWireReply['snapshot'];
  epoch: number;
  contextKey: string;
}
interface CandidateSource {
  origin: CandidateOrigin;
  revision: number;
  occurrenceId: string;
  location: SavedSourceLocation;
}

/** An explicitly selected saved generation; legacy data is never spliced into it. */
export class SavedQueryProvider implements ReportBridge {
  readonly analysisId = `an_${randomBytes(8).toString('hex')}`;
  private readonly nodes = new Map<string, { node: QueryNode; item: QueryItem }>();
  private readonly opaque = new Map<string, string>();
  private readonly reverse = new Map<string, string>();
  private readonly edges = new Map<
    string,
    { edge: QueryEdge; item: QueryItem; reply: QueryWireReply; certainty: QueryCertainty }
  >();
  private readonly cursors = new Map<string, { cursor: string; key: string }>();
  private sequence = 0;
  private disposed = false;
  private readonly declarations = new Map<string, SavedSourceLocation>();
  private readonly evidenceLocations = new Map<string, SavedSourceLocation>();
  private inputStaleReason?: Extract<HostToWebviewMessage, { type: 'stale' }>['reason'];
  private preparedExport?: SavedQueryExport;
  private preparedProjection?: Extract<WebviewToHostMessage, { type: 'getProjection' }>;
  private projectionRevision = 0;
  private readonly candidateOrigins = new Map<string, CandidateOrigin>();
  private readonly candidateInvestigations = new Map<
    string,
    Extract<HostToWebviewMessage, { type: 'candidateInvestigation' }>
  >();
  private readonly candidateSources = new Map<string, CandidateSource>();
  private candidateEpoch = 0;
  private investigationRevision = 0;
  private activeCandidateOrigin?: string;
  private candidateFilters: Filters = {};
  private candidateProjectionKey?: string;
  private candidateSelection?: string;
  private detailsRevision = 0;

  get staleReason(): Extract<HostToWebviewMessage, { type: 'stale' }>['reason'] | undefined {
    return this.inputStaleReason;
  }

  markStale(reason: Extract<HostToWebviewMessage, { type: 'stale' }>['reason']): void {
    this.inputStaleReason = reason;
    this.invalidateCandidateOrigins();
  }

  /** Rereading the same saved generation does not resolve an observed input change. */
  inheritStaleness(previous: SavedQueryProvider | undefined): void {
    if (
      previous &&
      previous.readyReply.snapshot.workspaceId === this.readyReply.snapshot.workspaceId &&
      previous.readyReply.snapshot.id === this.readyReply.snapshot.id &&
      previous.readyReply.snapshot.generation === this.readyReply.snapshot.generation
    )
      this.inputStaleReason = previous.staleReason;
  }

  private constructor(
    private readonly client: SavedQueryClient,
    readonly root: string,
    private readonly readyReply: QueryWireReply,
    private readonly variant: string,
    private readonly limits: { maxNodes: number; maxEdges: number }
  ) {}

  static async create(
    client: SavedQueryClient,
    root: string,
    variant?: string,
    limits = { maxNodes: 100, maxEdges: 200 }
  ): Promise<SavedQueryProvider> {
    try {
      const ready = await client.ready;
      const ids = ready.snapshot.variantIds;
      if (!variant && ready.snapshot.variantCount !== 1)
        throw new QueryClientError('QUERY_VARIANT_SELECTION_REQUIRED');
      const chosen = variant || ids[0];
      if (!chosen || (!ids.includes(chosen) && ready.snapshot.variantCount === ids.length))
        throw new QueryClientError('QUERY_VARIANT_UNKNOWN');
      if (ready.snapshot.coverage === 'Failed') throw new QueryClientError('QUERY_COVERAGE_FAILED');
      let selectedReady = ready;
      // The ready header is bounded to 64 IDs. A scoped Status proves exact selection
      // without treating that bounded header as the full saved variant inventory.
      if (ready.snapshot.variantCount !== 1 || variant) {
        selectedReady = await client.request({
          requestId: 'provider_variant',
          kind: 'Status',
          scope: { variantIds: [chosen] },
          freshness: 'AllowStale'
        });
        if (selectedReady.errors.length) throw new QueryClientError(selectedReady.errors[0].code);
        if (
          selectedReady.snapshot.workspaceId !== ready.snapshot.workspaceId ||
          selectedReady.snapshot.id !== ready.snapshot.id ||
          selectedReady.snapshot.generation !== ready.snapshot.generation
        )
          throw new QueryClientError('QUERY_GENERATION_CHANGED');
        if (
          selectedReady.snapshot.variantCount !== 1 ||
          selectedReady.snapshot.variantIds.length !== 1 ||
          selectedReady.snapshot.variantIds[0] !== chosen
        )
          throw new QueryClientError('QUERY_VARIANT_UNKNOWN');
      }
      return new SavedQueryProvider(client, root, selectedReady, chosen, limits);
    } catch (error) {
      client.dispose();
      throw error;
    }
  }

  analysisState(targetName: string): Extract<HostToWebviewMessage, { type: 'analysisComplete' }> {
    return {
      type: 'analysisComplete',
      analysisId: this.analysisId,
      completeness: 'partial',
      coverage: { discovered: 0, loaded: 0, analyzed: 0, failed: 0, skipped: 0, unresolved: 0 },
      capabilities: CAPABILITIES,
      target: { name: targetName, relativePath: '' },
      variantOptions: [],
      queryMetadata: this.metadata(this.readyReply, 0),
      limitations: [
        {
          code: 'savedIndex.scope',
          message:
            'Read-only saved Query. Counts describe returned pages; legacy project coverage is not measured.'
        },
        {
          code: 'savedIndex.symbols',
          message:
            'Dependencies are explicit symbol relations. Member-to-type aggregation, cycle proof and legacy project/namespace relations are not supplied.'
        },
        {
          code: 'savedIndex.freshness',
          message:
            'Input inventory and configuration are not verified. Refresh rereads saved data; it does not analyze or update the index.'
        }
      ]
    };
  }

  /** Host-only preparation from the last successful projection; no extra Query or output. */
  prepareSavedExport(analysisId: string): SavedQueryExport {
    if (this.disposed || analysisId !== this.analysisId)
      throw new QueryClientError('QUERY_ANALYSIS_EXPIRED');
    if (!this.preparedExport) throw new QueryClientError('QUERY_EXPORT_UNAVAILABLE');
    return {
      ...structuredClone(this.preparedExport),
      observedInputChange: this.staleReason
    };
  }

  /** Binds a validated output request to the projection actually received by the view. */
  prepareSavedExportFor(request: SavedExportRequest): PreparedSavedExport {
    const data = this.prepareSavedExport(request.analysisId);
    const prepared = this.preparedExport;
    const projection = this.preparedProjection;
    if (request.type === 'copyContext' && request.includeSnippets)
      throw new QueryClientError('QUERY_EXPORT_SNIPPETS_UNSUPPORTED');
    if (request.type === 'export' && (request.format !== 'json' || request.data !== undefined))
      throw new QueryClientError('QUERY_EXPORT_FORMAT_UNSUPPORTED');
    const selection = (value: typeof projection | SavedExportRequest) =>
      value && {
        scope: value.scope,
        granularity: value.granularity,
        filters: value.filters ?? {},
        search: value.search ?? '',
        includeIds: value.includeIds ?? []
      };
    if (
      !projection ||
      request.projectionRequestId !== projection.requestId ||
      !isDeepStrictEqual(selection(request), selection(projection))
    )
      throw new QueryClientError('QUERY_EXPORT_SELECTION_CHANGED');
    const assertCurrent = (): void => {
      if (this.disposed || request.analysisId !== this.analysisId)
        throw new QueryClientError('QUERY_ANALYSIS_EXPIRED');
      if (this.preparedExport !== prepared || this.preparedProjection !== projection)
        throw new QueryClientError('QUERY_EXPORT_SELECTION_CHANGED');
      const snapshot = this.readyReply.snapshot;
      if (
        snapshot.workspaceId !== data.snapshot.workspaceId ||
        snapshot.id !== data.snapshot.id ||
        snapshot.generation !== data.snapshot.generation ||
        snapshot.variantCount !== 1 ||
        snapshot.variantIds.length !== 1 ||
        snapshot.variantIds[0] !== data.selectedVariantId
      )
        throw new QueryClientError('QUERY_EXPORT_PIN_CHANGED');
      if (this.staleReason !== data.observedInputChange)
        throw new QueryClientError('QUERY_EXPORT_INPUT_CHANGED');
    };
    assertCurrent();
    return { data, assertCurrent };
  }

  async handle(message: unknown): Promise<HostToWebviewMessage> {
    const checked = validateWebviewMessage(message);
    if (!checked.ok)
      return { type: 'error', code: 'protocol.invalidMessage', message: checked.errors.join('; ') };
    const request = structuredClone(checked.value);
    const requestId = 'requestId' in request ? request.requestId : undefined;
    try {
      if (this.disposed) throw new QueryClientError('QUERY_ANALYSIS_EXPIRED');
      if (request.type === 'ready')
        return {
          type: 'capabilities',
          protocolVersion: PROTOCOL_VERSION,
          analysisId: this.analysisId,
          capabilities: CAPABILITIES
        };
      if (!('analysisId' in request) || request.analysisId !== this.analysisId)
        throw new QueryClientError('QUERY_ANALYSIS_EXPIRED');
      switch (request.type) {
        case 'searchEntities':
          return await this.search(request);
        case 'getProjection':
          return await this.projection(request);
        case 'getEntityDetails':
          return await this.details(request);
        case 'getEvidencePage':
          return this.evidence(request);
        case 'investigateCandidate':
          return await this.investigateCandidate(request);
        default:
          throw new QueryClientError('QUERY_OPERATION_UNSUPPORTED');
      }
    } catch (error) {
      return {
        type: 'error',
        requestId,
        analysisId: this.analysisId,
        code: error instanceof QueryClientError ? error.code : 'QUERY_PROVIDER_FAILED',
        message:
          error instanceof QueryClientError
            ? error.message
            : 'The saved Query could not be displayed.'
      };
    }
  }

  private async requestValidated(
    kind: string,
    fields: Record<string, unknown> = {},
    maxDepth = 1
  ): Promise<QueryWireReply> {
    if (this.disposed) throw new QueryClientError('QUERY_ANALYSIS_EXPIRED');
    const reply = await this.client.request({
      requestId: `provider_${++this.sequence}`,
      kind,
      scope: { variantIds: [this.variant] },
      freshness: 'AllowStale',
      budget: {
        maxNodes: Math.max(1, Math.min(500, this.limits.maxNodes)),
        maxEdges: Math.max(1, Math.min(1000, this.limits.maxEdges)),
        maxDepth,
        maxMilliseconds: 3000,
        maxChars: 500000,
        maxBytes: 1000000
      },
      ...fields
    });
    const requestedScope = fields.scope as
      { resultKinds?: string[]; includeExternal?: boolean } | undefined;
    if (requestedScope?.resultKinds || requestedScope?.includeExternal === false) {
      if (reply.snapshot.resultFilterVersion !== 1)
        throw new QueryClientError('QUERY_FILTER_UNSUPPORTED');
      if (
        [...reply.items, ...reply.candidates, ...reply.unresolved].some(
          (item) =>
            item.node &&
            ((requestedScope.resultKinds &&
              (!item.resultKind || !requestedScope.resultKinds.includes(item.resultKind))) ||
              (requestedScope.includeExternal === false && item.node.kind === 'ExternalSymbol'))
        )
      )
        throw new QueryClientError('QUERY_FILTER_RESULT_INVALID');
    }
    if (this.disposed) throw new QueryClientError('QUERY_ANALYSIS_EXPIRED');
    if (
      reply.snapshot.workspaceId !== this.readyReply.snapshot.workspaceId ||
      reply.snapshot.id !== this.readyReply.snapshot.id ||
      reply.snapshot.generation !== this.readyReply.snapshot.generation
    )
      throw new QueryClientError('QUERY_GENERATION_CHANGED');
    if (
      reply.snapshot.variantCount !== 1 ||
      reply.snapshot.variantIds.length !== 1 ||
      reply.snapshot.variantIds[0] !== this.variant
    )
      throw new QueryClientError('QUERY_VARIANT_CHANGED');
    if (reply.errors.length) throw new QueryClientError(reply.errors[0].code);
    return reply;
  }

  private async execute(
    kind: string,
    fields: Record<string, unknown> = {},
    maxDepth = 1
  ): Promise<QueryWireReply> {
    const reply = await this.requestValidated(kind, fields, maxDepth);
    const returned = [...reply.items, ...reply.candidates, ...reply.unresolved];
    if (this.nodes.size + this.edges.size + this.cursors.size + returned.length > 10000) {
      this.dispose();
      throw new QueryClientError('QUERY_SESSION_LIMIT');
    }
    for (const item of returned) {
      if (item.node) {
        this.nodes.set(item.node.id, { node: item.node, item });
      }
      if (item.edge) {
        const id = this.id(item.edge.id, 'rel');
        const returnedCertainty =
          certaintyRank(item.certainty) > certaintyRank(item.edge.certainty)
            ? item.certainty
            : item.edge.certainty;
        const prior = this.edges.get(id);
        // One opaque ID may be returned through several paths in this pinned session.
        // Later replies can supply locations, but cannot strengthen a known tentative path.
        const certainty =
          prior && certaintyRank(prior.certainty) > certaintyRank(returnedCertainty)
            ? prior.certainty
            : returnedCertainty;
        this.edges.set(id, { edge: item.edge, item, reply, certainty });
      }
    }
    return reply;
  }

  private id(sourceId: string, prefix: 'prj' | 'ns' | 'ty' | 'rel' | 'ev' = 'ty'): string {
    const key = `${prefix}:${sourceId}`;
    const existing = this.opaque.get(key);
    if (existing) return existing;
    const id = `${prefix}_${createHash('sha256').update(`${this.analysisId}/${this.variant}/${key}`).digest('hex').slice(0, 16)}`;
    if (this.reverse.has(id) && this.reverse.get(id) !== sourceId)
      throw new QueryClientError('QUERY_ID_COLLISION');
    this.opaque.set(key, id);
    this.reverse.set(id, sourceId);
    return id;
  }

  private entity(nodeId: string, certainty?: QueryCertainty): EntitySummary {
    const entry = this.nodes.get(nodeId);
    if (!entry) throw new QueryClientError('QUERY_ENTITY_NOT_RETURNED');
    const granularity: Granularity =
      entry.node.kind === 'Project'
        ? 'project'
        : entry.node.kind === 'Namespace'
          ? 'namespace'
          : 'type';
    const id = this.id(
      nodeId,
      granularity === 'project' ? 'prj' : granularity === 'namespace' ? 'ns' : 'ty'
    );
    const source = this.source(entry.item.path, entry.node.location);
    if (source && ['Type', 'Member', 'Document', 'Section'].includes(entry.node.kind))
      this.declarations.set(id, source);
    else this.declarations.delete(id);
    return {
      id,
      name: entry.node.name,
      fullName: entry.node.signature || entry.node.name,
      granularity,
      kind: entry.node.kind,
      certainty: certainty ?? entry.item.certainty,
      isExternal: entry.node.kind === 'ExternalSymbol',
      path: entry.item.path,
      reason: entry.item.reason,
      resultKind:
        entry.item.resultKind === 'Code'
          ? 'code'
          : entry.item.resultKind === 'Docs'
            ? 'docs'
            : entry.item.resultKind === 'Other'
              ? 'other'
              : undefined,
      sourceLocationAvailable: Boolean(source)
    };
  }

  private selected(opaqueId: string): string {
    const id = this.reverse.get(opaqueId);
    if (!id || !this.nodes.has(id)) throw new QueryClientError('QUERY_ENTITY_UNKNOWN');
    return id;
  }

  private resultScope(filters?: Filters): Record<string, unknown> {
    return {
      variantIds: [this.variant],
      ...(filters?.resultKinds?.length
        ? {
            resultKinds: [...new Set(filters.resultKinds)]
              .sort()
              .map((kind) => (kind === 'code' ? 'Code' : 'Docs'))
          }
        : {}),
      ...(filters?.includeExternal === false ? { includeExternal: false } : {})
    };
  }

  private scope(
    granularity?: Granularity,
    parentId?: string,
    filters?: Filters
  ): Record<string, unknown> {
    const scope = this.resultScope(filters);
    if (granularity && !(granularity === 'type' && filters?.resultKinds?.length))
      scope.kind =
        granularity === 'project' ? 'Project' : granularity === 'namespace' ? 'Namespace' : 'Type';
    if (parentId) {
      const parent = this.nodes.get(this.selected(parentId))!.node;
      if (parent.kind === 'Project') scope.projectId = parent.id;
      else if (
        parent.kind === 'Namespace' &&
        this.readyReply.snapshot.namespaceBrowseVersion === 1
      ) {
        scope.parentId = parent.id;
        scope.projectId = parent.parentId;
        scope.kind = 'Type';
      } else throw new QueryClientError('QUERY_SCOPE_UNSUPPORTED');
    }
    return scope;
  }

  private filters(filters?: Filters): void {
    if (
      (filters?.resultKinds?.length || filters?.includeExternal === false) &&
      this.readyReply.snapshot.resultFilterVersion !== 1
    )
      throw new QueryClientError('QUERY_FILTER_UNSUPPORTED');
    // These are report-v2 classifications; the Harness contract cannot prove them.
    if (
      filters?.kinds?.length ||
      filters?.projectKinds?.length ||
      filters?.basis?.length ||
      filters?.relationKinds?.length ||
      filters?.includeGenerated === false ||
      filters?.includeTests === false
    )
      throw new QueryClientError('QUERY_FILTER_UNSUPPORTED');
  }

  private entities(reply: QueryWireReply, parentId?: string, filters?: Filters): EntitySummary[] {
    const parent = parentId ? this.nodes.get(this.selected(parentId))!.node : undefined;
    if (
      parent?.kind === 'Namespace' &&
      (reply.snapshot.namespaceBrowseVersion !== 1 ||
        [...reply.items, ...reply.candidates, ...reply.unresolved].some(
          (item) => !item.node || item.node.kind !== 'Type' || item.node.parentId !== parent.id
        ))
    )
      throw new QueryClientError('QUERY_SCOPE_UNSUPPORTED');
    const certaintyByNode = new Map<string, QueryCertainty>();
    for (const item of [...reply.items, ...reply.candidates, ...reply.unresolved]) {
      if (!item.node || (parent && parent.kind !== 'Project' && item.node.parentId !== parent.id))
        continue;
      const previous = certaintyByNode.get(item.node.id);
      if (!previous || certaintyRank(item.certainty) > certaintyRank(previous))
        certaintyByNode.set(item.node.id, item.certainty);
    }
    return [...certaintyByNode]
      .map(([id, certainty]) => this.entity(id, certainty))
      .filter((item) => filters?.includeExternal !== false || !item.isExternal);
  }

  private async search(
    request: Extract<WebviewToHostMessage, { type: 'searchEntities' }>
  ): Promise<HostToWebviewMessage> {
    this.filters(request.filters);
    this.observeCandidateFilters(request.filters);
    // Query supports 128 UTF-16 chars; rejecting is preferable to changing the search.
    if (request.query.length > 128) throw new QueryClientError('QUERY_TERM_LIMIT');
    const key = JSON.stringify([
      request.query,
      request.granularity,
      request.parentId,
      request.filters,
      request.limit
    ]);
    const prior = request.cursor ? this.cursors.get(request.cursor) : undefined;
    if (request.cursor && (!prior || prior.key !== key))
      throw new QueryClientError('QUERY_CURSOR_INVALID');
    const reply = await this.execute(request.query.trim() ? 'Search' : 'Browse', {
      ...(request.query.trim() ? { term: request.query } : {}),
      scope: this.scope(
        request.query.trim() && request.granularity === 'type' ? undefined : request.granularity,
        request.parentId,
        request.filters
      ),
      pageSize: Math.min(500, request.limit ?? 100),
      ...(prior ? { cursor: prior.cursor } : {})
    });
    const items = this.entities(reply, request.parentId, request.filters);
    let nextCursor: string | null = null;
    if (reply.nextCursor) {
      if (this.cursors.size >= 10000) {
        this.dispose();
        throw new QueryClientError('QUERY_SESSION_LIMIT');
      }
      nextCursor = `cur_${randomBytes(8).toString('hex')}`;
      this.cursors.set(nextCursor, { cursor: reply.nextCursor, key });
    }
    return {
      type: 'searchResults',
      requestId: request.requestId,
      analysisId: this.analysisId,
      items,
      total: items.length,
      nextCursor,
      queryMetadata: this.metadata(reply, items.length)
    };
  }

  private async projection(
    request: Extract<WebviewToHostMessage, { type: 'getProjection' }>
  ): Promise<HostToWebviewMessage> {
    const projectionRevision = ++this.projectionRevision;
    this.observeCandidateFilters(request.filters);
    const candidateKey = JSON.stringify([
      request.scope.kind,
      request.scope.id ?? null,
      request.scope.depth ?? 1,
      request.granularity,
      request.search ?? '',
      request.includeIds ?? []
    ]);
    if (this.candidateProjectionKey !== undefined && this.candidateProjectionKey !== candidateKey)
      this.invalidateCandidateOrigins();
    this.candidateProjectionKey = candidateKey;
    this.preparedExport = undefined;
    this.preparedProjection = undefined;
    this.filters(request.filters);
    const scope = request.scope;
    if (scope.kind === 'cycle') throw new QueryClientError('QUERY_OPERATION_UNSUPPORTED');
    if (request.search && request.search.length > 128)
      throw new QueryClientError('QUERY_TERM_LIMIT');
    const origin = scope.id ? this.selected(scope.id) : undefined;
    if ((request.includeIds?.length ?? 0) > 64) throw new QueryClientError('QUERY_SELECTION_LIMIT');
    const included = request.includeIds?.map((id) => this.selected(id));
    const walking =
      scope.kind === 'dependencies' || scope.kind === 'dependents' || scope.kind === 'type';
    if (walking && !origin) throw new QueryClientError('QUERY_SCOPE_UNSUPPORTED');
    if (walking && this.nodes.get(origin!)!.item.certainty !== 'Resolved')
      throw new QueryClientError('QUERY_CANDIDATE_LEAF');
    const operation = walking ? 'Impact' : request.search?.trim() ? 'Search' : 'Browse';
    const reply = await this.execute(
      operation,
      walking
        ? {
            nodeId: origin,
            scope: this.resultScope(request.filters),
            dependents: scope.kind === 'dependents',
            includeCandidates: true,
            includeDocuments: true,
            budget: {
              maxNodes: Math.min(500, Math.max(1, this.limits.maxNodes)),
              maxEdges: Math.min(1000, Math.max(1, this.limits.maxEdges)),
              maxDepth: Math.min(8, Math.max(1, scope.depth ?? 1)),
              maxMilliseconds: 3000,
              maxChars: 500000,
              maxBytes: 1000000
            }
          }
        : {
            scope: this.scope(
              request.search?.trim() && request.granularity === 'type'
                ? undefined
                : request.granularity,
              scope.id ?? undefined,
              request.filters
            ),
            ...(request.search?.trim() ? { term: request.search } : {})
          }
    );
    let nodes = this.entities(
      reply,
      walking ? undefined : (scope.id ?? undefined),
      request.filters
    );
    const replies = [reply];
    // Explicitly revealed search hits stay visible, with their real kind and certainty.
    if (request.includeIds?.length) {
      const extra = await this.execute('Symbol', {
        ids: included,
        scope: this.resultScope(request.filters)
      });
      replies.push(extra);
      const combined = new Map(nodes.map((node) => [node.id, node]));
      for (const entity of this.entities(extra)) {
        const prior = combined.get(entity.id);
        combined.set(
          entity.id,
          prior?.certainty &&
            entity.certainty &&
            certaintyRank(prior.certainty) > certaintyRank(entity.certainty)
            ? prior
            : entity
        );
      }
      nodes = [...combined.values()];
      for (const entity of nodes) {
        const rawId = this.reverse.get(entity.id)!;
        const entry = this.nodes.get(rawId)!;
        if (entity.certainty)
          this.nodes.set(rawId, {
            node: entry.node,
            item: { ...entry.item, certainty: entity.certainty }
          });
      }
    }
    const displayLimit = Math.max(1, Math.min(500, this.limits.maxNodes));
    const displayTruncated = nodes.length > displayLimit;
    if (displayTruncated) {
      const selected = new Set(request.includeIds ?? []);
      nodes = [
        ...nodes.filter((node) => selected.has(node.id)),
        ...nodes.filter((node) => !selected.has(node.id))
      ].slice(0, displayLimit);
    }
    const visible = new Set(nodes.map((node) => this.reverse.get(node.id)!));
    const edges = [...reply.items, ...reply.candidates, ...reply.unresolved].flatMap((item) => {
      const edge = item.edge;
      if (!edge || !visible.has(edge.sourceNodeId) || !visible.has(edge.targetNodeId)) return [];
      return [
        {
          id: this.id(edge.id, 'rel'),
          sourceId: this.entity(edge.sourceNodeId).id,
          targetId: this.entity(edge.targetNodeId).id,
          basis: 'savedIndex',
          kinds: [edge.kind],
          certainty:
            certaintyRank(item.certainty) > certaintyRank(edge.certainty)
              ? item.certainty
              : edge.certainty,
          sourceOccurrenceId: edge.sourceOccurrenceId ?? null,
          targetOccurrenceId: edge.targetOccurrenceId ?? null,
          variantId: edge.variantId ?? null,
          evidenceCount: edge.evidence ? 1 : 0,
          inCycle: false
        }
      ];
    });
    const metadata = this.combinedMetadata(replies, nodes.length);
    if (displayTruncated) {
      metadata.truncated = true;
      metadata.truncationReasons = [
        ...new Set([...metadata.truncationReasons, 'DISPLAY_NODE_LIMIT'])
      ];
    }
    const projection: Projection = {
      scope,
      granularity: request.granularity,
      nodes,
      edges,
      totalNodeCount: nodes.length,
      totalEdgeCount: edges.length,
      truncated: metadata.truncated,
      queryMetadata: metadata
    };
    if (projectionRevision === this.projectionRevision && !this.disposed) {
      this.preparedExport = buildSavedQueryExport({
        analysisId: this.analysisId,
        pinned: this.readyReply.snapshot,
        selectedVariantId: this.variant,
        scope: { ...scope, id: origin ?? null },
        filters: request.filters,
        includedNodeIds: included ?? [],
        display: {
          granularity: request.granularity,
          nodeIds: nodes.map((node) => this.reverse.get(node.id)!),
          edgeIds: edges.map((edge) => this.reverse.get(edge.id)!),
          truncated: displayTruncated
        },
        responses: replies.map((reply, index) => ({
          operation: index === 0 ? operation : 'Symbol',
          reply
        }))
      });
      this.preparedProjection = structuredClone(request);
    }
    return {
      type: 'projection',
      requestId: request.requestId,
      analysisId: this.analysisId,
      projection
    };
  }

  private async details(
    request: Extract<WebviewToHostMessage, { type: 'getEntityDetails' }>
  ): Promise<HostToWebviewMessage> {
    this.filters(request.filters);
    this.observeCandidateFilters(request.filters);
    if (this.candidateSelection !== undefined && this.candidateSelection !== request.entityId)
      this.invalidateCandidateOrigins();
    this.candidateSelection = request.entityId;
    const detailsRevision = ++this.detailsRevision;
    const candidateEpoch = this.candidateEpoch;
    const nodeId = this.selected(request.entityId);
    const selected = this.nodes.get(nodeId)!;
    const scope = this.resultScope(request.filters);
    if (selected.item.certainty !== 'Resolved') {
      // Capture before awaiting, without registering the separate Symbol result.
      const candidate = this.entity(nodeId, selected.item.certainty);
      const symbol = await this.requestValidated('Symbol', { nodeId, scope });
      return {
        type: 'details',
        requestId: request.requestId,
        analysisId: this.analysisId,
        entity: { ...candidate },
        dependencies: [],
        dependents: [],
        queryMetadata: this.metadata(symbol, 1)
      };
    }
    const symbol = await this.execute('Symbol', { nodeId, scope });
    const entity = this.entities(symbol).find((item) => item.id === request.entityId);
    if (!entity) throw new QueryClientError('QUERY_ENTITY_NOT_RETURNED');
    const isDocument = selected.node.kind === 'Document' || selected.node.kind === 'Section';
    // Documentation reaches code through section/mention structure. Candidates remain
    // leaves in Query; ordinary code details and reverse exploration stay one hop.
    const outgoing = await this.execute(
      'Impact',
      {
        nodeId,
        scope,
        dependents: false,
        includeCandidates: true,
        ...(isDocument ? { includeDocuments: true } : {})
      },
      isDocument ? 4 : 1
    );
    const incoming = await this.execute('Impact', {
      nodeId,
      scope,
      dependents: true,
      includeCandidates: true
    });
    const metadata = this.combinedMetadata([symbol, outgoing, incoming], 1);
    const dependencies = this.entities(outgoing).filter((item) => item.id !== request.entityId);
    const dependents = this.entities(incoming).filter((item) => item.id !== request.entityId);
    let candidateOrigins: CandidateOriginAction[] = [];
    if (detailsRevision === this.detailsRevision && candidateEpoch === this.candidateEpoch) {
      candidateOrigins =
        selected.node.kind === 'SymbolMention'
          ? this.issueCandidateOrigins(outgoing, request.entityId, nodeId)
          : [];
      const visibleOrigins = new Set(candidateOrigins.map((action) => action.originId));
      for (const [id, origin] of this.candidateOrigins)
        if (origin.contextKey === request.entityId && !visibleOrigins.has(id))
          this.candidateOrigins.delete(id);
      if (this.activeCandidateOrigin && !this.candidateOrigins.has(this.activeCandidateOrigin))
        this.invalidateCandidateInvestigation();
    }
    return {
      type: 'details',
      requestId: request.requestId,
      analysisId: this.analysisId,
      entity: { ...entity },
      dependencies,
      dependents,
      relations: {
        outgoing: this.returnedRelations(outgoing),
        incoming: this.returnedRelations(incoming)
      },
      ...(candidateOrigins.length ? { candidateOrigins } : {}),
      queryMetadata: metadata
    };
  }

  private observeCandidateFilters(filters?: Filters): void {
    if (!isDeepStrictEqual(this.candidateFilters, filters ?? {})) {
      this.invalidateCandidateOrigins();
      this.candidateFilters = structuredClone(filters ?? {});
    }
  }

  private invalidateCandidateInvestigation(): void {
    this.investigationRevision++;
    this.activeCandidateOrigin = undefined;
    this.candidateInvestigations.clear();
    this.candidateSources.clear();
  }

  private invalidateCandidateOrigins(): void {
    this.candidateEpoch++;
    this.candidateOrigins.clear();
    this.invalidateCandidateInvestigation();
  }

  private issueCandidateOrigins(
    reply: QueryWireReply,
    selectionId: string,
    selectedRawId: string
  ): CandidateOriginAction[] {
    if (this.readyReply.snapshot.symbolDeclarationRowsVersion !== 1 || this.disposed) return [];
    const returned = [...reply.items, ...reply.candidates, ...reply.unresolved];
    const actions: CandidateOriginAction[] = [];
    for (const item of returned) {
      const edge = item.edge;
      if (
        !edge ||
        item.certainty !== 'Candidate' ||
        edge.certainty !== 'Candidate' ||
        edge.producer !== 'markdown-symbol-resolver' ||
        !['documents_symbol', 'mentions_symbol'].includes(edge.kind) ||
        (edge.variantId !== undefined && edge.variantId !== this.variant)
      )
        continue;
      const mention = returned.find((entry) => entry.node?.id === edge.sourceNodeId)?.node;
      const targetItem = returned.find(
        (entry) => entry.node?.id === edge.targetNodeId && entry.certainty === 'Candidate'
      );
      const target = targetItem?.node;
      if (
        !mention ||
        mention.kind !== 'SymbolMention' ||
        mention.id !== selectedRawId ||
        !target ||
        !['Type', 'Member'].includes(target.kind) ||
        !/^hsym_[0-9a-f]{64}$/.test(target.id) ||
        target.name.length === 0 ||
        target.name.length > 4096
      )
        continue;
      const relationId = this.opaque.get(`rel:${edge.id}`);
      const targetEntityId = this.opaque.get(`ty:${target.id}`);
      if (!relationId || !targetEntityId) continue;
      const existing = [...this.candidateOrigins.values()].find(
        (origin) =>
          origin.epoch === this.candidateEpoch &&
          origin.contextKey === selectionId &&
          origin.edge.id === edge.id &&
          origin.target.id === target.id
      );
      if (existing) {
        if (!actions.some((action) => action.originId === existing.action.originId))
          actions.push({ ...existing.action });
      } else {
        if (this.candidateOrigins.size >= 64) break;
        const originId = `ci_${randomBytes(16).toString('hex')}`;
        if (this.candidateOrigins.has(originId)) throw new QueryClientError('QUERY_ID_COLLISION');
        const action = { originId, relationId, targetEntityId, targetName: target.name };
        this.candidateOrigins.set(originId, {
          action,
          edge: structuredClone(edge),
          mention: structuredClone(mention),
          target: structuredClone(target),
          originalCandidate: structuredClone(targetItem!),
          reason: reply.diagnostics.some((issue) => issue.code === 'markdown.catalog-incomplete')
            ? 'catalog-incomplete'
            : (item.reason ?? 'candidate-relation'),
          snapshot: structuredClone(reply.snapshot),
          epoch: this.candidateEpoch,
          contextKey: selectionId
        });
        actions.push({ ...action });
      }
      if (actions.length === 8) break;
    }
    return actions;
  }

  private assertCandidateOrigin(originId: string): CandidateOrigin {
    const origin = this.candidateOrigins.get(originId);
    const snapshot = this.readyReply.snapshot;
    if (
      this.disposed ||
      !origin ||
      origin.epoch !== this.candidateEpoch ||
      origin.contextKey !== this.candidateSelection ||
      this.reverse.get(origin.contextKey) !== origin.mention.id ||
      origin.mention.kind !== 'SymbolMention' ||
      this.nodes.get(origin.mention.id)?.node.kind !== 'SymbolMention' ||
      origin.snapshot.workspaceId !== snapshot.workspaceId ||
      origin.snapshot.id !== snapshot.id ||
      origin.snapshot.generation !== snapshot.generation ||
      snapshot.variantCount !== 1 ||
      snapshot.variantIds.length !== 1 ||
      snapshot.variantIds[0] !== this.variant ||
      origin.snapshot.variantCount !== 1 ||
      origin.snapshot.variantIds[0] !== this.variant ||
      origin.edge.sourceNodeId !== origin.mention.id ||
      origin.edge.targetNodeId !== origin.target.id ||
      origin.edge.certainty !== 'Candidate' ||
      (origin.edge.variantId !== undefined && origin.edge.variantId !== this.variant)
    )
      throw new QueryClientError('QUERY_CANDIDATE_ORIGIN_EXPIRED');
    return origin;
  }

  /** Closing this investigation leaves its current origin available for a new explicit request. */
  dismissCandidateInvestigation(originId: string): void {
    if (this.candidateOrigins.has(originId) && this.activeCandidateOrigin === originId)
      this.invalidateCandidateInvestigation();
  }

  private async investigateCandidate(
    request: Extract<WebviewToHostMessage, { type: 'investigateCandidate' }>
  ): Promise<HostToWebviewMessage> {
    if (this.readyReply.snapshot.symbolDeclarationRowsVersion !== 1)
      throw new QueryClientError('QUERY_SYMBOL_DECLARATION_ROWS_UNSUPPORTED');
    const origin = this.assertCandidateOrigin(request.originId);
    this.invalidateCandidateInvestigation();
    this.activeCandidateOrigin = request.originId;
    const revision = this.investigationRevision;
    const assertCurrent = (): void => {
      if (
        this.assertCandidateOrigin(request.originId) !== origin ||
        revision !== this.investigationRevision ||
        this.activeCandidateOrigin !== request.originId
      )
        throw new QueryClientError('QUERY_CANDIDATE_INVESTIGATION_EXPIRED');
    };
    const reply = validateQueryReply(
      await this.requestValidated('Symbol', {
        nodeId: origin.target.id,
        scope: { variantIds: [this.variant], resultKinds: ['Code'], includeExternal: false },
        includeDeclarationRows: true,
        pageSize: 16,
        budget: {
          maxNodes: 17,
          maxEdges: 1,
          maxDepth: 1,
          maxMilliseconds: 3000,
          maxChars: 32000,
          maxBytes: 64000
        }
      })
    );
    assertCurrent();
    if (reply.snapshot.symbolDeclarationRowsVersion !== 1)
      throw new QueryClientError('QUERY_SYMBOL_DECLARATION_ROWS_UNSUPPORTED');
    const returned = [...reply.items, ...reply.candidates, ...reply.unresolved];
    const selected = returned.filter((item) => item.node);
    if (
      selected.length !== 1 ||
      selected[0].node!.id !== origin.target.id ||
      selected[0].node!.kind !== origin.target.kind ||
      selected[0].resultKind !== 'Code' ||
      returned.some((item) => item.edge || item.certainty !== 'Resolved')
    )
      throw new QueryClientError('QUERY_CANDIDATE_TARGET_INVALID');
    const rows = returned.filter((item) => item.occurrence);
    if (rows.length > 16 || returned.length !== rows.length + 1)
      throw new QueryClientError('QUERY_CANDIDATE_DECLARATION_INVALID');
    const declarations: Extract<
      HostToWebviewMessage,
      { type: 'candidateInvestigation' }
    >['declarations'] = [];
    const sources = new Map<string, CandidateSource>();
    const seen = new Set<string>();
    let unavailable = rows.length === 0;
    for (const row of rows) {
      const occurrence = row.occurrence!;
      const location = occurrence.location;
      if (
        occurrence.logicalSymbolId !== origin.target.id ||
        occurrence.variantId !== this.variant ||
        (origin.edge.targetOccurrenceId !== undefined &&
          origin.edge.targetOccurrenceId !== occurrence.id) ||
        !/^hocc_[0-9a-f]{64}\/(0|[1-9][0-9]{0,2})$/.test(row.id) ||
        !row.id.startsWith(occurrence.id + '/') ||
        row.kind !== 'declaration' ||
        row.resultKind !== 'Code' ||
        occurrence.declarations?.length !== 1 ||
        !isDeepStrictEqual(location, occurrence.declarations[0]) ||
        !isDeepStrictEqual(location, row.evidence)
      )
        throw new QueryClientError('QUERY_CANDIDATE_DECLARATION_INVALID');
      if (
        !location?.contentHash ||
        !/^[0-9a-f]{64}$/.test(location.contentHash) ||
        !location.rawSpan ||
        !row.path
      ) {
        unavailable = true;
        continue;
      }
      const key = JSON.stringify([
        occurrence.id,
        location.sourceId,
        location.contentHash,
        location.rawSpan
      ]);
      if (seen.has(key)) throw new QueryClientError('QUERY_CANDIDATE_DECLARATION_INVALID');
      seen.add(key);
      const capabilityId = `cd_${randomBytes(16).toString('hex')}`;
      if (sources.has(capabilityId)) throw new QueryClientError('QUERY_ID_COLLISION');
      const source = this.source(row.path, location)!;
      sources.set(capabilityId, {
        origin,
        revision,
        occurrenceId: occurrence.id,
        location: structuredClone(source)
      });
      declarations.push({
        capabilityId,
        occurrenceId: occurrence.id,
        variantId: this.variant,
        path: row.path
      });
    }
    assertCurrent();
    const response: Extract<HostToWebviewMessage, { type: 'candidateInvestigation' }> = {
      type: 'candidateInvestigation',
      requestId: request.requestId,
      analysisId: this.analysisId,
      originId: request.originId,
      relationId: origin.action.relationId,
      targetEntityId: origin.action.targetEntityId,
      certainty: 'Candidate',
      relationKind: origin.edge.kind,
      reason: origin.reason,
      message: '関係は未確定、宣言の存在を別途確認',
      queryMetadata: this.metadata(reply, declarations.length),
      declarations,
      ...(unavailable
        ? { unavailableReason: 'No declaration source location was returned for this variant.' }
        : {})
    };
    this.candidateInvestigations.set(request.originId, structuredClone(response));
    for (const [id, source] of sources) this.candidateSources.set(id, source);
    return response;
  }

  /** A separate, host-owned capability; never an entry in the normal declaration/evidence maps. */
  prepareCandidateSource(request: { analysisId: string; originId: string; capabilityId: string }): {
    location: SavedSourceLocation;
    assertCurrent: () => void;
  } {
    const record = this.candidateSources.get(request.capabilityId);
    const assertCurrent = (): void => {
      if (
        request.analysisId !== this.analysisId ||
        !record ||
        !/^cd_[0-9a-f]{32}$/.test(request.capabilityId) ||
        this.candidateSources.get(request.capabilityId) !== record ||
        this.assertCandidateOrigin(request.originId) !== record.origin ||
        record.origin.action.originId !== request.originId ||
        this.activeCandidateOrigin !== request.originId ||
        record.revision !== this.investigationRevision ||
        !this.candidateInvestigations.has(request.originId)
      )
        throw new QueryClientError('QUERY_CANDIDATE_SOURCE_EXPIRED');
    };
    assertCurrent();
    return { location: structuredClone(record!.location), assertCurrent };
  }

  /** Attributes belong to this reply, not to a later result stored under the same ID. */
  private relationInfo(item: QueryItem, reply: QueryWireReply): SavedRelation {
    const edge = item.edge!;
    const returned = [...reply.items, ...reply.candidates, ...reply.unresolved];
    const endpoint = (nodeId: string) => {
      const node =
        returned.find((entry) => entry.node?.id === nodeId)?.node ?? this.nodes.get(nodeId)?.node;
      return {
        id: this.id(
          nodeId,
          node?.kind === 'Project' ? 'prj' : node?.kind === 'Namespace' ? 'ns' : 'ty'
        ),
        name: node?.name
      };
    };
    const source = endpoint(edge.sourceNodeId);
    const target = endpoint(edge.targetNodeId);
    return {
      id: this.id(edge.id, 'rel'),
      sourceId: source.id,
      targetId: target.id,
      sourceName: source.name,
      targetName: target.name,
      kind: edge.kind,
      certainty:
        certaintyRank(item.certainty) > certaintyRank(edge.certainty)
          ? item.certainty
          : edge.certainty,
      evidenceCount: edge.evidence ? 1 : 0,
      sourceOccurrenceId: edge.sourceOccurrenceId ?? null,
      targetOccurrenceId: edge.targetOccurrenceId ?? null,
      variantId: edge.variantId ?? null
    };
  }

  private returnedRelations(reply: QueryWireReply): SavedRelation[] {
    const relations = new Map<string, SavedRelation>();
    for (const item of [...reply.items, ...reply.candidates, ...reply.unresolved]) {
      if (!item.edge) continue;
      const relation = this.relationInfo(item, reply);
      const prior = relations.get(relation.id);
      if (!prior || certaintyRank(relation.certainty) > certaintyRank(prior.certainty))
        relations.set(relation.id, relation);
    }
    return [...relations.values()];
  }

  private evidence(
    request: Extract<WebviewToHostMessage, { type: 'getEvidencePage' }>
  ): HostToWebviewMessage {
    if (request.cursor) throw new QueryClientError('QUERY_CURSOR_INVALID');
    const saved = this.edges.get(request.relationId);
    if (!saved) throw new QueryClientError('QUERY_EDGE_UNKNOWN');
    const evidenceId = this.id(saved.edge.id, 'ev');
    const source = this.source(saved.item.path, saved.edge.evidence);
    if (source) this.evidenceLocations.set(evidenceId, source);
    else this.evidenceLocations.delete(evidenceId);
    const items = saved.edge.evidence
      ? [
          {
            id: evidenceId,
            certainty: saved.certainty,
            kind: saved.edge.kind,
            producer: saved.edge.producer,
            documentPath: saved.item.path ?? null,
            location: saved.edge.evidence,
            sourceOccurrenceId: saved.edge.sourceOccurrenceId ?? null,
            targetOccurrenceId: saved.edge.targetOccurrenceId ?? null,
            variantId: saved.edge.variantId ?? null
          }
        ]
      : [];
    return {
      type: 'evidencePage',
      requestId: request.requestId,
      analysisId: this.analysisId,
      relationId: request.relationId,
      relation: { ...this.relationInfo(saved.item, saved.reply), certainty: saved.certainty },
      total: items.length,
      items,
      nextCursor: null,
      queryMetadata: this.metadata(saved.reply, items.length)
    };
  }

  /** Resolves only IDs actually issued by this pinned provider, never paths from the view. */
  resolveSource(
    request: Extract<WebviewToHostMessage, { type: 'openDeclaration' | 'openEvidence' }>
  ): SavedSourceLocation {
    if (this.disposed || request.analysisId !== this.analysisId)
      throw new QueryClientError('QUERY_ANALYSIS_EXPIRED');
    if (request.type === 'openDeclaration' && (request.declarationIndex ?? 0) !== 0)
      throw new QueryClientError('QUERY_DECLARATION_UNAVAILABLE');
    const source =
      request.type === 'openDeclaration'
        ? this.declarations.get(request.entityId)
        : this.evidenceLocations.get(request.evidenceId);
    if (!source) throw new QueryClientError('QUERY_SOURCE_NOT_RETURNED');
    return { ...source, span: { ...source.span } };
  }

  private source(
    relativePath: string | undefined,
    location: QueryNode['location']
  ): SavedSourceLocation | undefined {
    if (!relativePath || !location?.contentHash || !location.rawSpan) return undefined;
    return {
      root: this.root,
      relativePath,
      contentHash: location.contentHash,
      span: { ...location.rawSpan }
    };
  }

  private metadata(reply: QueryWireReply, returnedCount: number): QueryResultMetadata {
    return {
      provider: 'savedIndex',
      workspaceId: reply.snapshot.workspaceId,
      snapshotId: reply.snapshot.id,
      generation: reply.snapshot.generation,
      variantIds: [this.variant],
      coverage: reply.snapshot.coverage,
      freshness: reply.snapshot.freshness,
      truncated: reply.truncated,
      truncationReasons: [...reply.truncationReasons],
      diagnostics: reply.diagnostics.map((issue) => ({ ...issue })),
      returnedCount,
      totalKind: 'returned',
      candidateCount: reply.candidates.length,
      unresolvedCount: reply.unresolved.length,
      ...(reply.snapshot.resultFilterVersion !== undefined
        ? { resultFilterVersion: reply.snapshot.resultFilterVersion }
        : {})
    };
  }

  private combinedMetadata(replies: QueryWireReply[], returnedCount: number): QueryResultMetadata {
    const metadata = this.metadata(replies[0], returnedCount);
    metadata.truncated = replies.some((reply) => reply.truncated);
    metadata.truncationReasons = [...new Set(replies.flatMap((reply) => reply.truncationReasons))];
    metadata.diagnostics = [
      ...new Map(
        replies
          .flatMap((reply) => reply.diagnostics)
          .map((issue) => [JSON.stringify([issue.code, issue.message]), { ...issue }])
      ).values()
    ];
    metadata.candidateCount = replies.reduce((count, reply) => count + reply.candidates.length, 0);
    metadata.unresolvedCount = replies.reduce((count, reply) => count + reply.unresolved.length, 0);
    return metadata;
  }

  dispose(): void {
    this.disposed = true;
    this.invalidateCandidateOrigins();
    this.preparedExport = undefined;
    this.preparedProjection = undefined;
    this.client.dispose();
    this.nodes.clear();
    this.edges.clear();
    this.cursors.clear();
    this.opaque.clear();
    this.reverse.clear();
    this.declarations.clear();
    this.evidenceLocations.clear();
  }
}

function certaintyRank(value: QueryCertainty): number {
  return value === 'Unresolved' ? 2 : value === 'Candidate' ? 1 : 0;
}
