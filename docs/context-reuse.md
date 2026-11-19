# Experimental Responses literal-body factoring

`eng/opencode-context-reuse/` contains an experimental, explicitly opt-in OpenCode host adapter and its pure transformer. It is not bundled in the VSIX or MCP package and is not enabled by SharpDeps or any global host configuration. The native entry uses the host-provided OpenCode plugin SDK.

For the shared standard-MCP retrieval path, see [host-neutral evidence and re-fetch](mcp.md#host-neutral-selective-evidence-and-re-fetch). This HTTP experiment is not required for that path and is not evidence of Codex, Copilot or Claude Code support.

## Boundary

`transformResponsesRequest(request, options)` accepts a complete, plain-JSON OpenAI Responses request object. It returns the **same object** unless all gates pass. Options must explicitly contain `enabled: true`, `provider: "openai"`, `api: "responses"`, and a nonempty exact `toolNames` allowlist.

Only string outputs paired with earlier allowlisted `function_call` items are candidates. Messages, known opaque reasoning items, other tools, tools/schema definitions, instructions, and all non-output fields are preserved. Reasoning content is never interpreted for reuse. Unknown request fields or input shapes, incremental conversation markers, existing factoring markers, and oversized requests fall back unchanged. An individual target output containing an error, unknown text format, or incomplete source is instead preserved opaquely and excluded from matching; complete, independently validated outputs elsewhere in the same request may still be factored. An opaque output can never become an anchor or reference target. Any actual transform exception or inverse-integrity failure still leaves the entire request unchanged. `truncation` must be absent or `"disabled"`; `"auto"` is rejected because provider-side removal of earlier items could remove an anchor.

Each invocation uses fresh local maps. Nothing changes the durable transcript, external state, subsequent retrieval, or tool execution. Within-result repetition is not factored.

## Literal matching, not source identity

The strict agent-text parser requires saved snapshot/generation, selected project/variant, reply-group-local file paths, complete saved-range declarations, and an exact continuous numbered source block. It understands current Source headers and older explicit-path headers. Coverage may remain Partial: index coverage and completeness of one saved declaration range are different claims.

A candidate must match a **different earlier tool result in this same HTTP request** on snapshot, generation, scope, path, line range, role, visible hash metadata, and the entire numbered literal body including line endings. When hashes/raw spans are not shown, this proves literal text equality only, not source identity, freshness, semantic equivalence, or permission to skip retrieval.

The first full body receives an anchor; later duplicate bodies receive references. Source headings, scope, diagnostics, citations and all other metadata remain. Stable IDs are SHA-256 over the match tuple. No body or metadata is logged.

Before returning a changed request, the transformer verifies:

- anchor/ref overhead gives a strict UTF-8 serialized-request reduction;
- every reference resolves to an earlier full body in this request, never a reference or another result later in the request;
- restoration produces the original object values exactly;
- all non-target fields are deeply equal and original objects were not mutated.

`restoreResponsesRequest` is the inverse for this experimental marker format and fails unchanged on invalid references or mismatched context. `reuseStats` returns only byte and marker counts; supplying the same options as a third argument also returns outer-request eligibility and parsed/opaque target-output counts, not guessed per-cause reasons. It does not log or retain requests.

## Cost and cache limitation

An anchor is added to an earlier full tool result only when a duplicate first appears in the current request. That retroactive insertion changes the previously sent prefix and can reduce prompt-cache reuse. A strict serialized-byte reduction is **not** proof of lower cache-adjusted cost, token use, or latency. This experiment intentionally adds no pre-anchoring scheme, persistent state, or cache management. Keep host opt-in experimental until native-host behavior and model-quality/cost effects are evaluated separately.

Non-target structured tool outputs remain an unsupported wire shape in this initial version and cause a whole-request no-op. No target-array support is inferred from durable transcript representation.

## Supported host boundary

The optional `index.ts` entry uses OpenCode's official server-plugin `http.request` hook. It is pinned to OpenCode **2.2.0** and only handles a primary, complete OpenAI Responses HTTP request. Both `enabled: true` and exact provider, endpoint and tool-name settings are required. Production endpoints are restricted to `https://api.openai.com/v1/responses`; an explicit loopback-fixture option exists only for local verification.

The adapter checks the public plugin inventory on every request and accepts only itself as the sole external plugin. Other plugins, unknown inventory, host versions, incremental state, automatic provider truncation, WebSocket transport, signed/compressed bodies and non-primary requests fall back unchanged. Inventory and body reads are bounded; exceptions do not replace the original request. JSON values outside selected SharpDeps outputs remain unchanged, although JSON serialization whitespace may differ. The original Request and durable transcript remain intact.

The public host API does not certify a final-writer position or atomically lock plugin reloads. Compatibility therefore covers the tested pinned host with a stable, sole-external-plugin configuration, not arbitrary concurrent plugin installation/reload or later request mutators. Do not enable it in those configurations. A server-issued receipt, a model-provided `knownRanges` list, or a persistent session identifier is deliberately not accepted as proof that source remains in the current model context.

This does not integrate with Claude Code, Codex, VS Code's chat host, or other providers. It neither skips a SharpDeps tool call nor changes source retrieval, source freshness, access checks, or MCP responses. If context compaction removes an earlier result before request construction, that result is absent from the fresh match map and cannot seed a reference.

Primary host/API references:

- [OpenCode server plugins and HTTP request hooks](https://opencode.ai/docs/build/plugins/server/)
- [OpenCode plugins](https://opencode.ai/docs/build/plugins/)
- [OpenCode compaction](https://opencode.ai/docs/compaction/)
- [OpenAI Responses truncation behavior](https://developers.openai.com/api/reference/resources/responses/subresources/input_tokens/methods/count)
- [OpenAI prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching)

## Explicit configuration example

OpenCode 2.2.0 accepts a local plugin directory through the following configuration shape. This is an example, not an installed configuration. Use the actual provider identifier and exact SharpDeps tool name emitted by that host; do not broaden the allowlist to unrelated tools. Keep all files in `eng/opencode-context-reuse/` together. The SDK import is supplied by OpenCode, not by the SharpDeps VS Code extension.

```json
{
  "plugins": [
    {
      "package": "/absolute/path/to/eng/opencode-context-reuse",
      "options": {
        "enabled": true,
        "providerID": "YOUR_CONFIGURED_OPENAI_PROVIDER",
        "endpoint": "https://api.openai.com/v1/responses",
        "toolNames": ["YOUR_EXACT_SHARPDEPS_EXPLORE_TOOL"]
      }
    }
  ]
}
```

Remove the plugin entry or set `enabled` to `false` to disable it. The adapter changes no provider endpoint or authentication setting and creates no stored credentials. Do not enable production traffic on the strength of byte replay alone.
