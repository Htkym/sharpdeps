# SharpDeps — .NET Dependency Map

English · [日本語](README.ja.md) · [User guide](docs/guide.md)

SharpDeps helps you explore dependencies in a .NET solution from a VS Code editor tab. Start with projects, drill down to namespaces and types, and select a connection to inspect its source references.

![Project dependencies in SharpDeps](images/overview-en.png)

The screenshots and guide describe the **0.1.0 workbench**. Use a matching VSIX or build this source to follow these instructions.

## Install

Install a `.vsix` with **Extensions: Install from VSIX…** in the Command Palette. Published versions are available from the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=htkym.sharpdeps) and [GitHub Releases](https://github.com/Htkym/sharpdeps/releases); check the version in VS Code's Extensions view.

VS Code 1.90 or later and a trusted workspace are required to run analysis.

| Mode | What it shows | Requirements |
| --- | --- | --- |
| Quick | Declared project references and namespace dependencies inferred from `using` directives. | .NET 10 runtime; no SDK or MSBuild evaluation. The .NET Install Tool can acquire the runtime. |
| Semantic | Evaluated C# project references and code references resolved with MSBuild and Roslyn, including types and reference locations. | .NET 10 runtime and an installed SDK compatible with the target and its `global.json`. Restore the target's packages yourself. |

The [.NET Install Tool](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.vscode-dotnet-runtime) is installed as an extension dependency. SharpDeps does not acquire an SDK or restore project packages automatically.

## Open your first map

1. Open your solution folder in VS Code.
2. Right-click a `.sln`, `.slnx`, or supported project file in Explorer and choose **SharpDeps: Show Dependency Map**.
3. Select a node or connection. Use **Dependencies** or **Dependents** to narrow the graph, and open reference evidence from **Details**.

You can also use the Command Palette. Quick supports `.csproj`, `.fsproj`, `.vbproj`, and `.vcxproj` targets; Semantic is intended for SDK-style C# projects. A project target follows its transitive project references.

For type-level references, select **Semantic** and choose **Analyze**. See the [user guide](docs/guide.md) for setup, filters, evidence, cycles, exports, and troubleshooting.

## Explore and share

- Use the hierarchy or search to find projects, namespaces, and types. Graph and table views share the same scope.
- Use the **bottom bar** for horizontal/vertical direction, zoom, and **Fit**. **Spacing** opens above the bar to adjust node and rank spacing.
- A directed node pair has one line: solid when a declared, evaluated, or resolved relation exists; dashed when only inferred. Quick's solid lines show declarations, not proof of compiled code usage. Underlying relations remain available in Details.
- Export the current scope as Mermaid, JSON, SVG, or PNG. **Copy for agent** copies an evidence-backed context to the clipboard; it does not contact a service.
- Switch the UI with **日本語** or **English** in the top menu. Language, orientation, filters, selection, pane sizes, and camera are saved with the view.

Analysis and graph rendering run locally. A failed or cancelled analysis keeps the last successful result. Display limits restrict the picture, not discovery, search, or cycle detection.

## Build from source

Use Node.js 20 or later and .NET SDK 10:

```bash
npm ci
npm run package
```

This builds both analyzer hosts and the webview, then creates `sharpdeps-0.1.0.vsix`. To run a development host, use `npm run build:analyzer` and `npm run build`, then press **F5** in VS Code.

## License

[MIT](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for bundled components.
