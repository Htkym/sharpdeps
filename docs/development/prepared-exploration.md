# Prepared exploration

Status: P0 observations, the unreleased P1 contract, and the current P2 code draft.
Limited Windows builds and 306 targeted tests passed with SDK 10.0.404, including
actual native identity checks and small-fixture warm reuse. Full acceptance and
real-corpus performance measurements remain pending; see the validation scope below.
See [fixed-query replay](../../comparison/fixed-query-replay/README.md) for receipts.

## P0 observations

The bounded EF Core `DbContext` diagnostic used three repetitions per
entrypoint/budget: 12 serial requests, plus one CLI split-budget request. Shared
engine DLLs matched. These counts establish neither tail latency nor reliability.

All six MCP requests recorded a cache miss and capacity bypass, with no hit;
reader, fingerprint, derived and lexical work repeated. Reader preparation and
search/expand both contributed material work. The 30,000 ms preparation / 1,000 ms
query CLI diagnostic timed out without source units. Phase groups overlap.

OS cache state was unknown: index inspection and copy hashing preceded the timed
requests. CLI wall time included startup; MCP call time excluded its handshake.
Identity with the earlier 562 MB artifact remains unproven. Preserve the
original 2026-11-13 24-trial cohort and its frozen 8-second budgets separately;
these diagnostics cannot be compared directly with that cohort. Product speed,
index-build, concurrency and AI/token/cost improvements remain unmeasured.

## Hypotheses

Budget separation may clarify failures; proven generation reuse and search/expand
optimization may reduce repeated work. P0 does not establish that complete
`DbContext` exploration fits the initial 1,000 ms query default.

## P1 implementation contract

`ExploreRequest.PrepareMaxMilliseconds` defaults to 30,000 ms.
`Budget.MaxMilliseconds` is the query budget and initially remains 1,000 ms. Each
value must be finite and within 10..30,000 ms; explore no longer accepts zero.
CLI and MCP share this contract without a legacy shared-budget branch. Defaults
are initial policy choices awaiting broader measurement.

One request clock bounds the work. For request start `t0`, preparation allowance
`P`, query allowance `Q`, and query start `tq`, preparation ends by `t0 + P` and
query execution ends by `min(tq + Q, t0 + P + Q)`. Caller cancellation remains
active across both phases.

Preparation establishes a validated, frozen saved snapshot and all standard derived
and lexical indexes before query execution starts. The P1 path opens a reader and
fully hydrates/validates/freezes the snapshot on every request. P2 changes only how
an eligible host can establish that already validated snapshot, as described below.
Requested HttpGet projection preparation still finishes under the caller's
preparation budget before query time starts. Source integrity, freshness and trust
checks remain required on each request.

The P1 cross-reader derived cache retains four entries and 64 MiB of serialized
payload capacity, which is not a managed-heap limit. P2's prepared-host path bypasses
that cache; the limits and evidence from P1 must not be presented as P2 memory bounds.

## P2 code draft

`McpQueryAdapter` owns one `PreparedExplorationHost` and disposes it asynchronously
when the server ends. The one-shot CLI keeps a fresh fully validated reader path;
it does not retain a native connection across process exits. The shared query,
source verification, trust, freshness, scope and output contracts stay the same.
Embedding callers that create `McpQueryAdapter` must now await its `DisposeAsync`,
preferably with `await using`, to stop admission and drain retained resources.

The host's default capacity is one entry in total, keyed by absolute root, index
path and workspace UUID. Each entry owns one `IndexReadSession` and at most one
retained ready generation. A SQL lease exclusively owns its native connection and
reader transaction through preparation and query execution. Requests for the same
entry wait for that lease; they do not query concurrently on one connection.
Releasing the reader closes its transaction. An idle entry can retain its
connection and immutable prepared generation, with no idle SQLite transaction.

The default idle TTL is five minutes. Expiration retires only entries with no
active or waiting references; it does not interrupt an active lease. Admission may
replace an idle entry. Eligibility is checked on admission and by one timer armed
for the earliest idle deadline. Scheduling can delay actual disposal. If entry
capacity is occupied by active requests for other keys, admission fails with
`INDEX_PREPARATION_BUSY` and returns no source.
The MCP adapter's four-call admission slots and `MCP_BUSY` rejection are separate.
This grants no automatic retry. Host disposal stops admission, cancels the host
lifetime and waits for active and abandoned work to drain before closing sessions.
Shutdown and cancellation remain cooperative.

### Native reuse proof and fallback

The draft enables physical-identity reuse only on Windows. `IndexPhysicalIdentity`
asks SQLite for its actual borrowed main-database HANDLE using
`sqlite3_file_control` operation 29 (`SQLITE_FCNTL_WIN32_GET_HANDLE`). It compares
that HANDLE's `FileIdInfo` volume and 128-bit file ID with a newly opened handle
for the current index path. It never closes SQLite's borrowed HANDLE or reads a
private VFS structure layout. A path string, timestamp, size or saved generation ID
alone does not admit reuse.

`IndexReadSession` checks physical identity before pinning, starts the reader
transaction and reads metadata/header values that establish that transaction's
snapshot. It then reads `PRAGMA data_version` on the same retained connection and
checks physical identity again. A previous fully validated frozen snapshot can be
restored only when that pinned marker equals the retained marker and the schema,
manifest and generation/header checks also agree. A marker from a new probe
connection is never substituted for the same-connection comparison. Graph and FTS
queries use the leased reader's same pinned transaction.

After the reader transaction closes, the session retains validated state only if
the lease closed cleanly, physical identity still agrees and the same connection's
autocommit `data_version` still equals the pinned marker. A commit during the lease
therefore cannot attach a newer marker to an older snapshot. An external mutation
must invalidate reuse even if it preserves the saved snapshot/generation IDs.

Missing native support, a failed physical-identity proof or an unavailable marker
discards reusable state and takes a fresh reader/full-validation path. A changed
marker or incompatible validated header requires full validation before reuse can
resume. Corrupt metadata, invalid source evidence and failed validation remain
errors; fallback never means serving an old generation without proof. Saved reads
never start a writer, repair, restore or rebuild.

Unix native reuse is disabled in this draft. Unix requests use fresh readers and
full validation even inside a long-lived prepared host. The limited Windows checks
exercised current-file/different-FileID identity and native warm reuse on small
fixtures. Path replacement while retaining SQLite's native HANDLE, Linux runtime
fallback and packaged-artifact behavior remain untested. No Unix identity or reuse
claim follows from Windows code or test results.

### Owner lifetime, cancellation and memory

Each host preparation owner has its own finite 30,000 ms default allowance and a
token linked to host lifetime, independent of the waiting request's cancellation
or deadline. `ExecutionContext` flow is suppressed for owner work and lifecycle
callbacks, so request `AsyncLocal` diagnostics and cancellation do not become the
owner's lifetime. The owner prepares the validated snapshot and standard/search
indexes. A requested HttpGet projection is prepared afterward by the caller before
`StartQuery`, within that caller's preparation allowance.

The caller's P/Q/P+Q computation bound still limits its wait and query. If it leaves
while preparation continues, it receives no evidence; the finite owner may complete
afterward. The request sum is therefore not a ceiling on all background CPU work
after departure. Exactly one abandoned-work continuation disposes any eventual
reader and releases its lease. Cancelling one waiting caller does not cancel that
owner or another caller. Host shutdown can cancel the owner through host lifetime.

A preparation fault clears both the prepared generation and session. A later
authorized call creates a fresh session; the failed request is not retried
automatically and never receives stale fallback. A query failure need not erase an
otherwise valid frozen generation if its reader lease closes cleanly and the
release-time proof succeeds.

The host bypasses the earlier four-entry/64 MiB serialized-payload cache. Its default
retention is one ready generation for one admitted key. Only one preparation/query
lease owns an entry at a time. This is an entry count, not a heap-byte guarantee.
Preparation, replacement and active query objects can add transient
memory. Heap usage, peak replacement memory and practical corpus capacity remain
unmeasured.

### Diagnostics and validation scope

The draft emits an independent `explore-prepare` owner run. Request-side
`explore.host-wait` measures entry-gate waiting, `explore.host-prepare-wait` measures
waiting for the current owner, and `explore.host-owner` correlates that owner run.
Session decisions distinguish `validated-snapshot-hit`,
`validated-snapshot-miss` and `full-validation-fallback`; host decisions distinguish
`generation-prepared` and `generation-reused`. The owner's suppressed execution
context separates it from the caller's run. Gate waiting can include another
request's preparation/query lease; owner waiting covers the current preparation.
These intervals and overlapping
reader/derived/lexical groups must not be added together as exclusive work.

On 2026-11-14, limited Windows builds and targeted tests passed with .NET SDK
10.0.404. No product C# change was required after the first passing run.

| Targeted test group | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Index | 62 | 0 | 0 |
| Query | 127 | 0 | 0 |
| CLI | 37 | 0 | 0 |
| MCP | 80 | 0 | 0 |
| Total | 306 | 0 | 0 |

The limited checks included the actual Windows current-file/different-FileID native
identity test. Ten completed fixed-replay runs covered 34 cases: all 33 admitted
cases had exact CLI/MCP agreement; the unsupported-selection case retained its
specified protocol-surface difference. All replay index artifacts remained unchanged.
Saved `*.prepare-phase.jsonl` records from the default Windows path observed 23
`reader.session:validated-snapshot-hit` and 23 `explore.host:generation-reused`
decisions, with zero `full-validation-fallback` decisions in those records. This is
small-fixture native warm-reuse evidence, not a latency or large-index improvement.

The following acceptance work remains pending:

- The full test suite and full CI, plus packaged-artifact smoke checks.
- Linux runtime execution; the Unix full-validation fallback is currently supported
  by code inspection only, and Unix native reuse remains disabled.
- Path replacement while a native SQLite HANDLE is retained. The completed
  current-file/different-FileID check does not establish that scenario.
- P1/P2 real-corpus comparisons, retained heap and replacement peaks, concurrency
  and performance measurements, including effects above the old 64 MiB cache limit.
  Freeze a new cohort with commit/binary/corpus/runtime hashes, explicit split-v1
  budgets, cache/restore state and concurrency before measuring. Do not rerun or
  rewrite the original 24-trial shared-8-second P0 cohort as if its inputs had the
  new semantics.

The limited checks do not establish complete P2 acceptance, performance improvement
or a release. P2 does not change index schemas 1/2/3 or migrate a DB
in place. Matching newly built CLI/MCP/API consumers remain required for the
unreleased contract; publication requires a new package version. Published 0.4.0
and its historical behavior remain unchanged.
