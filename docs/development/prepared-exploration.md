# Prepared exploration

Status: P0 observations and the unreleased P1 implementation contract. P2 native
reader reuse is a design direction; its safety proof and runtime tests are pending.
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

Preparation includes saved-reader open, full snapshot hydration/validation/freezing,
and all standard derived and lexical indexes. Query execution begins after that
boundary. Source integrity, freshness and trust checks remain required.

P1 retains the existing derived cache: four entries and 64 MiB of serialized
payload capacity, which is not a managed-heap limit. Each request still owns its
fully validated SQL reader and transaction.

## P2 direction: unimplemented

The candidate owner retains one native SQLite connection, uses short transactions
and exclusive SQL leases, and keeps no idle transaction. It checks same-connection
`data_version`, actual SQLite-handle/VFS physical identity, schema, workspace and
active generation. Same-ID external mutations must invalidate reuse. New probe
connections cannot supply comparable `data_version` values; filesystem metadata
and generation IDs cannot prove unchanged contents. Windows and Unix native support
need separate proof. See [SQLite data_version](https://www.sqlite.org/pragma.html#pragma_data_version).

Skipping full validation requires proof of the same physical database and unchanged
snapshot. Changes, unsupported identity or uncertain state fall back to a fresh fully
validated reader. Unverified native shortcuts must not replace the current payload
fingerprint. Saved reads never start a writer, repair, restore or rebuild.

Preparation has a finite owner token independent of waiter cancellation. One waiter
can leave without cancelling shared work. Faults permit bounded recovery, never stale
fallback. TTL eviction and shutdown drain close connections after active leases finish.

The host retains at most one ready generation per root and bounded replacement work.
Payload capacity and heap limits remain undecided until measurement.

Required deterministic tests cover same-ID external SQL mutation, a commit between
probe and pin, same-size/same-timestamp file replacement, graph/FTS generation parity,
unsupported-VFS fallback, owner/waiter cancellation isolation, fault recovery, idle
WAL checkpointing, TTL eviction and shutdown drain. Windows replacement behavior and
native support require shipped-runtime tests before P2 can skip validation.
