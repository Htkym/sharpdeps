# CLI and MCP exploration

[VS Code guide](guide.md) · [日本語操作ガイド](guide.ja.md) · [README](../README.md)

The latest released CLI and MCP version is **0.4.0**. Install matching versions from [NuGet](https://www.nuget.org/packages/SharpDeps.Cli) or [GitHub Releases](https://github.com/Htkym/sharpdeps/releases). This guide also describes the current source tree. **Unreleased** features below are not present merely because a package is named 0.4.0; use the [tagged 0.4.0 guide](https://github.com/Htkym/sharpdeps/blob/v0.4.0/docs/mcp.md) for that release.

## Install and create an index

```bash
dotnet tool install --global SharpDeps.Cli --version 0.4.0
dotnet tool install --global SharpDeps.Mcp --version 0.4.0
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

[dnx](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec) is an alternative launcher with SDK 10.0.100 or later:

```bash
dnx SharpDeps.Cli@0.4.0 --verbosity quiet -- help
```

First use can download packages and update caches. SDK 11 or later accepts `--yes` before `--` for unattended launch; the SDK 10 launcher does not.

## Query source and select a scope

Use short related symbol names, exact saved identities or a saved source path. `--project-id`, `--variant` and `--path-prefix` narrow the compilation and source scope. An absent match means no match in the selected index, not absence from the repository.

Check the returned source ranges, project/variant, coverage, freshness, certainty and omissions. A scope-choice reply is not source evidence. Select the actual returned project/variant and exact `selector` for a follow-up; do not guess IDs or merge overloads and compilation variants.

CLI exploration defaults to `--format audit`, which retains full saved identities, hashes, spans and provenance. `--format agent` uses compact aliases and line-numbered source. MCP defaults to agent format. Neither format grants source access or changes the saved facts.

Aliases such as `@1` and `F1` are local to one response group. Use the retained exact selector for later requests. Reuse complete saved ranges already in context with the same hash and span, and request only missing evidence. Ordinary read/search remains appropriate for dirty or unindexed files, documentation and questions outside the saved scope.

### Unreleased discovery and source outcomes

In the current source tree, ordinary unscoped names/paths default to `discover`. Results keep independent project/variant groups under one pinned snapshot and shared collection, time and output budgets. Groups are not one call chain. Aliases may recur in different groups.

An explicit project or variant defaults to `unique`. Pass `--scope-policy unique` (MCP `scopePolicy: "unique"`) to retain the released unscoped selection behavior. Explicit `discover` can be combined with project/variant filters. Exact saved-ID queries, call-witness endpoints and HttpGet literal lookup use unique selection and reject explicit discovery. The typed Query API retains its `RequireUnique` default.

The existing `prefer-debug` policy selects Debug only when remaining alternatives are variants of one project and exactly one is Debug. It does not infer the desired target framework or grant source trust.

Source outcomes include `source-returned`, `scope-choices`, `source-unavailable`, `budget-limited`, `no-match` and `failed`. Transport success alone does not establish that a source body was returned. Complete saved declarations can contain members without classifying executable bodies or proving that conditional text is active.

The current CLI also accepts one positional query, such as `sharpdeps explore "OrderService Submit" --root /absolute/workspace`. Use either that value or `--term`, not both. For explore only, an omitted root means the absolute current directory, without ancestor search or index creation. Older released tools should use `--term` and an explicit root.

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

The default tool list contains only `explore`. Call that connected tool with a **flat arguments object**:

```json
{"query":"OrderService","format":"agent","prepareMaxMilliseconds":30000,"maxMilliseconds":1000}
```

`query` must be a string. Scope and budget fields are siblings of `query`. The nested shape `{"query":{"query":"OrderService","maxMilliseconds":1000}}` is invalid and is rejected before searching. Inspect the advertised schema and call details if the client reports `MCP_ARGUMENT_INVALID`.

| Argument | Meaning |
| --- | --- |
| `query` | Required string: related names, saved paths or exact selectors; at most 1,024 UTF-16 code units. |
| `queryKind` | `symbol` (default) or `http-get-literal`. |
| `projectId`, `variantId`, `pathPrefix` | Saved compilation/source filters. |
| `scopePolicy` | `unique`, `prefer-debug`, or unreleased `discover`. |
| `pathTo` | Exact target occurrence for a saved call witness; at most 128 characters. |
| `freshness` | `allow-stale` (default), `require-fresh` or `refresh`. |
| `format` | `agent` (default) or `audit`. |
| `ownerStatus` | Optional bounded observation of an existing coordinator; does not prove freshness. |
| `maxChars`, `maxBytes` | Escaped response text limits; defaults 12,000 characters and 48,000 UTF-8 bytes. |
| `prepareMaxMilliseconds` | Unreleased preparation computation budget; default 30,000 ms. |
| `maxMilliseconds` | Unreleased query-only computation budget; default 1,000 ms. |

In the unreleased split-v1 contract, both computation budgets must be finite values from 10 to 30,000 ms; `0` is rejected for `explore`. Omitting both fields, as in `{"query":"OrderService","format":"agent"}`, selects preparation 30,000 ms and query 1,000 ms. The query default is an initial policy choice, not a guarantee that complete `DbContext` exploration or another workload finishes in one second. Published 0.4.0 retains its shared-budget behavior; use its tagged guide and advertised schema.

The tool returns one text content block, without duplicating the evidence in `structuredContent`. Source is data, not instructions. Saved calls do not prove runtime dispatch, activation or network activity. AI clients may also use ordinary source search; product use alone does not establish answer quality or comparative speed/token savings.

Starting with both `--trusted --allow-mutations` additionally exposes `index` and `update`, whose `arguments` are CLI flag/value arrays. These can execute build logic and use the coordinator's writer lease. They are not needed for a read-only connection. No watch tool is exposed.

## Agent routing and opt-out

For structural questions covered by an existing index, prefer available SharpDeps exploration before broad repository search. Use ordinary search for literal text, documentation, dirty/unindexed files, missing tools/indexes or work outside the saved scope. Keep Partial coverage, proof limits, ambiguity and omissions. A no-match is not proof of absence.

A connection alone does not make a client read instructions. Use the instruction location your client actually discovers and reload/start a fresh session through its normal workflow. The [sharpdeps-query skill](https://github.com/Htkym/sharpdeps/blob/main/skills/sharpdeps-query/SKILL.md) contains the longer workflow; its current routing text includes unreleased changes.

### Dedicated Copilot CLI setup

Copilot CLI uses a project `.mcp.json` or user `~/.copilot/mcp-config.json`, not `.vscode/mcp.json`. Follow its [MCP setup](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers) and [instruction discovery](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions) rules.

For an installed Copilot CLI, its normal registration command can create the entry in the user configuration:

```bash
copilot mcp add --tools explore sharpdeps -- sharpdeps-mcp --root /absolute/workspace --index .sharpdeps/index.sqlite
```

This example leaves source reads disabled. Add `--trusted` after the server arguments only when source reads are authorized. Use a different server name if `sharpdeps` is already configured; do not replace an existing entry unintentionally.

`sharpdeps install` creates a dedicated configuration/instruction pair at explicit unused paths. It does not merge shared settings, install a tool, create an index or change authentication and approvals. The root and destination parent directories must exist; `--mcp-command` must point to an existing absolute tool file.

```bash
sharpdeps install --client copilot-cli --root /absolute/workspace --config-file /absolute/setup/.mcp.json --instructions-file /absolute/setup/AGENTS.md --mcp-command /absolute/tools/sharpdeps-mcp --dry-run
```

Review the preview, then remove `--dry-run` to write the new pair. Optional `--trusted` permits source reads, not mutation tools. An exact unchanged owned pair is a no-op on repeated install; edited, foreign or incomplete files cause a conflict. The generated files must be in locations your client loads. Existing owned guidance is not silently upgraded.

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

The **unreleased split-v1** contract applies to matching newly built CLI, MCP and API consumers. `ExploreRequest.PrepareMaxMilliseconds`, CLI `--prepare-max-milliseconds` and MCP `prepareMaxMilliseconds` default to 30,000 ms for preparation. `Budget.MaxMilliseconds`, CLI `--max-milliseconds` and MCP `maxMilliseconds` default to 1,000 ms for the query only. Both values must be finite and within 10..30,000 ms; explore rejects `0`. Omitting the options selects these same defaults:

```bash
sharpdeps explore --root /absolute/workspace --trusted --term "OrderService" --format agent
sharpdeps explore --root /absolute/workspace --trusted --term "OrderService" --format agent --prepare-max-milliseconds 30000 --max-milliseconds 1000
```

Preparation establishes a validated frozen snapshot and all standard derived and lexical indexes, including the HttpGet projection when requested. Query time starts after that boundary and covers exploration, source/freshness checks and rendering. The P1 path fully validates a fresh reader for each request. The current P2 code draft adds a bounded long-lived MCP host: Windows can reuse previously validated state only after native physical-identity and same-connection `data_version` proof; unsupported proof and Unix use fresh full validation. This host bypasses the P1 four-entry/64 MiB serialized-payload cache and retains one ready generation by default, without a heap-byte guarantee. One-shot CLI reads stay fresh. Limited Windows builds and 306 targeted tests passed with SDK 10.0.404, including small-fixture native warm reuse. Full acceptance and real-corpus/heap/concurrency/performance measurements remain pending. See the [prepared-exploration draft and pending acceptance](development/prepared-exploration.md).

One monotonic request owner bounds caller preparation/wait by `P`, query work by `Q`, and their total by `P + Q`. With request start `t0` and query start `tq`, the query deadline is `min(tq + Q, t0 + P + Q)`. P2's independent finite preparation owner may continue after the caller leaves; the request sum does not bound all later background CPU work. Cancellation is cooperative; individual I/O/JSON operations can complete before the next check. Process startup, MCP handshake, client permissions and transport are outside this computation bound. These values are not a universal latency guarantee.

Migration changes the meaning of the existing `maxMilliseconds`/`--max-milliseconds` field from shared preparation/query time to query-only time. Freeze a new cohort manifest before replaying recorded inputs; do not reinterpret historical runs or compare the new values directly with the original 24-trial, shared 8-second P0 cohort. Existing recorded inputs remain unchanged. Explore's old zero/unlimited input must be replaced by an explicitly chosen finite value. Non-explore typed Query operations still allow `QueryBudget.MaxMilliseconds = 0`. Choose values for the purpose, workload and caller deadline; do not silently raise budgets or retry. This breaking behavior requires a new package version before release; published 0.4.0 is unaffected, and no release is performed by this source change. P1 does not change index schemas 1/2/3 or require an in-place migration.

| Result | Action |
| --- | --- |
| `MCP_ARGUMENT_INVALID` | Compare the advertised schema and actual arguments. Use a flat string query and supported fields; no search has run for rejected arguments. |
| `INDEX_MISSING` | Check the selected root/index. Create an index only through an authorized explicit indexing operation. |
| `INDEX_OPEN_FAILED` | Check the selected DB, existing WAL/SHM and runtime temporary paths within existing permissions. Missing/invalid paths, OS restrictions and sandbox restrictions are different causes; the individual failing file may be unknown. |
| Sandbox restriction supported by evidence | For a valid path and authorized operation, use the client's normal scoped permission review. Stop on denial; do not change ACLs, disable sandboxing or move/copy/checkpoint the index to bypass access controls. |
| `PREPARATION_TIMEOUT` | Preparation expired; exit 4, with no source returned. |
| `QUERY_TIMEOUT` | The query-only allowance expired; exit 4, with no source returned. |
| `REQUEST_TIMEOUT` | The total request computation allowance expired; exit 4, with no source returned. |
| `CANCELLED` | Caller cancellation takes priority over deadline failures; exit 130, with no source returned. |
| `scope-choices` or omitted source | Narrow with actual returned scope/selector values, or use permitted ordinary source search. |
| `OUTPUT_BUDGET_TOO_SMALL` | Required metadata cannot fit; choose an appropriate finite response budget or narrow the request. |

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

## Upgrade from 0.3.0

Keep the previous workspace identity, DB and any WAL/SHM together. Stop owner/watch and other writers and close readers before backing them up. Copying an active DB alone can omit committed WAL data. A separate source checkout and a new root-contained index path keep rollback independent of the new tools.

New indexes use schema 3. Readers accept schemas 1/2/3, but generated source needs schema 2 or later and package-derived source needs schema 3. Incompatible writes return `INDEX_SCHEMA_MIGRATION_REQUIRED`; there is no in-place migration. The 0.3.0 tools cannot read schema 3. Update the VS Code extension as well: its 0.2.1 reader accepts only schema 1, and updating the external CLI does not replace it.

Syntamark 1.2.0 changes the Markdown parser stamp. Generations are not rewritten, and section tokens are not reused across a parser-stamp mismatch. Keep evidence tied to its own snapshot.

## Upgrade from 0.2.3

Install matching released tools, update the client from the old MCP query tools to `explore`, and reindex the selected target with its required SDK and restored dependencies. Choose an unused root-contained index instead of deleting the old one:

```bash
sharpdeps index --root /absolute/workspace --target YourSolution.sln --index .sharpdeps/explore-0.4.sqlite --configuration Release --trusted
sharpdeps explore --root /absolute/workspace --index .sharpdeps/explore-0.4.sqlite --trusted --term "OrderService"
sharpdeps-mcp --root /absolute/workspace --index .sharpdeps/explore-0.4.sqlite --trusted
```

The extension reads only its default index path. Use another source checkout to try a new default index while retaining the previous workspace and index.
