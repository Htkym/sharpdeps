# Third-party notices

SharpDeps ships the following third-party components inside the VSIX. The list is kept
short on purpose: everything else is a development dependency and is not packaged.

## elkjs (layout engine)

- Package: `elkjs`
- Licence: Eclipse Public License 2.0 (EPL-2.0)
- How it ships: bundled into `media/workers/elkLayout.worker.js`, which the webview runs
  in a worker to lay out the graph.
- Source: https://github.com/kieler/elkjs
- The EPL-2.0 requires that the licence text is available; the package's own `LICENSE.md`
  travels with the npm package, and the bundled worker keeps its copyright header where
  the bundler preserves it.

## mermaid (diagram rendering for the v1 viewer)

- Package: `mermaid`
- Licence: MIT
- How it ships: bundled into `media/viewer.js`.
- Source: https://github.com/mermaid-js/mermaid

## .NET runtime and Roslyn

The analyzer assemblies in `analyzer/bin/` are SharpDeps' own code. They are executed by
the .NET runtime and use Microsoft Roslyn/MSBuild assemblies that the .NET SDK provides;
no runtime or Roslyn binary is redistributed in the VSIX. The Semantic analyzer therefore
requires an installed .NET SDK, while Quick analysis does not.
