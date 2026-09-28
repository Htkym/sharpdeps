// Analysis result store (SD-013).
//
// The host registers an analyzer result (report v2 + evidence NDJSON) and serves
// search, entity details, and evidence pages from it. The webview never supplies a
// path, a byte offset, or a line number: it receives an opaque cursor that this store
// resolves against the registered analysis.
//
// Registration is validated before anything is served:
//   - the snapshot must satisfy the v2 contract (structure, ids, referential integrity),
//   - the evidence file must stay inside the report directory and be a plain file name,
//   - file sizes stay within the configured caps,
//   - the evidence record count must match the report's evidence index.
//
// Evidence is read line by line with a streaming UTF-8 decoder, so a page can never
// start in the middle of a multi-byte character.

import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import type {
  AnalysisRelation,
  AnalysisSnapshot,
  DeclarationRecord,
  EvidenceRecord,
  Granularity,
  PhysicalSpan,
  SourceDocument
} from './reportV2';
import {
  validateDeclarationRecord,
  validateEvidenceRecord,
  validateSnapshot
} from './reportV2Validation';
import { buildProjection, entitiesOf, type ProjectionRequest } from './graphProjection';
import type { EntitySummary } from '../view/protocolV2';

export const DEFAULT_PAGE_SIZE = 100;
export const MAX_PAGE_SIZE = 500;

/** Caps that keep a malformed analyzer output from exhausting the host. */
export interface ReportStoreLimits {
  maxReportBytes: number;
  maxEvidenceBytes: number;
  maxRegisteredAnalyses: number;
  maxGeneratedDocumentBytes: number;
  maxDeclarationsBytes: number;
}

const DEFAULT_LIMITS: ReportStoreLimits = {
  // The fixed Medium fixture produces a 59 MB snapshot (ADR-0004).
  maxReportBytes: 128 * 1024 * 1024,
  maxEvidenceBytes: 512 * 1024 * 1024,
  maxRegisteredAnalyses: 2,
  maxGeneratedDocumentBytes: 4 * 1024 * 1024,
  maxDeclarationsBytes: 64 * 1024 * 1024
};

export class ReportStoreError extends Error {
  constructor(
    message: string,
    public readonly code:
      | 'unknownAnalysis'
      | 'staleAnalysis'
      | 'invalidReport'
      | 'invalidEvidence'
      | 'invalidCursor'
      | 'limitExceeded'
      | 'ioError'
  ) {
    super(message);
    this.name = 'ReportStoreError';
  }
}

export interface EntityRecord extends EntitySummary {
  id: string;
  granularity: Granularity;
  name: string;
  fullName: string;
  kind: string;
  projectName?: string;
  targetFramework?: string;
  isExternal: boolean;
  inCycle: boolean;
}

export interface SearchPage {
  total: number;
  items: EntityRecord[];
  nextCursor?: string;
}

/** Display projection served to the graph view; ids are real analysis ids. */
export interface ProjectionView {
  nodes: EntityRecord[];
  edges: Array<{
    /** Representative relation id: the one an evidence request should use. */
    id: string;
    sourceId: string;
    targetId: string;
    basis: string;
    kinds: string[];
    /** Occurrences across all relations this edge aggregates. */
    evidenceCount: number;
    inCycle: boolean;
    generatedEvidenceCount: number;
    publicSurfaceEvidenceCount: number;
    underlyingRelationIds: string[];
    /**
     * Per-relation detail of an aggregated edge, representative first. The inspector
     * shows the breakdown so the edge count and the paged totals can be reconciled.
     */
    underlyingRelations: Array<{
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

export interface EvidencePage {
  relationId: string;
  total: number;
  items: EvidenceRecord[];
  nextCursor?: string;
}

export interface EntityDetails {
  entity: EntityRecord;
  dependencies: EntityRecord[];
  dependents: EntityRecord[];
  relations: AnalysisRelation[];
}

/** A type declaration that contains a document offset (SD-019). */
export interface DeclarationMatch {
  typeId: string;
  projectVariantId: string;
  declarationIndex: number;
  span: PhysicalSpan;
  documentId: string;
  relativePath: string;
  isPartial: boolean;
}

interface RelationOffset {
  startByte: number;
  count: number;
}

interface RegisteredAnalysis {
  analysisId: string;
  report: AnalysisSnapshot;
  evidencePath: string;
  evidenceBytes: number;
  relationOffsets: Map<string, RelationOffset>;
  declarationsPath?: string;
  declarationsBytes: number;
  /** Built on first use: declarations by type and by document. */
  declarationsByType?: Map<string, DeclarationRecord[]>;
  declarationsByDocument?: Map<string, DeclarationRecord[]>;
  /** Built on first use: evidence ids to their relation and line within the block. */
  evidenceById?: Map<string, { relationId: string; lineIndex: number }>;
  documentIdByPath: Map<string, string>;
  entities: EntityRecord[];
  entityById: Map<string, EntityRecord>;
  relationById: Map<string, AnalysisRelation>;
  relationsByEntity: Map<string, AnalysisRelation[]>;
  cycleMembers: Set<string>;
  targetPath?: string;
  stale: boolean;
}

interface CursorState {
  analysisId: string;
  kind: 'search' | 'evidence';
  key: string;
  offset: number;
}

export interface RegisterOptions {
  /** Directory that contains the report and evidence files. */
  directory: string;
  reportFileName: string;
  /** Checked after asynchronous IO, immediately before publishing the result. */
  isCurrent?: () => boolean;
  targetPath?: string;
}

export class ReportStore {
  private readonly analyses = new Map<string, RegisteredAnalysis>();
  private readonly cursors = new Map<string, CursorState>();
  private cursorSequence = 0;
  private readonly limits: ReportStoreLimits;

  constructor(limits: Partial<ReportStoreLimits> = {}) {
    this.limits = { ...DEFAULT_LIMITS, ...limits };
  }

  get analysisIds(): string[] {
    return [...this.analyses.keys()];
  }

  /** The analysis a UI request is expected to act on: the most recently registered one. */
  get currentAnalysisId(): string | undefined {
    return [...this.analyses.keys()].at(-1);
  }

  async register(options: RegisterOptions): Promise<AnalysisSnapshot> {
    const reportPath = safeJoin(options.directory, options.reportFileName);
    const size = await fileSize(reportPath);
    if (size > this.limits.maxReportBytes) {
      throw new ReportStoreError(
        `The report is larger than the ${this.limits.maxReportBytes} byte limit.`,
        'limitExceeded'
      );
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(await fs.promises.readFile(reportPath, 'utf8'));
    } catch (error) {
      throw new ReportStoreError(
        `The report could not be read: ${error instanceof Error ? error.message : String(error)}`,
        'invalidReport'
      );
    }

    const validation = validateSnapshot(parsed);
    if (!validation.ok) {
      throw new ReportStoreError(
        `The report does not satisfy the v2 contract: ${validation.errors.join('; ')}`,
        'invalidReport'
      );
    }

    const report = validation.value;
    if (report.completeness === 'failed') {
      throw new ReportStoreError(
        `The analysis failed: ${report.limitations.map((item) => item.message).join('; ') || 'No usable result was produced.'}`,
        'invalidReport'
      );
    }
    const index = report.evidenceIndex;
    if (index === null) {
      throw new ReportStoreError('The report has no evidence index.', 'invalidReport');
    }

    // The evidence file must be a plain file name beside the report: no absolute path,
    // no directory traversal, no subdirectory.
    const evidencePath = safeJoin(options.directory, index.fileName);
    if (path.basename(index.fileName) !== index.fileName) {
      throw new ReportStoreError(
        `The evidence file name must not contain a directory: ${index.fileName}`,
        'invalidEvidence'
      );
    }

    const evidenceBytes = await fileSize(evidencePath);
    if (evidenceBytes > this.limits.maxEvidenceBytes) {
      throw new ReportStoreError(
        `The evidence file is larger than the ${this.limits.maxEvidenceBytes} byte limit.`,
        'limitExceeded'
      );
    }

    const relationOffsets = new Map<string, RelationOffset>();
    for (const entry of index.relations) {
      relationOffsets.set(entry.relationId, { startByte: entry.startByte, count: entry.count });
    }

    const indexedTotal = index.relations.reduce((total, entry) => total + entry.count, 0);
    const actualTotal = await countLines(evidencePath);
    if (indexedTotal !== actualTotal) {
      throw new ReportStoreError(
        `The evidence index declares ${indexedTotal} record(s) but the file has ${actualTotal} line(s).`,
        'invalidEvidence'
      );
    }

    const analysis = this.buildAnalysis(report, evidencePath, evidenceBytes, relationOffsets);
    analysis.targetPath = options.targetPath;

    // Declarations are optional (Quick has none). When present, the file must satisfy
    // the same path and count rules as the evidence file.
    const declarationIndex = report.declarationIndex;
    if (declarationIndex) {
      if (path.basename(declarationIndex.fileName) !== declarationIndex.fileName) {
        throw new ReportStoreError(
          `The declarations file name must not contain a directory: ${declarationIndex.fileName}`,
          'invalidReport'
        );
      }

      const declarationsPath = safeJoin(options.directory, declarationIndex.fileName);
      const declarationsBytes = await fileSize(declarationsPath);
      if (declarationsBytes > this.limits.maxDeclarationsBytes) {
        throw new ReportStoreError(
          `The declarations file is larger than the ${this.limits.maxDeclarationsBytes} byte limit.`,
          'limitExceeded'
        );
      }

      const indexedDeclarations = declarationIndex.types.reduce(
        (total, entry) => total + entry.count,
        0
      );
      const actualDeclarations = await countLines(declarationsPath);
      if (indexedDeclarations !== actualDeclarations) {
        throw new ReportStoreError(
          `The declaration index declares ${indexedDeclarations} record(s) but the file has ${actualDeclarations} line(s).`,
          'invalidReport'
        );
      }

      analysis.declarationsPath = declarationsPath;
      analysis.declarationsBytes = declarationsBytes;
    }

    if (options.isCurrent && !options.isCurrent()) {
      throw new ReportStoreError(
        'The analysis was superseded before registration.',
        'staleAnalysis'
      );
    }
    this.evictIfNeeded();
    this.analyses.set(report.analysisId, analysis);
    return report;
  }

  release(analysisId: string): void {
    this.analyses.delete(analysisId);
    for (const [cursor, state] of this.cursors) {
      if (state.analysisId === analysisId) {
        this.cursors.delete(cursor);
      }
    }
  }

  search(
    analysisId: string,
    query: string,
    options: { limit?: number; cursor?: string; granularity?: Granularity; parentId?: string } = {}
  ): SearchPage {
    const analysis = this.requireAnalysis(analysisId);
    const limit = boundLimit(options.limit);
    const key = JSON.stringify([query, options.granularity, options.parentId]);
    const start = options.cursor
      ? this.resolveCursor(options.cursor, analysisId, 'search', key).offset
      : 0;

    const needle = query.trim().toLowerCase();
    const matches = analysis.entities.filter(
      (entity) =>
        (options.granularity === undefined || entity.granularity === options.granularity) &&
        (!options.parentId ||
          (entity.granularity === 'namespace'
            ? entity.projectId === options.parentId
            : entity.namespaceId === options.parentId)) &&
        (needle.length === 0 ||
          entity.name.toLowerCase().includes(needle) ||
          entity.fullName.toLowerCase().includes(needle))
    );

    const ordered = matches
      .map((entity) => ({ entity, rank: rankOf(entity, needle) }))
      .sort(
        (left, right) =>
          left.rank - right.rank || left.entity.fullName.localeCompare(right.entity.fullName)
      )
      .map((entry) => entry.entity);

    const items = ordered.slice(start, start + limit);
    const nextOffset = start + items.length;
    return {
      total: ordered.length,
      items,
      nextCursor:
        nextOffset < ordered.length
          ? this.issueCursor({ analysisId, kind: 'search', key, offset: nextOffset })
          : undefined
    };
  }

  getEntityDetails(analysisId: string, entityId: string): EntityDetails | undefined {
    const analysis = this.requireAnalysis(analysisId);
    const entity = analysis.entityById.get(entityId);
    if (!entity) {
      return undefined;
    }

    const directRelations = analysis.relationsByEntity.get(entityId) ?? [];
    const mixedGranularity = directRelations.some(
      (relation) =>
        analysis.entityById.get(relation.sourceEntityId)?.granularity !==
        analysis.entityById.get(relation.targetEntityId)?.granularity
    );
    if (entity.granularity !== 'type' && !mixedGranularity) {
      const projection = this.getProjection(analysisId, {
        granularity: entity.granularity,
        maxNodes: Number.MAX_SAFE_INTEGER,
        maxEdges: Number.MAX_SAFE_INTEGER
      });
      const edges = projection.edges.filter(
        (edge) => edge.sourceId === entityId || edge.targetId === entityId
      );
      return {
        entity: projection.nodes.find((node) => node.id === entityId) ?? entity,
        dependencies: uniqueById(
          edges
            .filter((edge) => edge.sourceId === entityId)
            .map((edge) => analysis.entityById.get(edge.targetId)!)
        ),
        dependents: uniqueById(
          edges
            .filter((edge) => edge.targetId === entityId)
            .map((edge) => analysis.entityById.get(edge.sourceId)!)
        ),
        relations: edges.flatMap((edge) =>
          edge.underlyingRelationIds.map((id) => analysis.relationById.get(id)!)
        )
      };
    }
    const relations = analysis.relationsByEntity.get(entityId) ?? [];
    const dependencies = relations
      .filter((relation) => relation.sourceEntityId === entityId)
      .map((relation) => analysis.entityById.get(relation.targetEntityId))
      .filter((entry): entry is EntityRecord => entry !== undefined);
    const dependents = relations
      .filter((relation) => relation.targetEntityId === entityId)
      .map((relation) => analysis.entityById.get(relation.sourceEntityId))
      .filter((entry): entry is EntityRecord => entry !== undefined);

    return {
      entity,
      dependencies: uniqueById(dependencies),
      dependents: uniqueById(dependents),
      relations
    };
  }

  /** The validated snapshot, for callers that need the whole model. */
  getReport(analysisId: string): AnalysisSnapshot {
    return this.requireAnalysis(analysisId).report;
  }

  getTargetPath(analysisId: string): string | undefined {
    return this.requireAnalysis(analysisId).targetPath;
  }
  markStale(analysisId: string): void {
    this.requireAnalysis(analysisId).stale = true;
  }
  isStale(analysisId: string): boolean {
    return this.requireAnalysis(analysisId).stale;
  }

  getRelation(analysisId: string, relationId: string): AnalysisRelation | undefined {
    return this.requireAnalysis(analysisId).relationById.get(relationId);
  }

  /** Document id of a workspace-relative path, or undefined when it is not in the analysis. */
  documentIdForPath(analysisId: string, relativePath: string): string | undefined {
    return this.requireAnalysis(analysisId).documentIdByPath.get(
      normalizeManifestPath(relativePath)
    );
  }

  /**
   * Types whose declaration contains the offset in a document (SD-019). One match per
   * project variant, innermost declaration first, so a cursor is never bound to a type
   * of another project by name alone.
   */
  async findTypesAt(
    analysisId: string,
    documentId: string,
    offset: number
  ): Promise<DeclarationMatch[]> {
    const analysis = this.requireAnalysis(analysisId);
    await this.ensureDeclarations(analysis);
    const records = analysis.declarationsByDocument?.get(documentId) ?? [];

    const matches = records
      .filter(
        (record) => offset >= record.span.start && offset < record.span.start + record.span.length
      )
      .sort((left, right) => left.span.length - right.span.length);

    const perVariant: DeclarationMatch[] = [];
    const seen = new Set<string>();
    for (const record of matches) {
      if (seen.has(record.projectVariantId)) {
        continue;
      }

      seen.add(record.projectVariantId);
      perVariant.push({
        typeId: record.typeId,
        projectVariantId: record.projectVariantId,
        declarationIndex: record.declarationIndex,
        span: record.span,
        documentId: record.documentId,
        relativePath: record.relativePath,
        isPartial: record.isPartial
      });
    }

    return perVariant;
  }

  /** One declaration of a type by its index (SD-019). */
  async findDeclaration(
    analysisId: string,
    typeId: string,
    declarationIndex = 0
  ): Promise<DeclarationRecord | undefined> {
    const analysis = this.requireAnalysis(analysisId);
    await this.ensureDeclarations(analysis);
    return analysis.declarationsByType?.get(typeId)?.[declarationIndex];
  }

  /** All declarations of a type, in declaration order (SD-019). */
  async declarationsForType(analysisId: string, typeId: string): Promise<DeclarationRecord[]> {
    const analysis = this.requireAnalysis(analysisId);
    await this.ensureDeclarations(analysis);
    return analysis.declarationsByType?.get(typeId) ?? [];
  }

  /**
   * One evidence record by its id (SD-019). The id is opaque, so the host can open what
   * the webview selected without the webview ever supplying a path or a position.
   */
  async findEvidence(analysisId: string, evidenceId: string): Promise<EvidenceRecord | undefined> {
    const analysis = this.requireAnalysis(analysisId);
    await this.ensureEvidenceIds(analysis);
    const location = analysis.evidenceById?.get(evidenceId);
    if (!location) {
      return undefined;
    }

    const offset = analysis.relationOffsets.get(location.relationId);
    if (!offset) {
      return undefined;
    }

    const records = await readEvidenceLines(
      analysis.evidencePath,
      offset.startByte,
      location.lineIndex,
      1
    );
    const line = records.lines[0];
    if (line === undefined) {
      return undefined;
    }

    try {
      const validation = validateEvidenceRecord(JSON.parse(line));
      return validation.ok && validation.value.id === evidenceId ? validation.value : undefined;
    } catch {
      return undefined;
    }
  }

  /** Indexes evidence ids to their relation and position once per analysis. */
  private async ensureEvidenceIds(analysis: RegisteredAnalysis): Promise<void> {
    if (analysis.evidenceById) {
      return;
    }

    const byId = new Map<string, { relationId: string; lineIndex: number }>();
    const records = await readEvidenceLines(
      analysis.evidencePath,
      0,
      0,
      [...analysis.relationOffsets.values()].reduce((sum, entry) => sum + entry.count, 0)
    );
    const positions = new Map<string, number>();
    for (const line of records.lines) {
      try {
        const parsed = JSON.parse(line) as { id?: unknown; relationId?: unknown };
        if (typeof parsed.id === 'string' && typeof parsed.relationId === 'string') {
          const lineIndex = positions.get(parsed.relationId) ?? 0;
          byId.set(parsed.id, { relationId: parsed.relationId, lineIndex });
          positions.set(parsed.relationId, lineIndex + 1);
        }
      } catch {
        // Malformed records are rejected by the evidence page reader.
      }
    }
    analysis.evidenceById = byId;
  }

  /** Loads the declarations file once and indexes it by type and by document. */
  private async ensureDeclarations(analysis: RegisteredAnalysis): Promise<void> {
    if (analysis.declarationsByType && analysis.declarationsByDocument) {
      return;
    }

    const byType = new Map<string, DeclarationRecord[]>();
    const byDocument = new Map<string, DeclarationRecord[]>();
    if (!analysis.declarationsPath) {
      return;
    }

    const total = (analysis.report.declarationIndex?.types ?? []).reduce(
      (sum, entry) => sum + entry.count,
      0
    );
    const records = await readEvidenceLines(analysis.declarationsPath, 0, 0, total);
    if (records.bytesRead > analysis.declarationsBytes) {
      throw new ReportStoreError(
        'The declarations file changed after registration.',
        'invalidReport'
      );
    }

    for (const line of records.lines) {
      let parsed: unknown;
      try {
        parsed = JSON.parse(line);
      } catch {
        throw new ReportStoreError(
          'The declarations file contains a malformed record.',
          'invalidReport'
        );
      }

      const validation = validateDeclarationRecord(parsed);
      if (!validation.ok) {
        throw new ReportStoreError(
          `The declarations file contains an invalid record: ${validation.errors.join('; ')}`,
          'invalidReport'
        );
      }

      const record = validation.value;
      const typeRecords = byType.get(record.typeId) ?? [];
      typeRecords.push(record);
      byType.set(record.typeId, typeRecords);
      const documentRecords = byDocument.get(record.documentId) ?? [];
      documentRecords.push(record);
      byDocument.set(record.documentId, documentRecords);
    }

    for (const list of byDocument.values()) {
      list.sort((left, right) => left.span.start - right.span.start);
    }

    for (const list of byType.values()) {
      list.sort((left, right) => left.declarationIndex - right.declarationIndex);
    }
    analysis.declarationsByType = byType;
    analysis.declarationsByDocument = byDocument;
  }

  /**
   * Display projection for the graph view: scope, granularity, and a display budget.
   * Relations are stored at the finest granularity, so a coarser view aggregates them
   * (see graphProjection.ts). Each edge keeps the relations it is derived from, and its
   * id is the representative relation, so an edge selection can always be paged for
   * evidence. Totals describe the whole scope, never the display budget.
   */
  getProjection(analysisId: string, options: ProjectionRequest = {}): ProjectionView {
    const analysis = this.requireAnalysis(analysisId);
    const projection = buildProjection(analysis.report, options);

    // Nodes come back as summaries from the snapshot; the store serves its own records
    // so the caller sees the same full names and cycle flags as everywhere else.
    const nodes = projection.nodes
      .map((node) => ({ ...analysis.entityById.get(node.id)!, ...node }))
      .filter((node): node is EntityRecord => node !== undefined);

    return {
      nodes,
      edges: projection.edges.map((edge) => ({
        id: edge.id,
        sourceId: edge.sourceId,
        targetId: edge.targetId,
        basis: edge.basis,
        kinds: edge.kinds,
        evidenceCount: edge.evidenceCount,
        inCycle: edge.inCycle,
        generatedEvidenceCount: edge.generatedEvidenceCount ?? 0,
        publicSurfaceEvidenceCount: edge.publicSurfaceEvidenceCount ?? 0,
        underlyingRelationIds: edge.underlyingRelationIds ?? [edge.id],
        underlyingRelations: (edge.underlyingRelationIds ?? [edge.id]).map((relationId) => {
          const relation = analysis.relationById.get(relationId);
          return {
            id: relationId,
            basis: relation?.basis ?? edge.basis,
            kinds: relation?.kinds ?? edge.kinds,
            evidenceCount: relation?.evidenceCount ?? 0,
            confidence: relation?.confidence ?? null
          };
        })
      })),
      totalNodeCount: projection.totalNodeCount,
      totalEdgeCount: projection.totalEdgeCount,
      truncated: projection.truncated
    };
  }

  /**
   * Reads one page of evidence for a relation. The page always starts at a line
   * boundary: lines are decoded with a streaming UTF-8 decoder and split on newlines,
   * so a page never begins or ends inside a multi-byte character.
   */
  async getEvidencePage(
    analysisId: string,
    relationId: string,
    options: { limit?: number; cursor?: string } = {}
  ): Promise<EvidencePage> {
    const analysis = this.requireAnalysis(analysisId);
    const offset = analysis.relationOffsets.get(relationId);
    if (!offset) {
      throw new ReportStoreError(
        `The relation ${relationId} has no evidence in this analysis.`,
        'invalidEvidence'
      );
    }

    const limit = boundLimit(options.limit);
    const start = options.cursor
      ? this.resolveCursor(options.cursor, analysisId, 'evidence', relationId).offset
      : 0;
    if (start > offset.count) {
      throw new ReportStoreError(
        'The cursor points past the end of the evidence.',
        'invalidCursor'
      );
    }

    const records = await readEvidenceLines(
      analysis.evidencePath,
      offset.startByte,
      start,
      Math.min(limit, offset.count - start)
    );

    const items: EvidenceRecord[] = [];
    for (const line of records.lines) {
      let parsed: unknown;
      try {
        parsed = JSON.parse(line);
      } catch {
        throw new ReportStoreError(
          'The evidence file contains a malformed record.',
          'invalidEvidence'
        );
      }

      const validation = validateEvidenceRecord(parsed);
      if (!validation.ok) {
        throw new ReportStoreError(
          `The evidence file contains an invalid record: ${validation.errors.join('; ')}`,
          'invalidEvidence'
        );
      }

      if (validation.value.relationId !== relationId) {
        throw new ReportStoreError(
          'The evidence file does not match the report index.',
          'invalidEvidence'
        );
      }

      items.push(validation.value);
    }

    const nextOffset = start + items.length;
    if (records.bytesRead > analysis.evidenceBytes) {
      throw new ReportStoreError(
        'The evidence file changed after registration.',
        'invalidEvidence'
      );
    }

    return {
      relationId,
      total: offset.count,
      items,
      nextCursor:
        nextOffset < offset.count
          ? this.issueCursor({ analysisId, kind: 'evidence', key: relationId, offset: nextOffset })
          : undefined
    };
  }

  /**
   * Content of a generated document the analysis retained (SD-011), or undefined when
   * the document is not generated or its content was not kept. The content lives beside
   * the report as `generated/<documentId>.cs`, so opening it never reads or writes the
   * user's repository. The id is opaque and must match the document id shape; the
   * resolved path is checked to stay inside the report directory.
   */
  async readGeneratedDocument(
    analysisId: string,
    documentId: string
  ): Promise<{ document: SourceDocument; text: string } | undefined> {
    const analysis = this.requireAnalysis(analysisId);
    const document = analysis.report.sourceManifest.find((entry) => entry.id === documentId);
    if (
      !document ||
      document.origin !== 'generatedSource' ||
      !/^doc_[a-f0-9]{16}$/.test(documentId)
    ) {
      return undefined;
    }

    const filePath = safeJoin(
      path.join(path.dirname(analysis.evidencePath), 'generated'),
      `${documentId}.cs`
    );

    let size: number;
    try {
      const stats = await fs.promises.stat(filePath);
      if (!stats.isFile()) {
        return undefined;
      }

      size = stats.size;
    } catch {
      // The content was not retained (budget) or the result directory was cleaned up.
      return undefined;
    }

    if (size > this.limits.maxGeneratedDocumentBytes) {
      return undefined;
    }

    return { document, text: await fs.promises.readFile(filePath, 'utf8') };
  }

  private buildAnalysis(
    report: AnalysisSnapshot,
    evidencePath: string,
    evidenceBytes: number,
    relationOffsets: Map<string, RelationOffset>
  ): RegisteredAnalysis {
    const projectNameByVariant = new Map(
      report.projects.map((project) => [project.variantId, project.name])
    );
    const cycleMembers = new Set(report.cycleGroups.flatMap((group) => group.memberIds));

    const entities: EntityRecord[] = [
      ...report.projects.map((project) => ({
        id: project.id,
        granularity: 'project' as const,
        name: project.name,
        fullName: project.relativePath,
        kind: project.kind,
        targetFramework: project.targetFramework,
        isExternal: false,
        inCycle: cycleMembers.has(project.id)
      })),
      ...report.namespaces.map((node) => ({
        id: node.id,
        granularity: 'namespace' as const,
        name: node.name,
        fullName: node.name,
        kind: 'namespace',
        projectName: projectNameByVariant.get(node.projectVariantId),
        isExternal: false,
        inCycle: cycleMembers.has(node.id)
      })),
      ...report.types.map((type) => ({
        id: type.id,
        granularity: 'type' as const,
        name: type.name,
        fullName: type.fullName,
        kind: type.kind,
        projectName: projectNameByVariant.get(type.projectVariantId),
        isExternal: type.isExternal === true,
        inCycle: cycleMembers.has(type.id)
      }))
    ];

    const entityById = new Map(entities.map((entity) => [entity.id, entity]));
    for (const node of ['project', 'namespace', 'type'].flatMap((granularity) =>
      entitiesOf(report, granularity as Granularity)
    )) {
      Object.assign(entityById.get(node.id)!, node);
    }
    const relationById = new Map(report.relations.map((relation) => [relation.id, relation]));
    const relationsByEntity = new Map<string, AnalysisRelation[]>();
    for (const relation of report.relations) {
      for (const entityId of [relation.sourceEntityId, relation.targetEntityId]) {
        const list = relationsByEntity.get(entityId) ?? [];
        list.push(relation);
        relationsByEntity.set(entityId, list);
      }
    }

    return {
      analysisId: report.analysisId,
      report,
      evidencePath,
      evidenceBytes,
      relationOffsets,
      declarationsBytes: 0,
      documentIdByPath: new Map(
        report.sourceManifest.map((document) => [
          normalizeManifestPath(document.relativePath),
          document.id
        ])
      ),
      entities,
      entityById,
      relationById,
      relationsByEntity,
      cycleMembers,
      stale: false
    };
  }

  private requireAnalysis(analysisId: string): RegisteredAnalysis {
    const analysis = this.analyses.get(analysisId);
    if (analysis) {
      return analysis;
    }

    if (this.analyses.size > 0) {
      throw new ReportStoreError(
        `The analysis ${analysisId} is no longer available; refresh the view.`,
        'staleAnalysis'
      );
    }

    throw new ReportStoreError(`No analysis is registered for ${analysisId}.`, 'unknownAnalysis');
  }

  private evictIfNeeded(): void {
    while (this.analyses.size >= this.limits.maxRegisteredAnalyses) {
      const oldest = this.analyses.keys().next().value;
      if (oldest === undefined) {
        return;
      }

      this.release(oldest);
    }
  }

  private issueCursor(state: CursorState): string {
    const cursor =
      'cur_' +
      createHash('sha256')
        .update(`cursor:${this.cursorSequence++}:${state.analysisId}:${state.kind}:${state.key}`)
        .digest('hex')
        .slice(0, 16);
    this.cursors.set(cursor, state);
    return cursor;
  }

  private resolveCursor(
    cursor: string,
    analysisId: string,
    kind: CursorState['kind'],
    key: string
  ): CursorState {
    const state = this.cursors.get(cursor);
    if (!state || state.analysisId !== analysisId || state.kind !== kind || state.key !== key) {
      throw new ReportStoreError('The cursor is not valid for this request.', 'invalidCursor');
    }

    return state;
  }
}

function boundLimit(limit: number | undefined): number {
  if (limit === undefined) {
    return DEFAULT_PAGE_SIZE;
  }

  if (!Number.isInteger(limit) || limit < 1) {
    throw new ReportStoreError('The page size must be a positive integer.', 'invalidCursor');
  }

  return Math.min(limit, MAX_PAGE_SIZE);
}

function rankOf(entity: EntityRecord, needle: string): number {
  if (needle.length === 0) {
    return 2;
  }

  const name = entity.name.toLowerCase();
  if (name === needle) {
    return 0;
  }

  return name.startsWith(needle) ? 1 : 2;
}

function uniqueById(entities: EntityRecord[]): EntityRecord[] {
  const seen = new Set<string>();
  return entities.filter((entity) => {
    if (seen.has(entity.id)) {
      return false;
    }

    seen.add(entity.id);
    return true;
  });
}

function safeJoin(directory: string, fileName: string): string {
  const resolvedDirectory = path.resolve(directory);
  const resolved = path.resolve(resolvedDirectory, fileName);
  const relative = path.relative(resolvedDirectory, resolved);
  if (relative.startsWith('..') || path.isAbsolute(relative)) {
    throw new ReportStoreError(
      `The path must stay inside the report directory: ${fileName}`,
      'invalidEvidence'
    );
  }

  return resolved;
}

function normalizeManifestPath(relativePath: string): string {
  return relativePath.replace(/\\/g, '/');
}

async function fileSize(filePath: string): Promise<number> {
  try {
    const stats = await fs.promises.stat(filePath);
    if (!stats.isFile()) {
      throw new ReportStoreError(`Not a file: ${path.basename(filePath)}`, 'ioError');
    }

    return stats.size;
  } catch (error) {
    if (error instanceof ReportStoreError) {
      throw error;
    }

    throw new ReportStoreError(`The file could not be read: ${path.basename(filePath)}`, 'ioError');
  }
}

async function countLines(filePath: string): Promise<number> {
  const handle = await fs.promises.open(filePath, 'r');
  try {
    const decoder = new TextDecoder('utf-8');
    const buffer = new Uint8Array(64 * 1024);
    let trailing = '';
    let lines = 0;

    for (;;) {
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, null);
      if (bytesRead === 0) {
        break;
      }

      const text = trailing + decoder.decode(buffer.subarray(0, bytesRead), { stream: true });
      const parts = text.split('\n');
      trailing = parts.pop() ?? '';
      lines += parts.filter((line) => line.trim().length > 0).length;
    }

    if (trailing.trim().length > 0) {
      lines++;
    }

    return lines;
  } finally {
    await handle.close();
  }
}

interface EvidenceLines {
  lines: string[];
  bytesRead: number;
}

/**
 * Reads <paramref name="count"/> complete lines starting at <paramref name="lineOffset"/>
 * lines after <paramref name="startByte"/>. Only whole lines are returned.
 */
async function readEvidenceLines(
  filePath: string,
  startByte: number,
  lineOffset: number,
  count: number
): Promise<EvidenceLines> {
  if (count <= 0) {
    return { lines: [], bytesRead: 0 };
  }

  const handle = await fs.promises.open(filePath, 'r');
  try {
    const decoder = new TextDecoder('utf-8');
    const buffer = new Uint8Array(64 * 1024);
    const lines: string[] = [];
    let position = startByte;
    let trailing = '';
    let skipped = 0;
    let bytesRead = 0;

    for (;;) {
      const { bytesRead: read } = await handle.read(buffer, 0, buffer.length, position);
      if (read === 0) {
        break;
      }

      position += read;
      bytesRead += read;

      const text = trailing + decoder.decode(buffer.subarray(0, read), { stream: true });
      const parts = text.split('\n');
      trailing = parts.pop() ?? '';

      for (const line of parts) {
        if (line.trim().length === 0) {
          continue;
        }

        if (skipped < lineOffset) {
          skipped++;
          continue;
        }

        lines.push(line);
        if (lines.length >= count) {
          return { lines, bytesRead };
        }
      }
    }

    if (trailing.trim().length > 0 && skipped >= lineOffset && lines.length < count) {
      lines.push(trailing);
    }

    return { lines, bytesRead };
  } finally {
    await handle.close();
  }
}
