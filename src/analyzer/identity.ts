// Identity rules for the v2 analysis model (SD-002, plan section 5.2).
//
// Every shared id is an opaque, prefixed, truncated SHA-256 hash. Ids never
// contain absolute host paths, so exports can be shared without leaking the
// local directory layout. The same logical inputs must always produce the same
// id; ids are not meant to survive renames or signature changes.

import { createHash } from 'node:crypto';

export type EntityIdPrefix = 'prj' | 'var' | 'ns' | 'ty' | 'mb';

const HASH_LENGTH = 16;

/** Field separator that cannot appear in a path, a symbol name, or a doc id. */
const UNIT = '\u001f';

function hash(prefix: string, parts: readonly string[]): string {
  const digest = createHash('sha256').update(parts.join(UNIT), 'utf8').digest('hex');
  return `${prefix}_${digest.slice(0, HASH_LENGTH)}`;
}

/** Normalizes a workspace-relative path for identity: forward slashes, no trailing slash. */
export function normalizeRelativePath(relativePath: string): string {
  return relativePath.replace(/\\/g, '/').replace(/\/+$/, '');
}

/**
 * Stable key for a type declaration. Prefers the Roslyn documentation comment id,
 * which already normalizes generic arity and constructed types, and falls back to
 * a structural key built from the containing type chain.
 */
export function typeKey(options: { documentationId: string }): string;
export function typeKey(options: {
  documentationId?: string | null;
  namespaceName?: string | null;
  containingTypes?: readonly string[];
  name: string;
  arity?: number;
}): string;
export function typeKey(options: {
  documentationId?: string | null;
  namespaceName?: string | null;
  containingTypes?: readonly string[];
  name?: string;
  arity?: number;
}): string {
  if (options.documentationId && options.documentationId.trim().length > 0) {
    return `doc:${options.documentationId.trim()}`;
  }
  const chain = [...(options.containingTypes ?? []), options.name ?? ''];
  const arity = options.arity ?? 0;
  return `sig:${options.namespaceName ?? ''}|${chain.join('.')}\`${arity}`;
}

/** Stable key for a member declaration. */
export function memberKey(options: { documentationId: string }): string;
export function memberKey(options: {
  documentationId?: string | null;
  containingTypeKey: string;
  name: string;
  parameterTypes?: readonly string[];
}): string;
export function memberKey(options: {
  documentationId?: string | null;
  containingTypeKey?: string;
  name?: string;
  parameterTypes?: readonly string[];
}): string {
  if (options.documentationId && options.documentationId.trim().length > 0) {
    return `doc:${options.documentationId.trim()}`;
  }
  const parameters = (options.parameterTypes ?? []).map((type) => type.trim()).join(',');
  return `sig:${options.containingTypeKey ?? ''}|${options.name ?? ''}(${parameters})`;
}

/**
 * Workspace root id. Used internally to scope project ids; the absolute path is
 * hashed away before anything leaves the analyzer.
 */
export function workspaceRootId(rootPath: string): string {
  return hash('wrk', ['root', normalizeRelativePath(rootPath)]);
}

export function projectLogicalId(rootId: string, projectRelativePath: string): string {
  return hash('prj', [rootId, normalizeRelativePath(projectRelativePath)]);
}

export function projectVariantId(
  logicalId: string,
  targetFramework: string,
  configuration: string,
  platform?: string | null
): string {
  return hash('var', [
    logicalId,
    targetFramework.trim().toLowerCase(),
    configuration.trim().toLowerCase(),
    (platform ?? '').trim().toLowerCase()
  ]);
}

export function namespaceId(variantId: string, namespaceName: string): string {
  return hash('ns', [variantId, namespaceName.trim()]);
}

export function typeId(variantId: string, key: string): string {
  return hash('ty', [variantId, key]);
}

export function memberId(variantId: string, key: string): string {
  return hash('mb', [variantId, key]);
}

/**
 * External types use the assembly identity instead of a project variant, so the
 * same referenced type is one node across projects.
 */
export function externalTypeId(assemblyIdentity: string, key: string): string {
  return hash('ty', ['ext', assemblyIdentity.trim(), key]);
}

export function documentId(rootId: string, relativePath: string, origin: string): string {
  return hash('doc', [rootId, normalizeRelativePath(relativePath), origin]);
}

export function relationId(options: {
  basis: string;
  sourceEntityId: string;
  targetEntityId: string;
  profileHash: string;
}): string {
  return hash('rel', [
    options.basis,
    options.sourceEntityId,
    options.targetEntityId,
    options.profileHash
  ]);
}

/**
 * Evidence ids include the document and span so that the same reference found in
 * two places never collapses into one record.
 */
export function evidenceId(options: {
  relationId: string;
  kind: string;
  documentId: string;
  spanKey: string;
}): string {
  return hash('ev', [options.relationId, options.kind, options.documentId, options.spanKey]);
}

export function cycleGroupId(scope: string, basis: string, memberIds: readonly string[]): string {
  return hash('cyc', [scope, basis, [...memberIds].sort().join(',')]);
}

export function diagnosticId(
  code: string,
  targetId: string | null | undefined,
  evidenceIdValue: string | null | undefined
): string {
  return hash('dg', [code, targetId ?? '', evidenceIdValue ?? '']);
}

export function analysisId(options: {
  targetId: string;
  mode: string;
  profileHash: string;
  startedAt: string;
}): string {
  return hash('an', [options.targetId, options.mode, options.profileHash, options.startedAt]);
}

export function requestId(sequence: number | string): string {
  return hash('req', [String(sequence)]);
}

/** Hash of the inputs that affect analysis results (configuration, TFM choices, ...). */
export function profileHash(options: {
  configuration: string;
  platform?: string | null;
  projectVariants: readonly { projectLogicalId: string; targetFramework: string }[];
}): string {
  const variants = options.projectVariants
    .map((variant) => `${variant.projectLogicalId}=${variant.targetFramework.trim().toLowerCase()}`)
    .sort();
  return hash('prf', [
    options.configuration.trim().toLowerCase(),
    (options.platform ?? '').trim().toLowerCase(),
    ...variants
  ]).slice(4);
}

/**
 * DOM ids are derived from the opaque id, never from symbol names, so a hostile
 * type name cannot inject markup or break a CSS selector.
 */
export function domId(prefix: string, id: string): string {
  return `${prefix}-${hash('dom', [id]).slice(4)}`;
}
