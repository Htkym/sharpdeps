# Third-party notices

SharpDeps redistributes third-party components in the VSIX and the separate CLI/MCP
NuGet tools. The sections below identify the main bundled components and their
distribution scope. Exact transitive dependencies are recorded in each host's
dependency manifest and the corresponding NuGet package metadata.

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

## Optional stdio MCP tool

The separate SharpDeps.Mcp tool uses the official ModelContextProtocol.Core 2.2.0
SDK (Apache-2.0), maintained at https://github.com/modelcontextprotocol/csharp-sdk.
It is not added to the VSIX or the existing CLI package. Its NuGet dependency
metadata records the SDK's dependencies and licences. Full Apache licence text
is linked above.

## Markdown parsing in the CLI and MCP tools

The 0.4.0 CLI/MCP candidate includes Syntamark 1.2.0 (MIT, copyright 2026 Htkym)
and its pinned YamlDotNet 18.1.0 dependency (MIT). Syntamark's runtime/source pair
and source commit are recorded in
[the Markdown runtime pin](https://github.com/Htkym/sharpdeps/blob/main/docs/contracts/markdown-runtime-pin.json).
The source package is retained for provenance; the tools execute the runtime
assembly. These Markdown dependencies are not bundled into the VSIX's Quick,
Semantic, or saved-index Query hosts.

Upstream sources and licence information:
https://github.com/Htkym/syntamark and https://github.com/aaubry/YamlDotNet.

## NuGet package-source inspection

The separate CLI/MCP tools also use NuGet.Packaging and NuGet.Frameworks
(Apache-2.0) to inspect package-derived source evidence. Their upstream source
and licence information are at https://github.com/NuGet/NuGet.Client; the full
Apache licence text is linked above. These dependencies are not part of the
VSIX's saved-index reader.
