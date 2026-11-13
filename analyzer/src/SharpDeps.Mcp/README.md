# SharpDeps.Mcp

Connect an MCP client to SharpDeps saved .NET dependency and source evidence.

## Install and connect

```bash
dotnet tool install --global SharpDeps.Mcp
```

Installation requires a .NET SDK; the installed server requires the .NET 10 runtime. Create the index separately using a matching [SharpDeps.Cli](https://www.nuget.org/packages/SharpDeps.Cli) and the target's SDK/restored dependencies.

Merge this entry into the configuration your MCP client actually loads:

```json
{
  "mcpServers": {
    "sharpdeps": {
      "command": "sharpdeps-mcp",
      "args": ["--root", "/absolute/workspace", "--index", ".sharpdeps/index.sqlite", "--trusted"]
    }
  }
}
```

Replace the workspace path. `--trusted` permits source reads; omit it for saved graph evidence without source text. Root, index and trust are fixed at startup. Client approvals remain separate. The default connection exposes one `explore` tool; it does not index, restore or start watch.

## Call explore

Use the advertised schema and a flat arguments object:

```json
{"query":"OrderService","format":"agent","prepareMaxMilliseconds":30000,"maxMilliseconds":1000}
```

`query` is a string. Do not nest another arguments object inside it. Use actual returned scope IDs/selectors for follow-ups and retain coverage, freshness/proof limits and omissions.

The unreleased split-v1 contract defaults to 30,000 ms for preparation and 1,000 ms for the query only. Omitting both budget fields, as in `{"query":"OrderService","format":"agent"}`, selects those defaults. Both accept finite values from 10 to 30,000 ms; explore rejects zero. Preparation fully hydrates, validates and freezes a fresh SQLite reader and prepares standard derived/lexical indexes before query time starts. One monotonic request owner caps total computation at the sum of both allowances. Cancellation is cooperative; process startup, MCP handshake and transport are outside the bound. The query default is an initial candidate, not a complete-`DbContext` guarantee.

Use matching newly built CLI/MCP/API consumers for this breaking contract. Existing `maxMilliseconds` now means query-only time; keep historical inputs frozen and create a new cohort manifest for replay. Do not silently raise budgets or retry. `PREPARATION_TIMEOUT`, `QUERY_TIMEOUT` and `REQUEST_TIMEOUT` return no source; caller `CANCELLED` takes priority and also returns no source. Published 0.4.0 keeps its released behavior; inspect the connected server's schema. No result or a successful transport alone proves that source was returned.

See the [CLI/MCP guide](https://github.com/Htkym/sharpdeps/blob/main/docs/mcp.md) for version-specific behavior, recovery and [agent setup/opt-out](https://github.com/Htkym/sharpdeps/blob/main/docs/mcp.md#agent-routing-and-opt-out). Removing guidance is not a CLI execution ban. The [VS Code extension](https://marketplace.visualstudio.com/items?itemName=htkym.sharpdeps) is installed separately.

[Source and issues](https://github.com/Htkym/sharpdeps) · [MIT license](https://github.com/Htkym/sharpdeps/blob/main/LICENSE)
