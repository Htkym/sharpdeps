// Dependency-free runtime validation for the v2 analysis model.
//
// The normative contract is schemas/report-v2.schema.json; the contract tests
// (`tests/contract/reportV2Contract.test.ts`) check that this validator and the
// schema agree on the golden fixtures. This validator is deliberately strict
// about structure and referential integrity and tolerant about extra properties,
// so a newer analyzer can add fields without breaking an older host.

import type { AnalysisSnapshot, EvidenceRecord, RelationBasis, RelationKind } from './reportV2';

export interface ValidationFailure {
  ok: false;
  errors: string[];
}

export interface ValidationSuccess<T> {
  ok: true;
  value: T;
}

export type ValidationResult<T> = ValidationSuccess<T> | ValidationFailure;

const MAX_ERRORS = 50;

const ID_PATTERNS = {
  analysis: /^an_[0-9a-f]{16}$/,
  workspaceRoot: /^wrk_[0-9a-f]{16}$/,
  project: /^prj_[0-9a-f]{16}$/,
  variant: /^var_[0-9a-f]{16}$/,
  namespace: /^ns_[0-9a-f]{16}$/,
  type: /^ty_[0-9a-f]{16}$/,
  member: /^mb_[0-9a-f]{16}$/,
  relation: /^rel_[0-9a-f]{16}$/,
  evidence: /^ev_[0-9a-f]{16}$/,
  cycleGroup: /^cyc_[0-9a-f]{16}$/,
  document: /^doc_[0-9a-f]{16}$/,
  diagnostic: /^dg_[0-9a-f]{16}$/,
  profileHash: /^[0-9a-f]{16}$/,
  request: /^req_[0-9a-f]{16}$/,
  cursor: /^cur_[0-9a-f]{16}$/
} as const;

const ENTITY_ID = /^(prj|var|ns|ty|mb)_[0-9a-f]{16}$/;

const RELATION_BASES: readonly RelationBasis[] = [
  'projectDeclared',
  'projectEvaluated',
  'usingInferred',
  'symbolResolved'
];

const RELATION_KINDS: readonly RelationKind[] = [
  'inherits',
  'implements',
  'signature',
  'constraint',
  'constructs',
  'calls',
  'memberAccess',
  'attribute',
  'typeUse',
  'compileTimeName',
  'projectDeclared',
  'projectEvaluated',
  'usingInferred'
];

class Errors {
  readonly messages: string[] = [];

  add(path: string, message: string): void {
    if (this.messages.length < MAX_ERRORS) {
      this.messages.push(`${path}: ${message}`);
    }
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function requireRecord(
  value: unknown,
  path: string,
  errors: Errors
): Record<string, unknown> | undefined {
  if (!isRecord(value)) {
    errors.add(path, 'expected an object');
    return undefined;
  }
  return value;
}

function requireString(
  value: unknown,
  path: string,
  errors: Errors,
  pattern?: RegExp
): string | undefined {
  if (typeof value !== 'string') {
    errors.add(path, 'expected a string');
    return undefined;
  }
  if (pattern && !pattern.test(value)) {
    errors.add(path, `value does not match ${pattern}`);
    return undefined;
  }
  return value;
}

function requireBoolean(value: unknown, path: string, errors: Errors): boolean | undefined {
  if (typeof value !== 'boolean') {
    errors.add(path, 'expected a boolean');
    return undefined;
  }
  return value;
}

function requireCount(value: unknown, path: string, errors: Errors): number | undefined {
  if (typeof value !== 'number' || !Number.isInteger(value) || value < 0) {
    errors.add(path, 'expected a non-negative integer');
    return undefined;
  }
  return value;
}

function requireEnum<T extends string>(
  value: unknown,
  path: string,
  errors: Errors,
  allowed: readonly T[]
): T | undefined {
  if (typeof value !== 'string' || !allowed.includes(value as T)) {
    errors.add(path, `expected one of ${allowed.join(', ')}`);
    return undefined;
  }
  return value as T;
}

function requireArray(
  value: unknown,
  path: string,
  errors: Errors,
  item?: (entry: unknown, itemPath: string) => void
): unknown[] | undefined {
  if (!Array.isArray(value)) {
    errors.add(path, 'expected an array');
    return undefined;
  }
  if (item) {
    value.forEach((entry, index) => item(entry, `${path}[${index}]`));
  }
  return value;
}

function optionalString(value: unknown, path: string, errors: Errors, pattern?: RegExp): void {
  if (value === undefined || value === null) {
    return;
  }
  requireString(value, path, errors, pattern);
}

function optionalCount(value: unknown, path: string, errors: Errors): void {
  if (value === undefined || value === null) {
    return;
  }
  requireCount(value, path, errors);
}

/** Validates a full snapshot, including referential integrity between its parts. */
export function validateSnapshot(input: unknown): ValidationResult<AnalysisSnapshot> {
  const errors = new Errors();
  const snapshot = requireRecord(input, '$', errors);
  if (!snapshot) {
    return { ok: false, errors: errors.messages };
  }

  if (snapshot.schemaVersion !== 2) {
    errors.add('$.schemaVersion', 'unsupported schema version; expected 2');
    return { ok: false, errors: errors.messages };
  }

  requireString(snapshot.analyzerVersion, '$.analyzerVersion', errors);
  requireString(snapshot.analysisId, '$.analysisId', errors, ID_PATTERNS.analysis);
  requireString(snapshot.createdAt, '$.createdAt', errors);
  requireEnum(snapshot.mode, '$.mode', errors, ['quick', 'semantic'] as const);
  requireEnum(snapshot.completeness, '$.completeness', errors, [
    'completeWithinScope',
    'partial',
    'failed'
  ] as const);

  validateTarget(snapshot.target, errors);
  validateProfile(snapshot.profile, errors);
  validateCapabilities(snapshot.capabilities, errors);
  validateCoverage(snapshot.coverage, errors);

  const projectIds = validateProjects(snapshot.projects, errors);
  const namespaceIds = validateNamespaces(snapshot.namespaces, errors);
  const typeIds = validateTypes(snapshot.types, errors);
  const entityIds = new Set([...projectIds, ...namespaceIds, ...typeIds]);
  const relationIds = validateRelations(snapshot.relations, errors, entityIds);
  validateCycleGroups(snapshot.cycleGroups, errors, relationIds, entityIds);
  validateDiagnostics(snapshot.diagnostics, errors, projectIds, namespaceIds, typeIds);
  validateEvidenceIndex(snapshot.evidenceIndex, errors, relationIds);
  validateSourceManifest(snapshot.sourceManifest, errors);
  validateLimitations(snapshot.limitations, errors, '$.limitations');

  return errors.messages.length === 0
    ? { ok: true, value: input as AnalysisSnapshot }
    : { ok: false, errors: errors.messages };
}

/** Validates one evidence NDJSON record. */
export function validateEvidenceRecord(input: unknown): ValidationResult<EvidenceRecord> {
  const errors = new Errors();
  const record = requireRecord(input, '$', errors);
  if (!record) {
    return { ok: false, errors: errors.messages };
  }

  requireString(record.id, '$.id', errors, ID_PATTERNS.evidence);
  requireString(record.relationId, '$.relationId', errors, ID_PATTERNS.relation);
  requireString(record.sourceEntityId, '$.sourceEntityId', errors, ENTITY_ID);
  requireString(record.targetEntityId, '$.targetEntityId', errors, ENTITY_ID);
  requireString(record.documentId, '$.documentId', errors, ID_PATTERNS.document);
  requireString(record.sourceContentHash, '$.sourceContentHash', errors);
  requireEnum(record.origin, '$.origin', errors, ['userSource', 'generatedSource'] as const);
  requireEnum(record.confidence, '$.confidence', errors, ['resolved', 'inferred'] as const);
  requireBoolean(record.publicSurface, '$.publicSurface', errors);
  requireEnum(record.kind, '$.kind', errors, RELATION_KINDS);

  optionalString(record.sourceTypeId, '$.sourceTypeId', errors, ID_PATTERNS.type);
  optionalString(record.sourceMemberId, '$.sourceMemberId', errors, ID_PATTERNS.member);
  optionalString(record.targetTypeId, '$.targetTypeId', errors, ID_PATTERNS.type);
  optionalString(record.targetMemberId, '$.targetMemberId', errors, ID_PATTERNS.member);

  if (record.physicalSpan !== undefined && record.physicalSpan !== null) {
    const span = requireRecord(record.physicalSpan, '$.physicalSpan', errors);
    if (span) {
      for (const key of [
        'start',
        'length',
        'startLine',
        'startCharacter',
        'endLine',
        'endCharacter'
      ]) {
        requireCount(span[key], `$.physicalSpan.${key}`, errors);
      }
    }
  }

  if (record.mappedLocation !== undefined && record.mappedLocation !== null) {
    const mapped = requireRecord(record.mappedLocation, '$.mappedLocation', errors);
    if (mapped) {
      requireString(mapped.relativePath, '$.mappedLocation.relativePath', errors);
      requireCount(mapped.line, '$.mappedLocation.line', errors);
      requireCount(mapped.character, '$.mappedLocation.character', errors);
    }
  }

  return errors.messages.length === 0
    ? { ok: true, value: input as EvidenceRecord }
    : { ok: false, errors: errors.messages };
}

function validateTarget(value: unknown, errors: Errors): void {
  const target = requireRecord(value, '$.target', errors);
  if (!target) {
    return;
  }
  requireEnum(target.kind, '$.target.kind', errors, ['solution', 'slnx', 'project'] as const);
  requireString(target.rootId, '$.target.rootId', errors, ID_PATTERNS.workspaceRoot);
  requireString(target.relativePath, '$.target.relativePath', errors);
}

function validateProfile(value: unknown, errors: Errors): void {
  const profile = requireRecord(value, '$.profile', errors);
  if (!profile) {
    return;
  }
  requireString(profile.configuration, '$.profile.configuration', errors);
  requireString(profile.profileHash, '$.profile.profileHash', errors, ID_PATTERNS.profileHash);
  optionalString(profile.platform, '$.profile.platform', errors);

  requireArray(profile.projectVariants, '$.profile.projectVariants', errors, (entry, path) => {
    const variant = requireRecord(entry, path, errors);
    if (!variant) {
      return;
    }
    requireString(variant.variantId, `${path}.variantId`, errors, ID_PATTERNS.variant);
    requireString(
      variant.projectLogicalId,
      `${path}.projectLogicalId`,
      errors,
      ID_PATTERNS.project
    );
    requireString(variant.targetFramework, `${path}.targetFramework`, errors);
    requireString(variant.configuration, `${path}.configuration`, errors);
    optionalString(variant.platform, `${path}.platform`, errors);
    requireEnum(variant.targetFrameworkSource, `${path}.targetFrameworkSource`, errors, [
      'targetFramework',
      'targetFrameworks',
      'inferred',
      'notSpecified'
    ] as const);
  });
}

function validateCapabilities(value: unknown, errors: Errors): void {
  const capabilities = requireRecord(value, '$.capabilities', errors);
  if (!capabilities) {
    return;
  }
  for (const key of ['typeGraph', 'evidence', 'generatedDocuments', 'cycleWitness', 'search']) {
    requireBoolean(capabilities[key], `$.capabilities.${key}`, errors);
  }
}

function validateCoverage(value: unknown, errors: Errors): void {
  const coverage = requireRecord(value, '$.coverage', errors);
  if (!coverage) {
    return;
  }
  for (const key of ['discovered', 'loaded', 'analyzed', 'failed', 'skipped', 'unresolved']) {
    requireCount(coverage[key], `$.coverage.${key}`, errors);
  }
}

function collectIds(
  values: unknown[] | undefined,
  path: string,
  errors: Errors,
  pattern: RegExp,
  key: string = 'id'
): Set<string> {
  const ids = new Set<string>();
  requireArray(values, path, errors, (entry, itemPath) => {
    const record = requireRecord(entry, itemPath, errors);
    if (!record) {
      return;
    }
    const id = requireString(record[key], `${itemPath}.${key}`, errors, pattern);
    if (id) {
      if (ids.has(id)) {
        errors.add(`${itemPath}.${key}`, `duplicate id ${id}`);
      }
      ids.add(id);
    }
  });
  return ids;
}

function validateProjects(value: unknown, errors: Errors): Set<string> {
  const ids = collectIds(
    Array.isArray(value) ? value : undefined,
    '$.projects',
    errors,
    ID_PATTERNS.project
  );
  requireArray(value, '$.projects', errors, (entry, path) => {
    const project = requireRecord(entry, path, errors);
    if (!project) {
      return;
    }
    requireString(project.variantId, `${path}.variantId`, errors, ID_PATTERNS.variant);
    requireString(project.name, `${path}.name`, errors);
    requireString(project.relativePath, `${path}.relativePath`, errors);
    requireString(project.groupPath, `${path}.groupPath`, errors);
    requireEnum(project.kind, `${path}.kind`, errors, [
      'app',
      'web',
      'library',
      'test',
      'desktop',
      'native',
      'unknown'
    ] as const);
    requireString(project.targetFramework, `${path}.targetFramework`, errors);
    requireEnum(project.loadState, `${path}.loadState`, errors, [
      'loaded',
      'failed',
      'skipped'
    ] as const);
    if (project.packageReferences !== undefined) {
      requireArray(
        project.packageReferences,
        `${path}.packageReferences`,
        errors,
        (item, itemPath) => requireString(item, itemPath, errors)
      );
    }
    validateLimitations(project.limitations, errors, `${path}.limitations`);
  });
  return ids;
}

function validateNamespaces(value: unknown, errors: Errors): Set<string> {
  const ids = collectIds(
    Array.isArray(value) ? value : undefined,
    '$.namespaces',
    errors,
    ID_PATTERNS.namespace
  );
  requireArray(value, '$.namespaces', errors, (entry, path) => {
    const namespace = requireRecord(entry, path, errors);
    if (!namespace) {
      return;
    }
    requireString(
      namespace.projectVariantId,
      `${path}.projectVariantId`,
      errors,
      ID_PATTERNS.variant
    );
    requireString(namespace.name, `${path}.name`, errors);
    requireCount(namespace.typeCount, `${path}.typeCount`, errors);
    optionalString(
      namespace.representativeDocumentId,
      `${path}.representativeDocumentId`,
      errors,
      ID_PATTERNS.document
    );
  });
  return ids;
}

function validateTypes(value: unknown, errors: Errors): Set<string> {
  const ids = collectIds(
    Array.isArray(value) ? value : undefined,
    '$.types',
    errors,
    ID_PATTERNS.type
  );
  requireArray(value, '$.types', errors, (entry, path) => {
    const type = requireRecord(entry, path, errors);
    if (!type) {
      return;
    }
    requireString(type.projectVariantId, `${path}.projectVariantId`, errors, ID_PATTERNS.variant);
    optionalString(type.namespaceId, `${path}.namespaceId`, errors, ID_PATTERNS.namespace);
    requireString(type.name, `${path}.name`, errors);
    requireString(type.fullName, `${path}.fullName`, errors);
    optionalString(type.documentationId, `${path}.documentationId`, errors);
    requireEnum(type.kind, `${path}.kind`, errors, [
      'class',
      'struct',
      'interface',
      'enum',
      'delegate',
      'record',
      'unknown'
    ] as const);
    requireEnum(type.accessibility, `${path}.accessibility`, errors, [
      'public',
      'internal',
      'protected',
      'private',
      'file',
      'unknown'
    ] as const);
    requireBoolean(type.isPartial, `${path}.isPartial`, errors);
    const declarationCount = requireCount(
      type.declarationCount,
      `${path}.declarationCount`,
      errors
    );
    if (declarationCount !== undefined && declarationCount < 1) {
      errors.add(`${path}.declarationCount`, 'expected at least 1');
    }
    optionalCount(type.memberCount, `${path}.memberCount`, errors);
  });
  return ids;
}

function validateRelations(
  value: unknown,
  errors: Errors,
  entityIds: ReadonlySet<string>
): Set<string> {
  const ids = collectIds(
    Array.isArray(value) ? value : undefined,
    '$.relations',
    errors,
    ID_PATTERNS.relation
  );
  requireArray(value, '$.relations', errors, (entry, path) => {
    const relation = requireRecord(entry, path, errors);
    if (!relation) {
      return;
    }
    for (const field of ['sourceEntityId', 'targetEntityId'] as const) {
      const id = requireString(relation[field], `${path}.${field}`, errors, ENTITY_ID);
      if (id && !id.startsWith('mb_') && !entityIds.has(id)) {
        errors.add(`${path}.${field}`, `unknown entity id ${id}`);
      }
    }
    requireEnum(relation.basis, `${path}.basis`, errors, RELATION_BASES);
    const kinds = requireArray(relation.kinds, `${path}.kinds`, errors, (item, itemPath) =>
      requireEnum(item, itemPath, errors, RELATION_KINDS)
    );
    if (kinds && kinds.length === 0) {
      errors.add(`${path}.kinds`, 'expected at least one kind');
    }
    const evidenceCount = requireCount(relation.evidenceCount, `${path}.evidenceCount`, errors);
    if (evidenceCount === 0) {
      errors.add(`${path}.evidenceCount`, 'expected at least 1');
    }
    requireCount(relation.distinctSourceMemberCount, `${path}.distinctSourceMemberCount`, errors);
    requireCount(
      relation.distinctSourceDocumentCount,
      `${path}.distinctSourceDocumentCount`,
      errors
    );
    requireCount(relation.generatedEvidenceCount, `${path}.generatedEvidenceCount`, errors);
    requireCount(relation.publicSurfaceEvidenceCount, `${path}.publicSurfaceEvidenceCount`, errors);
    if (relation.confidence !== undefined) {
      requireEnum(relation.confidence, `${path}.confidence`, errors, [
        'resolved',
        'inferred'
      ] as const);
    }
  });
  return ids;
}

function validateCycleGroups(
  value: unknown,
  errors: Errors,
  relationIds: ReadonlySet<string>,
  entityIds: ReadonlySet<string>
): void {
  collectIds(
    Array.isArray(value) ? value : undefined,
    '$.cycleGroups',
    errors,
    ID_PATTERNS.cycleGroup
  );
  requireArray(value, '$.cycleGroups', errors, (entry, path) => {
    const group = requireRecord(entry, path, errors);
    if (!group) {
      return;
    }
    requireEnum(group.scope, `${path}.scope`, errors, ['type', 'namespace', 'project'] as const);
    requireEnum(group.basis, `${path}.basis`, errors, RELATION_BASES);
    requireArray(group.memberIds, `${path}.memberIds`, errors, (item, itemPath) => {
      const id = requireString(item, itemPath, errors, ENTITY_ID);
      if (id && !id.startsWith('mb_') && !entityIds.has(id)) {
        errors.add(itemPath, `unknown entity id ${id}`);
      }
    });
    requireArray(
      group.internalRelationIds,
      `${path}.internalRelationIds`,
      errors,
      (item, itemPath) => {
        const id = requireString(item, itemPath, errors, ID_PATTERNS.relation);
        if (id && !relationIds.has(id)) {
          errors.add(itemPath, `unknown relation id ${id}`);
        }
      }
    );
    if (group.witness !== undefined && group.witness !== null) {
      const witness = requireRecord(group.witness, `${path}.witness`, errors);
      if (witness) {
        requireArray(witness.memberIds, `${path}.witness.memberIds`, errors, (item, itemPath) =>
          requireString(item, itemPath, errors, ENTITY_ID)
        );
        requireArray(
          witness.relationIds,
          `${path}.witness.relationIds`,
          errors,
          (item, itemPath) => {
            const id = requireString(item, itemPath, errors, ID_PATTERNS.relation);
            if (id && !relationIds.has(id)) {
              errors.add(itemPath, `unknown relation id ${id}`);
            }
          }
        );
      }
    }
    if (group.truncated !== undefined) {
      requireBoolean(group.truncated, `${path}.truncated`, errors);
    }
  });
}

function validateDiagnostics(
  value: unknown,
  errors: Errors,
  projectIds: ReadonlySet<string>,
  namespaceIds: ReadonlySet<string>,
  typeIds: ReadonlySet<string>
): void {
  collectIds(
    Array.isArray(value) ? value : undefined,
    '$.diagnostics',
    errors,
    ID_PATTERNS.diagnostic
  );
  requireArray(value, '$.diagnostics', errors, (entry, path) => {
    const diagnostic = requireRecord(entry, path, errors);
    if (!diagnostic) {
      return;
    }
    requireEnum(diagnostic.severity, `${path}.severity`, errors, [
      'error',
      'warning',
      'info'
    ] as const);
    requireString(diagnostic.code, `${path}.code`, errors);
    requireString(diagnostic.message, `${path}.message`, errors);
    if (typeof diagnostic.targetId === 'string') {
      if (!ENTITY_ID.test(diagnostic.targetId)) {
        errors.add(`${path}.targetId`, 'invalid entity id');
      } else if (
        !projectIds.has(diagnostic.targetId) &&
        !namespaceIds.has(diagnostic.targetId) &&
        !typeIds.has(diagnostic.targetId)
      ) {
        errors.add(`${path}.targetId`, `unknown entity id ${diagnostic.targetId}`);
      }
    }
    optionalString(diagnostic.evidenceId, `${path}.evidenceId`, errors, ID_PATTERNS.evidence);
    optionalString(diagnostic.analysisId, `${path}.analysisId`, errors, ID_PATTERNS.analysis);
  });
}

function validateEvidenceIndex(
  value: unknown,
  errors: Errors,
  relationIds: ReadonlySet<string>
): void {
  if (value === null) {
    return;
  }
  const index = requireRecord(value, '$.evidenceIndex', errors);
  if (!index) {
    return;
  }
  if (index.format !== 'ndjson') {
    errors.add('$.evidenceIndex.format', 'expected ndjson');
  }
  requireString(index.fileName, '$.evidenceIndex.fileName', errors);
  requireCount(index.byteLength, '$.evidenceIndex.byteLength', errors);
  requireArray(index.relations, '$.evidenceIndex.relations', errors, (entry, path) => {
    const relation = requireRecord(entry, path, errors);
    if (!relation) {
      return;
    }
    const id = requireString(
      relation.relationId,
      `${path}.relationId`,
      errors,
      ID_PATTERNS.relation
    );
    if (id && !relationIds.has(id)) {
      errors.add(`${path}.relationId`, `unknown relation id ${id}`);
    }
    requireCount(relation.startByte, `${path}.startByte`, errors);
    requireCount(relation.count, `${path}.count`, errors);
  });
}

function validateSourceManifest(value: unknown, errors: Errors): void {
  collectIds(
    Array.isArray(value) ? value : undefined,
    '$.sourceManifest',
    errors,
    ID_PATTERNS.document
  );
  requireArray(value, '$.sourceManifest', errors, (entry, path) => {
    const document = requireRecord(entry, path, errors);
    if (!document) {
      return;
    }
    requireString(document.relativePath, `${path}.relativePath`, errors);
    requireEnum(document.origin, `${path}.origin`, errors, [
      'userSource',
      'generatedSource'
    ] as const);
    requireString(document.contentHash, `${path}.contentHash`, errors);
    optionalCount(document.byteLength, `${path}.byteLength`, errors);
    optionalString(
      document.mappedFromDocumentId,
      `${path}.mappedFromDocumentId`,
      errors,
      ID_PATTERNS.document
    );
  });
}

function validateLimitations(value: unknown, errors: Errors, path: string): void {
  if (value === undefined) {
    return;
  }
  requireArray(value, path, errors, (entry, itemPath) => {
    const limitation = requireRecord(entry, itemPath, errors);
    if (!limitation) {
      return;
    }
    requireString(limitation.code, `${itemPath}.code`, errors);
    requireString(limitation.message, `${itemPath}.message`, errors);
    optionalString(limitation.scope, `${itemPath}.scope`, errors);
    optionalCount(limitation.count, `${itemPath}.count`, errors);
  });
}
