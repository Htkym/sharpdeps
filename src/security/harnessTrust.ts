import { trustDecision, type TrustDecision } from './trust';

export type HarnessOperation =
  'readSaved' | 'index' | 'update' | 'watch' | 'restore' | 'writeStore';

/** New harness policy; existing analyzer calls continue to use trustDecision unchanged. */
export function harnessTrustDecision(
  operation: HarnessOperation,
  isTrusted: boolean
): TrustDecision {
  switch (operation) {
    case 'readSaved':
      return { allowed: true };
    case 'index':
    case 'update':
    case 'watch':
    case 'restore':
    case 'writeStore':
      return trustDecision(isTrusted);
    default:
      return { allowed: false };
  }
}
