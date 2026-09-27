// Security boundaries (SD-023): trust policy, path containment, single-line text, the
// webview CSP, the bounded search query, and hostile names in the export formats.

import { describe, expect, it } from 'vitest';
import { isInsideRoot, isOutsideRoot, sanitizeSingleLine } from '../../src/security/paths';
import { trustDecision } from '../../src/security/trust';
import { buildWebviewCsp } from '../../src/view/csp';
import { MAX_QUERY_LENGTH, validateWebviewMessage } from '../../src/view/protocolV2';
import { buildContextExport, buildExportJson, buildMermaid } from '../../src/export/contextExport';
import type { ContextExportInput } from '../../src/export/contextExport';

describe('trust decision', () => {
  it('refuses analyzer work in an untrusted workspace', () => {
    const refused = trustDecision(false);
    expect(refused.allowed).toBe(false);
    expect(refused.action).toBe('manageTrust');
    expect(refused.message).toContain('信頼');
    expect(trustDecision(true)).toEqual({ allowed: true });
  });
});

describe('path containment', () => {
  it('accepts files inside the root and linked files that stay reachable', () => {
    expect(isInsideRoot('D:/repo', 'src/Domain/Order.cs')).toBe(true);
    expect(isInsideRoot('D:/repo', './src/Order.cs')).toBe(true);
    expect(isInsideRoot('D:/repo', 'src/../shared/Shared.cs')).toBe(true);
  });

  it('rejects escapes, absolute paths, and unusable segments', () => {
    expect(isOutsideRoot('D:/repo', '../secret.txt')).toBe(true);
    expect(isOutsideRoot('D:/repo', 'src/../../secret.txt')).toBe(true);
    expect(isOutsideRoot('D:/repo', 'C:/Windows/System32/config')).toBe(true);
    expect(isOutsideRoot('D:/repo', '/etc/passwd')).toBe(true);
    expect(isOutsideRoot('D:/repo', 'src/ord?er.cs')).toBe(true);
  });
});

describe('single-line text', () => {
  it('collapses control characters and truncates long values', () => {
    expect(sanitizeSingleLine('a\r\nb\tc')).toBe('a b c');
    expect(sanitizeSingleLine('x'.repeat(300)).length).toBeLessThanOrEqual(201);
  });
});

describe('webview CSP', () => {
  const csp = buildWebviewCsp('vscode-webview://abc', 'NONCE123');

  it('allows no eval, no remote origin, and locks frames and objects', () => {
    expect(csp).toContain("default-src 'none'");
    expect(csp).toContain("script-src 'nonce-NONCE123'");
    expect(csp).not.toContain('unsafe-eval');
    expect(csp).not.toContain('http:');
    expect(csp).not.toContain('https:');
    expect(csp).toContain("object-src 'none'");
    expect(csp).toContain("frame-src 'none'");
    expect(csp).toContain("base-uri 'none'");
    expect(csp).toContain('connect-src vscode-webview://abc');
    expect(csp).toContain('worker-src blob:');
  });
});

describe('bounded search query', () => {
  it('rejects a query that is longer than the limit', () => {
    const base = {
      type: 'searchEntities',
      requestId: 'req_0000000000000001',
      analysisId: 'an_0000000000000001'
    };

    expect(validateWebviewMessage({ ...base, query: 'x'.repeat(MAX_QUERY_LENGTH) }).ok).toBe(true);
    const tooLong = validateWebviewMessage({
      ...base,
      query: 'x'.repeat(MAX_QUERY_LENGTH + 1)
    });
    expect(tooLong.ok).toBe(false);
    expect(tooLong.ok ? '' : tooLong.errors.join(',')).toContain('longer than');
  });
});

describe('hostile names in exports', () => {
  const hostileName = '<script>alert(1)</script>\n`${x}` "quoted" "';

  function input(): ContextExportInput {
    return {
      analysisId: 'an_0123456789abcdef',
      mode: 'semantic',
      target: { name: 'Sample.sln', relativePath: 'src/Sample.sln' },
      completeness: 'completeWithinScope',
      limitations: [],
      granularity: 'type',
      scopeLabel: 'whole analysis',
      nodes: [
        { id: 'ty_1111111111111111', name: hostileName, granularity: 'type' },
        { id: 'ty_2222222222222222', name: 'B', granularity: 'type' }
      ],
      edges: [
        {
          id: 'rel_1111111111111111',
          sourceId: 'ty_1111111111111111',
          targetId: 'ty_2222222222222222',
          basis: 'symbolResolved',
          kinds: ['calls'],
          evidenceCount: 1,
          inCycle: false
        }
      ],
      totalNodeCount: 2,
      totalEdgeCount: 1,
      truncated: false,
      cycles: [],
      evidenceByRelation: {},
      includeSnippets: false
    };
  }

  it('keeps markup inert in Mermaid and JSON', () => {
    const mermaid = buildMermaid(input());
    // One line per declaration and no raw quote/backtick inside the label.
    const labelLine = mermaid.split('\n').find((line) => line.includes('script')) ?? '';
    expect(labelLine).not.toContain('\n');
    expect(labelLine).not.toContain('`');
    expect(labelLine.match(/"/g)?.length).toBe(2);

    const parsed = JSON.parse(buildExportJson(input())) as {
      selection: { nodes: Array<{ name: string }> };
    };
    expect(parsed.selection.nodes[0].name).toBe(hostileName);
  });

  it('keeps the context text readable without executing anything', () => {
    const text = buildContextExport(input());
    expect(text).toContain('script');
    expect(text).not.toContain('<script>alert(1)</script>\n');
  });
});
