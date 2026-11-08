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
waiting client. A watch resynchronization without an update emits no run.

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

## Coverage and compatibility

Idle watch scans, setup/restore, per-SDK/Git/package read attribution,
separate declaration versus operation time, exact MSBuild evaluation time, process
peak working set and DB/WAL capacity are not measured yet. Missing stages are absent,
not zero. Compilation acquisition includes Roslyn lazy work; no syntax retrieval is
moved or added. Caller manifests must separately record commit/binary/corpus hashes,
SDK, configuration, restore/cache state and parallelism before comparing runs.
This instrumentation is not a corpus baseline or a five-run cold benchmark.

All existing public signatures and reuse predicates stay unchanged. The shared sink
is internal in Core, with friend access restricted to the CLI, Query, Index, Roslyn and
CLI/Query test assemblies built in this solution. No shipped API baseline,
multi-targeted library or generated contract is changed. Full-oracle comparisons
should compare the unchanged snapshots/queries and assert branches separately from
this out-of-band stream. Never discard all saved diagnostics as measurement noise.
