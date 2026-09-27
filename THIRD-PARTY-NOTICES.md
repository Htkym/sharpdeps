# Third-party notices

SharpDeps ships the following third-party components inside the VSIX. The list is kept
short on purpose: everything else is a development dependency and is not packaged.

## elkjs (layout engine)

- Package: `elkjs`
- Licence: Eclipse Public License 2.0 (EPL-2.0)
- How it ships: bundled into `media/workers/elkLayout.worker.js`, which the webview runs
  in a worker to lay out the graph.
- Source: https://github.com/kieler/elkjs
- Full licence text: [resources/ELK-LICENSE.md](resources/ELK-LICENSE.md).

## mermaid (diagram rendering for the v1 viewer)

- Package: `mermaid`
- Licence: MIT
- How it ships: bundled into `media/viewer.js`.
- Source: https://github.com/mermaid-js/mermaid

## .NET runtime and Roslyn

The VSIX includes Roslyn compiler and workspace libraries, MSBuildLocator, the Roslyn
MSBuild build host, Microsoft.Extensions and System.Composition libraries,
Microsoft.VisualStudio.SolutionPersistence, and Humanizer alongside the SharpDeps
assemblies. These are dependencies of the published analyzer hosts. The .NET runtime
and the SDK's MSBuild implementation are not bundled; Semantic locates the installed SDK.

These components use the MIT licence. Their upstream package metadata and source links
are available in the analyzer's NuGet assets and dependency manifests. Quick and Semantic
run in separate processes and use their respective published Roslyn versions.
