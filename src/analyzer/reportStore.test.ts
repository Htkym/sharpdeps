import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { spawnSync } from 'node:child_process';
import { afterEach, describe, expect, it } from 'vitest';
import type { AnalysisSnapshot, EvidenceRecord } from './reportV2';
import { ReportStore, ReportStoreError } from './reportStore';
import { createReportBridge } from './reportBridge';
import { IDS, makeEvidence, makeSnapshot } from '../../tests/helpers/reportV2Fixtures';

const temporaryDirectories: string[] = [];

function createWorkspace(): string {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-store-'));
  temporaryDirectories.push(directory);
  return directory;
}

afterEach(() => {
  while (temporaryDirectories.length > 0) {
    const directory = temporaryDirectories.pop();
    if (directory) {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  }
});

interface WrittenAnalysis {
  directory: string;
  snapshot: AnalysisSnapshot;
}

function writeAnalysis(
  snapshot: AnalysisSnapshot,
  records: EvidenceRecord[],
  options: {
    evidenceFileName?: string;
    indexOverrides?: { startByte?: number; count?: number };
  } = {}
): WrittenAnalysis {
  const directory = createWorkspace();
  const fileName = options.evidenceFileName ?? 'evidence.ndjson';

  let offset = 0;
  const relations: Array<{ relationId: string; startByte: number; count: number }> = [];
  const grouped = new Map<string, EvidenceRecord[]>();
  for (const record of records) {
    const list = grouped.get(record.relationId) ?? [];
    list.push(record);
    grouped.set(record.relationId, list);
  }

  let ndjson = '';
  for (const [relationId, list] of grouped) {
    const start = offset;
    for (const record of list) {
      const line = `${JSON.stringify(record)}\n`;
      ndjson += line;
      offset += Buffer.byteLength(line, 'utf8');
    }

    relations.push({ relationId, startByte: start, count: list.length });
  }

  const resolved = {
    ...snapshot,
    evidenceIndex: {
      format: 'ndjson' as const,
      fileName,
      byteLength: Buffer.byteLength(ndjson, 'utf8'),
      relations: relations.map((entry) => ({
        ...entry,
        startByte: options.indexOverrides?.startByte ?? entry.startByte,
        count: options.indexOverrides?.count ?? entry.count
      }))
    },
    sourceManifest: snapshot.sourceManifest
  };

  fs.writeFileSync(
    path.join(directory, 'report-v2.json'),
    JSON.stringify(resolved, null, 2),
    'utf8'
  );
  fs.writeFileSync(path.join(directory, fileName), ndjson, 'utf8');
  return { directory, snapshot: resolved };
}

function snapshotWithEntities(names: string[]): AnalysisSnapshot {
  const base = makeSnapshot();
  return {
    ...base,
    namespaces: names.map((name, index) => ({
      id: `ns_${String(index).padStart(16, '0')}`,
      projectVariantId: IDS.variantA,
      name,
      typeCount: 0,
      representativeDocumentId: null
    })),
    types: [],
    relations: names.map((_, index) => ({
      id: `rel_${String(index).padStart(16, '0')}`,
      sourceEntityId: IDS.projectA,
      targetEntityId: `ns_${String(index).padStart(16, '0')}`,
      basis: 'usingInferred' as const,
      kinds: ['usingInferred' as const],
      evidenceCount: 1,
      distinctSourceMemberCount: 0,
      distinctSourceDocumentCount: 1,
      generatedEvidenceCount: 0,
      publicSurfaceEvidenceCount: 0,
      confidence: 'inferred' as const
    })),
    cycleGroups: []
  };
}

describe('ReportStore registration', () => {
  it('registers a valid analysis and serves it as the current one', async () => {
    const snapshot = snapshotWithEntities(['Core', 'Util']);
    const records = snapshot.relations.map((relation, index) =>
      makeEvidence({
        id: `ev_${String(index).padStart(16, '0')}`,
        relationId: relation.id,
        kind: 'usingInferred',
        physicalSpan: null,
        confidence: 'inferred'
      })
    );
    const { directory } = writeAnalysis(snapshot, records);
    const store = new ReportStore();

    const registered = await store.register({ directory, reportFileName: 'report-v2.json' });

    expect(registered.analysisId).toBe(snapshot.analysisId);
    expect(store.currentAnalysisId).toBe(snapshot.analysisId);
  });

  it('rejects a report that violates the v2 contract', () => {
    const store = new ReportStore();
    const directory = createWorkspace();
    fs.writeFileSync(
      path.join(directory, 'report-v2.json'),
      JSON.stringify({ schemaVersion: 1 }),
      'utf8'
    );

    return expect(store.register({ directory, reportFileName: 'report-v2.json' })).rejects.toThrow(
      ReportStoreError
    );
  });

  it('rejects an evidence index whose count does not match the file', async () => {
    const snapshot = snapshotWithEntities(['Core']);
    const records = [
      makeEvidence({
        relationId: snapshot.relations[0].id,
        kind: 'usingInferred',
        physicalSpan: null
      })
    ];
    const { directory } = writeAnalysis(snapshot, records, { indexOverrides: { count: 5 } });
    const store = new ReportStore();

    await expect(store.register({ directory, reportFileName: 'report-v2.json' })).rejects.toThrow(
      /declares 5 record/
    );
  });

  it('rejects an evidence file name that leaves the report directory', async () => {
    const snapshot = snapshotWithEntities(['Core']);
    const { directory } = writeAnalysis(snapshot, [], { evidenceFileName: '../outside.ndjson' });
    fs.writeFileSync(path.join(directory, '..', 'outside.ndjson'), '', 'utf8');
    const store = new ReportStore();

    await expect(store.register({ directory, reportFileName: 'report-v2.json' })).rejects.toThrow(
      ReportStoreError
    );
  });

  it('rejects a report above the configured size limit', async () => {
    const snapshot = snapshotWithEntities(['Core']);
    const { directory } = writeAnalysis(snapshot, []);
    const store = new ReportStore({ maxReportBytes: 10 });

    await expect(store.register({ directory, reportFileName: 'report-v2.json' })).rejects.toThrow(
      /larger than the 10 byte limit/
    );
  });

  it('reports a missing file as an io error', async () => {
    const store = new ReportStore();

    await expect(
      store.register({ directory: createWorkspace(), reportFileName: 'report-v2.json' })
    ).rejects.toThrow(/could not be read/);
  });
});

describe('ReportStore paging and cursors', () => {
  async function registerFive(): Promise<{ store: ReportStore; analysisId: string }> {
    const snapshot = snapshotWithEntities(['Alpha', 'Beta', 'Gamma', 'Delta', 'Epsilon']);
    const records = snapshot.relations.map((relation, index) =>
      makeEvidence({
        id: `ev_${String(index).padStart(16, '0')}`,
        relationId: relation.id,
        kind: 'usingInferred',
        physicalSpan: null,
        confidence: 'inferred'
      })
    );
    const { directory } = writeAnalysis(snapshot, records);
    const store = new ReportStore();
    await store.register({ directory, reportFileName: 'report-v2.json' });
    return { store, analysisId: snapshot.analysisId };
  }

  it('pages search results with opaque cursors', async () => {
    const { store, analysisId } = await registerFive();

    const first = store.search(analysisId, '', { limit: 2, granularity: 'namespace' });
    expect(first.total).toBe(5);
    expect(first.items.map((item) => item.name)).toEqual(['Alpha', 'Beta']);
    expect(first.nextCursor).toMatch(/^cur_[0-9a-f]{16}$/);

    const second = store.search(analysisId, '', {
      limit: 2,
      granularity: 'namespace',
      cursor: first.nextCursor
    });
    // Results are ordered by name, so the second page is Delta/Epsilon.
    expect(second.items.map((item) => item.name)).toEqual(['Delta', 'Epsilon']);

    const third = store.search(analysisId, '', {
      limit: 2,
      granularity: 'namespace',
      cursor: second.nextCursor
    });
    expect(third.items.map((item) => item.name)).toEqual(['Gamma']);
    expect(third.nextCursor).toBeUndefined();
  });

  it('rejects tampered, misused, or foreign cursors', async () => {
    const { store, analysisId } = await registerFive();
    const page = store.search(analysisId, '', { limit: 2, granularity: 'namespace' });

    expect(() =>
      store.search(analysisId, '', {
        limit: 2,
        granularity: 'namespace',
        cursor: 'cur_ffffffffffffffff'
      })
    ).toThrow(/cursor is not valid/);
    // A cursor issued for one query must not be reused for another.
    expect(() =>
      store.search(analysisId, 'Alpha', {
        limit: 2,
        granularity: 'namespace',
        cursor: page.nextCursor
      })
    ).toThrow(/cursor is not valid/);
    expect(() => store.search(analysisId, '', { limit: 0 })).toThrow(/positive integer/);
  });

  it('caps the page size', async () => {
    const { store, analysisId } = await registerFive();

    const page = store.search(analysisId, '', { limit: 10_000, granularity: 'namespace' });

    expect(page.items.length).toBeLessThanOrEqual(500);
  });
});

describe('ReportStore evidence pages', () => {
  it('reads complete lines and never splits a multi-byte character', async () => {
    const snapshot = snapshotWithEntities(['Core']);
    const relationId = snapshot.relations[0].id;
    const records = Array.from({ length: 4 }, (_, index) =>
      makeEvidence({
        id: `ev_${String(index).padStart(16, '0')}`,
        relationId,
        kind: 'usingInferred',
        physicalSpan: null,
        confidence: 'inferred',
        snippet: `日本語のコード断片 ${index} 🙂`
      })
    );
    const { directory } = writeAnalysis(snapshot, records);
    const store = new ReportStore();
    await store.register({ directory, reportFileName: 'report-v2.json' });

    const first = await store.getEvidencePage(snapshot.analysisId, relationId, { limit: 2 });
    expect(first.total).toBe(4);
    expect(first.items.map((item) => item.snippet)).toEqual([
      '日本語のコード断片 0 🙂',
      '日本語のコード断片 1 🙂'
    ]);
    expect(first.nextCursor).toBeDefined();

    const second = await store.getEvidencePage(snapshot.analysisId, relationId, {
      limit: 2,
      cursor: first.nextCursor
    });
    expect(second.items.map((item) => item.snippet)).toEqual([
      '日本語のコード断片 2 🙂',
      '日本語のコード断片 3 🙂'
    ]);
    expect(second.nextCursor).toBeUndefined();
  });

  it('fails safely when the index points into the middle of a character', async () => {
    const snapshot = snapshotWithEntities(['Core']);
    const relationId = snapshot.relations[0].id;
    const records = [
      makeEvidence({
        id: 'ev_0000000000000001',
        relationId,
        kind: 'usingInferred',
        physicalSpan: null,
        confidence: 'inferred',
        snippet: '日本語のコード断片'
      })
    ];
    const { directory } = writeAnalysis(snapshot, records, { indexOverrides: { startByte: 3 } });
    const store = new ReportStore();
    await store.register({ directory, reportFileName: 'report-v2.json' });

    await expect(
      store.getEvidencePage(snapshot.analysisId, relationId, { limit: 1 })
    ).rejects.toThrow(ReportStoreError);
  });

  it('rejects an unknown relation', async () => {
    const snapshot = snapshotWithEntities(['Core']);
    const { directory } = writeAnalysis(snapshot, [
      makeEvidence({
        relationId: snapshot.relations[0].id,
        kind: 'usingInferred',
        physicalSpan: null
      })
    ]);
    const store = new ReportStore();
    await store.register({ directory, reportFileName: 'report-v2.json' });

    await expect(
      store.getEvidencePage(snapshot.analysisId, 'rel_ffffffffffffffff')
    ).rejects.toThrow(/no evidence/);
  });
});

describe('ReportStore analysis lifetime', () => {
  it('distinguishes unknown and stale analysis ids', async () => {
    const snapshot = snapshotWithEntities(['Core']);
    const records = [
      makeEvidence({
        relationId: snapshot.relations[0].id,
        kind: 'usingInferred',
        physicalSpan: null
      })
    ];
    const { directory } = writeAnalysis(snapshot, records);
    const store = new ReportStore({ maxRegisteredAnalyses: 1 });

    await expect(
      store.getEvidencePage('an_ffffffffffffffff', 'rel_0000000000000000')
    ).rejects.toThrow(/No analysis is registered/);

    await store.register({ directory, reportFileName: 'report-v2.json' });
    store.release(snapshot.analysisId);

    expect(() => store.search(snapshot.analysisId, 'Core')).toThrow(/No analysis is registered/);
  });

  it('evicts the oldest analysis and reports it as stale', async () => {
    const first = { ...snapshotWithEntities(['Core']), analysisId: 'an_0000000000000001' };
    const second = { ...snapshotWithEntities(['Util']), analysisId: 'an_0000000000000002' };
    const firstDirectory = writeAnalysis(first, [
      makeEvidence({ relationId: first.relations[0].id, kind: 'usingInferred', physicalSpan: null })
    ]).directory;
    const secondDirectory = writeAnalysis(second, [
      makeEvidence({
        relationId: second.relations[0].id,
        kind: 'usingInferred',
        physicalSpan: null
      })
    ]).directory;

    const store = new ReportStore({ maxRegisteredAnalyses: 1 });
    await store.register({ directory: firstDirectory, reportFileName: 'report-v2.json' });
    await store.register({ directory: secondDirectory, reportFileName: 'report-v2.json' });

    expect(store.currentAnalysisId).toBe(second.analysisId);
    expect(() => store.search(first.analysisId, 'Core')).toThrow(/no longer available/);
  });
});

describe('ReportStore projection and details', () => {
  it('reports totals and truncation, and honours a local scope', async () => {
    const base = snapshotWithEntities(['Alpha', 'Beta', 'Gamma']);
    const namespaceId = (index: number) => `ns_${String(index).padStart(16, '0')}`;
    const snapshot: AnalysisSnapshot = {
      ...base,
      relations: [
        {
          ...base.relations[0],
          id: 'rel_0000000000000000',
          sourceEntityId: namespaceId(0),
          targetEntityId: namespaceId(1)
        },
        {
          ...base.relations[1],
          id: 'rel_0000000000000001',
          sourceEntityId: namespaceId(1),
          targetEntityId: namespaceId(2)
        }
      ]
    };
    const records = snapshot.relations.map((relation, index) =>
      makeEvidence({
        id: `ev_${String(index).padStart(16, '0')}`,
        relationId: relation.id,
        kind: 'usingInferred',
        physicalSpan: null,
        confidence: 'inferred'
      })
    );
    const { directory } = writeAnalysis(snapshot, records);
    const store = new ReportStore();
    await store.register({ directory, reportFileName: 'report-v2.json' });

    const budgeted = store.getProjection(snapshot.analysisId, {
      granularity: 'namespace',
      maxNodes: 2,
      maxEdges: 1
    });
    expect(budgeted.totalNodeCount).toBe(3);
    expect(budgeted.nodes.length).toBe(2);
    expect(budgeted.truncated).toBe(true);

    // Dependencies of Alpha reach Beta and (through Beta) Gamma.
    const local = store.getProjection(snapshot.analysisId, {
      granularity: 'namespace',
      scope: { kind: 'dependencies', id: namespaceId(0), depth: 2 }
    });
    expect(local.nodes.map((node) => node.name).sort()).toEqual(['Alpha', 'Beta', 'Gamma']);
    expect(local.truncated).toBe(false);
  });

  it('returns dependencies and dependents for an entity', async () => {
    const snapshot = snapshotWithEntities(['Alpha']);
    const records = [
      makeEvidence({
        relationId: snapshot.relations[0].id,
        kind: 'usingInferred',
        physicalSpan: null
      })
    ];
    const { directory } = writeAnalysis(snapshot, records);
    const store = new ReportStore();
    await store.register({ directory, reportFileName: 'report-v2.json' });

    const details = store.getEntityDetails(snapshot.analysisId, IDS.projectA);
    expect(details?.entity.name).toBe('App');
    expect(details?.dependencies.map((entry) => entry.name)).toEqual(['Alpha']);

    expect(store.getEntityDetails(snapshot.analysisId, 'ty_ffffffffffffffff')).toBeUndefined();
  });
});

describe('report bridge', () => {
  async function registerFive() {
    const snapshot = snapshotWithEntities(['Alpha', 'Beta', 'Gamma', 'Delta', 'Epsilon']);
    const records = snapshot.relations.map((relation, index) =>
      makeEvidence({
        id: `ev_${String(index).padStart(16, '0')}`,
        relationId: relation.id,
        kind: 'usingInferred',
        physicalSpan: null,
        confidence: 'inferred'
      })
    );
    const { directory } = writeAnalysis(snapshot, records);
    const store = new ReportStore();
    await store.register({ directory, reportFileName: 'report-v2.json' });
    return {
      store,
      bridge: createReportBridge(store, { pageSize: 2 }),
      analysisId: snapshot.analysisId
    };
  }

  it('answers capability negotiation', async () => {
    const { bridge, analysisId } = await registerFive();

    const response = await bridge.handle({ type: 'ready', protocolVersion: 2 });

    expect(response.type).toBe('capabilities');
    expect(response).toMatchObject({
      protocolVersion: 2,
      analysisId,
      capabilities: { search: true }
    });
  });

  it('rejects unknown message types and malformed ids before touching the store', async () => {
    const { bridge } = await registerFive();

    const unknown = await bridge.handle({ type: 'runRoslyn' });
    expect(unknown).toMatchObject({ type: 'error', code: 'protocol.unknownType' });

    const malformed = await bridge.handle({
      type: 'getEntityDetails',
      requestId: 'req_1',
      analysisId: 'an_0000000000000000',
      entityId: 'not-an-id'
    });
    expect(malformed).toMatchObject({ type: 'error', code: 'protocol.invalidMessage' });
  });

  it('maps store errors to error responses', async () => {
    const { bridge, analysisId } = await registerFive();

    // With another analysis registered, an unknown id means the view is stale.
    const stale = await bridge.handle({
      type: 'getEntityDetails',
      requestId: 'req_0000000000000001',
      analysisId: 'an_ffffffffffffffff',
      entityId: IDS.projectA
    });
    expect(stale).toMatchObject({ type: 'error', code: 'store.staleAnalysis' });

    const unknownEntity = await bridge.handle({
      type: 'getEntityDetails',
      requestId: 'req_0000000000000002',
      analysisId,
      entityId: 'ty_ffffffffffffffff'
    });
    expect(unknownEntity).toMatchObject({ type: 'error', code: 'store.unknownEntity' });

    const tampered = await bridge.handle({
      type: 'getEvidencePage',
      requestId: 'req_0000000000000003',
      analysisId,
      relationId: 'rel_0000000000000000',
      cursor: 'cur_ffffffffffffffff'
    });
    expect(tampered).toMatchObject({ type: 'error', code: 'store.invalidCursor' });
  });

  it('pages search results through the protocol', async () => {
    const { bridge, analysisId } = await registerFive();

    const first = await bridge.handle({
      type: 'searchEntities',
      requestId: 'req_0000000000000004',
      analysisId,
      query: '',
      granularity: 'namespace'
    });

    expect(first).toMatchObject({ type: 'searchResults', total: 5 });
    if (first.type !== 'searchResults') {
      throw new Error('unexpected response');
    }

    expect(first.items.map((item) => item.name)).toEqual(['Alpha', 'Beta']);
    expect(first.nextCursor).toMatch(/^cur_/);

    const second = await bridge.handle({
      type: 'searchEntities',
      requestId: 'req_0000000000000005',
      analysisId,
      query: '',
      granularity: 'namespace',
      cursor: first.nextCursor ?? undefined
    });
    expect(second).toMatchObject({ type: 'searchResults' });
    if (second.type !== 'searchResults') {
      throw new Error('unexpected response');
    }

    expect(second.items.map((item) => item.name)).toEqual(['Delta', 'Epsilon']);
  });

  it('states explicitly that host-side actions are not implemented yet', async () => {
    const { bridge, analysisId } = await registerFive();

    const response = await bridge.handle({
      type: 'export',
      requestId: 'req_0000000000000006',
      analysisId,
      format: 'mermaid',
      scope: { kind: 'root' }
    });

    expect(response).toMatchObject({ type: 'error', code: 'bridge.notImplemented' });
  });
});

describe.skipIf(!locateAnalyzerDll())(
  'ReportStore with the real analyzer output',
  () => {
    it('registers the Quick report and pages its evidence', async () => {
      const repoRoot = process.cwd();
      const workDir = createWorkspace();
      const run = spawnSync(
        (process.env.SHARPDEPTS_DOTNET || 'dotnet') as string,
        [
          locateAnalyzerDll() as string,
          '--solution',
          path.join(repoRoot, 'tests', 'fixtures', 'quick-baseline', 'Baseline.sln'),
          '--output',
          path.join(workDir, 'report.json'),
          '--max-projects',
          '60',
          '--max-edges',
          '200'
        ],
        { cwd: repoRoot, encoding: 'utf8' }
      );
      expect(run.status, run.stderr).toBe(0);

      const store = new ReportStore();
      const snapshot = await store.register({
        directory: workDir,
        reportFileName: 'report-v2.json'
      });

      const namespaces = store.search(snapshot.analysisId, 'Core', { limit: 10 });
      expect(namespaces.total).toBeGreaterThan(0);

      const relation = snapshot.relations.find((entry) => entry.evidenceCount >= 1);
      expect(relation).toBeDefined();
      const page = await store.getEvidencePage(snapshot.analysisId, relation!.id, { limit: 1 });

      expect(page.total).toBe(relation!.evidenceCount);
      expect(page.items[0].relationId).toBe(relation!.id);
      expect(page.items.length).toBeLessThanOrEqual(1);

      // The first render must not carry every evidence record of the analysis.
      const totalEvidence = (snapshot.evidenceIndex?.relations ?? []).reduce(
        (total, entry) => total + entry.count,
        0
      );
      expect(page.items.length).toBeLessThan(totalEvidence);
    });
  },
  120000
);

function locateAnalyzerDll(): string | undefined {
  const repoRoot = process.cwd();
  return [
    path.join(repoRoot, 'analyzer', 'bin', 'quick', 'code-map.dll'),
    path.join(repoRoot, 'analyzer', 'bin', 'code-map.dll')
  ].find((candidate) => fs.existsSync(candidate));
}
