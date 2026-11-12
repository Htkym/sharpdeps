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
{"query":"OrderService","format":"agent","maxMilliseconds":8000}
```

`query` is a string. Do not nest another arguments object inside it. Use actual returned scope IDs/selectors for follow-ups and retain coverage, freshness/proof limits and omissions.

The default 1000 ms budget includes saved-index preparation and exploration. The finite 8000 ms example does not change that default or guarantee completion; MCP has no separate preparation-budget field. No result or a successful transport alone proves that source was returned.

See the [CLI/MCP guide](https://github.com/Htkym/sharpdeps/blob/main/docs/mcp.md) for version-specific behavior, recovery and [agent setup/opt-out](https://github.com/Htkym/sharpdeps/blob/main/docs/mcp.md#agent-routing-and-opt-out). Removing guidance is not a CLI execution ban. The [VS Code extension](https://marketplace.visualstudio.com/items?itemName=htkym.sharpdeps) is installed separately.

[Source and issues](https://github.com/Htkym/sharpdeps) · [MIT license](https://github.com/Htkym/sharpdeps/blob/main/LICENSE)
