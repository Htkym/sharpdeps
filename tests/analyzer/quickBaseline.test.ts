// Integration regression test for the Quick analyzer.
//
// Runs the published analyzer DLL against tests/fixtures/quick-baseline and
// compares the normalized report with the committed snapshot. The analyzer DLL
// is not committed (analyzer/bin is ignored), so the test skips with an explicit
// reason when it has not been built locally.
//
// Refresh the snapshot after an intentional analyzer change:
//   npm run build:analyzer
//   $env:SHARPDEPTS_UPDATE_BASELINE='1'; npm test -- tests/analyzer/quickBaseline.test.ts

import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import type { CodeMapReport } from '../../src/analyzer/types';
import { normalizeQuickReport } from '../helpers/quickReportNormalizer';

const repoRoot = process.cwd();
const fixtureRoot = path.join(repoRoot, 'tests', 'fixtures', 'quick-baseline');
const solutionPath = path.join(fixtureRoot, 'Baseline.sln');
const expectedPath = path.join(fixtureRoot, 'expected', 'quick-report.json');

/** Analyzer DLL locations, in the order the extension looks for them. */
function locateAnalyzerDll(): string | undefined {
  const candidates = [
    path.join(repoRoot, 'analyzer', 'bin', 'quick', 'code-map.dll'),
    path.join(repoRoot, 'analyzer', 'bin', 'code-map.dll')
  ];
  return candidates.find((candidate) => fs.existsSync(candidate));
}

const analyzerDll = locateAnalyzerDll();
const dotnetPath = process.env.SHARPDEPTS_DOTNET || 'dotnet';
const updateBaseline = process.env.SHARPDEPTS_UPDATE_BASELINE === '1';

describe.skipIf(!analyzerDll)('Quick analyzer baseline fixture', () => {
  it('produces the committed report snapshot', async () => {
    const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-baseline-'));
    const outputPath = path.join(workDir, 'report.json');

    try {
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

      const actual = JSON.parse(fs.readFileSync(outputPath, 'utf8')) as CodeMapReport;
      const normalized = normalizeQuickReport(actual, fixtureRoot);

      if (updateBaseline) {
        const json = `${JSON.stringify(normalized, null, 2)}\n`;
        fs.mkdirSync(path.dirname(expectedPath), { recursive: true });
        fs.writeFileSync(expectedPath, await formatJson(json), 'utf8');
        expect(fs.existsSync(expectedPath)).toBe(true);
        return;
      }

      const expected = JSON.parse(fs.readFileSync(expectedPath, 'utf8')) as CodeMapReport;
      expect(normalized).toEqual(expected);
    } finally {
      fs.rmSync(workDir, { recursive: true, force: true });
    }
  });
});

if (!analyzerDll) {
  // Surfaced as a skipped suite above; this line documents the reason for CI logs.
  console.warn(
    'Quick analyzer baseline fixture skipped: analyzer/bin/quick/code-map.dll and ' +
      'analyzer/bin/code-map.dll were not found. Run `npm run build:analyzer` first.'
  );
}

/** Keep the refreshed snapshot formatted like the rest of the repository. */
async function formatJson(json: string): Promise<string> {
  const prettier = await import('prettier');
  return prettier.format(json, { filepath: expectedPath });
}
