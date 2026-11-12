# Phase diagnostics

Index, update and watch-update owners can write opt-in JSONL diagnostics with
`--metrics-directory <absolute-directory>`. Use a directory below the analyzed workspace's `.sharpdeps` directory.
Other destinations are refused. The destination must also be disjoint from the
running tool payload, the runtime's entire dotnet tree, NUGET_PACKAGES and default
NuGet package caches. Both ancestor and descendant overlap is refused before
creating directories, even when those dependency trees are inside .sharpdeps.
This protects the current reuse proof's SDK/tool/package inputs from the diagnostic
stream itself. The option is forwarded to the shared owner, and requests with different diagnostic destinations are not coalesced.
The owner emits one uniquely named file per actual extraction attempt, not per
waiting client. Watch reconciliation also emits a separate `watch-cycle` run, even when no extraction owner runs.

Existing files are never replaced. Linked output directories and workspace source
subtrees are refused. Output failures do not change indexing or publication results.
An absent file or a file without `run.end` is missing evidence, not a successful run.
No source text, project paths, query text, environment values or exception messages
are recorded. Labels are fixed identifiers. Normal stdout and saved graph diagnostics
are unchanged. This option does not enter selection/input hashes or reuse proofs.

Each record has a run ID, a monotonically increasing sequence and a payload.
`phase` records report inclusive wall time and time excluding directly measured
children. Exclusive time includes uninstrumented work. Nested/lazy phases must not
be added to their parents. Overlapping direct child intervals are unioned before
subtraction. A parent with unfinished children has a null exclusive value. Span IDs
identify repeated/nested phases without recording project identities. CPU,
allocation and GC deltas are process-wide and may include other requests. They are not per-request resource attribution.

Implemented intervals cover Index construction, physical capture/read/hash,
selected/output/structure fingerprint passes, semantic MSBuild open, compilation
acquisition, generated-document enumeration, symbol indexing, declaration/operation
collection, graph projection, catalog, Markdown prepare/projection/mentions, search
text, snapshot validation, stage validation/serialization/rehydration, graph insert,
FTS/search insert, publication guard and durable commit. A failed phase records its
elapsed interval but does not imply successful work. An exclusive run interrupted
before publication retains `incomplete`; shared-owner failures distinguish failed
and cancelled. A completed extraction run means durable publication succeeded.

Reuse decisions are emitted at existing proof capture, environment/budget rejection,
semantic admission and project selection boundaries. Some legacy compound guards
remain one combined reason rather than re-running checks to infer a finer cause.
Selected, dirty and clean project counts are distinct from the actual extracted and
reused collector counts. A successful closure proof does not establish edit reuse
or an end-to-end speed improvement.

## Saved queries

Saved CLI Query commands and `explore` accept the same opt-in option. The MCP host
accepts `--metrics-directory` at startup; tool arguments and tool schemas cannot set
or change it. Mutations use that host-selected destination too. Public positional
constructors and `CliApplication.ExploreAsync` retain their existing signatures.

Each admitted saved request writes its own run, including reader open/snapshot,
freeze/validation and derived indexes, search/resolve/expand, source read/hash and
freshness verification, and final rendering where those stages are reached.
Existing pinned and payload-cache decisions remain. CLI/MCP exploration also emits
`validated-cache-hit`, `validated-cache-miss`, `validated-cache-evicted` and
`validated-cache-capacity-bypass` for shared immutable derived data. Its key includes
canonical root/index, workspace, schema/identity, complete payload fingerprint and
derived version. Retention is limited to four entries and 64 MiB of complete
serialized payload bytes, not a managed-heap cap. Each call still opens its own
reader, hydrates and validates the full snapshot, and performs the requested source
and freshness checks. No live reader or transaction is retained by the cache.
A derived-cache hit does not mean the whole query is warm or faster.
A completed query run means the saved request
succeeded, not that freshness was verified. Failed and cancelled requests retain
their outcome; stages never reached are absent. Setup rejected before admission
may emit no run. Output failures never change the response or its exit status.
Query text, source contents, snapshot identities and filesystem paths are omitted.
Process-wide CPU/allocation/GC deltas can overlap concurrent requests and must not
be interpreted as exclusive request resource usage.

Fixed-query replay accepts validated `read` observations joined to their matching
`input.read-*` span, and `reported-duration` operation observations. These records
do not add intervals or durations to the replay phase totals. Unknown or invalid
events make the phase capture unavailable.
## Read attribution and MSBuild progress

`read` records report logical position differences on explicitly instrumented
existing product FileStreams, with
fixed `workspace`, `sdk`, `git`, `package` or `tool` categories chosen at the owning
call site. Every read explicitly identifies its matching phase span and parent
span, so parallel records join to the owning fingerprint/proof pass without
relying on adjacent JSONL order. Repeated passes and rereads remain separate. The
`input.read-*` spans cover the observed stream lifetime after it opens, including
any hash, parse and validation done before disposal. They are not isolated disk
latency. Stream position observation adds no reads, seeks or path inspection. A
failed/cancelled pass retains bytes already read; unavailable stream positions are
null with `stream-position-unavailable`. The earlier generic `read.bytes` counters
are replaced by these categorized observations, not a second copy to add to them.
These are not physical disk bytes or operating-system cache-hit measurements.
Roslyn/MSBuild internal reads, uninstrumented library reads and child-process I/O
are separate unmeasured scopes. Do not fill those missing scopes with category
sums, process counters, zero or estimates; explicit logical reads are not total
SDK/runtime/package I/O coverage.
Git and package proof phases retain both inventory/validation passes. SDK provider
validation reads are categorized as SDK, not as Git/package content reads.

`collector.operation` and `collector.declaration` are children of the existing
combined collector span. Operation collection still runs first. These inclusive
intervals can include Roslyn lazy work and must not be added to their parent.

The cold loader supplies a synchronous `IProgress<ProjectLoadProgress>` reporter
to the existing MSBuildWorkspace open call. `reported-duration` records retain the
producer's `Evaluate`, design-time `Build` and `Resolve` elapsed times individually,
without project paths, framework labels or inferred start times. The reporter binds
the owning run directly, so callbacks need no ambient execution context and cannot
be redirected to a later request. No asynchronous `Progress<T>` posting is used;
after reporter disposal, callbacks are ignored. Missing operation reports are null
with `no-progress-event`, invalid elapsed values with `invalid-elapsed`, and unknown
operations with `unsupported-progress-operation`.

These are reported operation durations, not measured child spans or exclusive
wall time. Parallel projects may overlap; never subtract or add them to
`semantic.msbuild-open` or infer a total evaluation wall interval from their sum.
Failure/cancellation can leave an unreported operation unfinished. A warm semantic
update that does not open MSBuild emits no MSBuild progress, not zero evaluation.


## Watch cycles and external resources

A `watch-cycle` has its own run ID, separate from `index.watch-update` owner runs.
It measures the existing settle, input scan, update wait and idle wait without
changing their order, intervals or cancellation. `watch.reconciliation` identifies
full resync (including startup) versus notification; `watch.notifications` reports
the existing native availability; `watch.wakeup` identifies notification versus
periodic resync. An update-wait interval includes admission/waiting and is not the
exclusive extraction cost. Spans are written when they close, so an unfinished
cycle does not establish an idle interval or a successful reconciliation.

A completed cycle means reconciliation and the following wait finished normally.
Stopping during a successful cycle's idle wait yields cancelled. A preceding
reconciliation failure remains failed if stop then cancels idle. Only the separate
completed extraction-owner run establishes durable publication. No cycle event
changes watcher revision checks, full-resync policy, owner lease or session expiry.

The opt-in [attach-only observer](../../comparison/process-observation.md) records
process RSS, OS lifetime peak working set, process CPU, process-wide cumulative I/O
and DB/WAL byte-length gauges
outside the product. The sampled interval maximum is not an exact interval peak;
OS peak can include pre-attachment work. Size changes are not cumulative writes:
actual DB/WAL bytes written remain null with a fixed reason. Whole-process I/O
counters include other files and non-disk operations and do not satisfy per-file
DB/WAL write acceptance. The observer can tail
these watch-cycle records or retain explicit caller lifecycle/setup markers. It
never launches restore, starts/stops the product, changes SQLite or infers idle
from a lack of updates. Keep its output outside the workspace and dependency trees.

## Coverage and compatibility

These collectors prepare later measurements; no corpus baseline, watch resource cap, overhead
result or performance adoption follows from these collectors. Setup/restore boundaries
require explicit caller markers. Exact per-file DB/WAL writes and exclusive total
MSBuild evaluation wall time are not available from this instrumentation; do not
substitute byte-length deltas or sums of overlapping progress durations. Missing
stages remain absent or explicitly null with fixed reasons, never zero.
Compilation acquisition includes Roslyn lazy work; no syntax retrieval is moved or
added. Caller manifests must separately record commit/binary/corpus hashes, SDK,
configuration, restore/cache state and parallelism before comparing runs.

All existing public signatures and reuse predicates stay unchanged. The shared sink
is internal in Core, with friend access restricted to CLI, Query, Index, Roslyn, Markdown and
CLI/Query test assemblies built in this solution. No shipped API baseline,
multi-targeted library or generated contract is changed. Full-oracle comparisons
should compare the unchanged snapshots/queries and assert branches separately from
this out-of-band stream. Never discard all saved diagnostics as measurement noise.
