# Change Log

## 0.5.0

- Add an experimental, explicitly enabled `prompt-context` CLI entry for bounded source evidence before model exploration. It uses a fixed trusted workspace and existing index, preserves coverage/freshness limits, skips unsupported or failed requests, and never installs hooks or blocks read/search. The documented Claude-style context format is not a universal host hook contract. Later retrieval may duplicate this context and increase tokens; token savings are unverified.
- Separate exploration preparation and query budgets across CLI, MCP and the Query API. Preparation defaults to 30,000 ms and query-only time to 1,000 ms; both accept 10–30,000 ms and reject zero. Timeouts and caller cancellation return no source. Cancellation is cooperative, and process startup and transport are outside these computation limits.
- Discover unscoped names and paths in separate project/variant groups under one saved snapshot. Use `--scope-policy unique` or MCP `scopePolicy: "unique"` for the previous unscoped selection behavior. Explicit scopes and the typed Query API retain unique selection by default.
- Present verified source before optional metadata, report source outcomes, and return complete saved member ranges when a type range exceeds the output budget. CLI exploration also accepts one positional query and defaults its omitted root to the current directory without searching ancestors or creating an index.
- Retain one validated MCP generation by default with a five-minute idle timeout. Windows reuses it only when physical-identity and same-connection validation succeed; unsupported proof and Unix use fresh full validation. One-shot CLI reads remain fresh, and no general latency or memory limit is promised.
- Make SharpDeps an optional source of indexed .NET structural evidence when needed. Reuse evidence already available, allow ordinary reads/search for known locations, and stop when the task has sufficient evidence. Initialization, tool descriptions and new dedicated setup carry the workflow; it is model guidance, not host-enforced tool ordering. Existing owned guidance is not silently upgraded, and user opt-out remains available. Align flat MCP arguments and recoverable input errors. Dedicated setup preserves existing user edits. Update VS Code saved-source encoding handling and provide a separate saved-index update reply timeout.
- Add opt-in overview/evidence/detail retrieval modes independently of agent/audit formatting. Overview defers source bodies and retains bounded saved pointers; known selectors can go directly to evidence, and detail adds related bodies only when requested. Omitted mode preserves existing retrieval. Keep scope, trust, freshness and global wire limits unchanged. Agent output shares file-path mappings and index-wide diagnostics instead of repeating them; token and latency benefits have not been established.
- Adopt Syntamark 2.0.0 with native YAML parsing and invalid-frontmatter rejection. Preserve the legacy Markdown profile. Incremental Markdown sessions remain an explicit development opt-in, not the default tool behavior.
- Upgrade CLI, MCP and the VS Code extension together to 0.5.0, then reconnect MCP clients. The existing `maxMilliseconds` field now measures query-only time; choose a separate finite preparation budget when needed. Index schemas 1/2/3 remain supported. See the [upgrade guide](https://github.com/Htkym/sharpdeps/blob/v0.5.0/docs/mcp.md#upgrade-from-040) for migration and parser-stamp handling.

## 0.4.0

- Update the extension's bundled Quick, Semantic, and saved Query hosts, including namespace representative files, generated-document identity and registration diagnostics, and readers for index schemas 1/2/3. The old 0.2.1 extension cannot read schema-3 indexes; updating an external CLI does not replace its bundled reader.
- Add agent/audit exploration formats, bounded member overviews and saved call witnesses, and explicit Debug-preference scope selection while preserving scope, uncertainty, and output limits.
- Retain verified source-generated and package-derived compilation bodies with explicit provenance. Reject missing or inconsistent restore, archive, extracted-source, or compilation evidence; validate lock evidence when required or selected.
- Capture direct member relationships and bounded registration, extension-binding, and HttpGet declaration facts. Look up captured HttpGet templates by exact literal without inferring effective routes.
- Reduce repeated snapshot preparation, catalog scans, SQLite insert setup, and duplicate graph-header storage while preserving structural validation, full source verification, cancellation, and publication guards.
- Add recoverable CLI/MCP setup commands, support explicit zero computation timeouts, and fix Unix IPC listener replacement. Report saved-source provenance without falsely marking complete query results as truncated.
- Adopt the verified Syntamark 1.2.0 runtime/source pair. Reuse immutable Markdown facts and CodeSpan evidence, bind both new parse options, and retain conservative symbol certainty and the legacy profile.
- Create new indexes with schema 3 while retaining readers for schemas 1/2/3. Generated bodies require schema 2 or later and package-derived bodies require schema 3; incompatible writes fail without an in-place migration. Keep previous indexes and use a fresh path to collect the new facts. The 0.3.0 tools cannot read schema-3 indexes. See [upgrade instructions](docs/mcp.md#upgrade-from-030).
- Package the extension, SharpDeps.Cli, and SharpDeps.Mcp together as 0.4.0 for Visual Studio Marketplace, NuGet.org, and GitHub Release assets with checksums. Open VSX publication is not included. Reject missing required credentials for the selected marketplaces before publication. Check each registry or release page for actual availability.

## 0.3.0

- Add shared CLI/MCP `explore` queries for scoped Roslyn symbols, related names, exact identities, and source paths. Return bounded saved source ranges with hashes, UTF-16 spans, semantic certainty, freshness, and explicit omissions.
- Replace the separate MCP query tools with one typed `explore` tool and one text result. Existing CLI query commands remain available; trusted MCP indexing and updates still require host-side opt-in.
- Track field and event declarations through Roslyn extraction and saved-index publication. Retain Markdown links, Partial coverage, and source verification.
- Rebuild selected targets into a new index when upgrading from 0.2.3. Do not rely on automatic migration or reuse of older semantic extraction proofs. See [upgrade instructions](docs/mcp.md#upgrade-from-023).
- Exploration may still require additional source reads; fewer tool calls or lower model-context usage are not guaranteed.
- Publish only the NuGet tools as 0.3.0, retaining Syntamark 1.0.0. The Visual Studio Marketplace extension remains at 0.2.1; no new VSIX or Open VSX publication is included.

## 0.2.3

- Fix root indexing when Markdown has a partial decoded-text mapping but complete raw source positions. Preserve Partial coverage, section identities, and parser diagnostics.
- Resolve Markdown symbol links using code catalog coverage independently of document coverage, and report actionable CLI indexing errors.
- Publish only the NuGet tools as 0.2.3. The Visual Studio Marketplace extension remains at 0.2.1. This release includes no new VSIX and does not publish an extension to Open VSX.

## 0.2.2

- Add the existing VS Code extension icon to the SharpDeps.Cli and SharpDeps.Mcp NuGet packages.
- Publish only the NuGet tools as 0.2.2. The Visual Studio Marketplace extension remains at 0.2.1. This release includes no new VSIX and does not publish an extension to Open VSX.

## 0.2.1 (2026-10-17)

- Read freshness-proof files with at most four workers while preserving full byte hashing, path/link checks, byte budgets, deterministic proof ordering, and cancellation.
- In a fixed three-project Windows comparison with Syntamark 1.0.0, the packaged CLI's verified-current context median decreased from 15.35 seconds to 8.36 seconds, with three measured samples per version. SDK size, filesystem caches, and host load still affect latency; the existing 30-second query maximum is unchanged.
- Generate GitHub release notes from the selected version's changelog section rather than copying the full release history.

## 0.2.0 (2026-10-17)

- Publish the VSIX, SharpDeps.Cli, and optional SharpDeps.Mcp together as version 0.2.0.
- Add saved-index CLI queries and context output with explicit freshness and trust boundaries and incremental reuse for supported closed inputs.
- Add an optional stdio MCP adapter for status, search, symbol, callers, callees, impact, and context. Mutations require explicit host-side opt-in and workspace trust.
- Follow newly committed saved-index generations in the editor, preserve namespace navigation, and improve keyboard selection and evidence navigation.
- Use the verified Syntamark 1.0.0 runtime/source pair. Reuse hashing buffers and avoid duplicate graph deserialization.
- Freshness-proof buffer reuse reduces allocations, not full input verification. Large SDK inputs can still make require-fresh queries slow, and the existing query timeout still applies.

## 0.1.0

- Interactive SVG graph and table views at project, namespace, and type granularity, with hierarchy navigation, search, filters, and cycle detection.
- Quick analysis without an SDK, and explicit MSBuild/Roslyn Semantic analysis with configuration and target-framework selection.
- Dependency evidence and declarations that open in the editor, including generated documents.
- Horizontal/vertical layout, zoom, spacing, resizable panes, keyboard navigation, and saved view state.
- English/Japanese UI with a top-menu language switch.
- English and Japanese user guides with screenshots of the current workbench.
- Mermaid, JSON, SVG, PNG, and evidence-backed clipboard exports of the current selection.
- Cancelled or failed analyses preserve the last successful result; stale results identify changed inputs.
- Correct project-kind detection in Semantic mode and SDK import recognition in Quick mode.
- Removed the unused Mermaid renderer and graph prototype from the build and package.
