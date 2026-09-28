import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { expect, it } from 'vitest';
import { compareWithGolden, type SemanticSummary } from './semanticSummary';

it('compares semantic values independently of object insertion order and requires a golden file', () => {
  const summary = JSON.parse(
    fs.readFileSync('tests/fixtures/semantic-baseline/expected/semantic-summary.json', 'utf8')
  ) as SemanticSummary;
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-golden-'));
  const file = path.join(directory, 'expected.json');
  try {
    fs.writeFileSync(file, JSON.stringify(Object.fromEntries(Object.entries(summary).reverse())));
    expect(compareWithGolden(summary, file, false).matches).toBe(true);
    expect(
      compareWithGolden(
        { ...summary, totals: { ...summary.totals, types: summary.totals.types + 1 } },
        file,
        false
      ).matches
    ).toBe(false);
    fs.unlinkSync(file);
    expect(() => compareWithGolden(summary, file, false)).toThrow();
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
