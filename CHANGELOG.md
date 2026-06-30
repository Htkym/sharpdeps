# Change Log

All notable changes to the SharpDeps extension are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- Initial release.
- Interactive dependency map for .NET solutions, rendered with Mermaid in an editor-tab webview.
- Project-level and namespace-level granularity with an instant toggle.
- Circular-dependency detection, highlighted in red on the graph and reported in the Problems panel.
- Export actions: copy Mermaid source, save as SVG, save as PNG.
- Explorer context-menu entry on `.sln` files and a "SharpDeps: Show Dependency Map" command.
- Automatic .NET runtime resolution via the .NET Install Tool, with a configurable `sharpdeps.dotnetPath` fallback.
