import { describe, expect, it } from 'vitest';
import { harnessTrustDecision, type HarnessOperation } from './harnessTrust';
import { trustDecision } from './trust';

describe('harness trust boundary', () => {
  it('keeps saved-result reads available and all mutations denied when untrusted', () => {
    expect(harnessTrustDecision('readSaved', false)).toEqual({ allowed: true });
    for (const operation of ['index', 'update', 'watch', 'restore', 'writeStore'] as const) {
      expect(harnessTrustDecision(operation, false)).toEqual(trustDecision(false));
      expect(harnessTrustDecision(operation, true)).toEqual({ allowed: true });
    }
    expect(trustDecision(false).allowed).toBe(false);
  });

  it('fails closed for an unknown operation even when trusted', () => {
    expect(harnessTrustDecision('unknown' as HarnessOperation, true).allowed).toBe(false);
  });
});
