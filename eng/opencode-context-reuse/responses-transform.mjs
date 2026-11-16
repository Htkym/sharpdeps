import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';

// Experimental, request-local literal factoring. No I/O, transcript mutation or cache.
const PREFIX = 'SharpDeps literal-body-v1 ';
const MAX_BYTES = 2 * 1024 * 1024;
const MAX_ITEMS = 512;
const MAX_BLOCKS = 2048;
const TOP_KEYS = new Set([
  'model',
  'input',
  'instructions',
  'tools',
  'tool_choice',
  'parallel_tool_calls',
  'temperature',
  'top_p',
  'max_output_tokens',
  'metadata',
  'store',
  'stream',
  'reasoning',
  'text',
  'truncation',
  'include',
  'service_tier',
  'safety_identifier',
  'prompt_cache_key',
  'prompt_cache_retention',
  'user'
]);

function jsonValue(value, seen = new Set(), depth = 0, budget = { nodes: 0, chars: 0 }) {
  if (++budget.nodes > 100000 || depth > 48) return false;
  if (value === null || typeof value === 'boolean') return true;
  if (typeof value === 'string') return (budget.chars += value.length) <= MAX_BYTES;
  if (typeof value === 'number') return Number.isFinite(value);
  if (typeof value !== 'object' || seen.has(value)) return false;
  const array = Array.isArray(value);
  const proto = Object.getPrototypeOf(value);
  if (!array && proto !== Object.prototype && proto !== null) return false;
  seen.add(value);
  const keys = Reflect.ownKeys(value);
  if (array && (value.length > 100000 || keys.length !== value.length + 1)) return false;
  for (const key of keys) {
    if (array && key === 'length') continue;
    if (typeof key !== 'string' || (budget.chars += key.length) > MAX_BYTES) return false;
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    if (
      !descriptor?.enumerable ||
      !Object.hasOwn(descriptor, 'value') ||
      !jsonValue(descriptor.value, seen, depth + 1, budget)
    )
      return false;
  }
  seen.delete(value);
  return true;
}

function targets(request, options) {
  if (
    options?.enabled !== true ||
    options.provider !== 'openai' ||
    options.api !== 'responses' ||
    !Array.isArray(options.toolNames) ||
    options.toolNames.length === 0 ||
    options.toolNames.length > 64 ||
    options.toolNames.some(
      (name) => typeof name !== 'string' || name.length === 0 || name.length > 256
    ) ||
    new Set(options.toolNames).size !== options.toolNames.length
  )
    return null;
  if (
    !jsonValue(request) ||
    request === null ||
    Array.isArray(request) ||
    Object.keys(request).some((key) => !TOP_KEYS.has(key)) ||
    (request.truncation !== undefined && request.truncation !== 'disabled') ||
    typeof request.model !== 'string' ||
    !Array.isArray(request.input) ||
    request.input.length > MAX_ITEMS ||
    Buffer.byteLength(JSON.stringify(request)) > MAX_BYTES
  )
    return null;
  const calls = new Map();
  const outputs = new Set();
  const selected = [];
  for (let index = 0; index < request.input.length; index++) {
    const item = request.input[index];
    if (!item || typeof item !== 'object' || Array.isArray(item)) return null;
    if (item.type === 'function_call') {
      if (
        typeof item.name !== 'string' ||
        typeof item.call_id !== 'string' ||
        !item.call_id ||
        typeof item.arguments !== 'string' ||
        calls.has(item.call_id)
      )
        return null;
      calls.set(item.call_id, item.name);
    } else if (item.type === 'function_call_output') {
      if (
        typeof item.call_id !== 'string' ||
        !calls.has(item.call_id) ||
        outputs.has(item.call_id) ||
        typeof item.output !== 'string'
      )
        return null;
      outputs.add(item.call_id);
      if (options.toolNames.includes(calls.get(item.call_id))) selected.push(index);
    } else if (item.type === 'reasoning') {
      // Known opaque Responses reasoning fields are preserved, never parsed for reusable text.
      if (
        Object.keys(item).some(
          (key) =>
            !['type', 'id', 'summary', 'content', 'encrypted_content', 'status'].includes(key)
        ) ||
        (item.id !== undefined && typeof item.id !== 'string') ||
        (item.summary !== undefined && !Array.isArray(item.summary)) ||
        (item.content !== undefined && !Array.isArray(item.content)) ||
        (item.encrypted_content != null && typeof item.encrypted_content !== 'string') ||
        (item.status !== undefined &&
          !['in_progress', 'completed', 'incomplete'].includes(item.status))
      )
        return null;
    } else if (
      (item.type === undefined || item.type === 'message') &&
      ['system', 'developer', 'user', 'assistant'].includes(item.role) &&
      (typeof item.content === 'string' || Array.isArray(item.content))
    ) {
      // These values are opaque and are never interpreted or rewritten.
    } else return null; // Incremental item references and unknown wire formats are unsupported.
  }
  return selected;
}

function lines(text) {
  const result = [];
  const regex = /[^\n]*\n|[^\n]+$/g;
  for (const match of text.matchAll(regex)) {
    const raw = match[0];
    const eol = raw.endsWith('\r\n') ? '\r\n' : raw.endsWith('\n') ? '\n' : '';
    result.push({ raw, text: raw.slice(0, raw.length - eol.length), eol, start: match.index });
    if (result.length > 100000) throw new Error('line limit');
  }
  return result;
}

function sourceHeader(text) {
  const match =
    /^Source (F[1-9]\d*):([1-9]\d*)-([1-9]\d*) (@[1-9]\d*(?:,@[1-9]\d*)*)(?: path=(.*?))?(?: role=(.+))?$/.exec(
      text
    );
  if (!match) return null;
  const start = Number(match[2]),
    end = Number(match[3]);
  if (
    !Number.isSafeInteger(start) ||
    !Number.isSafeInteger(end) ||
    end < start ||
    end - start > 100000
  )
    return null;
  return { file: match[1], start, end, path: match[5], role: match[6] ?? 'saved-declaration' };
}

function parseOutput(text, resultIndex) {
  if (text.includes(PREFIX)) return null;
  const rows = lines(text);
  if (!rows.length || !/^SharpDeps (?:explore|discover) /.test(rows[0].text)) return null;
  const blocks = [];
  let current = null,
    complete = false,
    outer = null;
  const groups = [];
  for (let i = 0; i < rows.length; i++) {
    const row = rows[i],
      value = row.text;
    if (value.startsWith('SharpDeps discover ')) {
      const match =
        /^SharpDeps discover generation=(\d+) snapshot=(\S+) outcome=\S+ groups=\d+ omittedGroups=\d+$/.exec(
          value
        );
      if (!match || outer || current) return null;
      outer = { generation: match[1], snapshot: match[2] };
    } else if (value.startsWith('SharpDeps explore ')) {
      const match =
        /^SharpDeps explore generation=(\d+) snapshot=(\S+) coverage=\S+ Freshness=\S+$/.exec(
          value
        );
      if (!match || (outer && (outer.generation !== match[1] || outer.snapshot !== match[2])))
        return null;
      current = {
        generation: match[1],
        snapshot: match[2],
        files: new Map(),
        scope: null,
        completeRange: false,
        completeDeclaration: outer ? complete : false,
        hashes: [],
        blocks: []
      };
      groups.push(current);
    } else if (value.startsWith('Source defaults:')) {
      if (
        !value.startsWith(
          'Source defaults: completeSavedRange=true, body=unknown, role=saved-declaration;'
        )
      )
        return null;
      complete = true;
      if (current) current.completeDeclaration = true;
    } else if (value.startsWith('Scope ')) {
      if (!current || current.scope) return null;
      const match = /^Scope project=(\S+) variant=(\S+) name=.* path=.*$/.exec(value);
      if (!match) return null;
      current.scope = { project: match[1], variant: match[2] };
    } else if (/^F[1-9]\d* /.test(value)) {
      if (!current) return null;
      const match = /^(F[1-9]\d*) (.+)$/.exec(value);
      let path = match[2],
        hash = '';
      const suffix = /(?: source=\S+)? saved-sha256=([a-fA-F0-9]{64})$/.exec(path);
      if (suffix) {
        hash = suffix[1];
        path = path.slice(0, suffix.index);
      }
      if (!path || current.files.has(match[1])) return null;
      current.files.set(match[1], { path, hash });
    } else if (value.startsWith('Source ')) {
      const header = sourceHeader(value);
      if (!header || !current?.completeDeclaration) return null;
      const bodyRows = rows.slice(i + 1, i + 2 + header.end - header.start);
      if (bodyRows.length !== header.end - header.start + 1) return null;
      for (let n = 0; n < bodyRows.length; n++)
        if (!bodyRows[n].text.startsWith(String(header.start + n) + '\t')) return null;
      if (/^\d+\t/.test(rows[i + bodyRows.length + 1]?.text ?? '')) return null;
      const block = {
        ...header,
        resultIndex,
        body: bodyRows.map((line) => line.raw).join(''),
        offset: bodyRows[0].start,
        eol: bodyRows[0].eol || row.eol || '\n',
        finalEol: bodyRows.at(-1).eol,
        count: bodyRows.length,
        group: current
      };
      current.blocks.push(block);
      blocks.push(block);
      if (blocks.length > MAX_BLOCKS) return null;
      i += bodyRows.length;
    } else if (/^\d+\t/.test(value)) return null;
    if (current && value.startsWith('Outcome=')) {
      if (
        !/^Outcome=\S+ source=(?:returned|unavailable|not-requested) range=complete-saved-declaration executableBody=unclassified$/.test(
          value
        )
      )
        return null;
      current.completeRange = true;
    }
    if (
      current &&
      !/^F[1-9]\d* /.test(value) &&
      /(?:hash|sha256|rawSpan|sourceVersion)=/.test(value)
    ) {
      current.hashes.push(value);
      if (current.hashes.reduce((sum, line) => sum + line.length, 0) > 16384) return null;
    }
  }
  if (blocks.length && !groups.length) return null;
  for (const block of blocks) {
    const group = block.group,
      file = group.files.get(block.file);
    if (
      !group.completeRange ||
      !group.scope ||
      group.scope.project === 'unselected' ||
      group.scope.variant === 'unselected'
    )
      return null;
    if (block.path !== undefined && (!block.path || (file && file.path !== block.path)))
      return null;
    const path = block.path ?? file?.path;
    if (!path) return null;
    // Hash/raw-span metadata, when present, is a conservative additional match condition.
    // Without it this is literal text factoring, not source-identity or freshness proof.
    block.key = JSON.stringify([
      group.snapshot,
      group.generation,
      group.scope.project,
      group.scope.variant,
      path,
      block.start,
      block.end,
      block.role,
      file?.hash ?? '',
      group.hashes,
      block.body
    ]);
    delete block.group;
  }
  return blocks;
}

function applyEdits(text, edits) {
  let end = text.length,
    out = '';
  for (const edit of [...edits].sort((a, b) => b.start - a.start)) {
    if (edit.start < 0 || edit.end > end || edit.end < edit.start)
      throw new Error('overlapping edits');
    out = edit.text + text.slice(edit.end, end) + out;
    end = edit.start;
  }
  return text.slice(0, end) + out;
}

function restore(request, indices) {
  const anchors = new Map(),
    replacements = new Map(),
    checks = [];
  let changed = false;
  for (const index of indices) {
    const text = request.input[index].output,
      rows = lines(text),
      edits = [];
    let sourceOrdinal = -1;
    for (let i = 0; i < rows.length; i++) {
      const row = rows[i];
      if (sourceHeader(row.text)) sourceOrdinal++;
      if (!row.text.includes(PREFIX)) continue;
      const anchor = /^SharpDeps literal-body-v1 anchor=([a-f0-9]{64}) lines=([1-9]\d*)$/.exec(
        row.text
      );
      const ref = /^SharpDeps literal-body-v1 ref=([a-f0-9]{64})$/.exec(row.text);
      const header = sourceHeader(rows[i - 1]?.text ?? '');
      if (!header) return null;
      if (anchor) {
        const count = Number(anchor[2]),
          bodyRows = rows.slice(i + 1, i + 1 + count);
        if (
          anchors.has(anchor[1]) ||
          count !== header.end - header.start + 1 ||
          bodyRows.length !== count ||
          bodyRows.some(
            (line, n) =>
              !line.text.startsWith(String(header.start + n) + '\t') || line.text.includes(PREFIX)
          )
        )
          return null;
        anchors.set(anchor[1], { body: bodyRows.map((line) => line.raw).join(''), index });
        checks.push({ index, sourceOrdinal, id: anchor[1] });
        edits.push({ start: row.start, end: row.start + row.raw.length, text: '' });
        i += count;
      } else if (ref) {
        const prior = anchors.get(ref[1]);
        if (!prior || prior.index >= index) return null;
        checks.push({ index, sourceOrdinal, id: ref[1] });
        edits.push({ start: row.start, end: row.start + row.raw.length, text: prior.body });
      } else return null;
    }
    if (edits.length) {
      replacements.set(index, applyEdits(text, edits));
      changed = true;
    }
  }
  if (!changed) return request;
  const restored = {
    ...request,
    input: request.input.map((item, index) =>
      replacements.has(index) ? { ...item, output: replacements.get(index) } : item
    )
  };
  const parsed = new Map();
  for (const check of checks) {
    if (!parsed.has(check.index))
      parsed.set(check.index, parseOutput(restored.input[check.index].output, check.index));
    const block = parsed.get(check.index)?.[check.sourceOrdinal];
    if (!block || createHash('sha256').update(block.key).digest('hex') !== check.id) return null;
  }
  return restored;
}

export function restoreResponsesRequest(request, options = {}) {
  try {
    const indices = targets(request, options);
    return indices ? (restore(request, indices) ?? request) : request;
  } catch {
    return request;
  }
}

export function transformResponsesRequest(request, options = {}) {
  try {
    const indices = targets(request, options);
    if (!indices) return request;
    const entries = new Map(),
      ids = new Map();
    let blockCount = 0;
    for (const index of indices) {
      // Preexisting markers may depend on missing history: do not layer a second transform.
      if (request.input[index].output.includes(PREFIX)) return request;
      const blocks = parseOutput(request.input[index].output, index);
      // Errors, unknown agent-text versions and incomplete source are opaque, not anchor candidates.
      if (!blocks) continue;
      if ((blockCount += blocks.length) > MAX_BLOCKS) return request;
      const local = new Set();
      for (const block of blocks) {
        if (local.has(block.key)) continue; // Never factor repeats within one result.
        local.add(block.key);
        const id = createHash('sha256').update(block.key).digest('hex');
        if (ids.has(id) && ids.get(id) !== block.key) return request;
        ids.set(id, block.key);
        if (!entries.has(id)) entries.set(id, []);
        entries.get(id).push(block);
      }
    }
    const edits = new Map();
    for (const [id, blocks] of entries) {
      if (blocks.length < 2) continue;
      const first = blocks[0];
      const anchor = PREFIX + 'anchor=' + id + ' lines=' + first.count + first.eol;
      const refs = blocks
        .slice(1)
        .map((block) => ({ block, text: PREFIX + 'ref=' + id + block.finalEol }));
      const saved =
        refs.reduce(
          (sum, ref) => sum + Buffer.byteLength(ref.block.body) - Buffer.byteLength(ref.text),
          0
        ) - Buffer.byteLength(anchor);
      if (saved <= 0) continue;
      const add = (block, text, insertion = false) => {
        if (!edits.has(block.resultIndex)) edits.set(block.resultIndex, []);
        edits.get(block.resultIndex).push({
          start: block.offset,
          end: block.offset + (insertion ? 0 : block.body.length),
          text
        });
      };
      add(first, anchor, true);
      for (const ref of refs) add(ref.block, ref.text);
    }
    if (!edits.size) return request;
    const result = {
      ...request,
      input: request.input.map((item, index) =>
        edits.has(index) ? { ...item, output: applyEdits(item.output, edits.get(index)) } : item
      )
    };
    const reverted = restore(result, indices);
    if (
      !reverted ||
      !isDeepStrictEqual(reverted, request) ||
      Buffer.byteLength(JSON.stringify(result)) >= Buffer.byteLength(JSON.stringify(request))
    )
      return request;
    // Explicitly verify all non-target fields; the inverse check alone is not a transport contract.
    const before = {
      ...request,
      input: request.input.map((item, index) => (edits.has(index) ? { ...item, output: '' } : item))
    };
    const after = {
      ...result,
      input: result.input.map((item, index) => (edits.has(index) ? { ...item, output: '' } : item))
    };
    return isDeepStrictEqual(before, after) ? result : request;
  } catch {
    return request;
  } // No payload, exception or source text is logged.
}

// Optional telemetry contains counts only. This helper never logs or retains either request.
export function reuseStats(before, after, options) {
  try {
    const oldBytes = Buffer.byteLength(JSON.stringify(before));
    const newBytes = Buffer.byteLength(JSON.stringify(after));
    const outputs = Array.isArray(after?.input)
      ? after.input
          .filter(
            (item, index) =>
              item?.type === 'function_call_output' &&
              typeof item.output === 'string' &&
              item.output !== before?.input?.[index]?.output
          )
          .map((item) => item.output)
          .join('\n')
      : '';
    const eligibility = {};
    if (options !== undefined) {
      const indices = targets(before, options);
      eligibility.requestSupported = indices !== null;
      eligibility.parsedTargetOutputs = 0;
      eligibility.opaqueTargetOutputs = 0;
      for (const index of indices ?? []) {
        if (parseOutput(before.input[index].output, index) === null)
          eligibility.opaqueTargetOutputs++;
        else eligibility.parsedTargetOutputs++;
      }
    }
    return {
      changed: before !== after,
      beforeBytes: oldBytes,
      afterBytes: newBytes,
      ...eligibility,
      savedBytes: oldBytes - newBytes,
      anchors: (outputs.match(/^SharpDeps literal-body-v1 anchor=/gm) ?? []).length,
      references: (outputs.match(/^SharpDeps literal-body-v1 ref=/gm) ?? []).length
    };
  } catch {
    return { changed: false };
  }
}
