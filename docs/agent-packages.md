# Project-bound agent package export

The scripts in `eng/agent-packages/` only forward arguments to an already installed `sharpdeps` CLI. They do not download software, create a .NET tool manifest, install a plugin, change source trust, or edit host profiles.

Run these commands from the SharpDeps repository root.

```powershell
.\eng\agent-packages\setup.ps1 export --client portable --root C:\src\app --output-dir C:\src\app\sharpdeps-agent --dry-run
```

```sh
sh eng/agent-packages/setup.sh export --client portable --root /src/app --output-dir /src/app/sharpdeps-agent --dry-run
```

Review the preview, then repeat without `--dry-run`. The output directory must be an explicit absolute child of the project with an existing parent. Routing-only is the default. Optional `--mcp-command COMMAND` bundles a saved-index server. For `portable`, COMMAND must be an explicit bare executable such as `sharpdeps-mcp`, resolved through the host PATH at launch. The exporter does not resolve it or guarantee a fixed absolute identity; it rejects absolute paths, relative paths and shell command strings without silently taking the basename. Use routing-only plus native direct MCP configuration if an absolute identity is required. For `claude-code` and `opencode-v2`, COMMAND remains an absolute existing executable. `--index` selects a root-contained index, and `--trusted` independently opts in to source access. Do not bundle a duplicate server when SharpDeps is already registered.

Targets: `portable` (Agent Plugins 1.0), `claude-code`, `opencode-v2`. These are export formats, not promises that every installed host version can load them or will select the skill. Follow the generated README and verify your host's native loading mechanism. No global apply is provided. Existing Copilot `install`/`uninstall` commands retain their original contract.

Delivery is host-specific rather than identical across all hosts:

- Portable (Codex/Copilot) and Claude packages keep `skills/sharpdeps-routing/SKILL.md` for native skill discovery and explicit loading.
- OpenCode V2 sends the full `routing.md` through its `instructions` array only. It intentionally omits both the `skills` configuration entry and the duplicate `SKILL.md`, avoiding a second copy of the same guidance when a skill is loaded. MCP configuration, explicit activation and permissions are unchanged.

To remove, first disable the package in the host, then run `setup.ps1 remove --root ROOT --output-dir OUTPUT` (or the shell equivalent). Only an exact unchanged owned package can be removed. The command moves it to a reported adjacent backup, rather than deleting it. Modified files, additional entries, unknown receipt versions and symlinks are refused. Incomplete exports retain a reported temporary directory for inspection; they do not alter a competing output directory.

The receipt binds absolute paths. Moving a package or editing generated files makes it ineligible for managed removal; do not describe the receipt hashes as cryptographic authentication. Activation and deactivation remain explicit user operations. Routing is conditional on missing structural evidence, respects simple lookups and opt-out, and never enables experimental context deduplication.

References: [portable format](https://developers.openai.com/plugins/build/plugins), [Claude components](https://code.claude.com/docs/en/plugins/components), [OpenCode instructions](https://opencode.ai/docs/instructions/), [OpenCode skills](https://opencode.ai/docs/skills/), [OpenCode MCP](https://opencode.ai/docs/mcp-servers/).
