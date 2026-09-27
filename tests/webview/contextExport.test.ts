// Export builders (SD-022): the context text, JSON, and Mermaid describe the same
// selection, never leak absolute paths, and state what must not be asserted.

import { describe, expect, it } from 'vitest';
import { buildContextExport, buildExportJson, buildMermaid } from '../../src/export/contextExport';
import type { ContextExportInput } from '../../src/export/contextExport';

function input(overrides: Partial<ContextExportInput> = {}): ContextExportInput {
  const base: ContextExportInput = {
    analysisId: 'an_0123456789abcdef',
    mode: 'semantic',
    target: { name: 'Sample.sln', relativePath: 'src/Sample.sln' },
    completeness: 'partial',
    configuration: 'Debug',
    platform: null,
    limitations: [
      { code: 'semantic.compilationErrors', message: '2 projects have compilation errors.' }
    ],
    granularity: 'type',
    scopeLabel: 'dependencies: ty_1111111111111111 (depth 1)',
    nodes: [
      {
        id: 'ty_1111111111111111',
        name: '注文サービス',
        granularity: 'type',
        kind: 'class',
        projectName: 'Domain',
        inCycle: true
      },
      { id: 'ty_2222222222222222', name: '在庫管理', granularity: 'type', isExternal: true }
    ],
    edges: [
      {
        id: 'rel_1111111111111111',
        sourceId: 'ty_1111111111111111',
        targetId: 'ty_2222222222222222',
        basis: 'symbolResolved',
        kinds: ['constructs'],
        evidenceCount: 3,
        inCycle: true,
        generatedEvidenceCount: 1,
        underlyingRelationIds: ['rel_1111111111111111']
      },
      {
        id: 'rel_2222222222222222',
        sourceId: 'ty_2222222222222222',
        targetId: 'ty_1111111111111111',
        basis: 'usingInferred',
        kinds: ['typeUse'],
        evidenceCount: 1,
        inCycle: false
      }
    ],
    totalNodeCount: 4,
    totalEdgeCount: 3,
    truncated: true,
    cycles: [
      {
        id: 'cyc_1111111111111111',
        scope: 'type',
        basis: 'symbolResolved',
        memberIds: ['ty_1111111111111111', 'ty_2222222222222222'],
        internalRelationIds: ['rel_1111111111111111'],
        witness: {
          memberIds: ['ty_1111111111111111', 'ty_2222222222222222'],
          relationIds: ['rel_1111111111111111']
        }
      }
    ],
    evidenceByRelation: {
      rel_1111111111111111: [
        {
          kind: 'constructs',
          origin: 'generatedSource',
          confidence: 'resolved',
          documentPath: 'generated/Domain/OrderFactory.g.cs',
          line: 5,
          character: 8
        }
      ],
      rel_2222222222222222: [
        {
          kind: 'typeUse',
          origin: 'userSource',
          confidence: 'inferred',
          documentPath: 'src/Domain/Order.cs',
          line: 12,
          character: 4
        }
      ]
    },
    includeSnippets: false
  };

  return { ...base, ...overrides };
}

describe('buildContextExport', () => {
  it('carries the target, conditions, evidence, cycles, and the do-not-assert list', () => {
    const text = buildContextExport(input());

    expect(text).toContain('src/Sample.sln');
    expect(text).toContain('Semantic (resolved references)');
    expect(text).toContain('partial');
    expect(text).toContain('semantic.compilationErrors');
    expect(text).toContain('注文サービス → 在庫管理 · constructs · 3 occurrence(s) · cycle');
    expect(text).toContain(
      'evidence: generated/Domain/OrderFactory.g.cs:5:8 · constructs · generated'
    );
    expect(text).toContain('推定');
    expect(text).toContain('verified path: 注文サービス → 在庫管理');
    expect(text).toContain('## Do not assert');
    expect(text).toContain('Do not present inferred dependencies as real references.');
    expect(text).toContain('2 relation(s) shown of 3');
    expect(text).not.toMatch(/[A-Za-z]:\\/);
  });

  it('marks Quick results as not proof and keeps snippets opt-in', () => {
    const quick = buildContextExport(input({ mode: 'quick', includeSnippets: false }));
    expect(quick).toContain('declared or inferred');
    expect(quick).not.toContain('snippet:');

    const withSnippet = buildContextExport(
      input({
        includeSnippets: true,
        evidenceByRelation: {
          rel_1111111111111111: [
            {
              kind: 'constructs',
              origin: 'userSource',
              confidence: 'resolved',
              documentPath: 'src/Domain/OrderService.cs',
              line: 20,
              character: 9,
              snippet: 'new `OrderFactory`()'
            }
          ]
        }
      })
    );
    expect(withSnippet).toContain('snippet:');
    // Backticks inside a snippet would break the markdown fence.
    expect(withSnippet).not.toContain('`OrderFactory`()');
  });
});

describe('buildExportJson', () => {
  it('uses a schema version and the same selection', () => {
    const parsed = JSON.parse(buildExportJson(input())) as {
      schemaVersion: number;
      selection: { nodes: unknown[]; edges: unknown[]; cycles: unknown[] };
      evidence: Record<string, unknown[]>;
    };

    expect(parsed.schemaVersion).toBe(1);
    expect(parsed.selection.nodes).toHaveLength(2);
    expect(parsed.selection.edges).toHaveLength(2);
    expect(parsed.selection.cycles).toHaveLength(1);
    expect(parsed.evidence['rel_1111111111111111']).toHaveLength(1);
  });
});

describe('buildMermaid', () => {
  it('shows the same nodes and marks inferred edges as dashed', () => {
    const mermaid = buildMermaid(input());

    expect(mermaid.startsWith('flowchart LR')).toBe(true);
    expect(mermaid).toContain('["注文サービス"]');
    expect(mermaid).toContain('-->|constructs|');
    expect(mermaid).toContain('-.->|typeUse|');
    // Two nodes, two edges: every id must be aliased, not printed raw.
    expect(mermaid).not.toContain('ty_1111111111111111');
  });
});
