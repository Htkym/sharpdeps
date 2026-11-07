# Index phase diagnostics

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
and cancelled. A completed run means durable publication succeeded.

Reuse decisions are emitted at existing proof capture, environment/budget rejection,
semantic admission and project selection boundaries. Some legacy compound guards
remain one combined reason rather than re-running checks to infer a finer cause.
Selected, dirty and clean project counts are distinct from the actual extracted and
reused collector counts. A successful closure proof does not establish edit reuse
or an end-to-end speed improvement.

## Coverage and compatibility

Query, idle watch scans, setup/restore, per-SDK/Git/package read attribution,
separate declaration versus operation time, exact MSBuild evaluation time, process
peak working set and DB/WAL capacity are not measured yet. Missing stages are absent,
not zero. Compilation acquisition includes Roslyn lazy work; no syntax retrieval is
moved or added. Caller manifests must separately record commit/binary/corpus hashes,
SDK, configuration, restore/cache state and parallelism before comparing runs.
This instrumentation is not a corpus baseline or a five-run cold benchmark.

All existing public signatures and reuse predicates stay unchanged. The shared sink
is internal in Core, with friend access restricted to the CLI, Index, Roslyn and
CLI test assembly built in this solution. No shipped API baseline,
multi-targeted library or generated contract is changed. Full-oracle comparisons
should compare the unchanged snapshots/queries and assert branches separately from
this out-of-band stream. Never discard all saved diagnostics as measurement noise.
