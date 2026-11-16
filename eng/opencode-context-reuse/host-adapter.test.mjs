import assert from 'node:assert/strict';
import { test } from 'node:test';
import { setupContextReuse, PLUGIN_ID } from './host-adapter.mjs';
import { restoreResponsesRequest } from './responses-transform.mjs';

const endpoint = 'http://127.0.0.1:12345/v1/responses';
const options = {
  enabled: true,
  providerID: 'fixture',
  endpoint,
  allowLoopbackFixture: true,
  toolNames: ['evidence_explore']
};
const inventory = {
  location: { directory: '/fixture' },
  data: [
    {
      id: PLUGIN_ID,
      source: { type: 'local' },
      features: { server: true },
      state: { status: 'active' }
    }
  ]
};
async function harness(overrides = {}) {
  let hook;
  const ctx = {
    options,
    app: { version: '2.2.0' },
    location: { directory: '/fixture' },
    plugin: { list: async () => inventory },
    session: {
      hook: async (name, fn, filter) => {
        assert.equal(name, 'http.request');
        assert.deepEqual(filter, { providerID: 'fixture' });
        hook = fn;
      }
    },
    ...overrides
  };
  await setupContextReuse(ctx);
  return hook;
}
const bodyText =
  'SharpDeps explore generation=1 snapshot=s coverage=Partial Freshness=unverified\nOutcome=source-returned source=returned range=complete-saved-declaration executableBody=unclassified\nSource defaults: completeSavedRange=true, body=unknown, role=saved-declaration; executable body is unclassified.\nScope project=p variant=v name=Fixture net10.0 Debug path=all saved paths\nFiles (F identifiers are group-local):\nF1 src/F.cs\nSource F1:10-15 @1\n' +
  Array.from({ length: 6 }, (_, i) => `${10 + i}\treturn "${'x'.repeat(180)}";`).join('\n') +
  '\n';
const body = {
  model: 'fixture',
  input: [
    {
      type: 'message',
      role: 'user',
      content: [{ type: 'input_text', text: 'unchanged' }]
    },
    ...['a', 'b'].flatMap((id) => [
      {
        type: 'function_call',
        call_id: id,
        name: 'evidence_explore',
        arguments: '{}'
      },
      { type: 'function_call_output', call_id: id, output: bodyText }
    ])
  ]
};
function event(value = body) {
  return {
    kind: 'primary',
    model: { providerID: 'fixture' },
    request: new Request(endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(value)
    })
  };
}
test('exact adapter factors, restores, preserves original Request and headers', async () => {
  const hook = await harness();
  const e = event();
  const original = e.request;
  await hook(e);
  assert.notEqual(e.request, original);
  assert.equal(original.bodyUsed, false);
  assert.deepEqual(await original.json(), body);
  const actual = await e.request.json();
  assert.deepEqual(
    restoreResponsesRequest(actual, {
      enabled: true,
      provider: 'openai',
      api: 'responses',
      toolNames: options.toolNames
    }),
    body
  );
  assert.equal(e.request.headers.has('content-length'), false);
});
test('unknown setup/event and disabled/version configuration do not throw or register', async () => {
  await setupContextReuse({});
  await setupContextReuse();
  for (const overrides of [
    { session: undefined },
    { plugin: undefined },
    { location: undefined },
    {
      session: {
        hook: async () => {
          throw new Error('fixture');
        }
      }
    },
    { options: undefined },
    { options: { ...options, enabled: false } },
    { app: { version: 'other' } }
  ])
    assert.equal(await harness(overrides), undefined);
  const hook = await harness();
  for (const e of [undefined, {}, { kind: 'primary' }]) await hook(e);
});
test('unsupported host/request/provider/truncation/headers remain same object', async () => {
  const hook = await harness();
  for (const change of [
    (e) => (e.kind = 'compaction'),
    (e) => (e.model.providerID = 'other'),
    (e) => e.request.headers.set('signature', 'fixture'),
    (e) => e.request.headers.set('content-encoding', 'gzip')
  ]) {
    const e = event();
    change(e);
    const old = e.request;
    await hook(e);
    assert.equal(e.request, old);
  }
  const e = event({ ...body, truncation: 'auto' });
  const old = e.request;
  await hook(e);
  assert.equal(e.request, old);
  const other = await harness({
    plugin: {
      list: async () => ({
        ...inventory,
        data: [...inventory.data, { source: { type: 'local' } }]
      })
    }
  });
  const x = event();
  const before = x.request;
  await other(x);
  assert.equal(x.request, before);
});
test('inventory timeout and stalled body fallback without late mutation', async () => {
  const hook = await harness({ plugin: { list: () => new Promise(() => {}) } });
  const e = event();
  const old = e.request;
  await hook(e);
  assert.equal(e.request, old);
  const normal = await harness();
  const controller = new AbortController();
  const x = {
    kind: 'primary',
    model: { providerID: 'fixture' },
    request: new Request(endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      duplex: 'half',
      signal: controller.signal,
      body: new ReadableStream({ start() {} })
    })
  };
  const initial = x.request;
  await normal(x);
  assert.equal(x.request, initial);
  controller.abort();
});
