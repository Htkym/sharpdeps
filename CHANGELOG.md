# Change Log

All notable changes to the SharpDeps extension are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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

[0.0.2]: https://github.com/Htkym/sharpdeps/releases/tag/v0.0.2
[0.0.1]: https://github.com/Htkym/sharpdeps/releases/tag/v0.0.1
