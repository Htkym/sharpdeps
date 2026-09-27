# Change Log

All notable changes to the SharpDeps extension are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.1.0] - 2026-09-27

### Added

- Interactive SVG dependency map backed by a validated analysis result (report v2), with graph and table views of the same selection, a hierarchy tree, and whole-index search.
- Inspector with node overview and a paged evidence list: `file:line:column`, resolved/inferred and generated markers, public-surface marks, per-edge occurrence counts, and the aggregated relation breakdown for coarse views.
- Open evidence and declarations in the editor; generated documents open read-only, and a file whose content changed since the analysis asks before revealing a position.
- `SharpDeps: Show Type Dependencies (cursor)` and `Show Type Dependents (cursor)`, resolved through declaration positions rather than names.
- Cycles tab that separates the member set from the verified cycle path, with each path edge linked to its evidence.
- Exports of the current selection as Mermaid, JSON, SVG, and PNG, plus an evidence-backed context copy for coding agents with an explicit "do not assert" section.
- View state persistence (target, selection, filters, scope, panes, table page, camera) with a versioned serializer and clamped values.
- Explicit states for stale results: unsaved edits, saves, and configuration changes mark the registered result as stale.
- Security boundaries: nonce-based CSP with locked objects/frames/base/forms, a trust guard before analysis, bounded search queries, path containment for opening files, and single-line sanitising for names in exports.
- Keyboard shortcuts (`/`, `g`/`t`, `Enter`, `Esc`, `+`/`-`/`0`) and focus return when the inspector closes.
- Semantic precision golden (`semantic-summary.json`) and a normalised accuracy check that found multi-TFM project entries missing for the second target framework.
- VS Code end-to-end suite (`npm run test:e2e`) that installs the extension into a dedicated profile and verifies activation, commands, trust, a real Quick analysis, and the Problems collection.
- Performance measurements and a content check for the packaged VSIX (`npm run perf`, `npm run check:vsix`).

### Changed

- The panel renders the new UI; the previous Mermaid viewer remains in the extension only for the cycle diagnostics.
- Run directories are kept for the two newest analyses so evidence paging and exports keep working, instead of being deleted after each run.

### Fixed

- Multi-targeted projects now publish one project entry per target framework, so the second TFM's types no longer reference a project that does not exist.
- Concurrent graph layouts no longer create (and leak) a second layout worker.
- Closing the inspector with Escape can no longer be undone by the graph's own selection handling.


## [0.0.4] - 2026-07-06

### Added

- Node colors by project kind (web, library, test, desktop, app) with a legend that lists the kinds present in the current graph.
- Zoom slider in the on-screen controls to set an arbitrary zoom level, alongside the existing zoom buttons, wheel, and pinch.
- Node-spacing and rank-spacing sliders (in the bottom-right controls panel) to adjust the graph layout density live.
- Show/hide test projects toggle in the controls panel: hiding re-lays out the graph without the test projects so the remaining graph is more compact.
- Draggable splitter between the graph and the circular-dependency sidebar to resize the two panes.

### Changed

- Thicker dependency edges and arrowheads for better readability.
- The graph now fits the available window when it opens and re-fits on window resize, instead of opening at a fixed small zoom.

## [0.0.3] - 2026-07-05

### Added

- Viewer toolbar action to copy a compact analysis summary and AI coding-agent handoff prompt, including project overview, cycle file paths, and warning/note excerpts.

## [0.0.2] - 2026-07-03

### Added

- Explorer context-menu and command support for `.slnx` solution files, in addition to `.sln`.
- Explorer context-menu and command support for right-clicking a project file (`.csproj`/`.fsproj`/`.vbproj`/`.vcxproj`) to generate a project-scoped dependency graph for that project and everything it transitively references via `ProjectReference`, without requiring a `.sln`/`.slnx`.

## [0.0.1] - 2026-06-30

### Added

- Initial release.
- Interactive dependency map for .NET solutions, rendered with Mermaid in an editor-tab webview.
- Project-level and namespace-level granularity with an instant toggle.
- Zoom and pan the graph with on-screen controls, Ctrl/Cmd + wheel, trackpad pinch, and drag-to-pan.
- Circular-dependency detection, highlighted in red on the graph and reported in the Problems panel.
- Toolbar actions to refresh the analysis, copy the Mermaid source, and export the graph as SVG or PNG.
- Explorer context-menu entry on `.sln` files and a "SharpDeps: Show Dependency Map" command.
- Automatic .NET runtime resolution via the .NET Install Tool, with a configurable `sharpdeps.dotnetPath` fallback.

[0.0.4]: https://github.com/Htkym/sharpdeps/releases/tag/v0.0.4
[0.0.3]: https://github.com/Htkym/sharpdeps/releases/tag/v0.0.3
[0.0.2]: https://github.com/Htkym/sharpdeps/releases/tag/v0.0.2
[0.0.1]: https://github.com/Htkym/sharpdeps/releases/tag/v0.0.1
