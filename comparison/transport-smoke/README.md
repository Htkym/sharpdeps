# Minimal real-transport smoke (prepared, not executed)

This standalone helper exercises **actual CLI and MCP DLL entrypoints** using an explicitly supplied .NET host. It does not invoke `CliApplication`, `McpQueryAdapter`, a test assembly, or an existing replay observer. Its only compiled source is `Program.cs`; binary references come from an explicit existing MCP product directory. No project/solution wiring, download, installation, indexing build, mutation host, or product compilation is performed by the helper.

This fixture build is scoped to **Windows x64**. Binary-only references do not register the copied native assets in the helper's `deps.json`, so the project also copies the same frozen `ProductRoot/runtimes/win-x64/native/e_sqlite3.dll` into the helper output's application directory for native loading. The original runtime-tree copy is retained. A missing source DLL fails the build; this changes no SQLite provider, product binary, or permissions. Verify the application-directory copy's SHA-256 against the frozen input before running the helper.

The helper references `SharpDeps.Analysis.Contracts`, `SharpDeps.Analysis.Core`, and `SharpDeps.Index`, the APIs used by `Program.cs`, plus the frozen `SQLitePCLRaw.batteries_v2` and `SQLitePCLRaw.provider.e_sqlite3` assemblies required for SQLite's runtime initialization. The latter two must be registered in the helper's `deps.json`; copying their files as content alone does not make dynamic initialization succeed. No manual provider initialization is added to `Program.cs`. Every frozen top-level product DLL is still copied unchanged as runtime content, alongside the runtime tree. Unused Workspaces assemblies are not made primary compiler references. This does not suppress warnings or resolve the frozen Workspaces assembly's separate SQLite assembly-version compatibility risk; that product path is outside this fixture's acceptance claim.

The output is always labelled `local-built-dll-integration-only`. It is not installed-tool or extracted-package acceptance, a benchmark, memory/concurrency proof, or a rerun of the frozen task-4 comparisons. Those comparisons retain their own 15000/15000 ms budgets and immutable evidence. This new fixture freezes split-v1 **30000 ms preparation / 1000 ms query**, 12000 result characters / 48000 UTF-8 bytes. A failure must be investigated, not retried with larger budgets.

## Exact scope and oracle

- Deterministic workspace UUID `98dd3285-ef47-458c-9c7e-50eeebcf122b`, fixed fixture cursor key, generation 1, partial coverage, project `App.csproj`, variant `net10.0`/`Debug`.
- UTF-8 without BOM `App.cs` is exactly `public sealed class Service { public int Value => 7; }` followed by LF. Saved span is start 0, length 54. No project compilation or source extraction is needed: the helper seeds the same public Index/contract APIs used by committed `McpFlowTests.Fixture`.
- One read-only MCP session: initialize protocol `2025-11-25`, initialized notification, tools/list, then agent twice, audit twice, missing symbol once, zero-preparation-budget once. Every successful source request uses the exact saved occurrence selector, explicit project/variant, unique scope, allow-stale freshness, and frozen budgets.
- Five CLI invocations match the five ordinary MCP calls. Each comparison removes **exactly one `Environment.NewLine` CLI framing suffix**, never `Trim`, line-ending normalization, or whitespace rewriting. Raw stdout and each MCP response are retained. Agent requires full source plus line range and omits the source hash; audit requires full source, saved span, and SHA-256. Repeated bodies must be identical. Missing symbol requires exit 2 / `SYMBOL_NOT_FOUND`, error status, and no source.
- The sixth CLI/MCP pair tests zero preparation budget as **pre-admission argument validation**, requiring CLI `ARGUMENT_INVALID` / exit 2 and RPC `MCP_ARGUMENT_INVALID` / -32602, no source and no new request trace. CLI and RPC argument diagnostics are not claimed body-equivalent. This is not preparation-timeout/no-source proof or deterministic four-slot/MCP_BUSY acceptance.
- Product trees, including DLLs, deps/runtimeconfig and native assets, are fully hashed before/after. All `SharpDeps.*.dll` names across both roots, except the MCP-only entrypoint, must exist and match in both roots and helper output. MCP entrypoint is separately bound in its payload manifest. Helper output and explicit dotnet host are hashed. DB/WAL/workspace/source are checked unchanged; SHM is retained in the artifact manifest, not treated as immutable input.
- Every ordinary request gets a new request-time copy of raw metrics and a request/run/owner association. Request terminal outcome is completed for success and failed for missing symbol. After graceful EOF, separate terminal copies must contain completed linked preparation owners. Request-time files are never replaced by terminal evidence. This is a bounded smoke receipt check, not the replay observer's full schema/phase accounting validator.
- Exact created Process handles are owned before fallible setup, with PID/start-time receipts, bounded output and cancellation-aware reads. Failure kills only those process trees. Forced cleanup, missing exit evidence, or cleanup errors must fail acceptance. All fixture/evidence files remain for review; the helper deletes nothing.

The parity oracle follows committed `McpFlowTests.AssertEquivalent`. Task-4 `p2-memory-concurrency-results-20261114/REPORT.md` documents why the extra CLI CRLF must not be hashed as MCP body, and why metrics must stay beneath the fixture's `.sharpdeps` directory. Its historical body hash is not an oracle for this new fixture.

## Future coordinated build/run only

Run only in a coordinated validation window; avoid competing workloads. No build, smoke, restore, or product host was executed when these sources were prepared. Independent static review and an external owned-process guard are required before execution.

PowerShell preparation/build example. All paths must be absolute and existing payloads must be frozen independently; substitute an unused run ID. The fresh source, build output and evidence directories are separate from each other and from product payloads. The helper rejects evidence inside product/helper directories, linked paths, and existing evidence directories. These commands are documented for future use only, not executed as part of preparation.

```powershell
$repo = 'C:\work\sharpdeps'
$dotnet = (Get-Command dotnet).Source
$cli = Join-Path $repo 'analyzer\src\SharpDeps.Cli\bin\Release\net10.0\SharpDeps.Cli.dll'
$mcpRoot = Join-Path $repo 'analyzer\src\SharpDeps.Mcp\bin\Release\net10.0'
$mcp = Join-Path $mcpRoot 'SharpDeps.Mcp.dll'
$run = 'C:\work\transport-smoke-UNUSED-ID'
if (Test-Path -LiteralPath $run) { throw 'Choose an unused run directory' }
$source = Join-Path $run 'source'
$build = Join-Path $run 'build'
$evidence = Join-Path $run 'evidence'
New-Item -ItemType Directory -Path $source | Out-Null
foreach ($name in @('Program.cs', 'TransportSmoke.csproj')) {
    $original = Join-Path $repo "comparison\transport-smoke\$name"
    $copy = Join-Path $source $name
    Copy-Item -LiteralPath $original -Destination $copy
    if ((Get-FileHash -LiteralPath $original).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) { throw 'Source copy mismatch' }
}
'{"sdk":{"version":"10.0.404","rollForward":"disable","allowPrerelease":false}}' | Set-Content -LiteralPath "$source\global.json" -Encoding utf8
'<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath "$source\NuGet.Config" -Encoding utf8
Get-ChildItem -LiteralPath $source -File | Get-FileHash | Select-Object Path, Hash | ConvertTo-Json | Set-Content -LiteralPath "$run\source-manifest.json" -Encoding utf8
Push-Location -LiteralPath $source
try {
    $sdk = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -ne '10.0.404') { throw 'Pinned SDK 10.0.404 unavailable; stop without installing' }
    & $dotnet restore '.\TransportSmoke.csproj' --configfile "$source\NuGet.Config" "-p:ProductRoot=$mcpRoot" "-p:BaseIntermediateOutputPath=$build\obj\" -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Offline restore failed; do not download prerequisites' }
    & $dotnet build '.\TransportSmoke.csproj' --no-restore -c Release "-p:ProductRoot=$mcpRoot" "-p:BaseIntermediateOutputPath=$build\obj\" -o "$build\out"
    if ($LASTEXITCODE -ne 0) { throw 'Helper build failed; do not start smoke' }
} finally { Pop-Location }
```

The next invocation must run **inside an externally supervised validation process** with its own wall-clock deadline and owned-process cleanup. The fragment below is not that supervisor. The supervisor must retain the helper PID/start-time and child identities even if the helper cannot finish its receipts, terminate only its owned process tree on expiry, and mark any forced cleanup as failure.

```powershell
# Run only under the external owned-process guard described above.
& $dotnet "$build\out\TransportSmoke.dll" $dotnet $cli $mcp $evidence
$outerExit = $LASTEXITCODE
[pscustomobject]@{ exitCode = $outerExit; evidence = $evidence } | ConvertTo-Json | Set-Content -LiteralPath "$evidence.outer-exit.json" -NoNewline
if ($outerExit -ne 0) { throw 'Smoke failed; preserve receipts, no budget expansion/retry' }
$summary = Get-Content -LiteralPath "$evidence\summary.json" -Raw | ConvertFrom-Json
if (-not $summary.passed -or $summary.forced -or $summary.cleanupErrors.Count) { throw 'Smoke not admitted' }
```

The isolated source pins SDK 10.0.404 with roll-forward disabled and checks the resolved version from that directory. The explicit NuGet configuration clears package sources; restore uses available local SDK/framework assets and caches, with NuGet auditing disabled. There are no NuGet PackageReferences. Missing prerequisites must cause a stop, not an installation or network-source fallback. Build then uses `--no-restore`. Preserve the frozen source/configuration manifest with the output receipts.

Expected coordinated scope: one offline restore/helper compile, one local smoke, six CLI processes and one MCP process, roughly 1–3 minutes on a ready machine, excluding payload hashing and review. The helper's 45-second RPC/process guards and cancellation-aware reads are nominal asynchronous guards, **not a global wall-clock bound**: synchronous hashing and post-cancellation drains are not independently deadline-bounded. An external owned-process guard is mandatory. It must retain its own exit receipt and verify every recorded PID/start-time identity has exited before releasing the validation window; `summary.json` alone is not acceptance.

Completed responses and metric copies are retained, but output-limit, cancellation, or cleanup/read-timeout failures can discard partial in-memory stream prefixes. Do not claim complete failure-wire preservation. A failed run remains failed even if partial diagnostics or the final helper summary are missing.

## Minimum promotion evidence for package acceptance

This helper does not claim package acceptance and cannot promote its mode flag. A separately reviewed package launcher/manifest is required:

1. Retain original package filename, kind (`nupkg`/VSIX/etc.), version, source revision/build provenance, byte count and SHA-256. Bind both actual CLI and MCP invocation entrypoints to that exact artifact, not nearby local build DLLs.
2. Retain extraction/install manifest mapping every invoked payload-relative file to its original package entry and hash, including union of engine DLLs, deps/runtimeconfig, native dependencies and apphost/shims. An extracted DLL invocation proves extracted payload behavior only; installed command/shim behavior needs actual installed-command invocation.
3. Record exact host/runtime/OS/architecture and command lines, helper source/build/output hashes, complete fixture DB/WAL/workspace/source hashes, the frozen option document, raw transport bodies and errors, request-time/terminal owner receipts, and pre/post payload/input checks.
4. Pair passing outer process exit with helper summary, complete manifest verification, all owned child exit receipts, no forced cleanup, and an independent result review. No path named `package` or local DLL hash alone establishes provenance.

Only the three new files in this directory belong to this change. Existing dirty replay observer and Index tests are untouched.
