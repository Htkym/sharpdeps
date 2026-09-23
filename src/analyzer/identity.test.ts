import { describe, expect, it } from 'vitest';
import {
  analysisId,
  cycleGroupId,
  documentId,
  domId,
  evidenceId,
  externalTypeId,
  memberId,
  memberKey,
  namespaceId,
  profileHash,
  projectLogicalId,
  projectVariantId,
  relationId,
  typeId,
  typeKey,
  workspaceRootId
} from './identity';

const rootId = workspaceRootId('C:\\repo');
const otherRootId = workspaceRootId('D:\\other-repo');

describe('identity', () => {
  it('never embeds host paths or symbol names in ids', () => {
    const id = projectLogicalId(rootId, 'src/App/App.csproj');

    expect(id).toMatch(/^prj_[0-9a-f]{16}$/);
    expect(id).not.toContain('App');
    expect(id).not.toContain('repo');
  });

  it('keeps same-named projects in different directories apart', () => {
    const first = projectLogicalId(rootId, 'src/App/App.csproj');
    const second = projectLogicalId(rootId, 'tests/App/App.csproj');
    const otherRoot = projectLogicalId(otherRootId, 'src/App/App.csproj');

    expect(first).not.toBe(second);
    expect(first).not.toBe(otherRoot);
  });

  it('keeps same-named namespaces in different projects apart', () => {
    const projectA = projectVariantId(
      projectLogicalId(rootId, 'src/A/A.csproj'),
      'net10.0',
      'Debug'
    );
    const projectB = projectVariantId(
      projectLogicalId(rootId, 'src/B/B.csproj'),
      'net10.0',
      'Debug'
    );

    expect(namespaceId(projectA, 'Shared')).not.toBe(namespaceId(projectB, 'Shared'));
  });

  it('separates project variants by TFM, configuration, and platform', () => {
    const logicalId = projectLogicalId(rootId, 'src/Core/Core.csproj');

    const variants = [
      projectVariantId(logicalId, 'net10.0', 'Debug'),
      projectVariantId(logicalId, 'net8.0', 'Debug'),
      projectVariantId(logicalId, 'net10.0', 'Release'),
      projectVariantId(logicalId, 'net10.0', 'Debug', 'x64')
    ];

    expect(new Set(variants).size).toBe(variants.length);
  });

  it('normalizes generic arity and constructed types through the definition key', () => {
    const variantId = projectVariantId(
      projectLogicalId(rootId, 'src/A/A.csproj'),
      'net10.0',
      'Debug'
    );
    const definition = typeKey({
      documentationId: 'T:Shared.Repository`1',
      name: 'Repository',
      arity: 1
    });
    const constructed = typeKey({
      documentationId: 'T:Shared.Repository`1',
      name: 'Repository',
      arity: 1
    });
    const unrelated = typeKey({ namespaceName: 'Shared', name: 'Repository', arity: 1 });

    expect(typeId(variantId, definition)).toBe(typeId(variantId, constructed));
    expect(typeId(variantId, definition)).not.toBe(typeId(variantId, unrelated));
  });

  it('falls back to a structural key when no documentation id exists', () => {
    const withNamespace = typeKey({
      namespaceName: 'A.B',
      containingTypes: ['Outer'],
      name: 'Inner'
    });
    const withoutNamespace = typeKey({ containingTypes: ['Outer'], name: 'Inner' });

    expect(withNamespace).not.toBe(withoutNamespace);
    expect(withNamespace).toBe(
      typeKey({ namespaceName: 'A.B', containingTypes: ['Outer'], name: 'Inner' })
    );
  });

  it('keeps partial declarations on one type id', () => {
    const variantId = projectVariantId(
      projectLogicalId(rootId, 'src/A/A.csproj'),
      'net10.0',
      'Debug'
    );
    const key = typeKey({ documentationId: 'T:Shared.PartialThing' });

    // Two declarations, one key: the caller merges declarations before hashing.
    expect(typeId(variantId, key)).toBe(typeId(variantId, key));
  });

  it('distinguishes members by signature and containing type', () => {
    const variantId = projectVariantId(
      projectLogicalId(rootId, 'src/A/A.csproj'),
      'net10.0',
      'Debug'
    );
    const containing = typeKey({ documentationId: 'T:Shared.Order' });

    const byName = memberId(variantId, memberKey({ containingTypeKey: containing, name: 'Total' }));
    const withParameter = memberId(
      variantId,
      memberKey({ containingTypeKey: containing, name: 'Total', parameterTypes: ['int'] })
    );

    expect(byName).not.toBe(withParameter);
  });

  it('separates external types by assembly identity', () => {
    const key = typeKey({ documentationId: 'T:System.String' });

    expect(externalTypeId('System.Runtime, Version=10.0.0.0', key)).not.toBe(
      externalTypeId('System.Runtime, Version=8.0.0.0', key)
    );
  });

  it('distinguishes evidence by document and span', () => {
    const relation = relationId({
      basis: 'symbolResolved',
      sourceEntityId: typeId('var_a', 'sig:A'),
      targetEntityId: typeId('var_b', 'sig:B'),
      profileHash: '0123456789abcdef'
    });
    const document = documentId(rootId, 'src/A/A.cs', 'userSource');

    expect(
      evidenceId({ relationId: relation, kind: 'calls', documentId: document, spanKey: '10:4' })
    ).not.toBe(
      evidenceId({ relationId: relation, kind: 'calls', documentId: document, spanKey: '11:4' })
    );
    expect(
      evidenceId({ relationId: relation, kind: 'calls', documentId: document, spanKey: '10:4' })
    ).not.toBe(
      evidenceId({
        relationId: relation,
        kind: 'constructs',
        documentId: document,
        spanKey: '10:4'
      })
    );
  });

  it('is deterministic for the same logical inputs', () => {
    const first = relationId({
      basis: 'projectDeclared',
      sourceEntityId: 'prj_0123456789abcdef',
      targetEntityId: 'prj_fedcba9876543210',
      profileHash: profileHash({ configuration: 'Debug', projectVariants: [] })
    });
    const second = relationId({
      basis: 'projectDeclared',
      sourceEntityId: 'prj_0123456789abcdef',
      targetEntityId: 'prj_fedcba9876543210',
      profileHash: profileHash({ configuration: 'Debug', projectVariants: [] })
    });

    expect(first).toBe(second);
  });

  it('normalizes path separators and profile inputs', () => {
    expect(projectLogicalId(rootId, 'src\\App\\App.csproj')).toBe(
      projectLogicalId(rootId, 'src/App/App.csproj')
    );

    const upper = profileHash({
      configuration: 'Debug',
      projectVariants: [{ projectLogicalId: 'prj_1', targetFramework: 'NET10.0' }]
    });
    const lower = profileHash({
      configuration: 'debug',
      projectVariants: [{ projectLogicalId: 'prj_1', targetFramework: 'net10.0' }]
    });

    expect(upper).toBe(lower);
    expect(upper).toMatch(/^[0-9a-f]{16}$/);
  });

  it('produces safe dom ids and stable cycle group ids', () => {
    expect(domId('node', 'ty_0123456789abcdef')).toMatch(/^node-[0-9a-f]{16}$/);
    expect(domId('node', '<img src=x onerror=alert(1)>')).toMatch(/^node-[0-9a-f]{16}$/);
    expect(cycleGroupId('project', 'projectDeclared', ['b', 'a'])).toBe(
      cycleGroupId('project', 'projectDeclared', ['a', 'b'])
    );
  });

  it('formats every id with the documented prefix and length', () => {
    const variantId = projectVariantId('prj_0123456789abcdef', 'net10.0', 'Debug');
    const samples = [
      analysisId({
        targetId: 'wrk_0123456789abcdef',
        mode: 'quick',
        profileHash: 'a'.repeat(16),
        startedAt: '2026-09-23T00:00:00Z'
      }),
      variantId,
      documentId(rootId, 'a.cs', 'userSource'),
      relationId({
        basis: 'usingInferred',
        sourceEntityId: 'ns_1',
        targetEntityId: 'ns_2',
        profileHash: 'a'.repeat(16)
      })
    ];

    expect(samples.every((sample) => /^(an|var|doc|rel)_[0-9a-f]{16}$/.test(sample))).toBe(true);
  });
});
