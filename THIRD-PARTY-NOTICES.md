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

## .NET runtime and Roslyn

The VSIX includes Roslyn compiler and workspace libraries, MSBuildLocator, the Roslyn
MSBuild build host, Microsoft.Extensions and System.Composition libraries,
Microsoft.VisualStudio.SolutionPersistence, and Humanizer alongside the SharpDeps
assemblies. These are dependencies of the published analyzer hosts. The .NET runtime
and the SDK's MSBuild implementation are not bundled; Semantic locates the installed SDK.

These components use the MIT licence. Their upstream package metadata and source links
are available in the analyzer's NuGet assets and dependency manifests. Quick and Semantic
run in separate processes and use their respective published Roslyn versions.

## SQLite saved-index reader

The saved Query host includes Microsoft.Data.Sqlite (MIT) and SQLitePCLRaw
(Apache-2.0, copyright 2014-2024 SourceGear, LLC), including its `e_sqlite3` native
SQLite builds. They are dependencies of the read-only index reader. SQLite itself
is in the public domain.

Full Apache licence text: [resources/APACHE-2.0-LICENSE.txt](resources/APACHE-2.0-LICENSE.txt).

The upstream package metadata records the licences and sources:
https://github.com/dotnet/dotnet and https://github.com/ericsink/SQLitePCL.raw.
SQLite's public-domain dedication is available at https://sqlite.org/copyright.html.
