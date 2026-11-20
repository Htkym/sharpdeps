# SharpDeps.Cli

Search C# symbols and trace .NET dependencies from your terminal. SharpDeps saves a local index of your solution so you can look up a method, find its callers and inspect the source. AI assistants can explore the same index through [SharpDeps.Mcp](https://www.nuget.org/packages/SharpDeps.Mcp).

## Index a solution

Install the tool with a .NET SDK. Running the tool requires the .NET 10 runtime. Indexing also requires an SDK compatible with your solution and its restored dependencies.

```sh
dotnet tool install --global SharpDeps.Cli
dotnet restore /absolute/path/to/Shop/Shop.sln
sharpdeps index --root /absolute/path/to/Shop --target Shop.sln --configuration Release --trusted
```

Replace the path and solution name with your own. Index workspaces you trust because indexing can run build logic and source generators. The default index is `.sharpdeps/index.sqlite` inside the workspace.

## Find a declaration and its callers

Use a method name from your solution. For example, to inspect `OrderStore.Save` and its indexed relationships, run:

```sh
sharpdeps explore --root /absolute/path/to/Shop --term "OrderStore.Save" --trusted --format agent
```

The result can include the declaration, related symbols and source locations. If several projects or target frameworks match, select the one you want. Partial coverage means other relationships may fall outside the index.

For a list of callers for an exact method, search by name and copy the matching result's `id`. Replace `ID_FROM_SEARCH` in the second command with that value.

```sh
sharpdeps search --root /absolute/path/to/Shop --term Save
sharpdeps callers --root /absolute/path/to/Shop --id ID_FROM_SEARCH --edge-kind calls
```

After changing code, refresh the index with the same target and configuration:

```sh
sharpdeps update --root /absolute/path/to/Shop --target Shop.sln --configuration Release --trusted
```

[CLI and MCP guide](https://github.com/Htkym/sharpdeps/blob/main/docs/mcp.md) · [Dependency map for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.sharpdeps)
