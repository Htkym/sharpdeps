# Change Log

All notable changes to the SharpDeps extension are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
