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

The unreleased split-v1 exploration contract defaults to 30,000 ms for preparation (`--prepare-max-milliseconds`) and 1,000 ms for the query only (`--max-milliseconds`). Both accept finite values from 10 to 30,000 ms; explore rejects zero. Omitting both options selects these defaults. Preparation includes full snapshot validation/freezing and standard derived/lexical indexes before query time starts. One monotonic request owner also caps total computation at the sum of both allowances. Cancellation is cooperative; startup and transport are outside the bound. The query default is an initial candidate, not a completion guarantee for every workload.

Matching newly built CLI/MCP/API consumers are required for this breaking contract; MCP uses flat `prepareMaxMilliseconds` and `maxMilliseconds` fields. Existing `--max-milliseconds` now means query-only time. Keep historical recorded inputs frozen and choose a new cohort before comparing runs; do not silently raise budgets or retry. Preparation, query and total-request timeouts return no source, as does caller cancellation. Published 0.4.0 retains its released behavior; consult the installed tool's help. Index schemas 1/2/3 are unchanged.

See the [CLI/MCP user guide](https://github.com/Htkym/sharpdeps/blob/main/docs/mcp.md), which distinguishes released and unreleased behavior, and `sharpdeps help` for supported arguments. [VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.sharpdeps) and [SharpDeps.Mcp](https://www.nuget.org/packages/SharpDeps.Mcp) are separate installations. Installing this tool does not replace the extension's bundled reader.

[Source and issues](https://github.com/Htkym/sharpdeps) · [MIT license](https://github.com/Htkym/sharpdeps/blob/main/LICENSE)
