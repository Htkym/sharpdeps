import { createHash, randomBytes } from 'node:crypto';
import type { Granularity } from './reportV2';
import type { ReportBridge } from './reportBridge';
import {
  QueryClientError,
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
  WebviewToHostMessage,
  Filters
} from '../view/protocolV2';

const CAPABILITIES = {
  typeGraph: true,
  evidence: true,
  generatedDocuments: false,
  cycleWitness: false,
  search: true
};

/** An explicitly selected saved generation; legacy data is never spliced into it. */
export class SavedQueryProvider implements ReportBridge {
  readonly analysisId = `an_${randomBytes(8).toString('hex')}`;
  private readonly nodes = new Map<string, { node: QueryNode; item: QueryItem }>();
  private readonly opaque = new Map<string, string>();
  private readonly reverse = new Map<string, string>();
  private readonly edges = new Map<
    string,
    { edge: QueryEdge; item: QueryItem; reply: QueryWireReply }
  >();
  private readonly cursors = new Map<string, { cursor: string; key: string }>();
  private sequence = 0;

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

  async handle(message: unknown): Promise<HostToWebviewMessage> {
    const checked = validateWebviewMessage(message);
    if (!checked.ok)
      return { type: 'error', code: 'protocol.invalidMessage', message: checked.errors.join('; ') };
    const request = checked.value;
    const requestId = 'requestId' in request ? request.requestId : undefined;
    try {
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

  private async execute(
    kind: string,
    fields: Record<string, unknown> = {}
  ): Promise<QueryWireReply> {
    const reply = await this.client.request({
      requestId: `provider_${++this.sequence}`,
      kind,
      scope: { variantIds: [this.variant] },
      freshness: 'AllowStale',
      budget: {
        maxNodes: Math.max(1, Math.min(500, this.limits.maxNodes)),
        maxEdges: Math.max(1, Math.min(1000, this.limits.maxEdges)),
        maxDepth: 1,
        maxMilliseconds: 3000,
        maxChars: 500000,
        maxBytes: 1000000
      },
      ...fields
    });
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
        this.edges.set(id, { edge: item.edge, item, reply });
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
    return {
      id: this.id(
        nodeId,
        granularity === 'project' ? 'prj' : granularity === 'namespace' ? 'ns' : 'ty'
      ),
      name: entry.node.name,
      fullName: entry.node.signature || entry.node.name,
      granularity,
      kind: entry.node.kind,
      certainty: certainty ?? entry.item.certainty,
      isExternal: entry.node.kind === 'ExternalSymbol'
    };
  }

  private selected(opaqueId: string): string {
    const id = this.reverse.get(opaqueId);
    if (!id || !this.nodes.has(id)) throw new QueryClientError('QUERY_ENTITY_UNKNOWN');
    return id;
  }

  private scope(granularity?: Granularity, parentId?: string): Record<string, unknown> {
    const scope: Record<string, unknown> = { variantIds: [this.variant] };
    if (granularity)
      scope.kind =
        granularity === 'project' ? 'Project' : granularity === 'namespace' ? 'Namespace' : 'Type';
    if (parentId) {
      const parent = this.nodes.get(this.selected(parentId))!.node;
      if (parent.kind === 'Project') scope.projectId = parent.id;
      else throw new QueryClientError('QUERY_SCOPE_UNSUPPORTED');
    }
    return scope;
  }

  private filters(filters?: Filters): void {
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
        request.parentId
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
    const reply = await this.execute(
      walking ? 'Impact' : request.search?.trim() ? 'Search' : 'Browse',
      walking
        ? {
            nodeId: origin,
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
              scope.id ?? undefined
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
      const extra = await this.execute('Symbol', { ids: included });
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
          evidenceCount: 0,
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
    const nodeId = this.selected(request.entityId);
    const selected = this.nodes.get(nodeId)!;
    const symbol = await this.execute('Symbol', { nodeId });
    const entity = this.entities(symbol).find((item) => item.id === request.entityId);
    if (!entity) throw new QueryClientError('QUERY_ENTITY_NOT_RETURNED');
    if (selected.item.certainty !== 'Resolved') {
      this.nodes.set(nodeId, selected);
      return {
        type: 'details',
        requestId: request.requestId,
        analysisId: this.analysisId,
        entity: { ...entity, certainty: selected.item.certainty },
        dependencies: [],
        dependents: [],
        queryMetadata: this.metadata(symbol, 1)
      };
    }
    const outgoing = await this.execute('Impact', {
      nodeId,
      dependents: false,
      includeCandidates: true
    });
    const incoming = await this.execute('Impact', {
      nodeId,
      dependents: true,
      includeCandidates: true
    });
    const metadata = this.combinedMetadata([symbol, outgoing, incoming], 1);
    return {
      type: 'details',
      requestId: request.requestId,
      analysisId: this.analysisId,
      entity: { ...entity },
      dependencies: this.entities(outgoing).filter((entity) => entity.id !== request.entityId),
      dependents: this.entities(incoming).filter((entity) => entity.id !== request.entityId),
      queryMetadata: metadata
    };
  }

  private evidence(
    request: Extract<WebviewToHostMessage, { type: 'getEvidencePage' }>
  ): HostToWebviewMessage {
    if (request.cursor) throw new QueryClientError('QUERY_CURSOR_INVALID');
    const saved = this.edges.get(request.relationId);
    if (!saved) throw new QueryClientError('QUERY_EDGE_UNKNOWN');
    const items = saved.edge.evidence
      ? [
          {
            id: this.id(saved.edge.id, 'ev'),
            certainty:
              certaintyRank(saved.item.certainty) > certaintyRank(saved.edge.certainty)
                ? saved.item.certainty
                : saved.edge.certainty,
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
      total: items.length,
      items,
      nextCursor: null,
      queryMetadata: this.metadata(saved.reply, items.length)
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
      unresolvedCount: reply.unresolved.length
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
    this.client.dispose();
    this.nodes.clear();
    this.edges.clear();
    this.cursors.clear();
    this.opaque.clear();
    this.reverse.clear();
  }
}

function certaintyRank(value: QueryCertainty): number {
  return value === 'Unresolved' ? 2 : value === 'Candidate' ? 1 : 0;
}
