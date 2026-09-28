import { describe, expect, it } from 'vitest';
import { validateEvidenceRecord, validateSnapshot } from './reportV2Validation';
import { IDS, makeEvidence, makeSnapshot } from '../../tests/helpers/reportV2Fixtures';

describe('validateSnapshot', () => {
  it('accepts a complete snapshot', () => {
    const result = validateSnapshot(makeSnapshot());

    expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
  });

  it('rejects an unknown schema version', () => {
    const result = validateSnapshot(makeSnapshot({ schemaVersion: 3 as unknown as 2 }));

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors[0]).toContain('unsupported schema version');
  });

  it('rejects a missing required field', () => {
    const snapshot = makeSnapshot() as unknown as Record<string, unknown>;
    delete snapshot.projects;

    const result = validateSnapshot(snapshot);

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('$.projects');
  });

  it('rejects malformed ids', () => {
    const snapshot = makeSnapshot();
    snapshot.analysisId = 'analysis-1';

    const result = validateSnapshot(snapshot);

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('$.analysisId');
  });

  it('rejects duplicate entity ids', () => {
    const snapshot = makeSnapshot();
    snapshot.types[1] = { ...snapshot.types[1], id: snapshot.types[0].id };

    const result = validateSnapshot(snapshot);

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('duplicate id');
  });

  it('rejects a relation that points at an unknown entity', () => {
    const snapshot = makeSnapshot();
    snapshot.relations[0] = { ...snapshot.relations[0], targetEntityId: 'ty_ffffffffffffffff' };

    const result = validateSnapshot(snapshot);

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('unknown entity id');
  });

  it('rejects a cycle group that references an unknown relation', () => {
    const snapshot = makeSnapshot({
      cycleGroups: [
        {
          id: IDS.cycle,
          scope: 'type',
          basis: 'symbolResolved',
          memberIds: [IDS.typeA, IDS.typeB],
          internalRelationIds: ['rel_ffffffffffffffff'],
          witness: null
        }
      ]
    });

    const result = validateSnapshot(snapshot);

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('unknown relation id');
  });

  it('rejects an evidence index entry for an unknown relation', () => {
    const snapshot = makeSnapshot();
    snapshot.evidenceIndex = {
      format: 'ndjson',
      fileName: 'evidence.ndjson',
      byteLength: 10,
      relations: [{ relationId: 'rel_ffffffffffffffff', startByte: 0, count: 1 }]
    };

    const result = validateSnapshot(snapshot);

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('unknown relation id');
  });

  it('accepts a snapshot without a side evidence file', () => {
    const result = validateSnapshot(makeSnapshot({ evidenceIndex: null }));

    expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
  });

  it('rejects an empty kinds array on a relation', () => {
    const snapshot = makeSnapshot();
    snapshot.relations[0] = { ...snapshot.relations[0], kinds: [] };

    const result = validateSnapshot(snapshot);

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('at least one kind');
  });

  it('rejects a relation with zero evidence', () => {
    const snapshot = makeSnapshot();
    snapshot.relations[0] = { ...snapshot.relations[0], evidenceCount: 0 };

    const result = validateSnapshot(snapshot);

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('evidenceCount');
  });

  it('rejects non-object input', () => {
    expect(validateSnapshot(null).ok).toBe(false);
    expect(validateSnapshot('report').ok).toBe(false);
    expect(validateSnapshot([]).ok).toBe(false);
  });
});

describe('validateEvidenceRecord', () => {
  it('accepts a resolved evidence record', () => {
    const result = validateEvidenceRecord(makeEvidence());

    expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
  });

  it('accepts a declaration-only record without a span', () => {
    const result = validateEvidenceRecord(
      makeEvidence({ kind: 'usingInferred', physicalSpan: null, confidence: 'inferred' })
    );

    expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
  });

  it('rejects an unknown evidence kind', () => {
    const result = validateEvidenceRecord(makeEvidence({ kind: 'guessed' as unknown as 'calls' }));

    expect(result.ok).toBe(false);
  });

  it('rejects a negative span offset', () => {
    const result = validateEvidenceRecord(
      makeEvidence({ physicalSpan: { ...makeEvidence().physicalSpan!, start: -1 } })
    );

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('physicalSpan.start');
  });
});
