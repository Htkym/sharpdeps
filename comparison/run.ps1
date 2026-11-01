<#
.SYNOPSIS
    Runs the fixed-question comparison protocol for one corpus and records
    setup costs separately from query results.

.DESCRIPTION
    Setup phase (never counted as query calls): SharpDeps restore + index on
    its isolated copy; CodeGraph init on its isolated copy, using the init
    arguments resolved by the preparation work (manual sync inside the
    scratch area only; never init --yes, never global state).

    Query phase: for each fixed question and each tool, one initial call in
    the tool's native syntax, then at most -MaxFollowups follow-up calls.
    A follow-up candidate is executed only when the selector it would send
    (its text) literally appears in that tool's accumulated stdout; a
    candidate's 'requires' string is an additional gate, never a
    substitute. The loop stops on sufficiency markers, the follow-up cap,
    an error exit, a timeout, an explicitly diagnosed unsupported outcome,
    exhausted evidence-based candidates, or the global call cap. Errors,
    timeouts, caps and unsupported outcomes are recorded as results, not
    treated as runner failures.

    Metrics per call come from Invoke-ComparisonQuery: non-TTY stdout as raw
    UTF-8 bytes and decoded UTF-16 code units, stderr kept separate.

    Evaluator data (expected facts, source paths) in the questions file is
    never passed to a tool and never copied into a corpus copy. Sufficiency
    markers and follow-up candidates come from the question entry itself or
    from an operator plan file (-PlanPath) whose entries must still satisfy
    the returned-evidence rule above.
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [ValidateSet('small', 'large')]
    [string]$CorpusId = 'small',
    [string]$CorpusSpecPath = '',
    [string]$QuestionsPath = '',
    [string]$PlanPath = '',
    [string]$OutputDir = '',
    [int]$MaxTotalCalls = 36,
    [int]$MaxFollowups = 2,
    [int]$QueryTimeoutSeconds = 300,
    [int]$SetupTimeoutSeconds = 1800,
    [string[]]$CodeGraphInitArguments = @('init'),
    [switch]$SkipSetup,
    [switch]$ListOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-Query.ps1')

# On any terminating error, clean up processes this run started.
trap {
    Stop-ComparisonProcesses
    break
}

if (-not $Root) {
    if ($env:SCG_ROOT) {
        $Root = $env:SCG_ROOT
    }
    elseif ($IsWindows -and (Test-Path 'D:\')) {
        $Root = 'D:\scg'
    }
    else {
        $Root = Join-Path (Split-Path $PSScriptRoot -Parent) '.scg'
    }
}
# Normalize to an absolute path before deriving or serializing any path;
# a relative Root would make the tools resolve corpus paths beneath the
# wrong working directory. Resolution is relative to the caller's
# PowerShell location and also handles a Root that does not exist yet.
$Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
$pins = Get-Content (Join-Path $PSScriptRoot 'pins.json') -Raw | ConvertFrom-Json

if ($CorpusSpecPath) {
    $spec = Get-Content $CorpusSpecPath -Raw | ConvertFrom-Json
}
else {
    $pin = $pins.corpora | Where-Object { $_.id -eq $CorpusId }
    if (-not $pin) {
        throw "Corpus '$CorpusId' not found in pins.json."
    }
    $spec = [pscustomobject]@{
        id            = $pin.id
        repository    = $pin.repository
        commit        = $pin.commit
        sharpdepsTarget = $pin.sharpdepsTarget
        configuration = $pin.configuration
        copies        = [pscustomobject]@{
            codegraph = Join-Path $Root "i/$($pin.id)-codegraph"
            sharpdeps = Join-Path $Root "i/$($pin.id)-sharpdeps"
        }
    }
}
if (-not $QuestionsPath) {
    $QuestionsPath = Join-Path $PSScriptRoot 'questions.json'
}
$questionsDoc = Get-Content $QuestionsPath -Raw | ConvertFrom-Json

function Resolve-CorpusQuestions {
    param(
        [Parameter(Mandatory)] $Spec,
        [Parameter(Mandatory)] $QuestionsDoc
    )
    # Strict pairing: a questions corpus belongs to a corpus spec only when
    # repository and commit both match (and the ids match when both sides
    # carry one). There is deliberately no fallback: picking another
    # corpus's questions would silently corrupt the comparison.
    $found = @()
    foreach ($entry in $QuestionsDoc.corpora) {
        if ($entry.repository -ne $Spec.repository) {
            continue
        }
        if ($entry.commit -ne $Spec.commit) {
            continue
        }
        if ($Spec.PSObject.Properties['id'] -and $entry.PSObject.Properties['id'] -and $entry.id -ne $Spec.id) {
            continue
        }
        $found += $entry
    }
    if ($found.Count -ne 1) {
        throw "Expected exactly one questions corpus for repository '$($Spec.repository)' at commit '$($Spec.commit)'; found $($found.Count). Refusing to substitute another corpus's questions."
    }
    return $found[0]
}
$corpusQuestions = Resolve-CorpusQuestions $spec $questionsDoc
$plan = @{}
if ($PlanPath) {
    $planDoc = Get-Content $PlanPath -Raw | ConvertFrom-Json
    foreach ($prop in $planDoc.PSObject.Properties) {
        $plan[$prop.Name] = $prop.Value
    }
}

if (-not $OutputDir) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDir = Join-Path $Root "o/run-$($spec.id)-$stamp"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if ($ListOnly) {
    $listSummary = [ordered]@{
        schemaVersion = 1
        listOnly      = $true
        corpus        = [ordered]@{
            id              = $spec.id
            repository      = $spec.repository
            commit          = $spec.commit
            sharpdepsTarget = $spec.sharpdepsTarget
            configuration   = $spec.configuration
        }
        questions     = @($corpusQuestions.questions | ForEach-Object { $_.id })
        recordedAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    $listPath = Join-Path $OutputDir 'run-summary.json'
    $listSummary | ConvertTo-Json -Depth 6 | Set-Content -Path $listPath -Encoding utf8
    Write-Output $listPath
    return
}

# Run-time validation: the recorded repository and commit come from the
# spec, so verify the actual copies still match them before setup indexes
# anything (and before queries when -SkipSetup skips setup). Copies that
# are Git checkouts are checked with the same validator preparation uses.
# An explicitly supplied spec whose copies are not Git checkouts is the
# documented non-Git smoke fixture and is exempt.
foreach ($copyProp in $spec.copies.PSObject.Properties) {
    $copyPath = $copyProp.Value
    if (Test-Path (Join-Path $copyPath '.git')) {
        Assert-CopyContent $copyPath $spec.commit
    }
    elseif (-not $CorpusSpecPath) {
        throw "Corpus copy $copyPath is not a Git checkout. Run prepare.ps1 first."
    }
}

$toolsDir = Join-Path $Root 't'
# The .bin shim is a batch file on Windows, which a shell-less process
# launch cannot execute; drive the package's own Node entry point instead.
$cgShimJs = Join-Path $toolsDir 'codegraph/node_modules/@colbymchenry/codegraph/npm-shim.js'
$cgFile = $null
$cgPrefixArgs = @()
if (Test-Path $cgShimJs) {
    $cgFile = (Get-Command 'node').Source
    $cgPrefixArgs = @($cgShimJs)
}
else {
    $cgFile = Join-Path $toolsDir 'codegraph/node_modules/.bin/codegraph'
    if ($IsWindows) {
        $cgFile = "$cgFile.cmd"
    }
}
if (-not (Test-Path $cgFile)) {
    throw "CodeGraph entry point not found under $toolsDir\codegraph. Run prepare.ps1 first."
}
$buildInfoPath = Join-Path $toolsDir 'sharpdeps-cli/build-info.json'
if (-not (Test-Path $buildInfoPath)) {
    throw "SharpDeps build info not found: $buildInfoPath. Run prepare.ps1 first."
}
$buildInfo = Get-Content $buildInfoPath -Raw | ConvertFrom-Json
$dotnet = (Get-Command 'dotnet').Source

$cgEnv = @{}
foreach ($prop in $pins.codegraph.processEnvironment.PSObject.Properties) {
    $cgEnv[$prop.Name] = $prop.Value
}

$summary = [ordered]@{
    schemaVersion = 1
    startedAtUtc  = [DateTime]::UtcNow.ToString('o')
    corpus        = [ordered]@{
        id              = $spec.id
        repository      = $spec.repository
        commit          = $spec.commit
        sharpdepsTarget = $spec.sharpdepsTarget
        configuration   = $spec.configuration
        copies          = $spec.copies
    }
    environment   = [ordered]@{
        os                    = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        dotnetVersion         = (& $dotnet --version).Trim()
        codegraphVersion      = $pins.codegraph.version
        sharpdepsSourceCommit = $buildInfo.sourceCommit
        sharpdepsSdk          = $buildInfo.sdkVersion
    }
    setup         = [ordered]@{ codegraph = @(); sharpdeps = @() }
    questions     = @()
    totalQueryCalls = 0
}

$setupFailed = @{ codegraph = $false; sharpdeps = $false }

if (-not $SkipSetup) {
    $cgInit = Invoke-ComparisonQuery -Tool 'codegraph' -FilePath $cgFile `
        -Arguments ($cgPrefixArgs + $CodeGraphInitArguments + @($spec.copies.codegraph)) `
        -WorkingDirectory $Root -Environment $cgEnv `
        -TimeoutSeconds $SetupTimeoutSeconds -OutBase (Join-Path $OutputDir 'setup-codegraph-init')
    $summary.setup.codegraph += $cgInit
    if ($cgInit.timedOut -or $cgInit.exitCode -ne 0 -or -not $cgInit.collectionComplete) {
        $setupFailed.codegraph = $true
    }
    $sdEnv = @{ DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false' }
    $restore = Invoke-ComparisonQuery -Tool 'sharpdeps' -FilePath $dotnet `
        -Arguments @('restore', (Join-Path $spec.copies.sharpdeps $spec.sharpdepsTarget),
            '--disable-parallel', '--nologo', "-p:Configuration=$($spec.configuration)") `
        -WorkingDirectory $spec.copies.sharpdeps -Environment $sdEnv `
        -TimeoutSeconds $SetupTimeoutSeconds -OutBase (Join-Path $OutputDir 'setup-sharpdeps-restore')
    $summary.setup.sharpdeps += $restore
    if ($restore.timedOut -or $restore.exitCode -ne 0 -or -not $restore.collectionComplete) {
        $setupFailed.sharpdeps = $true
    }
    else {
        $index = Invoke-ComparisonQuery -Tool 'sharpdeps' -FilePath $dotnet `
            -Arguments @('exec', $buildInfo.dll, 'index', '--root', $spec.copies.sharpdeps,
                '--target', $spec.sharpdepsTarget, '--configuration', $spec.configuration,
                '--index', '.sharpdeps/comparison.sqlite', '--timeout-seconds', '180', '--trusted') `
            -WorkingDirectory $spec.copies.sharpdeps `
            -TimeoutSeconds $SetupTimeoutSeconds -OutBase (Join-Path $OutputDir 'setup-sharpdeps-index')
        $summary.setup.sharpdeps += $index
        if ($index.timedOut -or $index.exitCode -ne 0 -or -not $index.collectionComplete) {
            $setupFailed.sharpdeps = $true
        }
        else {
            $status = Invoke-ComparisonQuery -Tool 'sharpdeps' -FilePath $dotnet `
                -Arguments @('exec', $buildInfo.dll, 'status', '--root', $spec.copies.sharpdeps,
                    '--index', '.sharpdeps/comparison.sqlite', '--quiet') `
                -WorkingDirectory $spec.copies.sharpdeps `
                -TimeoutSeconds $QueryTimeoutSeconds -OutBase (Join-Path $OutputDir 'setup-sharpdeps-status')
            $summary.setup.sharpdeps += $status
            if ($status.timedOut -or $status.exitCode -ne 0 -or -not $status.collectionComplete) {
                $setupFailed.sharpdeps = $true
            }
        }
    }
}

function Get-QuestionGuidance {
    param([Parameter(Mandatory)] $Question, [Parameter(Mandatory)] [string]$Tool)
    $markers = @()
    $candidates = @()
    if ($Question.PSObject.Properties['sufficiencyMarkers'] -and $Question.sufficiencyMarkers) {
        $markers = @($Question.sufficiencyMarkers)
    }
    if ($Question.PSObject.Properties['followupCandidates'] -and $Question.followupCandidates) {
        $candidates = @($Question.followupCandidates | Where-Object { $_.tool -eq $Tool })
    }
    if ($plan.ContainsKey($Question.id)) {
        $entry = $plan[$Question.id]
        if ($entry.PSObject.Properties['sufficiencyMarkers'] -and $entry.sufficiencyMarkers) {
            $markers = @($entry.sufficiencyMarkers)
        }
        if ($entry.PSObject.Properties['followupCandidates'] -and $entry.followupCandidates) {
            $candidates = @($entry.followupCandidates | Where-Object { $_.tool -eq $Tool })
        }
    }
    return @{ markers = $markers; candidates = $candidates }
}

function Invoke-ToolQuestion {
    param(
        [Parameter(Mandatory)] $Question,
        [Parameter(Mandatory)] [string]$Tool
    )
    $record = [ordered]@{ calls = @(); stopReason = $null; totalBytes = 0; totalUnits = 0 }
    if ($setupFailed[$Tool]) {
        $record.stopReason = 'setup-failed'
        return $record
    }
    $guidance = Get-QuestionGuidance $Question $Tool
    $accumulated = ''
    $usedCandidates = @()
    $followupsUsed = 0
    $callIndex = 0
    while ($true) {
        if ($summary.totalQueryCalls -ge $MaxTotalCalls) {
            $record.stopReason = 'total-cap'
            break
        }
        $isInitial = ($callIndex -eq 0)
        $kind = 'initial'
        $text = $null
        if ($isInitial) {
            if ($Tool -eq 'sharpdeps') {
                $text = $Question.initialNativeArguments.sharpdepsTerm
            }
            else {
                $text = $Question.initialNativeArguments.codegraphExplore
            }
        }
        else {
            $kind = 'followup'
            $next = $null
            foreach ($candidate in $guidance.candidates) {
                if ($usedCandidates -contains $candidate.text) {
                    continue
                }
                $requires = $candidate.text
                if ($candidate.PSObject.Properties['requires'] -and $candidate.requires) {
                    $requires = $candidate.requires
                }
                # The selector actually sent (the candidate text) must
                # itself appear in this tool's returned evidence;
                # 'requires' is only an additional gate, never a
                # substitute for the selector being returned.
                if ($accumulated.Contains($candidate.text) -and $accumulated.Contains($requires)) {
                    $next = $candidate
                    break
                }
            }
            if (-not $next) {
                if ($guidance.markers.Count -eq 0 -and $guidance.candidates.Count -eq 0) {
                    $record.stopReason = 'evidence-review-required'
                }
                else {
                    $record.stopReason = 'no-followup-evidence'
                }
                break
            }
            $text = $next.text
            $usedCandidates += $text
            $followupsUsed++
        }
        $outBase = Join-Path $OutputDir ("q-{0}-{1}-{2}" -f $Question.id, $Tool, $callIndex)
        if ($Tool -eq 'sharpdeps') {
            $result = Invoke-ComparisonQuery -Tool 'sharpdeps' -FilePath $dotnet `
                -Arguments @('exec', $buildInfo.dll, 'explore', '--root', $spec.copies.sharpdeps,
                    '--index', '.sharpdeps/comparison.sqlite', '--trusted', '--term', $text,
                    '--format', 'agent', '--scope-policy', 'unique', '--freshness', 'allow-stale',
                    '--max-chars', '12000', '--max-bytes', '48000', '--max-milliseconds', '1000') `
                -WorkingDirectory $spec.copies.sharpdeps `
                -TimeoutSeconds $QueryTimeoutSeconds -OutBase $outBase
        }
        else {
            $result = Invoke-ComparisonQuery -Tool 'codegraph' -FilePath $cgFile `
                -Arguments ($cgPrefixArgs + @('explore', $text, '--path', $spec.copies.codegraph)) `
                -WorkingDirectory $Root -Environment $cgEnv `
                -TimeoutSeconds $QueryTimeoutSeconds -OutBase $outBase
        }
        $summary.totalQueryCalls++
        $record.calls += [ordered]@{
            index      = $callIndex
            kind       = $kind
            text       = $text
            exitCode   = $result.exitCode
            timedOut   = $result.timedOut
            collectionComplete = $result.collectionComplete
            durationMs = $result.durationMs
            stdoutBytes = $result.stdoutBytes
            stdoutUtf16Units = $result.stdoutUtf16Units
            stdoutPath = $result.stdoutPath
            stderrPath = $result.stderrPath
        }
        $callText = ''
        if (Test-Path $result.stdoutPath) {
            $callText = [System.IO.File]::ReadAllText($result.stdoutPath, [System.Text.Encoding]::UTF8)
        }
        $callIndex++
        if ($result.timedOut) {
            $record.stopReason = 'timeout'
            break
        }
        if (-not $result.collectionComplete) {
            # The captured output may be partial (a descendant held the
            # pipes past the grace period). It is kept for diagnosis but
            # never counts as a valid result or as sufficiency evidence.
            $record.stopReason = 'collection-incomplete'
            break
        }
        if ($result.exitCode -ne 0) {
            $record.stopReason = 'error'
            break
        }
        # Only a complete, successful collection counts toward the totals
        # and may serve as evidence for sufficiency or follow-up gating.
        $record.totalBytes += $result.stdoutBytes
        $record.totalUnits += $result.stdoutUtf16Units
        $accumulated += $callText
        $stderrText = ''
        if (Test-Path $result.stderrPath) {
            $stderrText = [System.IO.File]::ReadAllText($result.stderrPath, [System.Text.Encoding]::UTF8)
        }
        if (Test-ExplicitUnsupported $accumulated $stderrText) {
            $record.stopReason = 'unsupported'
            break
        }
        if ($guidance.markers.Count -gt 0) {
            $allFound = $true
            foreach ($marker in $guidance.markers) {
                if (-not $accumulated.Contains($marker)) {
                    $allFound = $false
                    break
                }
            }
            if ($allFound) {
                $record.stopReason = 'sufficiency'
                break
            }
        }
        if ($followupsUsed -ge $MaxFollowups) {
            $record.stopReason = 'cap'
            break
        }
    }
    if (-not $record.stopReason) {
        $record.stopReason = 'cap'
    }
    return $record
}

foreach ($question in $corpusQuestions.questions) {
    $qRecord = [ordered]@{ id = $question.id; tools = [ordered]@{} }
    foreach ($tool in 'sharpdeps', 'codegraph') {
        $qRecord.tools[$tool] = Invoke-ToolQuestion $question $tool
    }
    $summary.questions += $qRecord
}

$summary.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
$summaryPath = Join-Path $OutputDir 'run-summary.json'
$summary | ConvertTo-Json -Depth 10 | Set-Content -Path $summaryPath -Encoding utf8
Stop-ComparisonProcesses
Write-Output $summaryPath
