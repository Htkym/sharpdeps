// Quick v2 regression test.
//
// Runs the published Quick analyzer against the baseline fixture and checks the
// report v2 output:
//   - the runtime validator and the JSON Schema both accept it,
//   - every evidence record is valid and indexed,
//   - the structural snapshot (entity labels, not ids) matches the committed one,
//   - no absolute host path leaks into the report,
//   - ids follow the documented patterns and are deterministic.
//
// Refresh the snapshot after an intentional change:
//   npm run build:analyzer
//   $env:SHARPDEPTS_UPDATE_BASELINE='1'; npm test -- tests/analyzer/quickV2.test.ts

import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import Ajv2020 from 'ajv/dist/2020';
import { describe, expect, it } from 'vitest';
import type { AnalysisSnapshot, EvidenceRecord } from '../../src/analyzer/reportV2';
import { validateEvidenceRecord, validateSnapshot } from '../../src/analyzer/reportV2Validation';
import { buildStructureSnapshot } from '../helpers/quickV2Snapshot';

const repoRoot = process.cwd();
const fixtureRoot = path.join(repoRoot, 'tests', 'fixtures', 'quick-baseline');
const solutionPath = path.join(fixtureRoot, 'Baseline.sln');
const expectedPath = path.join(fixtureRoot, 'expected', 'quick-v2-structure.json');
const updateBaseline = process.env.SHARPDEPTS_UPDATE_BASELINE === '1';
const dotnetPath = process.env.SHARPDEPTS_DOTNET || 'dotnet';

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

/** Locates the published Quick analyzer, in the order the extension looks for it. */
function locateAnalyzerDll(): string | undefined {
  const candidates = [
    path.join(repoRoot, 'analyzer', 'bin', 'quick', 'code-map.dll'),
    path.join(repoRoot, 'analyzer', 'bin', 'code-map.dll')
  ];
  return candidates.find((candidate) => fs.existsSync(candidate));
}

const analyzerDll = locateAnalyzerDll();

interface AnalyzerOutput {
  report: AnalysisSnapshot;
  evidence: EvidenceRecord[];
  evidenceNdjson: string;
}

function runAnalyzer(): AnalyzerOutput {
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-v2-'));
  try {
    const outputPath = path.join(workDir, 'report.json');
    const run = spawnSync(
      dotnetPath,
      [
        analyzerDll as string,
        '--solution',
        solutionPath,
        '--output',
        outputPath,
        '--max-projects',
        '60',
        '--max-edges',
        '200'
      ],
      { cwd: repoRoot, encoding: 'utf8' }
    );
    expect(
      run.status,
      `analyzer exited with ${run.status}\nstdout: ${run.stdout}\nstderr: ${run.stderr}`
    ).toBe(0);

    const report = JSON.parse(
      fs.readFileSync(path.join(workDir, 'report-v2.json'), 'utf8')
    ) as AnalysisSnapshot;
    const evidenceNdjson = fs.readFileSync(path.join(workDir, 'evidence.ndjson'), 'utf8');
    const evidence = evidenceNdjson
      .split('\n')
      .filter((line) => line.trim().length > 0)
      .map((line) => JSON.parse(line) as EvidenceRecord);
    return { report, evidence, evidenceNdjson };
  } finally {
    fs.rmSync(workDir, { recursive: true, force: true });
  }
}

describe.skipIf(!analyzerDll)('Quick analyzer report v2', () => {
  it('satisfies the v2 contract and matches the structural snapshot', async () => {
    const { report, evidence } = runAnalyzer();

    const runtimeResult = validateSnapshot(report);
    expect(runtimeResult.ok, runtimeResult.ok ? '' : runtimeResult.errors.join('\n')).toBe(true);
    expect(validateReportSchema(report), JSON.stringify(validateReportSchema.errors)).toBe(true);

    expect(evidence.length).toBeGreaterThan(0);
    for (const record of evidence) {
      const result = validateEvidenceRecord(record);
      expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
      expect(validateEvidenceSchema(record)).toBe(true);
    }

    const indexed = (report.evidenceIndex?.relations ?? []).reduce(
      (total, entry) => total + entry.count,
      0
    );
    expect(indexed).toBe(evidence.length);

    const structure = buildStructureSnapshot(report, evidence);

    if (updateBaseline) {
      fs.mkdirSync(path.dirname(expectedPath), { recursive: true });
      fs.writeFileSync(expectedPath, `${JSON.stringify(structure, null, 2)}\n`, 'utf8');
      expect(fs.existsSync(expectedPath)).toBe(true);
      return;
    }

    const expected = JSON.parse(fs.readFileSync(expectedPath, 'utf8')) as unknown;
    expect(structure).toEqual(expected);
  });

  it('keeps host paths out of the report', () => {
    const { report, evidenceNdjson } = runAnalyzer();
    const serialized = `${JSON.stringify(report)}\n${evidenceNdjson}`;

    expect(serialized).not.toContain(fixtureRoot);
    expect(serialized).not.toContain(repoRoot);
    expect(serialized).not.toMatch(/[A-Za-z]:\\\\/);
    expect(report.target.relativePath).toBe('Baseline.sln');
  });

  it('uses the documented id shapes and stays deterministic', () => {
    const first = runAnalyzer();
    const second = runAnalyzer();

    for (const relation of first.report.relations) {
      expect(relation.id).toMatch(/^rel_[0-9a-f]{16}$/);
      expect(relation.sourceEntityId).toMatch(/^(prj|var|ns|ty|mb)_[0-9a-f]{16}$/);
      expect(relation.targetEntityId).toMatch(/^(prj|var|ns|ty|mb)_[0-9a-f]{16}$/);
      expect(relation.confidence).toBe('inferred');
    }
    for (const document of first.report.sourceManifest) {
      expect(document.contentHash).toMatch(/^[0-9a-f]{64}$/);
    }

    // Ids are scoped to the workspace root, so two runs on the same checkout must
    // produce exactly the same ids and evidence bytes.
    expect(second.report.relations.map((relation) => relation.id)).toEqual(
      first.report.relations.map((relation) => relation.id)
    );
    expect(second.report.cycleGroups.map((group) => group.id)).toEqual(
      first.report.cycleGroups.map((group) => group.id)
    );
    expect(second.evidenceNdjson).toBe(first.evidenceNdjson);
  });

  it('never presents inferred cycles as verified paths', () => {
    const { report } = runAnalyzer();

    expect(report.capabilities.cycleWitness).toBe(false);
    expect(report.capabilities.typeGraph).toBe(false);
    expect(report.completeness).toBe('partial');
    expect(report.cycleGroups.length).toBeGreaterThan(0);
    for (const group of report.cycleGroups) {
      expect(group.witness ?? null).toBeNull();
      expect(['projectDeclared', 'usingInferred']).toContain(group.basis);
    }
  });
});

if (!analyzerDll) {
  console.warn(
    'Quick report v2 test skipped: analyzer/bin/quick/code-map.dll was not found. ' +
      'Run `npm run build:analyzer` first.'
  );
}
