// Contract tests: the JSON Schemas in schemas/ are the normative definition of
// the v2 model, the protocol, and the persisted view state. These tests check
// that the golden fixtures are schema-valid and that the runtime validator and
// the schema agree on the same inputs.

import * as fs from 'node:fs';
import * as path from 'node:path';
import Ajv2020 from 'ajv/dist/2020';
import { describe, expect, it } from 'vitest';
import type { CodeMapReport } from '../../src/analyzer/types';
import { adaptLegacyReport } from '../../src/analyzer/legacyAdapter';
import { validateEvidenceRecord, validateSnapshot } from '../../src/analyzer/reportV2Validation';
import { validateHostMessage, validateWebviewMessage } from '../../src/view/protocolV2';
import { IDS, makeEvidence, makeSnapshot } from '../helpers/reportV2Fixtures';

const schemaDirectory = path.join(process.cwd(), 'schemas');

function loadSchema(name: string): object {
  return JSON.parse(fs.readFileSync(path.join(schemaDirectory, name), 'utf8')) as object;
}

const ajv = new Ajv2020({ allErrors: true, strict: false });

const reportSchema = loadSchema('report-v2.schema.json');
const evidenceSchema = loadSchema('evidence-v2.schema.json');
const protocolSchema = loadSchema('protocol-v2.schema.json');
const viewStateSchema = loadSchema('view-state-v1.schema.json');

const validateReport = ajv.compile(reportSchema);
const validateEvidence = ajv.compile(evidenceSchema);
const validateProtocol = ajv.compile(protocolSchema);
const validateViewState = ajv.compile(viewStateSchema);

function describeAjvErrors(): string {
  return JSON.stringify(validateReport.errors ?? [], null, 2);
}

/** Adapts the committed Quick baseline so the contract covers real analyzer output. */
function adaptQuickBaseline() {
  const fixturePath = path.join(
    process.cwd(),
    'tests',
    'fixtures',
    'quick-baseline',
    'expected',
    'quick-report.json'
  );
  const raw = fs.readFileSync(fixturePath, 'utf8').replaceAll('<FIXTURE_ROOT>', 'C:/repo/sample');
  return adaptLegacyReport({
    report: JSON.parse(raw) as CodeMapReport,
    createdAt: '2026-09-23T00:00:00.000Z'
  });
}

describe('report v2 schema', () => {
  it('accepts the adapted Quick baseline', () => {
    const { snapshot } = adaptQuickBaseline();

    expect(validateReport(snapshot), describeAjvErrors()).toBe(true);
  });

  it('accepts a semantic snapshot with resolved evidence', () => {
    expect(validateReport(makeSnapshot()), describeAjvErrors()).toBe(true);
  });

  it('accepts a snapshot without a side evidence file', () => {
    expect(validateReport(makeSnapshot({ evidenceIndex: null }))).toBe(true);
  });

  it('rejects a malformed entity id in both validators', () => {
    const snapshot = makeSnapshot();
    snapshot.relations[0] = { ...snapshot.relations[0], targetEntityId: 'Core.Order' };

    expect(validateReport(snapshot)).toBe(false);
    expect(validateSnapshot(snapshot).ok).toBe(false);
  });

  it('checks referential integrity that JSON Schema cannot express', () => {
    const snapshot = makeSnapshot();
    snapshot.relations[0] = { ...snapshot.relations[0], targetEntityId: 'ty_ffffffffffffffff' };

    // The schema only sees a well-formed id; the runtime validator resolves it.
    expect(validateReport(snapshot)).toBe(true);
    expect(validateSnapshot(snapshot).ok).toBe(false);
  });

  it('rejects an unknown basis', () => {
    const snapshot = makeSnapshot();
    snapshot.relations[0] = {
      ...snapshot.relations[0],
      basis: 'guessed' as unknown as 'symbolResolved'
    };

    expect(validateReport(snapshot)).toBe(false);
    expect(validateSnapshot(snapshot).ok).toBe(false);
  });
});

describe('evidence v2 schema', () => {
  it('accepts resolved and declaration-only records', () => {
    const resolved = makeEvidence();
    const declared = makeEvidence({
      kind: 'projectDeclared',
      physicalSpan: null,
      confidence: 'inferred'
    });

    expect(validateEvidence(resolved)).toBe(true);
    expect(validateEvidence(declared)).toBe(true);
    expect(validateEvidenceRecord(resolved).ok).toBe(true);
    expect(validateEvidenceRecord(declared).ok).toBe(true);
  });

  it('rejects a record with a malformed document id', () => {
    const record = makeEvidence({ documentId: 'Program.cs' });

    expect(validateEvidence(record)).toBe(false);
    expect(validateEvidenceRecord(record).ok).toBe(false);
  });

  it('rejects a span with a missing end position', () => {
    const record = makeEvidence({
      physicalSpan: { start: 0, length: 3, startLine: 0, startCharacter: 0 } as never
    });

    expect(validateEvidence(record)).toBe(false);
    expect(validateEvidenceRecord(record).ok).toBe(false);
  });
});

describe('protocol v2 schema', () => {
  const messages: unknown[] = [
    { type: 'ready', protocolVersion: 2 },
    { type: 'analyze', requestId: 'req_0123456789abcdef', mode: 'quick' },
    {
      type: 'cancelAnalysis',
      requestId: 'req_0123456789abcdef',
      analysisId: 'an_0123456789abcdef'
    },
    {
      type: 'getProjection',
      requestId: 'req_0123456789abcdef',
      analysisId: 'an_0123456789abcdef',
      scope: { kind: 'type', id: 'ty_0123456789abcdef' },
      granularity: 'type',
      filters: { includeGenerated: false, includeTests: false }
    },
    {
      type: 'capabilities',
      protocolVersion: 2,
      capabilities: {
        typeGraph: false,
        evidence: true,
        generatedDocuments: false,
        cycleWitness: false,
        search: true
      },
      analysisId: 'an_0123456789abcdef'
    },
    {
      type: 'analysisProgress',
      analysisId: 'an_0123456789abcdef',
      stage: 'load',
      loaded: 3,
      elapsedMs: 1200
    },
    {
      type: 'analysisComplete',
      analysisId: 'an_0123456789abcdef',
      completeness: 'partial',
      coverage: { discovered: 6, loaded: 5, analyzed: 5, failed: 1, skipped: 0, unresolved: 0 }
    },
    {
      type: 'evidencePage',
      requestId: 'req_0123456789abcdef',
      analysisId: 'an_0123456789abcdef',
      relationId: 'rel_0123456789abcdef',
      total: 1,
      items: [makeEvidence()],
      nextCursor: null
    },
    { type: 'stale', analysisId: 'an_0123456789abcdef', reason: 'savedChange' },
    { type: 'error', code: 'analysis.notFound', message: 'Unknown analysis id' }
  ];

  it('accepts the golden messages', () => {
    for (const message of messages) {
      expect(validateProtocol(message), JSON.stringify(message)).toBe(true);
    }
  });

  it('agrees with the runtime validator on unknown types', () => {
    const unknown = { type: 'runRoslyn', requestId: 'req_0123456789abcdef' };

    expect(validateProtocol(unknown)).toBe(false);
    expect(validateWebviewMessage(unknown).ok).toBe(false);
    expect(validateHostMessage(unknown).ok).toBe(false);
  });

  it('agrees with the runtime validator on malformed ids', () => {
    const malformed = {
      type: 'getEntityDetails',
      requestId: 'req_0123456789abcdef',
      analysisId: 'an_0123456789abcdef',
      entityId: 'C:/repo/src/App.cs'
    };

    expect(validateProtocol(malformed)).toBe(false);
    expect(validateWebviewMessage(malformed).ok).toBe(false);
  });

  it('rejects a mismatched protocol version', () => {
    expect(validateProtocol({ type: 'ready', protocolVersion: 1 })).toBe(false);
  });
});

describe('view state v1 schema', () => {
  const state = {
    version: 1,
    targetId: 'wrk_0123456789abcdef',
    mode: 'semantic',
    profile: { configuration: 'Debug', platform: null },
    scope: { kind: 'type', id: IDS.typeA, depth: null },
    granularity: 'type',
    viewKind: 'graph',
    search: 'Order',
    filters: { includeGenerated: false, includeTests: false },
    selection: { entityId: IDS.typeA, relationId: IDS.relation },
    camera: { zoom: 1.25, scrollLeft: 40, scrollTop: 12 },
    paneWidths: { navigation: 220, inspector: 320 },
    history: [
      { scope: { kind: 'root' }, granularity: 'project', selectionId: null },
      { scope: { kind: 'type', id: IDS.typeA }, granularity: 'type', selectionId: IDS.typeA }
    ]
  };

  it('accepts a small persisted state', () => {
    expect(validateViewState(state), JSON.stringify(validateViewState.errors)).toBe(true);
  });

  it('rejects an unknown version and oversized history', () => {
    expect(validateViewState({ ...state, version: 2 })).toBe(false);
    expect(
      validateViewState({ ...state, history: Array.from({ length: 21 }, () => ({ scope: {} })) })
    ).toBe(false);
  });
});
