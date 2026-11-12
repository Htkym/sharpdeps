# SharpDeps.Cli

Save and explore .NET dependency and source evidence from the command line.

## Install

```bash
dotnet tool install --global SharpDeps.Cli
```

Installation requires a .NET SDK. An installed tool requires the .NET 10 runtime. Creating or updating an index additionally requires an SDK compatible with the target and its `global.json`, plus restored target dependencies. SharpDeps does not install an SDK or restore your project automatically.

## Index and explore

For a workspace you trust:

```bash
sharpdeps index --root /absolute/workspace --target YourSolution.sln --configuration Release --trusted
sharpdeps explore --root /absolute/workspace --trusted --term "OrderService Submit" --format agent
```

Indexing evaluates build logic and can run source generators. Exploration reads a saved generation and does not start indexing. The default index is `.sharpdeps/index.sqlite`; retain any chosen `--index` path in subsequent calls.

Use actual returned project/variant IDs and exact selectors to resolve ambiguity. Partial coverage, source/proof limits, freshness and omissions qualify the result; a missing match is not proof that code is absent.

The default exploration computation budget is 1000 ms, not a universal latency guarantee. Use finite budgets supported by the installed version; `--prepare-max-milliseconds`, when advertised, is a CLI-only separate preparation option. Do not use it as an MCP parameter. A timeout or access error is not source evidence.

See the [CLI/MCP user guide](https://github.com/Htkym/sharpdeps/blob/main/docs/mcp.md), which distinguishes released and unreleased behavior, and `sharpdeps help` for supported arguments. [VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.sharpdeps) and [SharpDeps.Mcp](https://www.nuget.org/packages/SharpDeps.Mcp) are separate installations. Installing this tool does not replace the extension's bundled reader.

[Source and issues](https://github.com/Htkym/sharpdeps) · [MIT license](https://github.com/Htkym/sharpdeps/blob/main/LICENSE)
