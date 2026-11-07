# Release preparation

This document describes the release inputs and procedure for version 0.4.0.
Check NuGet, Visual Studio Marketplace and GitHub Releases for the versions
actually available. Earlier changelog entries remain historical records.
Open VSX is excluded from the 0.4.0 publication target.

## Choose the outputs

The reviewed `RELEASE_TARGET` in `.github/workflows/release.yml` selects the
publication channels:

| Target | NuGet CLI/MCP | VSIX download | Visual Studio Marketplace | Open VSX |
| --- | --- | --- | --- | --- |
| `nuget-only` | Yes | No | No | No |
| `nuget-and-marketplace` | Yes | Yes | Yes | No |
| `all` | Yes | Yes | Yes | Yes |

The 0.4.0 target is `nuget-and-marketplace`. Open VSX requires a separate explicit
publication decision; its credential being present does not select that channel.
The workflow requires the credentials for the selected marketplaces before it
builds or publishes. This preflight checks their presence; publication still
depends on network access and the credentials' permissions.

Every target creates a GitHub Release containing `SharpDeps.Cli` and
`SharpDeps.Mcp` nupkgs and their `SHA256SUMS`. Targets that include the extension
also attach `sharpdeps-<version>.vsix` and its `.sha256` file. The NuGet job uses
the same verified tool packages preserved by the release job.

The extension includes its own Quick, Semantic, and saved-index Query hosts.
Updating a NuGet tool does not update those bundled hosts. The 0.4.0 reader accepts
SQLite index schemas 1/2/3; the 0.2.1 extension accepts schema 1 only. Syntamark 1.2.0 is
pinned in the CLI/MCP tools, not in the VSIX. Review the [upgrade instructions](mcp.md#upgrade-from-030)
and [extension setup](guide.md#read-a-saved-index) with the release notes.

## Prepare and verify a candidate

Keep these values equal: `package.json.version`, both root versions in
`package-lock.json`, and `<Version>` in the CLI and MCP project files. Add a
nonempty `## <version>` section to `CHANGELOG.md`; the workflow extracts that
section for the GitHub Release. Update current user guidance while retaining
historical changelog entries, pinned comparisons, and their original evidence.

With the repository dependencies installed, validate the definitions locally:

```bash
npm run check:release
node scripts/check-release.js --tag v0.4.0
```

The `--tag` argument checks the version string and stable Syntamark pair locally;
it does not create a tag. `npm run check:vsix` builds and checks the packaged
extension, including its three hosts, webview, runtime files, and notices. Check
both packed tools:

```bash
node scripts/check-release.js --cli-package <cli.nupkg> --mcp-package <mcp.nupkg>
```

The checker verifies their identities, tool entry points, required payloads,
and pinned Syntamark assembly bytes.

Tool candidates may use `<base>-rc.<number>` or `<base>-ci.<run>.<attempt>` when
passed with the matching `--cli-version` and `--mcp-version` arguments. Normal CI
uploads those tool candidates, the VSIX, and verification evidence as Actions
artifacts. Local packages and successful CI artifacts do not publish a release.
Keep each candidate's version, source commit, identity report, and checksums
together; retain earlier candidates and proof records when rebuilding.

## Publish from reviewed main

Merge the reviewed release-preparation PR and require the latest normal `ci.yml`
push run for that exact current `main` commit to succeed. The release tag must be
`v<package.json version>` and resolve to that same commit locally and on origin.
`scripts/Test-MainRelease.ps1` enforces these conditions and rechecks main before
publication. If main advances, verify the new current commit before tagging.

Pushing the matching `v*` tag starts `release.yml`; it has no manual dispatch
entry. The workflow builds and verifies the selected outputs, publishes selected
marketplaces, and creates the GitHub Release from the version's changelog section.
The dependent NuGet job checks package checksums and existing registry contents,
then publishes the missing CLI/MCP packages through OIDC. A matching existing
package is retained; different contents fail verification.

For a partial NuGet publication, re-run the failed job to reuse the sealed tool
artifact. Verify the selected registry listings and GitHub assets after the
workflow completes. A Marketplace or NuGet listing and its downloadable payload,
not a source version or an Actions artifact, establish publication on that channel.
