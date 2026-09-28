// Evidence presentation (SD-018).
//
// Evidence records arrive as plain objects from the host and are never trusted: every
// field is read defensively. Lines and characters are stored 0-based (Roslyn
// LinePosition) and are shown 1-based, which is what an editor shows.

import { translator, type Language } from './i18n';

export type EvidenceOrigin = 'userSource' | 'generatedSource' | 'unknown';
export type EvidenceConfidence = 'resolved' | 'inferred' | 'unknown';

export interface EvidenceView {
  id: string;
  kind: string;
  origin: EvidenceOrigin;
  generated: boolean;
  confidence: EvidenceConfidence;
  publicSurface: boolean;
  documentId?: string;
  documentPath?: string;
  /** 1-based line and character, when the record has a physical span. */
  line?: number;
  character?: number;
  endLine?: number;
  /** 1-based mapped location from a #line directive, when present. */
  mappedPath?: string;
  mappedLine?: number;
  mappedCharacter?: number;
  snippet?: string;
  /** One-line reference for the clipboard: path:line:character kind. */
  reference: string;
}

export function toEvidenceView(record: Record<string, unknown>): EvidenceView {
  const documentId = stringOrUndefined(record.documentId);
  const documentPath = stringOrUndefined(record.documentPath);
  const span = recordOf(record.physicalSpan);
  const mapped = recordOf(record.mappedLocation);
  const origin = readOrigin(record.origin);
  const confidence = readConfidence(record.confidence);
  const kind = stringOrUndefined(record.kind) ?? 'unknown';

  const line = span ? numberOrUndefined(span.startLine) : undefined;
  const character = span ? numberOrUndefined(span.startCharacter) : undefined;
  const endLine = span ? numberOrUndefined(span.endLine) : undefined;
  const mappedLine = mapped ? numberOrUndefined(mapped.line) : undefined;
  const mappedCharacter = mapped ? numberOrUndefined(mapped.character) : undefined;

  const path = documentPath ?? documentId ?? stringOrUndefined(record.id) ?? '(unknown document)';
  const reference =
    line === undefined ? `${path} ${kind}` : `${path}:${line + 1}:${(character ?? 0) + 1} ${kind}`;

  return {
    id: stringOrUndefined(record.id) ?? reference,
    kind,
    origin,
    generated: origin === 'generatedSource',
    confidence,
    publicSurface: record.publicSurface === true,
    documentId,
    documentPath,
    line: line === undefined ? undefined : line + 1,
    character: character === undefined ? undefined : character + 1,
    endLine: endLine === undefined ? undefined : endLine + 1,
    mappedPath: mapped ? stringOrUndefined(mapped.relativePath) : undefined,
    mappedLine: mappedLine === undefined ? undefined : mappedLine + 1,
    mappedCharacter: mappedCharacter === undefined ? undefined : mappedCharacter + 1,
    snippet: stringOrUndefined(record.snippet),
    reference
  };
}

export interface BasisDescription {
  /** Short label for the inspector header. */
  label: string;
  /** What the basis means, including which analyzer produced it. */
  detail: string;
  /** True when the references are resolved symbols (Semantic), not declared/inferred. */
  resolved: boolean;
}

export function describeBasis(basis: string, language: Language = 'en'): BasisDescription {
  const tr = translator(language);
  switch (basis) {
    case 'symbolResolved':
      return {
        label: tr('Semantic (resolved references)'),
        detail: tr('Roslyn resolved these symbol references.'),
        resolved: true
      };
    case 'projectDeclared':
      return {
        label: tr('Quick (declared dependencies)'),
        detail: tr('Dependencies declared in project files; actual references are not included.'),
        resolved: false
      };
    case 'projectEvaluated':
      return {
        label: tr('Semantic (evaluated project dependencies)'),
        detail: tr('Project dependencies based on MSBuild evaluation.'),
        resolved: false
      };
    case 'usingInferred':
      return {
        label: tr('Quick (inferred)'),
        detail: tr('Dependencies inferred from using directives; actual use is not guaranteed.'),
        resolved: false
      };
    default:
      return {
        label: basis,
        detail: tr('The meaning of this basis is unknown.'),
        resolved: false
      };
  }
}

export interface EdgeSummary {
  /** Occurrences on the edge (the sum over the relations it aggregates). */
  total: number;
  generated: number;
  publicSurface: number;
  relationCount: number;
  /** True when the edge aggregates more than one relation. */
  aggregated: boolean;
  /** The underlying relations with their own counts, representative first. */
  relations: Array<{ id: string; evidenceCount: number; kinds: string[]; basis: string }>;
}

export function summarizeEdge(edge: {
  id: string;
  basis: string;
  kinds: string[];
  evidenceCount: number;
  generatedEvidenceCount?: number;
  publicSurfaceEvidenceCount?: number;
  underlyingRelations?: Array<{
    id: string;
    basis: string;
    kinds: string[];
    evidenceCount: number;
  }>;
}): EdgeSummary {
  const relations =
    edge.underlyingRelations && edge.underlyingRelations.length > 0
      ? edge.underlyingRelations
      : [
          {
            id: edge.id,
            basis: edge.basis,
            kinds: edge.kinds,
            evidenceCount: edge.evidenceCount
          }
        ];

  return {
    total: relations.reduce((total, relation) => total + relation.evidenceCount, 0),
    generated: edge.generatedEvidenceCount ?? 0,
    publicSurface: edge.publicSurfaceEvidenceCount ?? 0,
    relationCount: relations.length,
    aggregated: relations.length > 1,
    relations
  };
}

/** Limitations worth repeating inside the details of one selection. */
export function relevantLimitations(
  limitations: Array<{ code: string; message: string }>,
  scope: 'evidence' | 'generated' | 'any'
): Array<{ code: string; message: string }> {
  const matches = (code: string): boolean => {
    if (scope === 'any') {
      return true;
    }

    if (scope === 'generated') {
      return code.includes('generated');
    }

    return (
      code.includes('unresolved') ||
      code.includes('compilationErrors') ||
      code.includes('evidence') ||
      code.includes('generated')
    );
  };

  return limitations.filter((limitation) => matches(limitation.code));
}

function recordOf(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null
    ? (value as Record<string, unknown>)
    : undefined;
}

function stringOrUndefined(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function numberOrUndefined(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function readOrigin(value: unknown): EvidenceOrigin {
  return value === 'userSource' || value === 'generatedSource' ? value : 'unknown';
}

function readConfidence(value: unknown): EvidenceConfidence {
  return value === 'resolved' || value === 'inferred' ? value : 'unknown';
}
