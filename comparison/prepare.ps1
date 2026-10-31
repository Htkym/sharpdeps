<#
.SYNOPSIS
    Prepares the fixed inputs and tools for the SharpDeps / CodeGraph comparison.

.DESCRIPTION
    Stage Inputs: fetches each pinned corpus commit once and creates two isolated
    working copies per corpus (one per tool). Submodules are never initialized and
    no source file is trimmed.

    Stage Tools: installs the pinned CodeGraph npm package into a private prefix
    with a private cache (no global install, no scripts), after verifying the
    registry integrity values against pins.json, and builds SharpDeps.Cli from
    this repository checkout, recording the source commit it was built from.

    Every native command runs through a logger that preserves its exit code,
    stdout and stderr under <Root>/o/logs. The first failing step stops the run;
    its cause stays in the log and in <Root>/o/prepare-manifest.json.
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [ValidateSet('Inputs', 'Tools', 'All')]
    [string]$Stage = 'All',
    [ValidateSet('Small', 'Large', 'All')]
    [string]$Corpus = 'All',
    [string]$RepoRoot = ''
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path $PSScriptRoot -Parent
}
if (-not $Root) {
    if ($env:SCG_ROOT) {
        $Root = $env:SCG_ROOT
    }
    elseif ($IsWindows -and (Test-Path 'D:\')) {
        $Root = 'D:\scg'
    }
    else {
        $Root = Join-Path $RepoRoot '.scg'
    }
}

$inputsDir = Join-Path $Root 'i'
$toolsDir = Join-Path $Root 't'
$cacheDir = Join-Path $Root 'c'
$outputDir = Join-Path $Root 'o'
$logDir = Join-Path $outputDir 'logs'
foreach ($dir in $inputsDir, $toolsDir, $cacheDir, $outputDir, $logDir) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$pins = Get-Content (Join-Path $PSScriptRoot 'pins.json') -Raw | ConvertFrom-Json

$manifest = [ordered]@{
    schemaVersion = 1
    startedAtUtc  = [DateTime]::UtcNow.ToString('o')
    root          = $Root
    repoRoot      = $RepoRoot
    stage         = $Stage
    corpus        = $Corpus
    steps         = @()
    success       = $false
}

function Save-Manifest {
    $manifest.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    $manifest | ConvertTo-Json -Depth 8 |
        Set-Content -Path (Join-Path $outputDir 'prepare-manifest.json') -Encoding utf8
}

function Invoke-Logged {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [string]$WorkingDirectory = '',
        [hashtable]$Environment = @{}
    )
    $stdoutPath = Join-Path $logDir "$Name.stdout.log"
    $stderrPath = Join-Path $logDir "$Name.stderr.log"
    # Start-Process joins -ArgumentList with bare spaces and mangles paths
    # containing spaces, so launch through ProcessStartInfo.ArgumentList,
    # which passes each argument to the child process intact.
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    foreach ($arg in $Arguments) {
        $psi.ArgumentList.Add($arg)
    }
    if ($WorkingDirectory) {
        $psi.WorkingDirectory = $WorkingDirectory
    }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($key in $Environment.Keys) {
        $psi.Environment[$key] = $Environment[$key]
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()
    $outStream = [System.IO.File]::Create($stdoutPath)
    $errStream = [System.IO.File]::Create($stderrPath)
    $outTask = $process.StandardOutput.BaseStream.CopyToAsync($outStream)
    $errTask = $process.StandardError.BaseStream.CopyToAsync($errStream)
    $process.WaitForExit()
    [System.Threading.Tasks.Task]::WaitAll(@($outTask, $errTask))
    $outStream.Dispose()
    $errStream.Dispose()
    $sw.Stop()
    $code = $process.ExitCode
    $manifest.steps += [ordered]@{
        name       = $Name
        exitCode   = $code
        durationMs = [int]$sw.ElapsedMilliseconds
        stdout     = $stdoutPath
        stderr     = $stderrPath
    }
    if ($code -ne 0) {
        throw "Step '$Name' failed with exit code $code. See $stderrPath"
    }
    return $code
}

function Get-GitHead {
    param([Parameter(Mandatory)] [string]$RepoPath)
    return (git -C $RepoPath rev-parse HEAD).Trim()
}

function Prepare-Corpus {
    param([Parameter(Mandatory)] $Spec)
    $id = $Spec.id
    $srcDir = Join-Path $inputsDir "$id-src"
    if (Test-Path $srcDir) {
        $head = Get-GitHead $srcDir
        if ($head -ne $Spec.commit) {
            throw "Existing source copy $srcDir is at $head, expected $($Spec.commit). Not overwriting."
        }
        $manifest.steps += [ordered]@{ name = "inputs-$id-fetch"; exitCode = 0; reused = $true }
    }
    else {
        Invoke-Logged -Name "inputs-$id-init" -FilePath 'git' -Arguments @('init', $srcDir) | Out-Null
        Invoke-Logged -Name "inputs-$id-remote" -FilePath 'git' -Arguments @('-C', $srcDir, 'remote', 'add', 'origin', $Spec.repository) | Out-Null
        $fetchCode = 0
        try {
            Invoke-Logged -Name "inputs-$id-fetch" -FilePath 'git' -Arguments @('-C', $srcDir, 'fetch', '--depth', '1', 'origin', $Spec.commit) | Out-Null
        }
        catch {
            # The pinned fetch failed; keep its log as the first cause and fall back
            # to a full fetch of the same pinned commit. Never substitute another ref.
            $manifest.steps += [ordered]@{ name = "inputs-$id-fetch-fallback"; note = 'depth-1 SHA fetch failed; full fetch of the same commit follows' }
            Invoke-Logged -Name "inputs-$id-fetch-full" -FilePath 'git' -Arguments @('-C', $srcDir, 'fetch', 'origin') | Out-Null
        }
        Invoke-Logged -Name "inputs-$id-checkout" -FilePath 'git' -Arguments @('-C', $srcDir, '-c', 'advice.detachedHead=false', 'checkout', '--detach', $Spec.commit) | Out-Null
        $head = Get-GitHead $srcDir
        if ($head -ne $Spec.commit) {
            throw "Source copy $srcDir resolved to $head, expected $($Spec.commit)."
        }
    }
    foreach ($tool in 'codegraph', 'sharpdeps') {
        $copyDir = Join-Path $inputsDir "$id-$tool"
        if (Test-Path $copyDir) {
            $copyHead = Get-GitHead $copyDir
            if ($copyHead -ne $Spec.commit) {
                throw "Existing copy $copyDir is at $copyHead, expected $($Spec.commit). Not overwriting."
            }
        }
        else {
            Invoke-Logged -Name "inputs-$id-$tool-clone" -FilePath 'git' -Arguments @('clone', '--quiet', $srcDir, $copyDir) | Out-Null
            Invoke-Logged -Name "inputs-$id-$tool-origin" -FilePath 'git' -Arguments @('-C', $copyDir, 'remote', 'set-url', 'origin', $Spec.repository) | Out-Null
            Invoke-Logged -Name "inputs-$id-$tool-checkout" -FilePath 'git' -Arguments @('-C', $copyDir, '-c', 'advice.detachedHead=false', 'checkout', '--detach', $Spec.commit) | Out-Null
            $copyHead = Get-GitHead $copyDir
            if ($copyHead -ne $Spec.commit) {
                throw "Copy $copyDir resolved to $copyHead, expected $($Spec.commit)."
            }
        }
        if ($tool -eq 'sharpdeps') {
            $tracked = @(git -C $copyDir ls-files -- '*.cs')
            $longest = 0
            foreach ($rel in @(git -C $copyDir ls-files)) {
                $len = (Join-Path $copyDir $rel).Length
                if ($len -gt $longest) {
                    $longest = $len
                }
            }
            $manifest.steps += [ordered]@{
                name             = "inputs-$id-survey"
                trackedCSharp    = $tracked.Count
                expectedCSharp   = $Spec.expectedTrackedCSharp
                countMatchesPin  = ($tracked.Count -eq $Spec.expectedTrackedCSharp)
                longestPathChars = $longest
            }
        }
    }
}

function Prepare-CodeGraph {
    $cg = $pins.codegraph
    $platformKey = $null
    if ($IsWindows) {
        $platformKey = 'win32-x64'
    }
    elseif ($IsLinux) {
        $platformKey = 'linux-x64'
    }
    else {
        throw "Unsupported OS for the pinned CodeGraph platform packages."
    }
    $platform = $cg.integrity.platforms.$platformKey
    # A shell-less process launch cannot execute the npm batch shim on
    # Windows, so npm is always driven through node and its own CLI script.
    $npmCmd = (Get-Command 'npm').Source
    $nodeExe = (Get-Command 'node').Source
    $npmDir = Split-Path $npmCmd -Parent
    $npmCli = @(
        (Join-Path $npmDir 'node_modules/npm/bin/npm-cli.js'),
        (Join-Path $npmDir 'lib/node_modules/npm/bin/npm-cli.js')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $npmCli) {
        throw "npm CLI script not found next to the npm shim: $npmDir"
    }

    $mainIntegrity = (& $nodeExe $npmCli view "$($cg.package)@$($cg.version)" dist.integrity "--registry=$($cg.registry)").Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'npm view for the main CodeGraph package failed.'
    }
    if ($mainIntegrity -ne $cg.integrity.main) {
        throw "Main package integrity mismatch. Registry: $mainIntegrity Pin: $($cg.integrity.main)"
    }
    $platformIntegrity = (& $nodeExe $npmCli view "$($platform.package)@$($cg.version)" dist.integrity "--registry=$($cg.registry)").Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'npm view for the CodeGraph platform package failed.'
    }
    if ($platformIntegrity -ne $platform.integrity) {
        throw "Platform package integrity mismatch. Registry: $platformIntegrity Pin: $($platform.integrity)"
    }
    $manifest.steps += [ordered]@{ name = 'tools-codegraph-integrity'; exitCode = 0; platform = $platformKey }

    $prefix = Join-Path $toolsDir 'codegraph'
    $cache = Join-Path $cacheDir 'npm'
    $installedManifest = Join-Path $prefix 'node_modules/@colbymchenry/codegraph/package.json'
    $installedVersion = $null
    if (Test-Path $installedManifest) {
        $installedVersion = (Get-Content $installedManifest -Raw | ConvertFrom-Json).version
    }
    if ($installedVersion -ne $cg.version) {
        $envForInstall = @{}
        foreach ($prop in $cg.processEnvironment.PSObject.Properties) {
            $envForInstall[$prop.Name] = $prop.Value
        }
        Invoke-Logged -Name 'tools-codegraph-install' -FilePath $nodeExe -Arguments @(
            $npmCli, 'install', '--prefix', $prefix, '--cache', $cache, '--no-save', '--ignore-scripts',
            '--include=optional', '--no-audit', '--no-fund', "--registry=$($cg.registry)",
            "$($cg.package)@$($cg.version)"
        ) -Environment $envForInstall | Out-Null
    }
    $mainPkg = Get-Content $installedManifest -Raw | ConvertFrom-Json
    $platformManifest = Join-Path $prefix "node_modules/@colbymchenry/codegraph-$platformKey/package.json"
    if (-not (Test-Path $platformManifest)) {
        throw "Platform package manifest not found after install: $platformManifest"
    }
    $platformPkg = Get-Content $platformManifest -Raw | ConvertFrom-Json
    if ($mainPkg.version -ne $cg.version -or $platformPkg.version -ne $cg.version) {
        throw "Installed CodeGraph versions ($($mainPkg.version) / $($platformPkg.version)) do not match pin $($cg.version)."
    }
    $shim = Join-Path $prefix 'node_modules/.bin/codegraph.cmd'
    if (-not $IsWindows) {
        $shim = Join-Path $prefix 'node_modules/.bin/codegraph'
    }
    $manifest.steps += [ordered]@{
        name            = 'tools-codegraph-verify'
        exitCode        = 0
        mainVersion     = $mainPkg.version
        platformVersion = $platformPkg.version
        platform        = $platformKey
        shim            = $shim
        shimExists      = (Test-Path $shim)
    }
}

function Prepare-SharpDeps {
    $dotnet = (Get-Command 'dotnet').Source
    $outDir = Join-Path $toolsDir 'sharpdeps-cli'
    $artifacts = Join-Path $cacheDir 'sharpdeps-artifacts'
    $project = Join-Path $RepoRoot 'analyzer/src/SharpDeps.Cli/SharpDeps.Cli.csproj'
    Invoke-Logged -Name 'tools-sharpdeps-build' -FilePath $dotnet -Arguments @(
        'build', $project, '-c', 'Release', '--artifacts-path', $artifacts, '-o', $outDir
    ) | Out-Null
    $dll = Join-Path $outDir 'SharpDeps.Cli.dll'
    if (-not (Test-Path $dll)) {
        throw "SharpDeps.Cli.dll not found in build output: $outDir"
    }
    $sourceCommit = (git -C $RepoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not resolve the SharpDeps source commit.'
    }
    $sdk = (& $dotnet --version).Trim()
    $info = [ordered]@{
        sourceCommit = $sourceCommit
        sdkVersion   = $sdk
        dll          = $dll
        builtAtUtc   = [DateTime]::UtcNow.ToString('o')
    }
    $info | ConvertTo-Json | Set-Content -Path (Join-Path $outDir 'build-info.json') -Encoding utf8
    $manifest.steps += [ordered]@{ name = 'tools-sharpdeps-verify'; exitCode = 0; sourceCommit = $sourceCommit; sdkVersion = $sdk; dll = $dll }
}

try {
    if ($Stage -eq 'Inputs' -or $Stage -eq 'All') {
        foreach ($spec in $pins.corpora) {
            if ($Corpus -ne 'All' -and $spec.id -ne $Corpus.ToLowerInvariant()) {
                continue
            }
            Prepare-Corpus $spec
        }
    }
    if ($Stage -eq 'Tools' -or $Stage -eq 'All') {
        Prepare-CodeGraph
        Prepare-SharpDeps
    }
    $manifest.success = $true
}
finally {
    Save-Manifest
}
