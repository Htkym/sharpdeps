# Change Log

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
