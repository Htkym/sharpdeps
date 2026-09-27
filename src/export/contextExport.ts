// Exports and the evidence-backed AI handoff (SD-022).
//
// Everything is built from the registered analysis result: no network call is made, no
// source file is read, and relative paths only. The context text states what is known,
// what is inferred, and what the reader must not assert as fact.

import type { EntitySummary, ProjectionEdge, ProjectionCycleGroup } from '../view/protocolV2';
import { sanitizeSingleLine } from '../security/paths';

export interface ContextEvidence {
  kind: string;
  origin: string;
  confidence: string;
  documentPath?: string;
  line?: number;
  character?: number;
  snippet?: string;
}

export interface ContextExportInput {
  analysisId: string;
  mode: 'quick' | 'semantic';
  target: { name: string; relativePath: string };
  completeness: string;
  configuration?: string;
  platform?: string | null;
  projectVariants?: { projectPath: string; targetFramework: string }[];
  filters?: Record<string, unknown>;
  search?: string;
  limitations: Array<{ code: string; message: string }>;
  granularity: string;
  scopeLabel: string;
  nodes: EntitySummary[];
  edges: ProjectionEdge[];
  totalNodeCount: number;
  totalEdgeCount: number;
  truncated: boolean;
  cycles: ProjectionCycleGroup[];
  /** Evidence per relation id, already limited by the caller. */
  evidenceByRelation: Record<string, ContextEvidence[]>;
  includeSnippets: boolean;
  maxEvidencePerRelation?: number;
}

const DEFAULT_EVIDENCE_LIMIT = 5;
const MAX_NODES_LISTED = 200;
const MAX_EDGES_LISTED = 300;

/** Markdown handoff document for a coding agent or a review. */
export function buildContextExport(input: ContextExportInput): string {
  const lines: string[] = [];
  lines.push('# SharpDeps analysis context');
  lines.push('');
  lines.push(`- Target: \`${input.target.relativePath}\` (${input.target.name})`);
  lines.push(
    `- Mode: ${input.mode === 'semantic' ? 'Semantic (resolved references)' : 'Quick (declared/inferred)'}`
  );
  lines.push(`- Analysis id: \`${input.analysisId}\``);
  lines.push(`- Completeness: ${input.completeness}`);
  for (const variant of input.projectVariants ?? [])
    lines.push(
      `- TFM: ${sanitizeSingleLine(variant.projectPath)} — ${sanitizeSingleLine(variant.targetFramework)}`
    );
  lines.push(`- Search: ${sanitizeSingleLine(input.search || '(none)')}`);
  lines.push(`- Filters: ${sanitizeSingleLine(JSON.stringify(input.filters ?? {}))}`);
  if (input.configuration) {
    lines.push(
      `- Configuration: ${input.configuration}${input.platform ? ` / ${input.platform}` : ''}`
    );
  }
  lines.push(
    `- Scope: ${input.scopeLabel} · ${input.granularity} · ${input.nodes.length} node(s) shown of ${input.totalNodeCount} · ${input.edges.length} relation(s) shown of ${input.totalEdgeCount}`
  );
  if (input.truncated) {
    lines.push('- The display is truncated; the counts above describe the whole scope.');
  }
  lines.push('');

  lines.push('## How to read this');
  lines.push('');
  if (input.mode === 'quick') {
    lines.push(
      '- Quick results list **declared or inferred** dependencies. They are not proof that a reference exists: do not assert them as facts.'
    );
  } else {
    lines.push(
      '- Semantic code relations list **resolved references** with evidence positions, aggregated for namespace and project views. `projectEvaluated` relations separately record evaluated project references; they do not prove code usage or an exact source position.'
    );
  }
  lines.push(
    '- Edges with an inferred basis are marked `推定` and their occurrences cannot be treated as real references.'
  );
  lines.push('- Only the paths below were analysed; anything not listed is outside this analysis.');
  lines.push('');

  lines.push('## Entities');
  lines.push('');
  for (const node of input.nodes.slice(0, MAX_NODES_LISTED)) {
    const parts = [`\`${sanitizeSingleLine(node.name)}\``, node.granularity];
    if (node.projectName) {
      parts.push(node.projectName);
    }
    if (node.kind) {
      parts.push(node.kind);
    }
    if (node.inCycle) {
      parts.push('in cycle');
    }
    if (node.isExternal) {
      parts.push('external');
    }
    lines.push(`- ${parts.join(' · ')}`);
  }
  if (input.nodes.length > MAX_NODES_LISTED) {
    lines.push(`- … ${input.nodes.length - MAX_NODES_LISTED} more node(s) omitted`);
  }
  lines.push('');

  lines.push('## Relations');
  lines.push('');
  const limit = input.maxEvidencePerRelation ?? DEFAULT_EVIDENCE_LIMIT;
  for (const edge of input.edges.slice(0, MAX_EDGES_LISTED)) {
    const source = nameOf(input.nodes, edge.sourceId);
    const target = nameOf(input.nodes, edge.targetId);
    const inferred = edge.basis === 'usingInferred' ? ' · 推定' : '';
    lines.push(
      `- ${source} → ${target} · ${edge.kinds.join(', ') || edge.basis} · ${edge.evidenceCount} occurrence(s)${inferred}${edge.inCycle ? ' · cycle' : ''}`
    );

    const evidence = input.evidenceByRelation[edge.id] ?? [];
    for (const record of evidence.slice(0, limit)) {
      const location = record.documentPath
        ? `${record.documentPath}${record.line ? `:${record.line}:${record.character ?? 1}` : ''}`
        : '(no position)';
      const flags = [
        record.confidence !== 'resolved' ? record.confidence : undefined,
        record.origin === 'generatedSource' ? 'generated' : undefined
      ].filter(Boolean);
      lines.push(
        `  - evidence: ${location} · ${record.kind}${flags.length > 0 ? ` · ${flags.join(' · ')}` : ''}`
      );
      if (input.includeSnippets && record.snippet) {
        lines.push(`    - snippet: \`${record.snippet.replace(/`/g, "'")}\``);
      }
    }
    if (evidence.length > limit) {
      lines.push(`  - … ${evidence.length - limit} more evidence record(s) omitted`);
    }
  }
  if (input.edges.length > MAX_EDGES_LISTED) {
    lines.push(`- … ${input.edges.length - MAX_EDGES_LISTED} more relation(s) omitted`);
  }
  lines.push('');

  if (input.cycles.length > 0) {
    lines.push('## Cycles');
    lines.push('');
    for (const group of input.cycles) {
      lines.push(
        `- ${group.memberIds.length} member(s), ${group.internalRelationIds.length} internal edge(s)`
      );
      if (group.witness && group.witness.memberIds.length > 0) {
        const path = group.witness.memberIds.map((id) => nameOf(input.nodes, id));
        lines.push(`  - verified path: ${path.join(' → ')}`);
      } else {
        lines.push('  - no verified path; treat the members as mutually reachable only.');
      }
    }
    lines.push('');
  }

  lines.push('## Limitations');
  lines.push('');
  if (input.limitations.length === 0) {
    lines.push('- None reported.');
  } else {
    for (const limitation of input.limitations) {
      lines.push(`- ${limitation.code}: ${limitation.message}`);
    }
  }
  lines.push('');
  lines.push('## Do not assert');
  lines.push('');
  lines.push('- Do not present inferred dependencies as real references.');
  lines.push('- Do not claim the analysis covers files that are not listed above.');
  lines.push('- Do not treat this snapshot as current: re-analyse before relying on it.');

  return `${lines.join('\n')}\n`;
}

/** JSON export of the same selection, with the schema version and conditions. */
export function buildExportJson(input: ContextExportInput): string {
  return `${JSON.stringify(
    {
      schemaVersion: 1,
      kind: 'sharpdeps.selection',
      analysisId: input.analysisId,
      mode: input.mode,
      target: input.target,
      completeness: input.completeness,
      configuration: input.configuration ?? null,
      platform: input.platform ?? null,
      projectVariants: input.projectVariants ?? [],
      filters: input.filters ?? {},
      search: input.search ?? '',
      limitations: input.limitations,
      selection: {
        scopeLabel: input.scopeLabel,
        granularity: input.granularity,
        totalNodeCount: input.totalNodeCount,
        totalEdgeCount: input.totalEdgeCount,
        truncated: input.truncated,
        nodes: input.nodes.map((node) => ({
          id: node.id,
          name: node.name,
          granularity: node.granularity,
          kind: node.kind ?? null,
          projectName: node.projectName ?? null,
          inCycle: node.inCycle === true,
          isExternal: node.isExternal === true
        })),
        edges: input.edges.map((edge) => ({
          id: edge.id,
          sourceId: edge.sourceId,
          targetId: edge.targetId,
          basis: edge.basis,
          kinds: edge.kinds,
          evidenceCount: edge.evidenceCount,
          inCycle: edge.inCycle,
          generatedEvidenceCount: edge.generatedEvidenceCount ?? 0,
          publicSurfaceEvidenceCount: edge.publicSurfaceEvidenceCount ?? 0,
          underlyingRelationIds: edge.underlyingRelationIds ?? [edge.id]
        })),
        cycles: input.cycles.map((group) => ({
          id: group.id,
          memberIds: group.memberIds,
          internalRelationIds: group.internalRelationIds,
          witness: group.witness
        }))
      },
      evidence: input.evidenceByRelation
    },
    null,
    2
  )}\n`;
}

/** Mermaid for the same selection; inferred edges are dashed. */
export function buildMermaid(input: ContextExportInput): string {
  const scopeLabel = input.scopeLabel.replace(
    /(?:ty|ns|prj|pv)_[a-f0-9]+/g,
    (id) => input.nodes.find((node) => node.id === id)?.name ?? '(outside view)'
  );
  const lines: string[] = [
    'flowchart LR',
    `  %% Target: ${sanitizeSingleLine(input.target.relativePath)}; ${input.mode}; ${input.completeness}`,
    `  %% Profile: ${sanitizeSingleLine(input.configuration ?? 'Debug')} / ${sanitizeSingleLine(input.platform ?? 'Default')}`,
    ...(input.projectVariants ?? []).map(
      (variant) =>
        `  %% TFM: ${sanitizeSingleLine(variant.projectPath)} — ${sanitizeSingleLine(variant.targetFramework)}`
    ),
    `  %% Shown: ${input.nodes.length}/${input.totalNodeCount} nodes; ${input.edges.length}/${input.totalEdgeCount} relations; truncated: ${input.truncated}`,
    `  %% Scope: ${sanitizeSingleLine(scopeLabel)}; search: ${sanitizeSingleLine(input.search ?? '')}; filters: ${sanitizeSingleLine(JSON.stringify(input.filters ?? {}))}`
  ];
  const ids = new Map<string, string>();
  const alias = (id: string): string => {
    const existing = ids.get(id);
    if (existing) {
      return existing;
    }

    const next = `n${ids.size + 1}`;
    ids.set(id, next);
    return next;
  };

  for (const node of input.nodes) {
    // A label is one line and one quoted string: a type name must not break out of it.
    const label = sanitizeSingleLine(node.name).replace(/["`]/g, "'");
    lines.push(`  ${alias(node.id)}["${label}"]`);
  }

  for (const edge of input.edges) {
    const label = sanitizeSingleLine(edge.kinds.join(', ') || edge.basis, 80);
    const arrow = edge.basis === 'usingInferred' ? '-.->' : '-->';
    lines.push(`  ${alias(edge.sourceId)} ${arrow}|${label}| ${alias(edge.targetId)}`);
  }

  return `${lines.join('\n')}\n`;
}

function nameOf(nodes: readonly EntitySummary[], id: string): string {
  const name = nodes.find((node) => node.id === id)?.name ?? id;
  return sanitizeSingleLine(name);
}
