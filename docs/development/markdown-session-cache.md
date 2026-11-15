# Prepared Markdown and parser sessions

## Decision

Each coordinator owns an in-memory prepared-input cache. Exact reuse binds the workspace, scope, source, opaque source version, raw-text hash and UTF-16 length, parser/runtime/source pin, and every parse option. Document UUIDs, section tokens, snapshot IDs and generations are projected again. Symbol mentions and search rows are rebuilt against the current code catalog even on a prepared-input hit.

The cache holds at most 64 entries, 4 Mi UTF-16 source units and 262144 charged items, with one latest entry per workspace/scope/source and least-recently-used eviction. These are logical retention bounds, not a heap-size limit. Eligible sessions additionally charge a second source copy, canonical parser output items and retained paragraph payloads. A session that cannot fit is dropped while its prepared result can still be cached within the ordinary bounds. Neither session state nor prepared inputs survive a coordinator restart.

## Optional session updates

Both ordinary builds and session-enabled builds use the official Syntamark 2.0.0 runtime/Source pair and the same runtime pin. `SharpDepsUseMarkdownSessions=true` enables the optional session implementation; it replaces the old preview-selection property `SharpDepsUseMarkdownSessionCandidate`. That former property no longer selects a package or enables sessions. Ordinary builds continue using full preparation for changed inputs and the prepared cache for exact reuse.

Session verification isolates intermediate files, outputs and per-project lockfiles under its artifacts path. Generate those locks with `RestoreLockedMode=false`, then verify a `RestoreLockedMode=true` restore without changing their hashes. Both paths restore Source for byte verification without compiling its code into the runtime consumer. The official pair and producer manifest are in `eng/markdown/feed`; the old private `session-feed` remains historical evidence. Loose DLL references and repacking an existing version are not supported.

An unchanged input uses the prepared cache directly. Changed inputs may continue a session only within the same workspace/scope/source/options/runtime lineage. The previous captured raw hash, source version, facts identity and session binding are checked independently of the new version. One contiguous edit is derived in old-source UTF-16 coordinates without normalizing BOMs or line endings. Only complete, eligible paragraph sessions are retained. The parser reports actual reuse for supported edits and uses its canonical full path for unsupported syntax, edits and resource boundaries. The adapter keeps its facts-only fallback when auxiliary syntax exhausts its resource budget. Partial and failed results retain their status, diagnostics and coverage.

Prepared entries and sessions are immutable. A candidate owns a bounded copy of the cache inventory. Parsing, cancellation or rejected publication cannot replace the committed inventory. After the durable index transaction commits, the owner adopts the candidate only if its cache revision is still current. Abandoned candidates are disposed to release their references. Parser block identities never become durable graph identities; `MarkdownIdentityMatcher` remains responsible for document and section bindings.

## Diagnostics and acceptance

Optional phase diagnostics distinguish `cache`, `session-delta` and `full` preparation. Full session fallbacks have fixed reason labels. Reused, rebased and parsed block counts are emitted only for actual session updates. These labels do not enter graph diagnostics, SQLite schemas, query responses or `updateMode`.

Both the default and opt-in session paths require their own restore and focused validation. Normal CI checks the formal pair and default path; the separate session workflow performs locked restore, artifact verification and real adapter/owner tests. Session acceptance compares mixed edits and undo, raw facts, syntax, identities, mention resolution and saved FTS output with the cold path using the same snapshot identity. Runtime/source package conformance and artifact verification remain required. Hashing, line splitting and canonical facts reconstruction still contain whole-document work. Correctness validation does not establish a speed or memory improvement. The formal parser is now the default dependency. Session performance acceptance and enabling sessions by default remain separate decisions.
