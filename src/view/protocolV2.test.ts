import { describe, expect, it } from 'vitest';
import { PROTOCOL_VERSION, validateHostMessage, validateWebviewMessage } from './protocolV2';

const requestId = 'req_0123456789abcdef';
const analysisId = 'an_0123456789abcdef';

describe('validateWebviewMessage', () => {
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
