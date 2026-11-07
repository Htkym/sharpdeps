# Omission-sensitive exploration discovery

Status: unreleased v0.5 development. Package versions remain unchanged.

## Decision

CLI/MCP entrypoints default ordinary name/path requests to `Discover` only when neither project nor variant was supplied. Explicit scopes default to `RequireUnique`; callers can explicitly request discovery within those filters. The typed Query API keeps `RequireUnique` as its default to preserve existing callers.

Exact saved-ID tokens, retained call-witness requests and HttpGet literal lookup keep exact-selection semantics. They default to `RequireUnique` and reject explicit discovery before workspace access. Explicit unique and prefer-debug behavior remains unchanged. Both entrypoints use the same request factory and identity-token recognition.

Discovery returns separate saved project/variant groups under one pinned snapshot, collection budget, deadline and actual escaped wire-output budget. Edges and source ranges retain their own group's scope instead of being merged by logical identity; groups must not be interpreted as one call chain. Selection and source outcomes, group limits, freshness and omissions stay visible. Source access still requires trust; discovery never starts a writer, watcher, restore or build.

Only CLI `explore` may omit its root. It selects the absolute current directory without ancestor search and retains `.sharpdeps/index.sqlite` as the default index. Missing-index recovery identifies the selected root and index without creating them. One positional query and `--term` are alternate forms, not additive selectors.

## Agent guidance

Start with short names or a saved path, then use exact returned selectors for missing members and omitted ranges. Symbol/file aliases are group-local in discovery and may recur across groups. A complete saved range with the same hash and span that remains in the current context need not be reread. This is not persistent already-read state. Normal read/search remains available for dirty or unindexed files, other spans, missing documentation and lost context.

CLI keeps audit as its format default; MCP keeps agent. MCP exploration returns source text once, without a duplicate structured-content copy. Benchmark and token metrics do not enter normal query output.
