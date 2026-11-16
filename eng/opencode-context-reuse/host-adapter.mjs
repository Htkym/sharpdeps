import { transformResponsesRequest, restoreResponsesRequest } from './responses-transform.mjs';

// Experimental OpenCode 2.2.0 HTTP entry. Disabled unless every option is explicit.
// No persisted state, model calls, filesystem access, credential access or logging.
const MAX_BYTES = 2 * 1024 * 1024;
export const PLUGIN_ID = 'sharpdeps-context-reuse-experimental';

function allowedEndpoint(value, fixture) {
  if (typeof value !== 'string') return;
  try {
    const url = new URL(value);
    if (url.username || url.password || url.search || url.hash) return;
    if (url.href === 'https://api.openai.com/v1/responses') return url.href;
    if (
      fixture &&
      url.protocol === 'http:' &&
      ['127.0.0.1', '[::1]'].includes(url.hostname) &&
      url.pathname === '/v1/responses'
    )
      return url.href;
  } catch {
    /* Unsupported configuration leaves the request unchanged. */
  }
}

function supportedHeaders(headers) {
  const contentType = headers.get('content-type') ?? '';
  if (!/^application\/json(?:\s*;\s*charset\s*=\s*utf-8)?\s*$/i.test(contentType)) return false;
  for (const [name] of headers) {
    if (
      [
        'content-encoding',
        'transfer-encoding',
        'signature',
        'signature-input',
        'content-md5',
        'digest',
        'content-digest',
        'repr-digest',
        'x-amz-date',
        'x-amz-content-sha256'
      ].includes(name) ||
      name.includes('signature')
    )
      return false;
  }
  const authorization = headers.get('authorization');
  if (authorization !== null && !/^Bearer\s+\S+$/i.test(authorization)) return false;
  const length = headers.get('content-length');
  return length === null || (/^\d+$/.test(length) && Number(length) <= MAX_BYTES);
}

async function readBounded(request) {
  const reader = request.clone().body?.getReader();
  if (!reader) return;
  let timer;
  let stop = () => {};
  const stopped = new Promise((_, reject) => {
    stop = () => reject(new Error('Body read stopped'));
    timer = setTimeout(stop, 1000);
    request.signal.addEventListener('abort', stop, { once: true });
  });
  const chunks = [];
  let size = 0;
  try {
    while (true) {
      const next = await Promise.race([reader.read(), stopped]);
      if (next.done) break;
      size += next.value.byteLength;
      if (size > MAX_BYTES) {
        // Do not wait on a tee cancellation while the untouched original is unread.
        void reader.cancel().catch(() => {});
        return;
      }
      chunks.push(next.value);
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) {
      bytes.set(chunk, offset);
      offset += chunk.byteLength;
    }
    return bytes;
  } catch {
    void reader.cancel().catch(() => {});
    return;
  } finally {
    clearTimeout(timer);
    request.signal.removeEventListener('abort', stop);
    reader.releaseLock();
  }
}

export async function setupContextReuse(ctx) {
  try {
    const options = ctx?.options ?? {};
    const endpoint = allowedEndpoint(options.endpoint, options.allowLoopbackFixture === true);
    if (
      typeof ctx?.session?.hook !== 'function' ||
      typeof ctx?.plugin?.list !== 'function' ||
      typeof ctx?.location?.directory !== 'string' ||
      ctx?.app?.version !== '2.2.0' ||
      options.enabled !== true ||
      !endpoint ||
      typeof options.providerID !== 'string' ||
      options.providerID.length === 0 ||
      !Array.isArray(options.toolNames) ||
      options.toolNames.length === 0 ||
      options.toolNames.some((name) => typeof name !== 'string' || !name) ||
      new Set(options.toolNames).size !== options.toolNames.length
    )
      return;
    const providerID = options.providerID;
    const toolNames = [...options.toolNames];
    await ctx.session.hook(
      'http.request',
      async (event) => {
        try {
          const original = event?.request;
          if (
            event?.kind !== 'primary' ||
            event?.model?.providerID !== providerID ||
            !original ||
            original.url !== endpoint ||
            original.method !== 'POST' ||
            original.bodyUsed ||
            original.signal.aborted ||
            !supportedHeaders(original.headers)
          )
            return;
          // The public inventory does not enumerate hooks. Only a sole external
          // server plugin is supported; pinned host builtins are not user plugins.
          let inventoryTimer;
          let listed;
          try {
            listed = await Promise.race([
              ctx.plugin.list(),
              new Promise((_, reject) => {
                inventoryTimer = setTimeout(() => reject(new Error('Inventory timeout')), 1000);
              })
            ]);
          } finally {
            clearTimeout(inventoryTimer);
          }
          const inventory = listed?.data;
          if (listed?.location?.directory !== ctx.location.directory || !Array.isArray(inventory))
            return;
          const external = inventory.filter((item) => item.source?.type !== 'builtin');
          if (
            external.length !== 1 ||
            external[0].id !== PLUGIN_ID ||
            external[0].state?.status !== 'active' ||
            external[0].features?.server !== true
          )
            return;
          const bytes = await readBounded(original);
          if (!bytes || original.signal.aborted) return;
          const body = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
          if (
            !body ||
            typeof body !== 'object' ||
            Array.isArray(body) ||
            (body.truncation !== undefined && body.truncation !== 'disabled')
          )
            return;
          const config = {
            enabled: true,
            provider: 'openai',
            api: 'responses',
            toolNames
          };
          const transformed = transformResponsesRequest(body, config);
          if (
            transformed === body ||
            JSON.stringify(restoreResponsesRequest(transformed, config)) !== JSON.stringify(body)
          )
            return;
          const serialized = JSON.stringify(transformed);
          if (new TextEncoder().encode(serialized).byteLength >= bytes.byteLength) return;
          const headers = new Headers(original.headers);
          headers.delete('content-length');
          const replacement = new Request(original, {
            headers,
            body: serialized
          });
          if (!original.signal.aborted && event.request === original) event.request = replacement;
        } catch {
          /* Parse/shape/transform failures leave the original request unchanged. */
        }
      },
      { providerID }
    );
  } catch {
    /* Unsupported host setup leaves all requests unchanged. */
  }
}
