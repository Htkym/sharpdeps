# CLI and MCP exploration

[VS Code guide](guide.md) · [日本語操作ガイド](guide.ja.md) · [README](../README.md)

This guide covers SharpDeps CLI and MCP 0.5.1. Check [NuGet](https://www.nuget.org/packages/SharpDeps.Cli) or [GitHub Releases](https://github.com/Htkym/sharpdeps/releases) for availability and install matching versions. For the older release, use the [tagged 0.5.0 guide](https://github.com/Htkym/sharpdeps/blob/v0.5.0/docs/mcp.md).

To locate a declaration, use `find_symbols`. Pass its selector to `read_symbols` for the declaration or to `relations` for incoming or outgoing edges. Use `explore` for a broader structural query. See [typed symbol investigation](contracts/symbol-investigation.md) for arguments and examples.

## Install and create an index

```bash
dotnet tool install --global SharpDeps.Cli --version 0.5.1
dotnet tool install --global SharpDeps.Mcp --version 0.5.1
```

Installation and local-manifest tool restore require a .NET SDK. Running an installed tool or the tool DLL directly requires the .NET 10 runtime. Indexing also needs an SDK compatible with your target and its `global.json`, plus restored dependencies. SharpDeps does not acquire that SDK or restore the target automatically.

Prepare a workspace you trust, restore its dependencies yourself, then create an index:

```bash
dotnet restore /absolute/workspace/YourSolution.sln
sharpdeps index --root /absolute/workspace --target YourSolution.sln --configuration Release --trusted
sharpdeps explore --root /absolute/workspace --trusted --term "OrderService Submit" --format agent
```

Replace the paths and names with your own. `index` evaluates build logic and may run source generators. Use it only for trusted source. Exploration reads a published saved generation; it does not restore, build, index or start watch.

The default index is `.sharpdeps/index.sqlite` under the selected root. Use a root-contained `--index` path to select another index, and retain it in every later command and MCP startup. The VS Code extension reads only the default path.

Repeat `--project` to index the C# projects you want to explore together. For example, to follow calls between an API and its services in a large checkout, run:

```bash
sharpdeps index --root /absolute/workspace --target YourSolution.sln --project src/Api/Api.csproj --project src/Services/Services.csproj --configuration Release --trusted
```

Paths are relative to `--root`. Use the same selection with `update`. A scoped index omits workspace Markdown and collects code from the selected compilations. Its saved diagnostics include the number of explicitly selected projects in `cli.explicitlySelectedProjects`. Unused files in local package caches do not consume its input budget. Scoped updates load the compilations again. Queries report `unverified` freshness and verify the hashes of source they return. To keep the whole-workspace generation available alongside the scoped generation, give the scoped index its own `--index` path.

[dnx](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec) is an alternative launcher with SDK 10.0.100 or later:

```bash
dnx SharpDeps.Cli@0.5.1 --verbosity quiet -- help
```

First use can download packages and update caches. SDK 11 or later accepts `--yes` before `--` for unattended launch; the SDK 10 launcher does not.

## Query source and select a scope

Use short related symbol names, exact saved identities or a saved source path. `--project-id`, `--variant` and `--path-prefix` narrow the compilation and source scope. An absent match means no match in the selected index, not absence from the repository.

Check the returned source ranges, project/variant, coverage, freshness, certainty and omissions. A scope-choice reply is not source evidence. Select the actual returned project/variant and exact `selector` for a follow-up; do not guess IDs or merge overloads and compilation variants.

CLI exploration defaults to `--format audit`, which retains full saved identities, hashes, spans and provenance. `--format agent` uses compact aliases and line-numbered source. MCP defaults to agent format. Neither format grants source access or changes the saved facts.

Retrieval depth is available through CLI --response-mode overview|evidence|detail or MCP responseMode. MCP omission defaults to evidence; CLI and typed Query omission retain legacy retrieval. Overview is optional when discovery is useful; use evidence directly when the target is known, and detail only when additional evidence is needed. These modes do not establish measured token or latency savings. Overview returns saved candidates, scope-specific relation summaries and exact selectors without retrieving source bodies for the response. Bodies are explicitly deferred, not absent; saved pointers do not prove current source freshness. Existing index and freshness proof checks remain in force and may read files for verification. An overview request consisting of exact saved selectors, or using pathTo, goes directly to evidence. Evidence retrieves requested declarations while deferring related bodies; detail additionally allows related declarations and owner context within the same budgets. Responses identify requested and effective modes. No mode grants source permission, changes freshness requirements, combines compilation scopes, or increases the total escaped wire limits. Intentionally deferred bodies and budget/access omissions remain distinct. A budget too small for an evidence response may still return the existing explicit failure with no evidence.

Aliases such as `@1` and `F1` are local to one response group. The agent format lists `F1 path` mappings before source blocks; a `Source F1:start-end @n` heading refers to that table without repeating the path. Consumers of older agent text should accept both this form and headings with `path=...`. In multi-group agent replies, identical index-wide diagnostics are shown once under `Index diagnostics (all saved scopes)`; per-group coverage, freshness, errors and omissions remain separate. Audit formatting is unchanged. Use the retained exact selector for later requests. Reuse complete saved ranges already in context with the same hash and span, and request only missing evidence. Ordinary read/search remains appropriate for dirty or unindexed files, documentation and questions outside the saved scope.

### Discovery and source outcomes

Since 0.5.0, ordinary unscoped names/paths default to `discover`. Results keep independent project/variant groups under one pinned snapshot and shared collection, time and output budgets. Groups are not one call chain. Aliases may recur in different groups.

An explicit project or variant defaults to `unique`. Pass `--scope-policy unique` (MCP `scopePolicy: "unique"`) to retain the 0.4.0 unscoped selection behavior. Explicit `discover` can be combined with project/variant filters. Exact saved-ID queries, call-witness endpoints and HttpGet literal lookup use unique selection and reject explicit discovery. The typed Query API retains its `RequireUnique` default.

The existing `prefer-debug` policy selects Debug only when remaining alternatives are variants of one project and exactly one is Debug. It does not infer the desired target framework or grant source trust.

Source outcomes include `source-returned`, `scope-choices`, `source-unavailable`, `budget-limited`, `no-match` and `failed`. Transport success alone does not establish that a source body was returned. Complete saved declarations can contain members without classifying executable bodies or proving that conditional text is active.

The current CLI also accepts one positional query, such as `sharpdeps explore "OrderService Submit" --root /absolute/workspace`. Use either that value or `--term`, not both. For explore only, an omitted root means the absolute current directory, without ancestor search or index creation. The 0.4.0 tools should use `--term` and an explicit root.

## Connect an MCP client

With the matching MCP tool installed, add a stdio entry using the command and arguments below. This example is for clients with an mcpServers mapping; use your client's own configuration format and preserve other servers and settings:

```json
{
  "mcpServers": {
    "sharpdeps": {
      "command": "sharpdeps-mcp",
      "args": ["--root", "/absolute/workspace", "--index", ".sharpdeps/index.sqlite", "--trusted"]
    }
  }
}
```

The host fixes root, index and source trust at startup. Add `--trusted` only when source reads are authorized; omit it to allow saved graph queries without source reads. Client folder trust, tool-call approvals and SharpDeps source trust are separate. Restart/reconnect through the client's normal workflow after changing its configuration.

A read-only server advertises `find_symbols`, `read_symbols`, `relations` and `explore`. For example, call `explore` with a **flat arguments object**:

```json
{
  "query": "OrderService",
  "format": "agent",
  "prepareMaxMilliseconds": 30000,
  "maxMilliseconds": 1000
}
```

`query` must be a string. Scope and budget fields are siblings of `query`. The nested shape `{"query":{"query":"OrderService","maxMilliseconds":1000}}` is invalid and is rejected before searching. Inspect the advertised schema and call details if the client reports `MCP_ARGUMENT_INVALID`.

| Argument                               | Meaning                                                                                                                                                       |
| -------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `query`                                | Required string: related names, saved paths or exact selectors; at most 1,024 UTF-16 code units.                                                              |
| `queryKind`                            | `symbol` (default) or `http-get-literal`.                                                                                                                     |
| `projectId`, `variantId`, `pathPrefix` | Saved compilation/source filters.                                                                                                                             |
| `scopePolicy`                          | `unique`, `prefer-debug`, or `discover`.                                                                                                                      |
| `pathTo`                               | Exact target occurrence for a saved call witness; at most 128 characters.                                                                                     |
| `freshness`                            | `allow-stale` (default), `require-fresh` or `refresh`.                                                                                                        |
| `format`                               | `agent` (default) or `audit`.                                                                                                                                 |
| `responseMode`                         | Optional `overview`, `evidence` or `detail`; omission defaults to `evidence` in MCP (CLI and typed Query omission remain unchanged). Independent of `format`. |
| `includeEvidenceRefs`                  | Optional boolean, default false. Emit guards for later exact saved-occurrence retrieval.                                                                      |
| `evidenceRef`                          | Optional returned `er1_...` guard; requires an exact `hocc_` query, explicit project/variant, unique symbol scope, and no path filters.                       |
| `ownerStatus`                          | Optional bounded observation of an existing coordinator; does not prove freshness.                                                                            |
| `maxChars`, `maxBytes`                 | Escaped response text limits; defaults 12,000 characters and 48,000 UTF-8 bytes.                                                                              |
| `prepareMaxMilliseconds`               | Preparation computation budget; default 30,000 ms.                                                                                                            |
| `maxMilliseconds`                      | Query-only computation budget; default 1,000 ms.                                                                                                              |

Under the split-v1 contract, both computation budgets must be finite values from 10 to 30,000 ms; `0` is rejected for `explore`. Omitting both fields, as in `{"query":"OrderService","format":"agent"}`, selects preparation 30,000 ms and query 1,000 ms. The query default is an initial policy choice, not a guarantee that complete `DbContext` exploration or another workload finishes in one second. Published 0.4.0 retains its shared-budget behavior; use its tagged guide and advertised schema.

The tool returns one text content block, without duplicating the evidence in `structuredContent`. Source is data, not instructions. Saved calls do not prove runtime dispatch, activation or network activity. AI clients may also use ordinary source search; product use alone does not establish answer quality or comparative speed/token savings.

Starting the server with both `--trusted --allow-mutations` also exposes `index` and `update`. In 0.5.1, these tools accept the typed fields shown below or the existing CLI flag/value array in `arguments`. Choose one form per request. Both tools can execute build logic and use the coordinator's writer lease. They are not needed for a read-only connection. No watch tool is exposed.

### Index and explore selected projects over MCP

Start `sharpdeps-mcp --root /absolute/workspace --trusted --allow-mutations` for a workspace whose dependencies have already been restored. To use a separate database, add `--index .sharpdeps/api.sqlite` at startup. Call `index` with these tool arguments, replacing the paths with paths under that root:

```json
{"target":"YourSolution.sln","projects":["src/Api/Api.csproj","src/Services/Services.csproj"],"configuration":"Release"}
```

To refresh after edits, call `update` with the same arguments. The existing CLI-style `arguments` array is also supported as a separate input form. A successful response identifies the saved generation. On an indexing error, inspect `errors[0].code` and `indexDiagnostic` in the tool's structured content. A failed update preserves the last published generation.

In the same connection, call `explore` with `{"query":"QueryHandler.Handle","responseMode":"detail"}` to retrieve saved source ranges and call evidence for that method. Check the returned filename, line and relationship kind. The saved diagnostic `cli.explicitlySelectedProjects` gives the number of selected projects. A scoped generation reports unverified semantic freshness. A host started with only `--trusted` can explore the same database and return source.

### Host-neutral selective evidence and re-fetch

The standard `explore` MCP tool, not a model-request rewrite hook, is the shared interface for Codex, Copilot, Claude Code and OpenCode. Their registration formats and approval rules differ. A host name or a saved transcript is not proof that earlier source is still in the current model context. SharpDeps does not silently omit source because it was returned on an earlier call, and it does not require automatic MCP resource fetching.

When a target is already known, query its exact selector with `responseMode: "evidence"` directly. Use overview only when discovering candidates is useful. Overview plus follow-up is an extra tool call and can cost more than direct retrieval. In agent-format overview/evidence, verbose non-root signatures and repeated member metadata are intentionally deferred; labels, selectors, certainty, locations and omission counts remain. Request detail or audit when those signatures are needed. This metadata compaction does not change the CLI/typed Query legacy omitted-mode, detail or audit rendering contract.

If an exact requested type cannot fit within the current evidence budget, eligible complete saved member ranges inside that same requested type may be returned instead. The parent type remains explicitly omitted; no complete type body or unrelated related-type body is implied. This retrieval fallback applies independently of agent/audit formatting and never increases a budget or bypasses source checks.

For a guarded follow-up, explicitly request references:

```json
{ "query": "OrderService", "responseMode": "overview", "includeEvidenceRefs": true }
```

Copy the exact occurrence, project, variant and `evidenceRef` from the returned `Evidence ref` row into a normal tool call:

```json
{
  "query": "<returned hocc_ selector>",
  "projectId": "<returned project>",
  "variantId": "<returned variant>",
  "scopePolicy": "unique",
  "responseMode": "evidence",
  "evidenceRef": "<returned er1_ guard>"
}
```

CLI equivalents are `--include-evidence-refs` and `--evidence-ref <guard>` alongside the exact `--term`, `--project-id`, `--variant` and `--scope-policy unique` arguments. A valid guard binds workspace, snapshot, generation, scope, occurrence and the whole saved declaration set, including source hashes and raw spans. It is **not** authentication, source permission, current freshness, a body cache, or proof that a client retains source. Source reads and verification still run normally. If the selection changed, `EVIDENCE_REF_MISMATCH` returns no body; obtain a new reference explicitly rather than silently dropping the guard. References are available only for saved code occurrences with complete declaration metadata, and are bounded by the same output quota.

A new connection with no prior conversation can re-fetch using the same guard while the saved selection remains valid. If the selector or guard itself was lost, rediscover it normally. Saved-selection equality and fresh-source verification remain distinct. Tight budgets can still produce an explicit budget-limited result without source.

The separate [experimental OpenCode context-reuse adapter](context-reuse.md) is optional, OpenCode-specific, and **not** this common feature. Its old multi-result replay savings cannot be transferred to the standard MCP path. Native-host transport checks, offline byte measurements and real-model quality/cost comparisons are separate acceptance gates; fewer output bytes alone do not establish a better answer or lower total cost.

## Agent routing and opt-out

Use the smallest evidence source that answers the question. Read known relevant files or ranges directly when they suffice, including across files. For missing indexed evidence, default to typed symbol discovery, exact reads and one-hop relations. Before submitting, check concrete method names, decisive conditions, failure paths and limits against evidence already in context. For exception-sensitive behavior, trace try/catch/finally, release failures and cleanup/disposal, including logging, rethrow and exception replacement. Retrieve only specific missing evidence; the check does not require another call. Stop when sufficient and preserve uncertainty. Do not automatically escalate to `explore`: it remains available for explicit user requests or manual investigation. Its overview/evidence/detail modes remain unchanged.

Use ordinary read/search for simple literal text or documentation-only questions, dirty/unindexed files, unavailable tools/indexes or work outside the saved scope. Necessary current-file verification before an edit remains allowed. Report a blocked exploration before using permitted targeted fallback; do not imply that unavailable evidence was checked. Keep Partial coverage, proof limits, ambiguity and omissions. A no-match is not proof of absence.

The server sends this workflow in both initialization instructions and the `explore` tool description, so the tool description carries the core rule even when a client does not forward server instructions to its model. These are model-facing instructions, not a security boundary: the server cannot intercept another tool's reads/searches or prevent a final answer. Actual tool-order enforcement requires support and configuration in the AI host; connecting MCP alone does not provide it. Source permissions, user opt-out and higher-priority instructions still apply.

A connection alone does not make a client read instructions. Use the instruction location your client actually discovers and reload/start a fresh session through its normal workflow. The [sharpdeps-query skill](https://github.com/Htkym/sharpdeps/blob/main/skills/sharpdeps-query/SKILL.md) contains the routine workflow and links to specialized interface contracts. Distribute the entire skill directory, including `references`, rather than copying only `SKILL.md`; read reference sections only when needed. It describes the 0.5.1 interface.

### Project-bound agent package setup

`agent-export` exports setup files for an already installed host. It does **not** install SharpDeps, create an index, activate a plugin, edit existing host profiles, grant source access, or enable experimental context reuse.

Choose one registration path:

- **SharpDeps MCP already registered:** keep that server and export routing only. Do not register a second server through the package.
- **No SharpDeps MCP registered:** explicitly bundle its launch command, or register MCP separately using the host's normal workflow. Routing-only output without an accessible server is not a completed integration.

Preview a routing-only package in an explicit project child directory:

```bash
sharpdeps agent-export --client portable --root /absolute/workspace --output-dir /absolute/workspace/sharpdeps-agent --dry-run
```

Use `portable` for Codex/Copilot, `claude-code` for Claude Code, or `opencode-v2` for OpenCode V2. The root and output parent must exist; the output must be a new project-contained directory. Review the returned files, then repeat without `--dry-run`. For optional MCP bundling add `--mcp-command`: portable requires an explicit bare executable such as `sharpdeps-mcp` on the host PATH; Claude/OpenCode require an absolute existing executable. Select the existing root-contained index with `--index`. Add `--trusted` only when source reads are authorized; exporting does not change index trust by itself. See the [export contract and thin shell/PowerShell entry points](agent-packages.md).

| Host            | What to activate explicitly                                                                                                   | What the package provides                                                                                                      |
| --------------- | ----------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| Codex / Copilot | Load the portable package with that host's supported plugin mechanism; follow the generated README.                           | A discoverable routing skill, plus MCP only if requested. Skill discovery does not mean its full body was loaded.              |
| Claude Code     | Load the exported plugin through Claude Code's supported plugin mechanism.                                                    | A routing skill and optional MCP registration. A plugin-root instruction file is not assumed to be always-on guidance.         |
| OpenCode V2     | Load the exported `opencode.config.json` through its supported configuration mechanism, without replacing unrelated settings. | `routing.md` through `instructions`, plus optional MCP. The same SharpDeps skill is deliberately not registered a second time. |

Check these separately in a fresh host session:

1. **Connection:** the intended SharpDeps server is connected and advertises `find_symbols`, `read_symbols`, `relations` and `explore` with the expected workspace/index and permissions. A manifest-validation success alone is not this check.
2. **Guidance:** the intended routing file or skill is discoverable/loaded through the host's actual path. Do not manually add a second copy of MCP initialization instructions, or infer full skill loading from a listed skill name.
3. **Useful behavior:** for a real task needing missing cross-file evidence, inspect whether focused exploration returns useful source or explicit candidates and whether the answer has adequate evidence. Simple lookups may correctly skip it. A forced diagnostic call proves connectivity, not natural adoption or token savings. Testing with a real provider may incur charges.

If a check fails, report which stage failed. Do not reinstall, reindex, change trust or grant permissions automatically. A no-hit or unavailable index is not evidence that the target is absent.

To disable, first remove/disable the host's package activation or configuration entry. Then preview `sharpdeps agent-remove --root /absolute/workspace --output-dir /absolute/workspace/sharpdeps-agent --dry-run` and repeat without `--dry-run` only for an unchanged owned package. Removal moves it to an adjacent backup; modified, extra, moved or symlinked files are refused. Routing-only removal does not unregister an independently configured MCP server. Keep unrelated host settings and user instructions unchanged.

### Dedicated Copilot CLI setup

Copilot CLI uses a project `.mcp.json` or user `~/.copilot/mcp-config.json`, not `.vscode/mcp.json`. Follow its [MCP setup](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers) and [instruction discovery](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions) rules.

For an installed Copilot CLI, its normal registration command can create the entry in the user configuration:

```bash
copilot mcp add --tools find_symbols,read_symbols,relations,explore sharpdeps -- sharpdeps-mcp --root /absolute/workspace --index .sharpdeps/index.sqlite
```

This example leaves source reads disabled. Add `--trusted` after the server arguments only when source reads are authorized. Use a different server name if `sharpdeps` is already configured; do not replace an existing entry unintentionally.

`sharpdeps install` creates a dedicated configuration/instruction pair at explicit unused paths. It does not merge shared settings, install a tool, create an index or change authentication and approvals. The root and destination parent directories must exist; `--mcp-command` must point to an existing absolute tool file.

```bash
sharpdeps install --client copilot-cli --root /absolute/workspace --config-file /absolute/setup/.mcp.json --instructions-file /absolute/setup/AGENTS.md --mcp-command /absolute/tools/sharpdeps-mcp --dry-run
```

Review the preview, then remove `--dry-run` to write the new pair. Optional `--trusted` permits source reads, not mutation tools. An exact unchanged owned pair is a no-op on repeated install; edited, foreign or incomplete files cause a conflict. The generated files must be in locations your client loads. Existing owned guidance is not silently upgraded. To replace an unchanged old pair with this workflow, explicitly preview and uninstall that pair, retain its backups, then preview and install the new pair at the selected paths. Review manually merged instructions instead of overwriting them. Reload the client so the new server/tool description and discovered instructions are actually used.

### Experimental opt-in prompt context before model exploration

`sharpdeps prompt-context` is an explicit opt-in command for hosts that can attach context before a model processes a prompt. It makes at most one read-only CLI exploration against an existing fixed index. It does not call an AI, create/update an index, start watch, install hooks, or deny ordinary read/search. This is experimental pre-supplied evidence, not a model-selected MCP call. It adds context and may duplicate later evidence; lower total token use is not established. Leave it disabled unless you explicitly choose to evaluate it for your workload.

The command reads one UTF-8 JSON object from stdin with `prompt` and absolute `cwd` strings. Supply an absolute `--root`, a root-contained `--index`, and `--trusted` only for a workspace whose source reads are authorized. The submitted `cwd` must match that root; it does not search ancestors or subprojects. A prompt cannot replace the root, index or trust setting.

```bash
sharpdeps prompt-context --root /absolute/workspace --index .sharpdeps/index.sqlite --trusted
```

Eligibility is deliberately lexical, not a general intent classifier. A structural term such as dependency, call, implementation or its supported Japanese equivalent must occur with a code-name hint. Selection prefers explicit backtick names or `.cs` paths, then directly adjacent method-call forms such as `saveChanges()`, then mixed-case names such as `DbContext`. Each tier preserves appearance order; duplicate names are removed and at most three are selected. Additional detected names are counted in stderr and marked as omitted in the injected context. The evidence does not cover the entire question merely because some source was returned. Invalid quoted/path tokens are not rescued by extracting a fragment.

Supported literal/documentation-only markers and explicit opt-out phrases, missing names, a different working directory and a missing index produce no context. The heuristic cannot recognize every opt-out, negation or question type, and quoted text can resemble a request. Persist opt-out in the configuration and disable the hook when its selection is unsuitable. A miss does not establish that SharpDeps cannot answer the question; the agent can use normal permitted exploration.

Successful output is a single Claude-style `hookSpecificOutput` JSON object with `hookEventName: "UserPromptSubmit"` and `additionalContext`. It preserves the complete rendered agent evidence, including source locations, independent scope groups, coverage, freshness and omissions. Source text remains data, not instructions. No source result, an exploration error or an oversized envelope produces empty stdout, not a truncated source body. Status/reason/elapsed-time JSON, the omitted-candidate count and the numeric product exit code (null before exploration) go to stderr without prompts or source. An `attempted` record is not evidence that context was injected. The context contains the original agent text with real line breaks, not an additional JSON-encoded copy.

The input limit is 64 KiB with a two-second cooperative read deadline; the prompt limit is 16,000 UTF-16 code units. Exploration uses at most 10,000 response characters / 12,000 UTF-8 bytes, and the final JSON envelope must fit 16 KiB. Defaults are the same finite preparation 30,000 ms and query 1,000 ms budgets as `explore`; pass `--prepare-max-milliseconds` and `--max-milliseconds` to choose values within 10..30,000. Startup, input, transport and host processing are outside those computation budgets. The one-shot command prepares afresh, can add latency even on a miss, and does not retain conversation state or deduplicate earlier injected evidence. Measure this overhead as well as any saved reads; token or latency improvements are not guaranteed.

For [Claude Code's UserPromptSubmit command hook](https://code.claude.com/docs/en/hooks#userpromptsubmit), review and merge this example into the host's normal settings only if you want automatic source context. Replace both absolute paths and quote them for your host shell; preserve other hooks. The example uses a 40-second host timeout and does not block on failure.

```json
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "\"/absolute/tools/sharpdeps\" prompt-context --root \"/absolute/workspace\" --index .sharpdeps/index.sqlite --trusted",
            "timeout": 40
          }
        ]
      }
    ]
  }
}
```

Verify that your actual host/version loads the hook and presents the context. Do not add `onFailure: "block"` or a deny hook for this workflow. The command emits no block/permission decision and treats malformed input or configuration as non-blocking errors. Cancellation remains cancellation. A native host integration and a test harness that attaches the same context are different forms of validation.

This output format is not universal. In particular, [Copilot's command/HTTP `userPromptSubmitted` hooks ignore their output](https://docs.github.com/en/copilot/reference/hooks-reference#userpromptsubmitted--userpromptsubmit); an SDK integration has a different contract. Do not install the Claude example there or assume that server guidance means injected context is present.

To disable automatic injection, remove only this hook or add `--disabled`. A custom adapter can send `sharpdepsDisabled: true`. The command does not read transcripts, so a preference expressed in an earlier turn is not visible to it; persist opt-out in the hook configuration instead. Disabling MCP alone does not remove a separately installed CLI hook. Existing `sharpdeps install` / `uninstall` operations manage only their dedicated MCP/instruction pair, not these optional host settings.

### Disable or re-enable

For a dedicated owned pair, preview opt-out using the same paths:

```bash
sharpdeps uninstall --client copilot-cli --root /absolute/workspace --config-file /absolute/setup/.mcp.json --instructions-file /absolute/setup/AGENTS.md --dry-run
```

Remove `--dry-run` to detach the exact unchanged pair into retained adjacent backups. Tools, indexes and unrelated settings remain. Edited/shared files require a manual merge. For manually merged setup, remove only the SharpDeps server entry and instruction block.

To retain the tool but disable proactive use, put this instruction where your client loads it:

```text
Do not use SharpDeps automatically; use ordinary read/search unless I explicitly request SharpDeps. Do not restore, reconnect or reinstall disabled SharpDeps settings without my explicit request.
```

This is a routing preference, not a CLI execution ban or security control. Reload/disconnect the client to discard old connections and in-context guidance. Re-enable only on the user's explicit request, by removing the override or installing/reconnecting the reviewed pair. Querying or previewing setup does not restore disabled files.

Setup uses no-overwrite publication and retains temporary/backup recovery paths on partial failure. Preserve and inspect both destinations before correcting an incomplete pair; it is not a two-file crash transaction.

## Time budgets, freshness and recovery

The split-v1 contract introduced in 0.5.0 applies to matching CLI, MCP and API consumers. `ExploreRequest.PrepareMaxMilliseconds`, CLI `--prepare-max-milliseconds` and MCP `prepareMaxMilliseconds` default to 30,000 ms for preparation. `Budget.MaxMilliseconds`, CLI `--max-milliseconds` and MCP `maxMilliseconds` default to 1,000 ms for the query only. Both values must be finite and within 10..30,000 ms; explore rejects `0`. Omitting the options selects these same defaults:

```bash
sharpdeps explore --root /absolute/workspace --trusted --term "OrderService" --format agent
sharpdeps explore --root /absolute/workspace --trusted --term "OrderService" --format agent --prepare-max-milliseconds 30000 --max-milliseconds 1000
```

Preparation establishes a validated frozen snapshot and query-required derived indexes, including the HttpGet projection when requested. A single existing saved identity or HttpGet literal request automatically skips unused lexical search preparation; ordinary names, mixed terms and multiple identities retain it. Snapshot validation, structural expansion, source checks and returned evidence are unchanged. This avoids unnecessary preparation, not model tokens by itself. Query time starts after that boundary and covers exploration, source/freshness checks and rendering. The long-lived MCP host can reuse validated state on Windows only after native physical-identity and same-connection `data_version` proof. Unsupported proof and Unix use fresh full validation. The default retention is one ready generation with a five-minute idle TTL, without a heap-byte guarantee. Its independent finite preparation owner can continue after a caller leaves; abandoned readers are eventually drained. One-shot CLI reads use fresh validation.

One monotonic request owner bounds caller preparation/wait by `P`, query work by `Q`, and their total by `P + Q`. With request start `t0` and query start `tq`, the query deadline is `min(tq + Q, t0 + P + Q)`. The MCP host's independent finite preparation owner may continue after the caller leaves; the request sum does not bound all later background CPU work. Cancellation is cooperative; individual I/O/JSON operations can complete before the next check. Process startup, MCP handshake, client permissions and transport are outside this computation bound. These values are not a universal latency guarantee.

When upgrading from 0.4.0, `maxMilliseconds`/`--max-milliseconds` changes from shared preparation/query time to query-only time. Replace explore's old zero/unlimited input with an explicitly chosen finite value. Non-explore typed Query operations still allow `QueryBudget.MaxMilliseconds = 0`. Choose values for the purpose, workload and caller deadline; do not silently raise budgets or retry. Index schemas 1/2/3 are unchanged; there is no automatic in-place migration.

| Result                                    | Action                                                                                                                                                                                                                             |
| ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `MCP_ARGUMENT_INVALID`                    | Compare the advertised schema and actual arguments. Use a flat string query and supported fields; no search has run for rejected arguments.                                                                                        |
| `INDEX_MISSING`                           | Check the selected root/index. Create an index only through an authorized explicit indexing operation.                                                                                                                             |
| `INDEX_OPEN_FAILED`                       | Check the selected DB, existing WAL/SHM and runtime temporary paths within existing permissions. Missing/invalid paths, OS restrictions and sandbox restrictions are different causes; the individual failing file may be unknown. |
| Sandbox restriction supported by evidence | For a valid path and authorized operation, use the client's normal scoped permission review. Stop on denial; do not change ACLs, disable sandboxing or move/copy/checkpoint the index to bypass access controls.                   |
| `PREPARATION_TIMEOUT`                     | Preparation expired; exit 4, with no source returned.                                                                                                                                                                              |
| `QUERY_TIMEOUT`                           | The query-only allowance expired; exit 4, with no source returned.                                                                                                                                                                 |
| `REQUEST_TIMEOUT`                         | The total request computation allowance expired; exit 4, with no source returned.                                                                                                                                                  |
| `CANCELLED`                               | Caller cancellation takes priority over deadline failures; exit 130, with no source returned.                                                                                                                                      |
| `scope-choices` or omitted source         | Narrow with actual returned scope/selector values, or use permitted ordinary source search.                                                                                                                                        |
| `OUTPUT_BUDGET_TOO_SMALL`                 | Required metadata cannot fit; choose an appropriate finite response budget or narrow the request.                                                                                                                                  |

Do not automatically retry failed calls, raise limits or change settings without a reason and authorization. A larger deadline does not fix access, schema or source-proof errors.

`allow-stale` verifies returned source against saved hashes while leaving semantic freshness unverified. `require-fresh` additionally needs trusted current input/configuration proof before and after collection; failed verification returns no source. `refresh` reports `UPDATE_REQUIRED`: explicitly update through the trusted coordinator. Coverage, including Partial, is independent of freshness.

`--owner-status` or MCP `ownerStatus: true` optionally observes an existing owner. It never starts analysis/watch or grants trust. Its 200 ms allowance stays within the query/request computation and output budgets; owner state can change immediately afterward. A running owner or matching saved generation is not a current-input proof.

Response character/UTF-8 limits include conservative JSON escaping for one text content result, not only source text; they exclude the client-controlled JSON-RPC envelope. They are not model token counts. CLI `--max-nodes`, `--max-edges` and `--max-depth` also bound collection. Whole saved source ranges, body classification, remaining omissions and provenance must be read together.

## Exact call witnesses and HttpGet declarations

For one directed saved call witness, pass exact same-snapshot occurrence IDs as `query` and `pathTo` (CLI `--term` and `--path-to`). Names/logical IDs are not endpoints. Only retained resolved, occurrence-bound calls or constructions in the selected scope can connect the endpoints. The result is at most one minimum-hop witness within that retained evidence, not a globally shortest path or proof of runtime behavior. The target is a pointer, not a request for its source body.

Use `queryKind: "http-get-literal"` (CLI `--query-kind http-get-literal`) for an exact captured HttpGet declaration fragment, including the valid empty string. It compares decoded saved text literally and does not resolve an effective URL: `items/42` does not match `items/{id}`. This mode rejects `pathTo`. Follow an actual returned occurrence selector in symbol mode for further structural evidence.

## Update, watch and API reference

Use explicit trusted `update` or `serve --watch` only when authorized. Watch publishes committed generations from saved filesystem changes; unsaved edits are not indexed. Refresh in VS Code rereads the saved index and does not update it. See [the extension guide](guide.md#read-a-saved-index).

The CLI also provides `status`, `search`, `symbol`, `callers`, `callees`, `impact`, `context`, `coordinator-status` and `doctor`. Use `sharpdeps help` and the [CLI reference](https://github.com/Htkym/sharpdeps/blob/main/docs/contracts/cli-skill-v1.ja.md) for command-specific arguments. Earlier MCP tools such as `search` and `context` were replaced by typed `explore` in 0.3.0; existing CLI query commands remain.

For .NET consumers, [saved-query/source contracts](https://github.com/Htkym/sharpdeps/blob/main/docs/contracts/query-context-v1.ja.md) describe snapshot identity, validation and source proofs. `IndexReader.Snapshot` exposes recursively read-only validated collections; arbitrary `IQueryIndex` inputs remain defensively validated. Reader lifetimes and generation pins still apply.

## Upgrade from 0.5.0

For globally installed tools, update both packages and reconnect your MCP client so it discovers the 0.5.1 tool schemas.

```sh
dotnet tool update --global SharpDeps.Cli --version 0.5.1
dotnet tool update --global SharpDeps.Mcp --version 0.5.1
```

Existing 0.5.0 indexes remain readable. To index only selected C# projects, use the [project indexing example](#index-and-explore-selected-projects-over-mcp). Use a separate `--index` to keep a full-workspace index alongside the selected one. Pass the same project selection to subsequent updates.

## Upgrade from 0.4.0

Update the CLI, MCP server and VS Code extension to matching versions, and reconnect the MCP client so it loads the new server and schema. An external CLI update does not replace the VSIX's bundled reader. API consumers must also use the matching contract.

Exploration now defaults to 30,000 ms preparation and 1,000 ms query-only time. Existing `maxMilliseconds`/`--max-milliseconds` is query-only; zero is rejected for explore. Ordinary unscoped names and paths now default to `discover`. Use `--scope-policy unique` or MCP `scopePolicy: "unique"` to retain the previous unscoped selection behavior. Read source outcomes and omissions rather than treating transport success as returned source.

Index schemas 1/2/3 are unchanged, and old indexes are not converted in place. Retain the old tools and index for rollback; stop writers and close readers before copying the DB with its WAL/SHM. Use a separate source checkout and unused root-contained index path for independent trials.

The 0.5.0 CLI/MCP tools use Syntamark 2.0.0 and its native YAML contract; 0.4.0 used 1.2.0. A parser-stamp mismatch prevents section-token reuse and does not rewrite old generations. Invalid frontmatter stops publication of the new Markdown facts and retains the previously committed generation. Keep evidence tied to its own snapshot. Incremental Markdown sessions remain disabled by default; the development opt-in does not change that default or promise a performance improvement.

## Upgrade from 0.3.0

Keep the previous workspace identity, DB and any WAL/SHM together. Stop owner/watch and other writers and close readers before backing them up. Copying an active DB alone can omit committed WAL data. A separate source checkout and a new root-contained index path keep rollback independent of the new tools.

New indexes use schema 3. Readers accept schemas 1/2/3, but generated source needs schema 2 or later and package-derived source needs schema 3. Incompatible writes return `INDEX_SCHEMA_MIGRATION_REQUIRED`; there is no in-place migration. The 0.3.0 tools cannot read schema 3. Update the VS Code extension as well: its 0.2.1 reader accepts only schema 1, and updating the external CLI does not replace it.

For the Markdown parser change and retained-generation behavior, see [Upgrade from 0.4.0](#upgrade-from-040).

## Upgrade from 0.2.3

Install matching released tools, update the client from the old MCP query tools to `explore`, and reindex the selected target with its required SDK and restored dependencies. Choose an unused root-contained index instead of deleting the old one:

```bash
sharpdeps index --root /absolute/workspace --target YourSolution.sln --index .sharpdeps/explore-0.5.sqlite --configuration Release --trusted
sharpdeps explore --root /absolute/workspace --index .sharpdeps/explore-0.5.sqlite --trusted --term "OrderService"
sharpdeps-mcp --root /absolute/workspace --index .sharpdeps/explore-0.5.sqlite --trusted
```

The extension reads only its default index path. Use another source checkout to try a new default index while retaining the previous workspace and index.

### MCP response-mode default compatibility

Since 0.5.0, MCP `explore` treats an omitted `responseMode` as `evidence`, including audit-format requests. This is a behavioral compatibility change for existing MCP clients: requested declarations (or bounded contained-member fallback) are retained, but related bodies are deferred rather than automatically retrieved. Their relations, selectors and explicit omission reasons remain available. Request `responseMode: "detail"` when additional related bodies or owner context are needed; it is not a promise of byte-for-byte legacy output. Explicit `overview`, `evidence` and `detail` retain their meanings. CLI omission and the typed Query API default remain unchanged. No source permission, freshness check, ambiguity resolution or preparation/query/output budget is relaxed. Fewer returned bytes on focused fixtures do not establish lower end-to-end AI token usage or equal answer quality.

To reproduce an exploration across MCP and CLI, set `responseMode` and `--response-mode` to the same value.

### Routing v5

Default to `find_symbols`, `read_symbols`, and `relations` for missing indexed evidence. Do not automatically escalate to `explore`; retain it for explicit requests or manual investigation. Before submitting, check concrete methods, branch conditions, failure and cleanup paths against existing evidence. Use typed/source reads only for missing evidence; do not force an extra call. Keep concrete symbol names, conditions and failure paths already supported by evidence in the answer; do not fetch again solely to comply. Four read tools and their CLI equivalents remain available. New managed installations allow all four; old registrations and routing-v1/v2/v3/v4 packages remain immutable and require explicit removal/reinstallation or export to adopt the new guidance. No permission or budget is expanded.
