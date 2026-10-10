import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import * as path from 'node:path';

export type QueryCertainty = 'Resolved' | 'Candidate' | 'Unresolved';
export interface QueryLocation {
  sourceId: string;
  contentHash?: string;
  rawSpan?: { start: number; length: number; end: number };
}
export interface QueryNode {
  id: string;
  kind: string;
  name: string;
  parentId?: string;
  signature?: string;
  location?: QueryLocation;
}
export interface QueryEdge {
  id: string;
  sourceNodeId: string;
  targetNodeId: string;
  sourceOccurrenceId?: string;
  targetOccurrenceId?: string;
  variantId?: string;
  kind: string;
  certainty: QueryCertainty;
  producer: string;
  evidence?: QueryLocation;
}
export interface QueryItem {
  id: string;
  kind: string;
  label: string;
  certainty: QueryCertainty;
  path?: string;
  node?: QueryNode;
  edge?: QueryEdge;
  evidence?: QueryLocation;
}
export interface QuerySnapshot {
  workspaceId: string;
  id: string;
  generation: number;
  variantIds: string[];
  variantCount: number;
  coverage: 'CompleteWithinScope' | 'Partial' | 'Failed';
  freshness: string;
}
export interface QueryWireReply {
  apiVersion: '1';
  requestId: string;
  snapshot: QuerySnapshot;
  items: QueryItem[];
  candidates: QueryItem[];
  unresolved: QueryItem[];
  truncated: boolean;
  truncationReasons: string[];
  nextCursor?: string;
  diagnostics: { code: string; message: string }[];
  errors: { code: string; message: string }[];
}
export interface SavedQueryClient {
  readonly ready: Promise<QueryWireReply>;
  request(request: Record<string, unknown>): Promise<QueryWireReply>;
  dispose(): void;
}

export class QueryClientError extends Error {
  constructor(public readonly code: string) {
    super(`Saved Query failed (${code}).`);
  }
}

/** One owned, read-only host pins its IndexReader until this client is disposed. */
export class QueryClient implements SavedQueryClient {
  readonly ready: Promise<QueryWireReply>;
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly pending = new Map<
    string,
    {
      resolve: (reply: QueryWireReply) => void;
      reject: (error: QueryClientError) => void;
      timer: ReturnType<typeof setTimeout>;
    }
  >();
  private buffer = Buffer.alloc(0);
  private closed = false;
  private receivedReady = false;
  private snapshot?: QuerySnapshot;
  private settleReady!: (reply: QueryWireReply) => void;
  private rejectReady!: (error: QueryClientError) => void;
  private readonly readyTimer: ReturnType<typeof setTimeout>;

  constructor(options: { dotnetPath: string; hostPath: string; root: string; cwd: string }) {
    if (
      ![options.dotnetPath, options.hostPath, options.root, options.cwd].every(
        (value) => path.isAbsolute(value) && !value.includes('\0')
      )
    )
      throw new QueryClientError('QUERY_HOST_ARGUMENT_INVALID');
    this.ready = new Promise((resolve, reject) => {
      this.settleReady = resolve;
      this.rejectReady = reject;
    });
    this.readyTimer = setTimeout(() => this.fail('QUERY_HOST_TIMEOUT'), 10000);
    this.child = spawn(options.dotnetPath, [options.hostPath, '--root', options.root], {
      cwd: options.cwd,
      shell: false,
      windowsHide: true,
      stdio: 'pipe'
    });
    this.child.stdout.on('data', (data: Buffer) => this.read(data));
    // Runtime failures can include machine paths; retain neither stderr nor source text.
    this.child.stderr.on('data', () => undefined);
    this.child.once('error', () => this.fail('QUERY_HOST_UNAVAILABLE'));
    this.child.once('close', () => this.fail('QUERY_HOST_EXIT'));
    this.child.stdin.on('error', () => this.fail('QUERY_HOST_EXIT'));
  }

  async request(request: Record<string, unknown>): Promise<QueryWireReply> {
    await this.ready;
    const id = request.requestId;
    if (this.closed) throw new QueryClientError('QUERY_SESSION_CLOSED');
    if (typeof id !== 'string' || id === 'ready' || this.pending.has(id) || this.pending.size >= 8)
      throw new QueryClientError('QUERY_REQUEST_INVALID');
    const line = JSON.stringify(request) + '\n';
    if (line.length > 65536) throw new QueryClientError('QUERY_REQUEST_LIMIT');
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => this.fail('QUERY_HOST_TIMEOUT'), 10000);
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(line, 'utf8', (error) => {
        if (error) this.fail('QUERY_HOST_EXIT');
      });
    });
  }

  private read(data: Buffer): void {
    if (this.closed) return;
    this.buffer = Buffer.concat([this.buffer, data]);
    // Query's maximum serialized budget is 4 MB. Bound before decoding or parsing.
    if (this.buffer.length > 4000002) {
      this.fail('QUERY_RESPONSE_LIMIT');
      return;
    }
    let newline: number;
    while ((newline = this.buffer.indexOf(10)) >= 0) {
      const line = this.buffer.subarray(0, newline);
      this.buffer = this.buffer.subarray(newline + 1);
      try {
        const value: unknown = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(line));
        if (
          !isRecord(value) ||
          value.apiVersion !== '1' ||
          typeof value.requestId !== 'string' ||
          !Array.isArray(value.errors)
        )
          throw new QueryClientError('QUERY_PROTOCOL_INVALID');
        const errorCode = value.errors.length ? readIssue(value.errors[0]) : undefined;
        if (!this.receivedReady) {
          if (value.requestId !== 'ready') throw new QueryClientError('QUERY_HANDSHAKE_INVALID');
          if (errorCode) throw new QueryClientError(errorCode);
          const reply = validateReply(value);
          this.snapshot = reply.snapshot;
          this.receivedReady = true;
          clearTimeout(this.readyTimer);
          this.settleReady(reply);
        } else {
          if (value.requestId === 'host' && errorCode) throw new QueryClientError(errorCode);
          const waiting = this.pending.get(value.requestId);
          if (!waiting) throw new QueryClientError('QUERY_REQUEST_MISMATCH');
          const reply = errorCode && !value.snapshot ? undefined : validateReply(value);
          if (
            reply &&
            (reply.snapshot.workspaceId !== this.snapshot!.workspaceId ||
              reply.snapshot.id !== this.snapshot!.id ||
              reply.snapshot.generation !== this.snapshot!.generation)
          )
            throw new QueryClientError('QUERY_GENERATION_CHANGED');
          this.pending.delete(value.requestId);
          clearTimeout(waiting.timer);
          if (!reply) waiting.reject(new QueryClientError(errorCode!));
          else waiting.resolve(reply);
        }
      } catch (error) {
        this.fail(error instanceof QueryClientError ? error.code : 'QUERY_PROTOCOL_INVALID');
        return;
      }
    }
  }

  private fail(code: string): void {
    if (this.closed) return;
    this.closed = true;
    clearTimeout(this.readyTimer);
    const error = new QueryClientError(code);
    this.rejectReady(error);
    for (const waiting of this.pending.values()) {
      clearTimeout(waiting.timer);
      waiting.reject(error);
    }
    this.pending.clear();
    this.buffer = Buffer.alloc(0);
    this.child.stdin.end();
    // QueryHost has no children or analysis work. Kill only the process we spawned.
    if (this.child.exitCode === null && this.child.signalCode === null) this.child.kill();
  }

  dispose(): void {
    this.fail('QUERY_SESSION_CLOSED');
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function readIssue(value: unknown): string {
  if (
    !isRecord(value) ||
    typeof value.code !== 'string' ||
    !/^[A-Za-z0-9_.-]{1,100}$/.test(value.code)
  )
    throw new QueryClientError('QUERY_PROTOCOL_INVALID');
  return value.code;
}
function isText(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= 65536;
}
function optionalText(value: unknown): boolean {
  return value === undefined || isText(value);
}
function location(value: unknown): boolean {
  if (value === undefined) return true;
  if (!isRecord(value) || !isText(value.sourceId) || !optionalText(value.contentHash)) return false;
  if (value.rawSpan === undefined) return true;
  const span = value.rawSpan;
  return (
    isRecord(span) &&
    [span.start, span.length, span.end].every(
      (part) => Number.isSafeInteger(part) && (part as number) >= 0
    ) &&
    (span.start as number) + (span.length as number) === span.end
  );
}
function validateReply(value: Record<string, unknown>): QueryWireReply {
  const snapshot = value.snapshot;
  if (
    !isRecord(snapshot) ||
    !isText(snapshot.workspaceId) ||
    !isText(snapshot.id) ||
    !Number.isSafeInteger(snapshot.generation) ||
    (snapshot.generation as number) < 0 ||
    !Number.isSafeInteger(snapshot.variantCount) ||
    !Array.isArray(snapshot.variantIds) ||
    snapshot.variantIds.length > 64 ||
    (snapshot.variantCount as number) < snapshot.variantIds.length ||
    !snapshot.variantIds.every(isText) ||
    new Set(snapshot.variantIds).size !== snapshot.variantIds.length ||
    !['CompleteWithinScope', 'Partial', 'Failed'].includes(String(snapshot.coverage)) ||
    !['unverified', 'dirty', 'verified-current'].includes(String(snapshot.freshness)) ||
    typeof value.truncated !== 'boolean' ||
    !Array.isArray(value.truncationReasons) ||
    !value.truncationReasons.every((x) => typeof x === 'string') ||
    !Array.isArray(value.diagnostics) ||
    !Array.isArray(value.errors) ||
    !optionalText(value.nextCursor) ||
    ![value.items, value.candidates, value.unresolved].every(
      (x) => Array.isArray(x) && x.length <= 1500
    )
  )
    throw new QueryClientError('QUERY_PROTOCOL_INVALID');
  for (const item of [
    ...(value.items as unknown[]),
    ...(value.candidates as unknown[]),
    ...(value.unresolved as unknown[])
  ]) {
    if (
      !isRecord(item) ||
      !isText(item.id) ||
      item.id.length > 256 ||
      typeof item.label !== 'string' ||
      typeof item.kind !== 'string' ||
      !['Resolved', 'Candidate', 'Unresolved'].includes(String(item.certainty)) ||
      !optionalText(item.path) ||
      !location(item.evidence) ||
      (item.node !== undefined &&
        (!isRecord(item.node) ||
          item.node.id !== item.id ||
          typeof item.node.kind !== 'string' ||
          typeof item.node.name !== 'string' ||
          !optionalText(item.node.parentId) ||
          !optionalText(item.node.signature) ||
          !location(item.node.location))) ||
      (item.edge !== undefined &&
        (!isRecord(item.edge) ||
          item.edge.id !== item.id ||
          typeof item.edge.sourceNodeId !== 'string' ||
          typeof item.edge.targetNodeId !== 'string' ||
          !isText(item.edge.kind) ||
          !isText(item.edge.producer) ||
          !optionalText(item.edge.sourceOccurrenceId) ||
          !optionalText(item.edge.targetOccurrenceId) ||
          !optionalText(item.edge.variantId) ||
          !location(item.edge.evidence) ||
          !['Resolved', 'Candidate', 'Unresolved'].includes(String(item.edge.certainty))))
    )
      throw new QueryClientError('QUERY_PROTOCOL_INVALID');
  }
  for (const [items, certainty] of [
    [value.items, 'Resolved'],
    [value.candidates, 'Candidate'],
    [value.unresolved, 'Unresolved']
  ] as const) {
    if (!(items as QueryItem[]).every((item) => item.certainty === certainty))
      throw new QueryClientError('QUERY_PROTOCOL_INVALID');
  }
  for (const issue of [...value.errors, ...value.diagnostics]) {
    readIssue(issue);
    if (!isRecord(issue) || typeof issue.message !== 'string')
      throw new QueryClientError('QUERY_PROTOCOL_INVALID');
  }
  return value as unknown as QueryWireReply;
}
