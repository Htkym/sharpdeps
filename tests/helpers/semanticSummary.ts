// Normalised summary of a semantic v2 report (SD-025).
//
// Ids and timestamps change between runs, so the summary keeps only what a precision
// regression must not change: per-variant type/relation counts, evidence and basis
// totals, declaration counts, generated documents, and cycle groups. The golden file is
// compared in tests and refreshed with SHARPDEPTS_UPDATE_BASELINE=1.

import * as fs from 'node:fs';
import * as path from 'node:path';
import type { AnalysisSnapshot } from '../../src/analyzer/reportV2';

export interface SemanticSummary {
  schemaVersion: number;
  mode: string;
  completeness: string;
  capabilities: Record<string, boolean>;
  totals: {
    projects: number;
    namespaces: number;
    types: number;
    relations: number;
    evidence: number;
    declarations: number;
    generatedDocuments: number;
    cycleGroups: number;
  };
  /** One entry per loaded project variant, sorted by name. */
  variants: Array<{
    name: string;
    targetFramework: string;
    types: number;
    relations: number;
    /** Relations whose source type is declared in this variant. */
    outgoing: number;
  }>;
  basis: Record<string, number>;
  confidence: Record<string, number>;
  relationKinds: Record<string, number>;
  limitations: string[];
  /** Totals per origin, so generated documents stay visible. */
  documents: Record<string, number>;
}

export function summarize(snapshot: AnalysisSnapshot): SemanticSummary {
  const typeVariant = new Map(snapshot.types.map((type) => [type.id, type.projectVariantId]));

  const basis: Record<string, number> = {};
  const confidence: Record<string, number> = {};
  const relationKinds: Record<string, number> = {};
  const outgoingByVariant = new Map<string, number>();

  for (const relation of snapshot.relations) {
    basis[relation.basis] = (basis[relation.basis] ?? 0) + 1;
    const key = relation.confidence ?? 'unknown';
    confidence[key] = (confidence[key] ?? 0) + 1;
    for (const kind of relation.kinds) {
      relationKinds[kind] = (relationKinds[kind] ?? 0) + 1;
    }

    const variantId = typeVariant.get(relation.sourceEntityId);
    if (variantId) {
      outgoingByVariant.set(variantId, (outgoingByVariant.get(variantId) ?? 0) + 1);
    }
  }

  const typesByVariant = new Map<string, number>();
  for (const type of snapshot.types) {
    typesByVariant.set(type.projectVariantId, (typesByVariant.get(type.projectVariantId) ?? 0) + 1);
  }

  const documents: Record<string, number> = {};
  for (const document of snapshot.sourceManifest) {
    documents[document.origin] = (documents[document.origin] ?? 0) + 1;
  }

  const variants = snapshot.projects
    .map((project) => ({
      name: project.name,
      targetFramework: project.targetFramework,
      types: typesByVariant.get(project.variantId) ?? 0,
      relations: snapshot.relations.filter(
        (relation) => typeVariant.get(relation.sourceEntityId) === project.variantId
      ).length,
      outgoing: outgoingByVariant.get(project.variantId) ?? 0
    }))
    // Relations are emitted per source type, so `relations` and `outgoing` coincide; the
    // two names keep the intent readable in the golden file.
    .filter((entry) => entry.types > 0 || entry.relations > 0)
    .sort((left, right) => left.name.localeCompare(right.name));

  return {
    schemaVersion: snapshot.schemaVersion,
    mode: snapshot.mode,
    completeness: snapshot.completeness,
    capabilities: {
      typeGraph: snapshot.capabilities.typeGraph,
      evidence: snapshot.capabilities.evidence,
      generatedDocuments: snapshot.capabilities.generatedDocuments,
      cycleWitness: snapshot.capabilities.cycleWitness,
      search: snapshot.capabilities.search
    },
    totals: {
      projects: snapshot.projects.length,
      namespaces: snapshot.namespaces.length,
      types: snapshot.types.length,
      relations: snapshot.relations.length,
      evidence: (snapshot.evidenceIndex?.relations ?? []).reduce(
        (total, entry) => total + entry.count,
        0
      ),
      declarations: (snapshot.declarationIndex?.types ?? []).reduce(
        (total, entry) => total + entry.count,
        0
      ),
      generatedDocuments: snapshot.sourceManifest.filter(
        (document) => document.origin === 'generatedSource'
      ).length,
      cycleGroups: snapshot.cycleGroups.length
    },
    variants,
    basis,
    confidence,
    relationKinds,
    limitations: [...new Set(snapshot.limitations.map((limitation) => limitation.code))].sort(),
    documents
  };
}

/** Reads or writes the golden summary; returns the difference line when it changed. */
export function compareWithGolden(
  summary: SemanticSummary,
  goldenPath: string,
  update: boolean
): { matches: boolean; expected?: SemanticSummary } {
  const serialized = `${JSON.stringify(summary, null, 2)}\n`;
  if (update || !fs.existsSync(goldenPath)) {
    fs.mkdirSync(path.dirname(goldenPath), { recursive: true });
    fs.writeFileSync(goldenPath, serialized, 'utf8');
    return { matches: true };
  }

  const expected = JSON.parse(fs.readFileSync(goldenPath, 'utf8')) as SemanticSummary;
  return { matches: serialized === `${JSON.stringify(expected, null, 2)}\n`, expected };
}

/** Short diff of the first differing keys, for an actionable failure message. */
export function diff(summary: SemanticSummary, expected: SemanticSummary): string[] {
  const differences: string[] = [];
  const flat = (value: unknown, prefix: string, into: Map<string, string>): void => {
    if (value !== null && typeof value === 'object') {
      for (const [key, nested] of Object.entries(value as Record<string, unknown>)) {
        flat(nested, prefix.length > 0 ? `${prefix}.${key}` : key, into);
      }
      return;
    }

    into.set(prefix, JSON.stringify(value));
  };

  const actualFlat = new Map<string, string>();
  const expectedFlat = new Map<string, string>();
  flat(summary, '', actualFlat);
  flat(expected, '', expectedFlat);
  for (const key of new Set([...actualFlat.keys(), ...expectedFlat.keys()])) {
    if (actualFlat.get(key) !== expectedFlat.get(key)) {
      differences.push(
        `${key}: ${actualFlat.get(key) ?? '(missing)'} != ${expectedFlat.get(key) ?? '(missing)'}`
      );
    }
  }

  return differences.slice(0, 20);
}
