import test from 'node:test';
import assert from 'node:assert/strict';
import {
  transformResponsesRequest as transform,
  restoreResponsesRequest as restore,
  reuseStats
} from './responses-transform.mjs';

const options = {
  enabled: true,
  provider: 'openai',
  api: 'responses',
  toolNames: ['sharpdeps_explore']
};
function evidence({
  snapshot = 'snapshot-1',
  generation = '1',
  project = 'p1',
  variant = 'v1',
  path = 'src/One.cs',
  start = 10,
  role = '',
  body = Array.from({ length: 6 }, (_, n) => 'return value' + n + ' + "' + 'x'.repeat(180) + '";'),
  hash = '',
  eol = '\n',
  old = false,
  complete = true,
  coverage = 'Partial'
} = {}) {
  const rows = [
    'SharpDeps explore generation=' +
      generation +
      ' snapshot=' +
      snapshot +
      ' coverage=' +
      coverage +
      ' Freshness=unverified',
    'Outcome=source-returned source=returned range=complete-saved-declaration executableBody=unclassified',
    'Source defaults: completeSavedRange=' +
      complete +
      ', body=unknown, role=saved-declaration; executable body is unclassified.',
    'Scope project=' +
      project +
      ' variant=' +
      variant +
      ' name=Example net10.0 Debug path=all saved paths',
    'Files (F identifiers are group-local):',
    'F1 ' + path + (hash ? ' saved-sha256=' + hash : ''),
    'Source F1:' +
      start +
      '-' +
      (start + body.length - 1) +
      ' @1' +
      (old ? ' path=' + path : '') +
      (role ? ' role=' + role : ''),
    ...body.map((line, n) => start + n + '\t' + line),
    'Diagnostic example.warning=1',
    'Unverified: configuration not checked',
    'Relations omitted=1; no runtime-dispatch claim.'
  ];
  return rows.join(eol) + eol;
}
function request(outputs = [evidence(), evidence()], names = []) {
  const input = [
    { role: 'system', content: 'Preserve me; ' + 'z'.repeat(30) },
    { type: 'message', role: 'user', content: [{ type: 'input_text', text: 'Question' }] },
    { role: 'assistant', content: 'Existing answer' }
  ];
  for (let i = 0; i < outputs.length; i++) {
    input.push({
      type: 'function_call',
      call_id: 'c' + i,
      name: names[i] ?? 'sharpdeps_explore',
      arguments: '{"query":"Run"}'
    });
    input.push({
      type: 'function_call_output',
      call_id: 'c' + i,
      output: outputs[i],
      status: 'completed'
    });
  }
  return {
    model: 'fixture-model',
    input,
    tools: [{ type: 'function', name: 'sharpdeps_explore', parameters: { type: 'object' } }],
    metadata: { preserve: 'all fields' },
    instructions: 'Unchanged',
    stream: true,
    store: false,
    text: { format: { type: 'text' } },
    reasoning: { effort: 'medium' }
  };
}
function frozen(value) {
  if (value && typeof value === 'object') {
    Object.values(value).forEach(frozen);
    Object.freeze(value);
  }
  return value;
}

test('explicit opt-in, provider and exact allowlist are all required', () => {
  const original = frozen(request());
  for (const setting of [
    undefined,
    {},
    { ...options, enabled: false },
    { ...options, enabled: 'true' },
    { ...options, provider: 'other' },
    { ...options, api: 'chat-completions' },
    { ...options, toolNames: [] },
    { ...options, toolNames: ['sharpdeps'] },
    { ...options, toolNames: ['sharpdeps_explore_extra'] },
    { ...options, toolNames: ['sharpdeps_explore', 'sharpdeps_explore'] }
  ])
    assert.strictEqual(transform(original, setting), original);
  for (const name of ['sharpdeps_explore_extra', 'other_tool', 'Sharpdeps_explore']) {
    const mixed = request([evidence(), evidence()], ['sharpdeps_explore', name]);
    assert.strictEqual(transform(mixed, options), mixed);
  }
});

test('exact repeat is reversible, request-local, non-mutating and net smaller', () => {
  const original = frozen(
    request(
      [evidence(), evidence(), 'non-target payload'],
      ['', '', 'other_tool'].map((name) => name || 'sharpdeps_explore')
    )
  );
  const before = JSON.stringify(original);
  const transformed = transform(original, options);
  assert.notStrictEqual(transformed, original);
  assert.equal(JSON.stringify(original), before);
  assert.deepEqual(restore(transformed, options), original);
  for (let i = 0; i < original.input.length; i++) {
    if ([4, 6].includes(i)) {
      assert.deepEqual(
        { ...transformed.input[i], output: '' },
        { ...original.input[i], output: '' }
      );
    } else assert.strictEqual(transformed.input[i], original.input[i]);
  }
  assert.deepEqual({ ...transformed, input: null }, { ...original, input: null });
  const stats = reuseStats(original, transformed);
  assert.equal(stats.anchors, 1);
  assert.equal(stats.references, 1);
  assert.ok(stats.savedBytes > 0);
  assert.ok(!JSON.stringify(stats).includes('value'));
  assert.ok(transformed.input[4].output.includes('anchor='));
  assert.ok(transformed.input[6].output.includes('ref='));
  assert.ok(transformed.input[6].output.includes('Diagnostic example.warning=1'));
  assert.ok(transformed.input[6].output.includes('coverage=Partial'));
  assert.deepEqual(transform(original, options), transformed); // Fresh map and stable IDs.
  assert.strictEqual(transform(transformed, options), transformed); // Idempotent.
});

test('known opaque reasoning remains identical; unknown reasoning shapes fail closed', () => {
  const opaque = frozen({
    type: 'reasoning',
    id: 'rs-fixture',
    summary: [{ type: 'summary_text', text: 'Opaque summary' }],
    encrypted_content: 'opaque ciphertext',
    status: 'completed'
  });
  const original = frozen({ ...request(), input: [...request().input, opaque] });
  const transformed = transform(original, options);
  assert.notStrictEqual(transformed, original);
  assert.strictEqual(transformed.input.at(-1), opaque);
  assert.deepEqual(restore(transformed, options), original);
  for (const invalid of [
    { ...opaque, summary: 'invalid' },
    { ...opaque, unknown: true }
  ]) {
    const unsupported = { ...request(), input: [...request().input, invalid] };
    assert.strictEqual(transform(unsupported, options), unsupported);
  }
});

test('different identity context or literal body never factors', () => {
  for (const delta of [
    { snapshot: 'snapshot-2' },
    { generation: '2' },
    { project: 'p2' },
    { variant: 'v2' },
    { path: 'src/Other.cs' },
    { start: 20 },
    { role: 'saved-owner-declaration' },
    { body: Array.from({ length: 6 }, () => 'changed ' + 'y'.repeat(180)) },
    { hash: 'a'.repeat(64) }
  ]) {
    const original = request([evidence(), evidence(delta)]);
    assert.strictEqual(transform(original, options), original);
  }
  const hashes = request([evidence({ hash: 'a'.repeat(64) }), evidence({ hash: 'b'.repeat(64) })]);
  assert.strictEqual(transform(hashes, options), hashes);
  const sameHash = request([
    evidence({ hash: 'a'.repeat(64) }),
    evidence({ hash: 'a'.repeat(64) })
  ]);
  assert.notStrictEqual(transform(sameHash, options), sameHash);
});

test('incremental, unknown, malformed and oversized requests fail closed', () => {
  for (const field of [
    'previous_response_id',
    'conversation',
    'context_management',
    'compaction',
    'unknown_field'
  ]) {
    const original = { ...request(), [field]: null };
    assert.strictEqual(transform(original, options), original);
  }
  const disabled = { ...request(), truncation: 'disabled' };
  assert.notStrictEqual(transform(disabled, options), disabled);
  const cases = [
    { ...request(), truncation: 'auto' },
    { ...request(), truncation: null },
    { ...request(), input: 'not an item array' },
    { ...request(), input: [{ type: 'item_reference', id: 'prior' }, ...request().input] },
    { ...request(), model: 'x'.repeat(2 * 1024 * 1024) }
  ];
  const structured = request();
  structured.input[4].output = [{ type: 'input_text', text: evidence() }];
  cases.push(structured);
  const unmatched = request();
  unmatched.input[4].call_id = 'missing';
  cases.push(unmatched);
  const duplicate = request();
  duplicate.input[5].call_id = 'c0';
  cases.push(duplicate);
  for (const original of cases) assert.strictEqual(transform(original, options), original);
});

test('invalid target text is opaque; reserved markers still abort the entire request', () => {
  const malformed = [
    'Unknown target output',
    evidence({ complete: false }),
    evidence().replace('range=complete-saved-declaration', 'range=truncated'),
    evidence().replace('Source F1:10-15', 'Source Q1:10-15'),
    evidence().replace('Source F1:10-15', 'Source F1:10-16'),
    evidence().replace('12\t', '13\t'),
    evidence().replace('Diagnostic example.warning=1', '16\textra\nDiagnostic example.warning=1'),
    evidence().replace('Scope project=p1', 'Scope project=unselected'),
    evidence() + 'SharpDeps literal-body-v1 ref=' + 'a'.repeat(64) + '\n'
  ];
  for (const text of malformed) {
    const original = frozen(request([evidence(), evidence(), text]));
    const transformed = transform(original, options);
    if (text.includes('SharpDeps literal-body-v1 ')) {
      assert.strictEqual(transformed, original);
    } else {
      assert.notStrictEqual(transformed, original);
      assert.strictEqual(transformed.input[8], original.input[8]);
      assert.deepEqual(restore(transformed, options), original);
      assert.equal(reuseStats(original, transformed, options).opaqueTargetOutputs, 1);
      assert.equal(reuseStats(original, transformed, options).parsedTargetOutputs, 2);
    }
    // An opaque output cannot itself seed a reference.
    const unsupportedPair = request([text, evidence()]);
    assert.strictEqual(transform(unsupportedPair, options), unsupportedPair);
  }
});

test('compaction, no-match, within-result duplicates and net increases retain full originals', () => {
  const one = request([evidence()]);
  assert.strictEqual(transform(one, options), one);
  const sameOutput = request([evidence() + evidence()]);
  assert.strictEqual(transform(sameOutput, options), sameOutput);
  const short = request([evidence({ body: ['x'] }), evidence({ body: ['x'] })]);
  assert.strictEqual(transform(short, options), short);
  const missing = evidence()
    .slice(0, evidence().indexOf('Source F1:'))
    .replace('source-returned', 'no-match');
  const noMatch = request([missing, missing]);
  assert.strictEqual(transform(noMatch, options), noMatch);
  const transformed = transform(request(), options);
  // A reference alone, after the full body was compacted away, must never seed reuse.
  const referenceOnly = request([transformed.input[6].output, evidence()]);
  assert.strictEqual(transform(referenceOnly, options), referenceOnly);
  assert.strictEqual(restore(referenceOnly, options), referenceOnly);
});

test('Unicode, CRLF and legacy explicit-path headers round-trip byte-exactly', () => {
  for (const old of [false, true]) {
    const text = evidence({
      old,
      eol: '\r\n',
      path: 'src/日本語 File.cs',
      role: 'markdown saved-range',
      body: Array.from({ length: 4 }, () => '文字😀 "quoted" \\\\ ' + 'abc'.repeat(80))
    });
    const original = frozen(request([text, text]));
    const transformed = transform(original, options);
    assert.notStrictEqual(transformed, original);
    assert.equal(restore(transformed, options).input[4].output, text);
    assert.equal(restore(transformed, options).input[6].output, text);
    assert.deepEqual(restore(transformed, options), original);
  }
});

test('parallel calls use output order, not call order, and references cannot point forward', () => {
  const base = request(),
    original = { ...base, input: [base.input[3], base.input[5], base.input[6], base.input[4]] };
  const transformed = transform(original, options);
  assert.notStrictEqual(transformed, original);
  assert.ok(transformed.input[2].output.includes('anchor='));
  assert.ok(transformed.input[3].output.includes('ref='));
  assert.deepEqual(restore(transformed, options), original);
  const reordered = {
    ...transformed,
    input: [transformed.input[0], transformed.input[1], transformed.input[3], transformed.input[2]]
  };
  assert.strictEqual(restore(reordered, options), reordered);
});

test('multigroup file aliases are local and malformed restoration never leaks a partial rewrite', () => {
  const group = evidence({ variant: 'v2', path: 'src/Two.cs' });
  const original = request([evidence() + group, evidence() + group]);
  const transformed = transform(original, options);
  assert.equal(reuseStats(original, transformed).anchors, 2);
  assert.equal(reuseStats(original, transformed).references, 2);
  assert.deepEqual(restore(transformed, options), original);
  const broken = {
    ...transformed,
    input: transformed.input.map((item, index) =>
      index === 6
        ? { ...item, output: item.output.replace(/ref=[a-f0-9]{64}/, 'ref=' + 'f'.repeat(64)) }
        : item
    )
  };
  assert.strictEqual(restore(broken, options), broken);
  const alteredScope = {
    ...transformed,
    input: transformed.input.map((item, index) =>
      index === 6 ? { ...item, output: item.output.replace('variant=v1', 'variant=other') } : item
    )
  };
  assert.strictEqual(restore(alteredScope, options), alteredScope);
  const spoofed = request([transformed.input[4].output, evidence()]);
  assert.strictEqual(transform(spoofed, options), spoofed);
});
