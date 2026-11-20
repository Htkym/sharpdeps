# SharpDeps.Mcp

Give your AI coding assistant a local Model Context Protocol (MCP) server for exploring C# code. Ask which methods call a service, what a handler calls across projects, or where a type is declared. SharpDeps can return the matching source lines so you can check the answer in your editor.

## Connect your assistant

Install the server with a .NET SDK. You need the .NET 10 runtime to run it. To index a solution, use an SDK compatible with the solution and restore its dependencies. Run the second command in your solution folder, replacing `Shop.sln` with its filename.

```sh
dotnet tool install --global SharpDeps.Mcp
dotnet restore Shop.sln
```

Add `sharpdeps-mcp` as a stdio server in your assistant's MCP settings. For clients that use `mcpServers`, replace the path below with the absolute path to your solution folder.

```json
{
  "mcpServers": {
    "sharpdeps": {
      "command": "sharpdeps-mcp",
      "args": ["--root", "/absolute/path/to/Shop", "--trusted", "--allow-mutations"]
    }
  }
}
```

Use this configuration for a workspace you trust. `--trusted` permits the assistant to read source. Adding `--allow-mutations` lets it create and update the index, which can execute the project's build logic and source generators. For read-only exploration of an existing index, omit `--allow-mutations`. You can also create the index from a terminal with the [SharpDeps CLI](https://www.nuget.org/packages/SharpDeps.Cli).

## Ask about your code

Reconnect your client and try these prompts. Substitute a method from your solution for `OrderStore.Save`.

> Use SharpDeps to index Shop.sln in Release.

> What calls OrderStore.Save? Show the source file and line for the calls you find.

The assistant can query the `explore` tool with `{"query":"OrderStore.Save"}`. If the name matches several methods, choose the project, target framework and declaration you meant. A reported call describes the relationship found in that build configuration. When coverage is partial, other calls may fall outside the index.

After changing code, ask the assistant to update the index with the same solution and configuration. An `unverified` result means SharpDeps could not confirm that all inputs still match the saved index.

[CLI and MCP guide](https://github.com/Htkym/sharpdeps/blob/main/docs/mcp.md) · [Dependency map for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.sharpdeps)
