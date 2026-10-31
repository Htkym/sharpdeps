$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$out = Join-Path $env:RUNNER_TEMP 'control-format-output'
$baseline = Join-Path $env:RUNNER_TEMP 'control-format-baseline'
New-Item -ItemType Directory -Force $out, $baseline | Out-Null
$tracked = @(git ls-files -- '*.cs')
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw 'Unable to enumerate tracked C# sources' }
foreach ($file in $tracked) {
    $destination = Join-Path $baseline $file
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item $file $destination
}
$verifier = Join-Path $env:RUNNER_TEMP 'control-format-verifier'
Copy-Item 'scripts/FormatTokenCheck' $verifier -Recurse
$verifierProject = Join-Path $verifier 'FormatTokenCheck.csproj'
dotnet build $verifierProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Token verifier build failed' }
$verifierDll = Join-Path $verifier 'bin/Release/net10.0/FormatTokenCheck.dll'
function Check-Tokens([string] $name) {
    dotnet $verifierDll $baseline $root (Join-Path $out "$name.json")
    if ($LASTEXITCODE -ne 0) { throw "$name token check failed" }
}
$projects = @(
    'analyzer/src/SharpDeps.Analysis.Contracts/SharpDeps.Analysis.Contracts.csproj',
    'analyzer/src/SharpDeps.Analysis.Core/SharpDeps.Analysis.Core.csproj',
    'analyzer/src/SharpDeps.Analysis.Markdown/SharpDeps.Analysis.Markdown.csproj',
    'analyzer/src/SharpDeps.Analysis.Quick/SharpDeps.Analysis.Quick.csproj',
    'analyzer/src/SharpDeps.Analysis.Roslyn/SharpDeps.Analysis.Roslyn.csproj',
    'analyzer/src/SharpDeps.Cli/SharpDeps.Cli.csproj',
    'analyzer/src/SharpDeps.Index/SharpDeps.Index.csproj',
    'analyzer/src/SharpDeps.Mcp/SharpDeps.Mcp.csproj',
    'analyzer/src/SharpDeps.Query/SharpDeps.Query.csproj',
    'analyzer/src/SharpDeps.QueryHost/SharpDeps.QueryHost.csproj',
    'analyzer/src/SharpDeps.QuickHost/SharpDeps.QuickHost.csproj',
    'analyzer/src/SharpDeps.SemanticHost/SharpDeps.SemanticHost.csproj',
    'analyzer/tests/SharpDeps.Analysis.Tests/SharpDeps.Analysis.Tests.csproj',
    'analyzer/tests/SharpDeps.Cli.Tests/SharpDeps.Cli.Tests.csproj',
    'analyzer/tests/SharpDeps.Index.Tests/SharpDeps.Index.Tests.csproj',
    'analyzer/tests/SharpDeps.Index.Tests/WriterLockProbe/WriterLockProbe.csproj',
    'analyzer/tests/SharpDeps.Markdown.Tests/SharpDeps.Markdown.Tests.csproj',
    'analyzer/tests/SharpDeps.Mcp.Tests/SharpDeps.Mcp.Tests.csproj',
    'analyzer/tests/SharpDeps.Query.Tests/SharpDeps.Query.Tests.csproj'
)
$excluded = @('tests/fixtures/')
foreach ($project in $projects) {
    if (-not (Test-Path $project)) { throw "Missing project: $project" }
    $lock = Join-Path (Split-Path $project) 'packages.lock.json'
    if (Test-Path $lock) {
        dotnet restore $project --locked-mode
    } else {
        dotnet restore $project -p:RestorePackagesWithLockFile=false
    }
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
}
$pilot = 'analyzer/src/SharpDeps.Query/QueryService.cs'
$pilotBefore = (Get-FileHash $pilot -Algorithm SHA256).Hash
dotnet format style 'analyzer/src/SharpDeps.Query/SharpDeps.Query.csproj' --diagnostics IDE2001 --include $pilot --no-restore --verbosity normal
if ($LASTEXITCODE -ne 0) { throw 'IDE2001 pilot failed' }
if ($pilotBefore -eq (Get-FileHash $pilot -Algorithm SHA256).Hash) { throw 'Pilot did not change: IDE2001 effectiveness not established' }
Check-Tokens 'pilot-token-check'
foreach ($project in $projects) {
    if (-not (Test-Path $project)) { throw "Missing solution project: $project" }
}
foreach ($project in $projects) {
    dotnet format style $project --diagnostics IDE2001 --include . --exclude @excluded --no-restore --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw "IDE2001 formatting failed: $project" }
}
Check-Tokens 'all-source-token-check'
foreach ($project in $projects) {
    dotnet format style $project --diagnostics IDE2001 --include . --exclude @excluded --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw "IDE2001 verification failed: $project" }
}
$changed = @(git diff --name-only)
if ($LASTEXITCODE -ne 0) { throw 'git diff failed' }
if (@($changed | Where-Object { $_ -notmatch '\.cs$' }).Count -gt 0) { throw 'Unexpected changed paths' }
foreach ($file in $changed) {
    foreach ($excludedPath in $excluded) {
        if ($file -eq $excludedPath -or $file.StartsWith($excludedPath, [StringComparison]::Ordinal)) { throw "Excluded source changed: $file" }
    }
}
git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed' }
git diff --binary --output="$out/control-format.patch" -- '*.cs'
if ($LASTEXITCODE -ne 0) { throw 'Patch generation failed' }
[ordered]@{ sourceCommit = $env:GITHUB_SHA; sdk = (dotnet --version); completed = $true; changedFiles = $changed; scope = 'IDE2001 only; hand-written C# projects; fixture/template/generated exclusions in editorconfig'; projects = $projects; excludedPaths = $excluded; tests = 'Not run: this job produces a reviewed formatting proposal, not release acceptance' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'summary.json')
"IDE2001 proposal: $($changed.Count) changed source files; token checks passed; no commit or push." >> $env:GITHUB_STEP_SUMMARY
