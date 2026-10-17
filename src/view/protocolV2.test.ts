import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import Ajv2020 from 'ajv/dist/2020';
import { PROTOCOL_VERSION, validateHostMessage, validateWebviewMessage } from './protocolV2';

const requestId = 'req_0123456789abcdef';
const analysisId = 'an_0123456789abcdef';

describe('candidate investigation contract', () => {
  const schema = new Ajv2020({ strict: false }).compile(
    JSON.parse(readFileSync('schemas/protocol-v2.schema.json', 'utf8'))
  );
  const origin = {
    originId: `ci_${'1'.repeat(32)}`,
    relationId: 'rel_1111111111111111',
    targetEntityId: 'ty_1111111111111111',
    targetName: 'Compute'
  };
  const declaration = {
    capabilityId: `cd_${'2'.repeat(32)}`,
    occurrenceId: `hocc_${'3'.repeat(64)}`,
    variantId: `hvar_${'4'.repeat(64)}`,
    path: 'src/実装🐈.cs'
  };
  const metadata = {
    provider: 'savedIndex',
    workspaceId: 'workspace-a',
    snapshotId: 'snapshot-a',
    generation: 5,
    variantIds: [declaration.variantId],
    coverage: 'Partial',
    freshness: 'unverified',
    truncated: false,
    truncationReasons: [],
    diagnostics: [],
    returnedCount: 1,
    totalKind: 'returned',
    candidateCount: 0,
    unresolvedCount: 0
  };
  const response = {
    type: 'candidateInvestigation',
    requestId,
    analysisId,
    originId: origin.originId,
    relationId: origin.relationId,
    targetEntityId: origin.targetEntityId,
    certainty: 'Candidate',
    relationKind: 'mentionsSymbol',
    reason: 'Potential symbol match',
    message: 'Declaration found; relation remains a candidate.',
    queryMetadata: metadata,
    declarations: [declaration]
  };
  const check = (message: unknown, expected: boolean, direction: 'host' | 'webview') => {
    expect(
      (direction === 'host' ? validateHostMessage(message) : validateWebviewMessage(message)).ok
    ).toBe(expected);
    expect(schema(message), JSON.stringify(schema.errors)).toBe(expected);
  };

  it('requires issued candidate roles and reuses the bounded declaration return anchor', () => {
    for (const type of ['investigateCandidate', 'dismissCandidateInvestigation']) {
      const request = { type, requestId, analysisId, originId: origin.originId };
      check(request, true, 'webview');
      check({ ...request, originId: declaration.capabilityId }, false, 'webview');
      check({ ...request, originId: 'docs/Input.cs' }, false, 'webview');
    }
    const request = {
      type: 'openCandidateDeclaration',
      requestId,
      analysisId,
      originId: origin.originId,
      capabilityId: declaration.capabilityId,
      returnUi: {
        navTab: 'structure',
        focus: 'declaration',
        id: origin.targetEntityId,
        scroll: { navigation: 12, inspector: 34, map: 56 },
        searchCount: 40
      }
    };
    check(request, true, 'webview');
    check({ ...request, returnUi: undefined }, true, 'webview');
    check({ ...request, capabilityId: origin.originId }, false, 'webview');
    check(
      { ...request, returnUi: { ...request.returnUi, id: declaration.capabilityId } },
      false,
      'webview'
    );
    check(
      {
        ...request,
        returnUi: { ...request.returnUi, scroll: { navigation: -1, inspector: 0, map: 0 } }
      },
      false,
      'webview'
    );
  });

  it('bounds origin actions and exact declarations without upgrading relation certainty', () => {
    const details = {
      type: 'details',
      requestId,
      analysisId,
      entity: {},
      candidateOrigins: [origin]
    };
    check(details, true, 'host');
    check({ ...details, candidateOrigins: Array(8).fill(origin) }, true, 'host');
    for (const candidateOrigins of [
      null,
      Array(9).fill(origin),
      [{ ...origin, targetEntityId: 'mb_1111111111111111' }],
      [{ ...origin, targetName: 'x'.repeat(4097) }]
    ])
      check({ ...details, candidateOrigins }, false, 'host');
    check(response, true, 'host');
    check(
      { ...response, declarations: [], unavailableReason: 'No verified occurrence.' },
      true,
      'host'
    );
    check({ ...response, declarations: Array(16).fill(declaration) }, true, 'host');
    for (const changed of [
      { certainty: 'Resolved' },
      { targetEntityId: 'mb_1111111111111111' },
      { declarations: Array(17).fill(declaration) },
      { declarations: [{ ...declaration, capabilityId: origin.originId }] },
      { declarations: [{ ...declaration, occurrenceId: declaration.variantId }] },
      { declarations: [{ ...declaration, variantId: declaration.occurrenceId }] },
      { declarations: [{ ...declaration, path: '' }] },
      { reason: 'x'.repeat(4097) },
      { queryMetadata: { ...metadata, candidateCount: -1 } },
      { queryMetadata: { ...metadata, variantIds: ['variant-a'] } },
      { queryMetadata: { ...metadata, diagnostics: Array(65).fill({ code: 'x', message: 'x' }) } }
    ])
      check({ ...response, ...changed }, false, 'host');
  });
});

describe('validateWebviewMessage', () => {
  it('accepts bounded source-return anchors and rejects paths or unbounded UI data', () => {
    const ui = {
      navTab: 'structure',
      focus: 'declaration',
      id: 'ty_1111111111111111',
      scroll: { navigation: 12, inspector: 340, map: 56 },
      searchCount: 40
    };
    const request = {
      type: 'openDeclaration',
      requestId,
      analysisId,
      entityId: ui.id,
      returnUi: ui
    };
    expect(validateWebviewMessage(request).ok).toBe(true);
    expect(validateWebviewMessage({ ...request, returnUi: undefined }).ok).toBe(true);
    expect(
      validateWebviewMessage({
        type: 'openEvidence',
        requestId,
        analysisId,
        evidenceId: 'ev_1111111111111111',
        returnUi: { ...ui, focus: 'evidence', id: 'ev_1111111111111111' }
      }).ok
    ).toBe(true);
    for (const returnUi of [
      { ...ui, id: 'docs/調査.md' },
      { ...ui, focus: 'evidence' },
      { ...ui, navTab: 'arbitrary-tab' },
      { ...ui, scroll: { ...ui.scroll, inspector: -1 } },
      { ...ui, scroll: { ...ui.scroll, navigation: Infinity } },
      { ...ui, scroll: { ...ui.scroll, map: 10000001 } },
      { ...ui, searchCount: 501 },
      { ...ui, searchCount: 1.5 }
    ])
      expect(validateWebviewMessage({ ...request, returnUi }).ok).toBe(false);
    const response = {
      type: 'sourceReturn',
      requestId,
      analysisId,
      state: { version: 1, granularity: 'type' },
      ui
    };
    expect(validateHostMessage(response).ok).toBe(true);
    expect(validateHostMessage({ ...response, ui: { ...ui, id: '#arbitrary-selector' } }).ok).toBe(
      false
    );
  });

  it('restricts result classification filters and keeps display paths out of source-open requests', () => {
    const request = { type: 'searchEntities', requestId, analysisId, query: '調査' };
    for (const resultKinds of [undefined, ['docs'], ['code', 'docs']])
      expect(validateWebviewMessage({ ...request, filters: { resultKinds } }).ok).toBe(true);
    for (const resultKinds of [[], ['Other'], ['Document'], 'docs', ['code', 'docs', 'docs'], [1]])
      expect(validateWebviewMessage({ ...request, filters: { resultKinds } }).ok).toBe(false);
    expect(
      validateWebviewMessage({
        type: 'openDeclaration',
        requestId,
        analysisId,
        path: 'docs/調査.md'
      }).ok
    ).toBe(false);
  });

  it('validates saved projection provenance without requiring it from legacy exports', () => {
    for (const type of ['copyContext', 'export'] as const) {
      const request = {
        type,
        requestId,
        analysisId,
        scope: { kind: 'root' },
        ...(type === 'export' ? { format: 'json' } : {})
      };
      expect(validateWebviewMessage(request).ok).toBe(true);
      expect(validateWebviewMessage({ ...request, projectionRequestId: requestId }).ok).toBe(true);
      expect(validateWebviewMessage({ ...request, projectionRequestId: 'raw-saved-id' }).ok).toBe(
        false
      );
    }
  });
  it('requires an exact saved session for primary updates and a bounded host phase', () => {
    expect(validateWebviewMessage({ type: 'updateSavedIndex', requestId, analysisId }).ok).toBe(
      true
    );
    expect(validateWebviewMessage({ type: 'updateSavedIndex', requestId }).ok).toBe(false);
    expect(
      validateHostMessage({
        type: 'savedUpdateState',
        analysisId,
        phase: 'cancelling',
        canUpdate: false
      }).ok
    ).toBe(true);
    expect(
      validateHostMessage({
        type: 'savedUpdateState',
        analysisId,
        phase: 'rolledBack',
        canUpdate: true
      }).ok
    ).toBe(false);
  });
  it('accepts a version 2 ready message', () => {
    const result = validateWebviewMessage({ type: 'ready', protocolVersion: PROTOCOL_VERSION });

    expect(result.ok).toBe(true);
  });

  it('rejects an unknown message type', () => {
    const result = validateWebviewMessage({ type: 'runRoslyn', requestId });

    expect(result.ok).toBe(false);
    expect(result.ok ? undefined : result.code).toBe('unknownType');
  });

  it('rejects a message without a type', () => {
    const result = validateWebviewMessage({ requestId });

    expect(result.ok).toBe(false);
    expect(result.ok ? undefined : result.code).toBe('invalidMessage');
  });

  it('rejects a missing required field', () => {
    const result = validateWebviewMessage({ type: 'getProjection', requestId, analysisId });

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('scope');
  });

  it('rejects malformed ids and cursors', () => {
    const badRequest = validateWebviewMessage({
      type: 'getEntityDetails',
      requestId: 'req-1',
      analysisId,
      entityId: 'ty_0123456789abcdef'
    });
    const badCursor = validateWebviewMessage({
      type: 'getEvidencePage',
      requestId,
      analysisId,
      relationId: 'rel_0123456789abcdef',
      cursor: 'offset:512'
    });

    expect(badRequest.ok).toBe(false);
    expect(badCursor.ok).toBe(false);
    expect(badCursor.ok ? [] : badCursor.errors.join('\n')).toContain('cursor');
  });

  it('rejects an invalid scope or export format', () => {
    const badScope = validateWebviewMessage({
      type: 'getProjection',
      requestId,
      analysisId,
      scope: { kind: 'file', id: 'ty_0123456789abcdef' },
      granularity: 'type'
    });
    const badFormat = validateWebviewMessage({
      type: 'export',
      requestId,
      analysisId,
      format: 'pdf',
      scope: { kind: 'root' }
    });

    expect(badScope.ok).toBe(false);
    expect(badFormat.ok).toBe(false);
  });

  it('rejects a scope that points at a path instead of an id', () => {
    const result = validateWebviewMessage({
      type: 'getProjection',
      requestId,
      analysisId,
      scope: { kind: 'type', id: 'C:\\repo\\src\\A.cs' },
      granularity: 'type'
    });

    expect(result.ok).toBe(false);
    expect(result.ok ? [] : result.errors.join('\n')).toContain('entity id');
  });

  it('rejects a traversal depth outside the supported range', () => {
    const result = validateWebviewMessage({
      type: 'getProjection',
      requestId,
      analysisId,
      scope: { kind: 'dependents', id: 'ty_0123456789abcdef', depth: 9 },
      granularity: 'type'
    });

    expect(result.ok).toBe(false);
  });

  it('accepts a well-formed analyze request', () => {
    const result = validateWebviewMessage({
      type: 'analyze',
      requestId,
      mode: 'semantic',
      targetId: 'wrk_0123456789abcdef',
      profile: { configuration: 'Debug' }
    });

    expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
  });
});

describe('validateHostMessage', () => {
  it('accepts a capabilities message', () => {
    const result = validateHostMessage({
      type: 'capabilities',
      protocolVersion: PROTOCOL_VERSION,
      capabilities: {
        typeGraph: true,
        evidence: true,
        generatedDocuments: false,
        cycleWitness: true,
        search: true
      },
      analysisId
    });

    expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
  });

  it('rejects an unknown host message type', () => {
    const result = validateHostMessage({ type: 'renderMermaid', mermaid: 'flowchart LR' });

    expect(result.ok).toBe(false);
    expect(result.ok ? undefined : result.code).toBe('unknownType');
  });

  it('rejects a progress message without an analysis id', () => {
    const result = validateHostMessage({ type: 'analysisProgress', stage: 'load' });

    expect(result.ok).toBe(false);
  });

  it('accepts the reveal and view-state messages added for SD-019/SD-021', () => {
    expect(
      validateHostMessage({
        type: 'reveal',
        analysisId: 'an_0000000000000001',
        entityId: 'ty_0000000000000001',
        scope: { kind: 'dependencies', id: 'ty_0000000000000001', depth: 1 },
        granularity: 'type'
      }).ok
    ).toBe(true);

    expect(
      validateHostMessage({
        type: 'viewState',
        state: { version: 1, targetName: 'Sample.sln' }
      }).ok
    ).toBe(true);

    // A reveal without an analysis is invalid, and a view state must be an object.
    expect(validateHostMessage({ type: 'reveal', entityId: 'ty_0000000000000001' }).ok).toBe(false);
    expect(validateHostMessage({ type: 'viewState', state: 'restore me' }).ok).toBe(false);
  });
  it('accepts a stale notification', () => {
    const result = validateHostMessage({
      type: 'stale',
      analysisId,
      reason: 'unsavedChange',
      relativePaths: ['src/App/Program.cs']
    });

    expect(result.ok, result.ok ? '' : result.errors.join('\n')).toBe(true);
  });
});
