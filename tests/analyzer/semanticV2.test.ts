// Semantic report v2 contract test (SD-011 / SD-013).
//
// Runs the published SemanticHost against the fixture solution, validates the report
// with the runtime validator and the JSON Schemas, and registers it in the result
// store so evidence paging is exercised on real semantic output.

import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import Ajv2020 from 'ajv/dist/2020';
import { describe, expect, it } from 'vitest';
import type { AnalysisSnapshot, EvidenceRecord } from '../../src/analyzer/reportV2';
import { validateEvidenceRecord, validateSnapshot } from '../../src/analyzer/reportV2Validation';
import { ReportStore } from '../../src/analyzer/reportStore';

const repoRoot = process.cwd();
const fixtureDirectory = path.join(repoRoot, 'tests', 'fixtures', 'semantic-baseline');
const solutionPath = path.join(fixtureDirectory, 'SemanticBaseline.sln');
const hostDll = path.join(repoRoot, 'analyzer', 'bin', 'semantic', 'sharpdeps-semantic-host.dll');
const dotnet = process.env.SHARPDEPTS_DOTNET || 'dotnet';

const ajv = new Ajv2020({ allErrors: true, strict: false });
const validateReportSchema = ajv.compile(
  JSON.parse(
    fs.readFileSync(path.join(repoRoot, 'schemas', 'report-v2.schema.json'), 'utf8')
  ) as object
);
const validateEvidenceSchema = ajv.compile(
  JSON.parse(
    fs.readFileSync(path.join(repoRoot, 'schemas', 'evidence-v2.schema.json'), 'utf8')
  ) as object
);

interface SemanticOutput {
  directory: string;
  snapshot: AnalysisSnapshot;
  evidence: EvidenceRecord[];
}

function runSemanticHost(): SemanticOutput {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-semantic-'));
  const run = spawnSync(
    dotnet,
    [
      hostDll,
      '--solution',
      solutionPath,
      '--output',
      path.join(directory, 'probe.json'),
      '--configuration',
      'Debug'
    ],
    { cwd: repoRoot, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 }
  );
  expect(run.status, `semantic host exited with ${run.status}\n${run.stderr}`).toBe(0);

  const snapshot = JSON.parse(
    fs.readFileSync(path.join(directory, 'report-v2.json'), 'utf8')
  ) as AnalysisSnapshot;
  const lines = fs
    .readFileSync(path.join(directory, 'evidence.ndjson'), 'utf8')
    .split('\n')
    .filter((line) => line.trim().length > 0);

  return { directory, snapshot, evidence: lines.map((line) => JSON.parse(line) as EvidenceRecord) };
}

describe.skipIf(!fs.existsSync(hostDll))('Semantic report v2', () => {
  it('satisfies the v2 contract with resolved evidence', () => {
    const { directory, snapshot, evidence } = runSemanticHost();
    try {
      const runtime = validateSnapshot(snapshot);
      expect(runtime.ok, runtime.ok ? '' : runtime.errors.join('\n')).toBe(true);
      expect(validateReportSchema(snapshot), JSON.stringify(validateReportSchema.errors)).toBe(
        true
      );

      expect(snapshot.mode).toBe('semantic');
      expect(snapshot.capabilities.typeGraph).toBe(true);
      expect(snapshot.capabilities.cycleWitness).toBe(true);
      expect(snapshot.types.length).toBeGreaterThan(0);
      expect(snapshot.relations.length).toBeGreaterThan(0);
      expect(snapshot.relations.every((relation) => relation.basis === 'symbolResolved')).toBe(
        true
      );
      expect(snapshot.relations.every((relation) => relation.confidence === 'resolved')).toBe(true);

      expect(evidence.length).toBeGreaterThan(0);
      for (const record of evidence) {
        const validated = validateEvidenceRecord(record);
        expect(validated.ok, validated.ok ? '' : validated.errors.join('\n')).toBe(true);
        expect(validateEvidenceSchema(record)).toBe(true);
      }

      const indexed = (snapshot.evidenceIndex?.relations ?? []).reduce(
        (total, entry) => total + entry.count,
        0
      );
      expect(indexed).toBe(evidence.length);

      // The fixture declares public types with public members, so public-surface
      // evidence must exist; a Quick-only report cannot produce it.
      expect(snapshot.relations.some((relation) => relation.publicSurfaceEvidenceCount > 0)).toBe(
        true
      );

      // SD-011: the generated documents the workspace obtained are part of the result,
      // evidence inside them is counted as generated, and no obj path is exposed.
      expect(snapshot.capabilities.generatedDocuments).toBe(true);
      const generated = snapshot.sourceManifest.filter(
        (document) => document.origin === 'generatedSource'
      );
      expect(generated.length).toBeGreaterThan(0);
      for (const document of generated) {
        expect(document.relativePath.startsWith('generated/')).toBe(true);
        expect(document.relativePath).not.toContain('obj/');
        expect(document.contentHash).toMatch(/^[0-9a-f]{64}$/);
      }

      expect(snapshot.relations.some((relation) => relation.generatedEvidenceCount > 0)).toBe(true);

      const serialized = `${JSON.stringify(snapshot)}\n${fs.readFileSync(
        path.join(directory, 'evidence.ndjson'),
        'utf8'
      )}`;
      expect(serialized).not.toContain(fixtureDirectory);
      expect(serialized).not.toMatch(/[A-Za-z]:\\\\/);
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  }, 120000);

  it('registers in the result store and pages evidence', async () => {
    const { directory, snapshot } = runSemanticHost();
    try {
      const store = new ReportStore();
      const registered = await store.register({ directory, reportFileName: 'report-v2.json' });
      expect(registered.analysisId).toBe(snapshot.analysisId);

      const search = store.search(snapshot.analysisId, 'OrderStore', { limit: 10 });
      expect(search.total).toBeGreaterThan(0);

      const relation = snapshot.relations.find((entry) => entry.evidenceCount >= 1);
      const page = await store.getEvidencePage(snapshot.analysisId, relation!.id, { limit: 1 });
      expect(page.total).toBe(relation!.evidenceCount);
      expect(page.items[0].confidence).toBe('resolved');

      // The retained generated content is served read-only from the result directory.
      const generated = snapshot.sourceManifest.find(
        (document) => document.origin === 'generatedSource'
      );
      expect(generated).toBeDefined();
      const content = await store.readGeneratedDocument(snapshot.analysisId, generated!.id);
      expect(content?.text).toContain('class OrderFactory');
      expect(fs.existsSync(path.join(directory, 'generated', `${generated!.id}.cs`))).toBe(true);
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  }, 120000);
});

if (!fs.existsSync(hostDll)) {
  console.warn(
    'Semantic report v2 test skipped: analyzer/bin/semantic/sharpdeps-semantic-host.dll was not found. ' +
      'Run `npm run build:analyzer` first.'
  );
}
