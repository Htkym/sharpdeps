// Normalizes a Quick (v1) analyzer report so it can be compared against a
// committed snapshot regardless of the machine or path separators.
//
// Rules:
//   - Any path-like string has the absolute fixture root replaced with
//     `<FIXTURE_ROOT>` (case-insensitive, `/` or `\` separators) and remaining
//     backslashes are turned into `/`.
//   - `mermaid` is left byte-compatible except for CRLF -> LF, because Mermaid
//     labels contain literal `\n` sequences that must not be rewritten.
//   - Every string has CRLF normalized to LF.

import type { CodeMapReport } from '../../src/analyzer/types';

export const FIXTURE_ROOT_PLACEHOLDER = '<FIXTURE_ROOT>';

export function normalizeQuickReport(report: CodeMapReport, fixtureRoot: string): CodeMapReport {
  const root = fixtureRoot.replace(/\\/g, '/').replace(/\/+$/, '');
  return normalizeValue(report, root, undefined) as CodeMapReport;
}

function normalizeValue(value: unknown, root: string, key: string | undefined): unknown {
  if (typeof value === 'string') {
    const normalized = normalizeString(value, root, key === 'mermaid');
    // Project lookup keys fold case on Windows; path spelling is asserted separately.
    return ['lookupKey', 'sourceKey', 'targetKey'].includes(key ?? '') &&
      normalized.startsWith(FIXTURE_ROOT_PLACEHOLDER)
      ? FIXTURE_ROOT_PLACEHOLDER + normalized.slice(FIXTURE_ROOT_PLACEHOLDER.length).toLowerCase()
      : normalized;
  }
  if (Array.isArray(value)) {
    return value.map((entry) => normalizeValue(entry, root, undefined));
  }
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value).map(([entryKey, entryValue]) => [
        entryKey,
        normalizeValue(entryValue, root, entryKey)
      ])
    );
  }
  return value;
}

function normalizeString(value: string, root: string, textOnly: boolean): string {
  const withoutCrlf = value.replace(/\r\n/g, '\n');
  if (textOnly) {
    return withoutCrlf;
  }

  const pattern = new RegExp(escapeRegExp(root).replace(/\//g, '[\\\\/]'), 'gi');
  return withoutCrlf.replace(pattern, FIXTURE_ROOT_PLACEHOLDER).replace(/\\/g, '/');
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
