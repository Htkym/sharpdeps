# Fixed-query process replay

Build the existing MCP test project, then run its test assembly as a replay harness:

```text
dotnet analyzer/tests/SharpDeps.Mcp.Tests/bin/Release/net10.0/SharpDeps.Mcp.Tests.dll --fixed-query-replay input.json new-receipt-directory
```

The input is a JSON object with `root`, `cliDll`, `mcpDll`, `repositoryRoot`, `trusted`, optional `index` (default `.sharpdeps/index.sqlite`), optional `phaseMetrics` and `abortOnIncompletePreparation` (both default `false`), and `cases`. Each case has `id`, `query`, explicit `format`, `freshness`, `maxChars`, `maxBytes`, and optional `maxMilliseconds`, `prepareMaxMilliseconds`, `scopePolicy`, `projectId`, `variantId`, `pathPrefix`, `ownerStatus`, and `purpose`. Missing or null time budgets are omitted from both CLI arguments and flat MCP tool arguments. Use absolute existing host/repository/workspace paths and a new output directory. The harness only queries an existing index; it does not restore, index, update, install or call a model.

Exploration uses the `split-v1` budget contract. `prepareMaxMilliseconds` covers full saved-index validation and standard derived/lexical preparation; its default is 30000 ms. `maxMilliseconds` covers the subsequent query, source verification and rendering; its default is 1000 ms. Both accept finite values from 10 through 30000 ms, and the total request bound is their sum. Exploration no longer accepts zero as an unlimited deadline. Before starting cases, the harness requires both listed MCP budget properties to advertise these finite bounds and defaults; older contracts are rejected while retaining the handshake, schema and failure receipts. Each receipt's `effective` object records `budgetContract`, both effective budgets and `totalMaxMilliseconds`, including defaults when options were omitted. Caller cancellation remains distinct from every timeout.

`historical-cases.json` preserves five recovered queries and their explicit `unique` budgets. Each has a separately labelled omitted-policy discovery case. Its original zero-valued deadlines remain frozen and are rejected by `split-v1` hosts. A current-contract replay needs a separate, labelled cohort that explicitly converts those deadlines to finite preparation/query budgets; do not rewrite the historical inputs or claim an unchanged request comparison. Set the appropriate fixed workspace for each corpus before use; do not execute EF Core and Orleans queries against an unrelated index. These are current-build replays of recovered requests, not exact historical-product reproductions: the original binary and index were not contemporaneously bound. The old instructions forcing `unique` must not silently replace the separate default-discovery condition.

Receipts retain the CLI argument vector, exact stdout/stderr bytes and exit status; MCP initialize, initialized notification, tools/schema, full request and raw JSON-RPC response; snapshot/source manifest and payload hashes; observed outcome, errors, omissions, scope and source units; raw source and exactly numbered/displayed source UTF-16/UTF-8 sizes, actual escaped tool-result JSON sizes and JSON-RPC envelope/line-delimiter overhead; and process wall/CPU observations. A final manifest hashes every retained artifact. Explore source is never added to protocol stdout or duplicated in `structuredContent`.

Before starting either query host, the harness compares the captured adjacent shared DLLs: `SharpDeps.Cli`, `SharpDeps.Index`, `SharpDeps.Query`, `SharpDeps.Analysis.Contracts`, and `SharpDeps.Analysis.Core` are required in both payloads; any other `SharpDeps.Analysis.*.dll` present in either payload must also exist with identical bytes in the other. `identity.json` records the hashes, lengths and comparison status from the existing payload inventory. Missing or different DLLs reject the replay while retaining identity, failure and artifact-manifest receipts. MCP uses the CLI assembly's exploration entrypoint, so the CLI DLL is part of this shared-engine check. This binds the captured payload bytes to this replay; it does not prove that the source commit produced those binaries or that files were immutable after capture.

Each case records `processState`, `exploreRequestOrdinal`, and `osCacheState` separately for CLI and MCP. `fresh` means no prior explore call in that process; `reused` means at least one prior explore call. CLI starts a new process for every case and always has ordinal 1; MCP uses one process and increasing explore-call ordinals, excluding initialize and tools/list. The MCP handshake has already completed before its first explore call. OS cache state remains `unknown`, including after the harness reads the saved snapshot. These facts do not establish cold or warm readiness, prepared-cache hits, or a valid performance baseline.

Audit source IDs and content hashes must match the snapshot. Raw spans are checked against hash-verified source bytes and the selected symbol's whole saved declaration boundaries, including merged complete ranges. Code scopes and saved document-to-code scope links are verified separately; a document-only response can legitimately have no code scope, an independent scope-policy decision, or a saved project with no selected variant. Agent responses are checked against saved declarations and retain possible spans when the display cannot prove a unique raw span. Physical source bytes preserve BOMs and their UTF-16 coordinates. Stored generated/package bytes come from the snapshot, without resolving virtual paths on disk. Receipts contain source code, so keep them in an authorized private destination.

The default equivalence rule requires identical CLI/MCP text and matching error status. `protocol-error` permits the documented CLI argument envelope versus MCP InvalidParams difference. `deadline-sensitive` retains independent deadline outcomes without asserting equal timings or readiness; it does not waive unrelated failures. Receipts preserve every public error and separate exact `PREPARATION_TIMEOUT`, `QUERY_TIMEOUT` and `REQUEST_TIMEOUT` codes through `cliTimeoutCode`/`mcpTimeoutCode`. Phase request outcomes distinguish `preparation-timeout`, `query-timeout` and `request-timeout`; an `INTERNAL_ERROR`, including one alongside a timeout, remains a failure. These exceptions are explicit inputs, not exclusions chosen after observing a result.

Run the focused tests through the existing .NET test entrypoint:

```text
dotnet test analyzer/tests/SharpDeps.Mcp.Tests/SharpDeps.Mcp.Tests.csproj --filter "FullyQualifiedName~SharpDeps.Mcp.Tests.FixedQueryReplayTests|FullyQualifiedName~SharpDeps.Mcp.Tests.ReplayPhaseMetricsTests"
```

Set `SHARPDEPS_REPLAY_SDK_HOME` to a directory containing the intended SDK `global.json` when SDK identity must be pinned. Set `SHARPDEPS_REPLAY_ARTIFACT_ROOT` to retain the test receipts at a chosen private directory. The tests cover exact members and overloads without unrelated neighbours, type/member fallback, partial declarations, three projects and four separate TFM/configuration scopes sharing a linked physical file, four encodings, Japanese/emoji/CRLF, stored generated/package inputs, escaped/tight/large wire budgets, omitted and explicit split time budgets, explicit unique versus default discovery, no match, unsupported selection, trust, changed source, missing owner/configuration freshness proof, deadline observations, generation boundaries and actual MCP cancellation-race observations. A sent cancellation notification is not proof that the host accepted cancellation. Process exit, stream drain and cancellation cleanup are bounded; a capture failure retains the artifacts already written. Negative oracle tests reject incorrect source provenance and unrelated complete declarations. Separate stdout/stderr overflow probes verify immediate read-failure cleanup while waiting for exit, finishing a session, or awaiting a protocol line.

Set `phaseMetrics: true` only for hosts supporting `--metrics-directory`. The default sends no diagnostic flags; every target must still advertise the `split-v1` budget contract. CLI cases use separate fresh sinks below the workspace's `.sharpdeps`; the sequential MCP host uses one fresh sink with before/after request boundaries. The host alone selects the sink, never tool arguments. Existing SDK/tool/package isolation guards still apply. A refused sink or missing file cannot distinguish pre-admission rejection from unavailable diagnostics unless the public response proves argument rejection.

Each receipt records separate CLI/MCP `phaseMilliseconds` and `phaseMetrics`, with exact JSONL copies and hashes per case. Values are the interval union of the named closed spans listed in `phaseMetrics.groups`, not sums of nested/repeated inclusive times or exclusive time. Groups may overlap and must not be added. Reader preparation covers reader open/snapshot and `explore.prepare-standard`, including derived and lexical-index spans. Search/scope covers search, resolve, expand and discovery scope ranking/selection, excluding remaining discovery orchestration. Source verification covers source read/decode/hash/freshness, not unit selection. Rendering covers `explore.render`, not transport serialization. Failed/cancelled elapsed spans are retained without implying successful work. Unreached stages are null with reasons. Disabled, rejected, absent, malformed, truncated, unfinished, ambiguous or changed/out-of-boundary evidence remains explicitly unavailable. The collector bounds each sink to 4096 files/16 MiB and parsing to 64 KiB per JSONL record/65536 records, and verifies run IDs, contiguous sequence, schema, span identity, finite intervals and parent containment. A capture interruption retains available sidecars and a separate phase receipt before propagating the failure.

Limits: this is correctness/observability coverage, not a benchmark. Unreported candidate counts remain null. MCP request CPU is the host process delta; CLI CPU includes startup and the entire process. CLI OS-signal cancellation, a live coordinator's changed-configuration proof, concurrent publication during a request, packaged binaries, historical corpus execution and natural model usage require their separate acceptance runs. Do not infer tokens, costs or Read replacement from output sizes. Existing Query and MCP lifecycle tests retain their deeper semantic and mutation coverage.

## Preparation ownership and shutdown

Set `abortOnIncompletePreparation: true` with `phaseMetrics: true` when a later
case must not overlap unproven MCP preparation. A missing, unfinished or invalid
request/owner trace stops the block before the next CLI starts. A terminal owner
outcome establishes completion, not successful query work. The request-time
receipt and copied sidecars stay immutable.

Shutdown closes stdin and has a 45-second bound with separate bounded cleanup.
`mcp-shutdown.json` distinguishes graceful stream drain, process exit and forced
termination. Forced termination or a stream failure is never successful drain.
After confirmed process exit, separately captured `late-owners.json` and
`late-owners/<ownerId>.*` retain each registered owner's terminal evidence when
available; missing or unfinished evidence remains unavailable. The
`late-owners-status.json` receipt discloses that partial process output is not
retained after shutdown or stream failure. These records are not exhaustive
proof of descendant containment.

The focused tests include a synthetic held-owner stdio probe that cannot finish
until EOF, and a non-exiting child that must be bounded and rejected as successful
drain. These test the observer, not shipped-MCP saturation or request-cancellation
reclamation. The separate admission fixture exercises the real adapter through
instrumented in-process SDK transport; actual CLI/MCP entrypoint and installed-shim
smokes are separate acceptance checks.

For isolated test builds, explicitly set `SHARPDEPS_REPLAY_REPOSITORY_ROOT` to the
absolute checkout being tested. The tests launch adjacent same-build CLI/MCP
assemblies from their test output, not the checkout's `bin` directories. Preserve
the existing SDK pin and private artifact-root settings, and use fresh build,
scratch and receipt directories.

Scoped closeout validation on 2026-11-14 covered the frozen integration over base
`aac1937`: all 55 replay/phase-metrics tests passed with no failures or skips. MCP,
Index and Analysis test projects built with zero warnings and zero errors. Index
and Analysis tests were not run locally; ordinary CI remains a separate gate.
Receipts are retained in `sharpdeps-p2-closeout-20261114`.

A separate smoke of the already installed `841bb08` CLI candidate restored one
package-free net10.0 solution, completed cold semantic indexing, and returned the
expected resolved Roslyn call edge from the same saved snapshot. SDK 10.0.404 was
selected; the saved-context process used runtime 10.0.15. Coverage remained
`Partial` and freshness `unverified`. This validates that fixture's packaged
semantic extraction and SharpDeps SQLite publication/read, not private Roslyn
storage activation or package acceptance for a new head.

## Historical P1/P2 evidence, 2026-11-14

The accepted historical product comparison used P1
`9e9cc5fa291e35d673efc91b38486d824967f85c` and P2
`6c3a53bef984cbaf36c2f4a3485d128c49b25a80`. The private evidence bundles are
identified below by portable directory names. They contain frozen inputs, source
hashes, raw receipts and original failures; they are not part of a new execution.

- `p2-matched-observer-verification-20261114`: isolated compilation and 55 focused
  replay/phase-metrics tests passed. The initial controller grouping failure was
  preserved; its corrected deterministic fixtures passed 76 checks.
- `p2-matched-smoke-freeze-20261114`: a subsequent case-sensitive payload-key fix
  passed the same 76 controller checks again. Both preserved product arms passed
  12 small-fixture calls (six CLI/MCP pairs), with exact text parity and graceful
  drain. The complete observer and inputs were frozen. The corrected historical
  controller SHA-256 is
  `A85B696A0D0EB37F3BDECDC45E922D242FA4D9CCB0D423A795F56DAE0311139D`.
- `p2-matched-speed-20261114`: all 24 planned calls in fixed P1/P2/P2/P1 order were
  admitted, with 12 exact CLI/MCP pairs, no timeout, no missing observation and
  graceful drain. Both arms used 15-second preparation plus 15-second query
  budgets. Product, observer and DB/WAL/workspace preservation passed; SHM was
  observed separately.
- `p2-memory-concurrency-results-20261114`: the corrected separate real-corpus
  run completed 38 exact-body calls, 19 per arm, including natural four-call
  bursts, sequential reuse and idle/TTL reacquisition. Original failed attempts
  and corrections remain preserved.
- `p2-native-replacement-verification-20261114`: four focused Index tests passed,
  but both retained-handle directory-retirement attempts were OS-denied with
  `0x80070005`. This proves denied-operation classification and original
  graph/FTS validity, not successful retained-handle replacement. SQL generation
  publication, closed-connection fallback and filesystem replacement are
  distinct claims.

The historical matched-controller scripts and Windows directory-swap diagnostic
remain preserved with their evidence; they are not promoted into this checkout's
ordinary test or package-smoke entrypoints. Old source/binary bindings are not
valid for a changed checkout. Do not rerun the historical benchmark or denied
replacement operations merely to validate these reusable replay changes.

These results are descriptive and cohort-specific. The ABBA comparison observed
only two MCP sessions per arm, with reused calls clustered inside those sessions
and unknown OS cache state. The memory run used one P1-then-P2 pair and sampled
process counters, not managed object collectibility or a universal memory bound.
Natural bursts do not prove simultaneous admission, fifth-call `MCP_BUSY` or
cancellation slot reclamation. None of these counts establishes p95, statistical
significance, production SLA, throughput, successful Windows retained-handle
replacement, installed-package acceptance for a new head, or a new integration
pass. Preserved source-range verification is not whole-workspace verification.
