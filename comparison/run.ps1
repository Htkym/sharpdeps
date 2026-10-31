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
    A follow-up candidate is executed only when its required evidence string
    literally appears in that tool's accumulated stdout. The loop stops on
    sufficiency markers, the follow-up cap, an error exit, a timeout, an
    unsupported outcome, exhausted evidence-based candidates, or the global
    call cap. Errors, timeouts, caps and unsupported outcomes are recorded
    as results, not treated as runner failures.

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
    [switch]$SkipSetup
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-Query.ps1')

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
$corpusQuestions = $questionsDoc.corpora | Where-Object { $_.id -eq $spec.id }
if (-not $corpusQuestions) {
    # Fixture specs carry their own corpus id inside the questions file.
    $corpusQuestions = $questionsDoc.corpora | Select-Object -First 1
}
$plan = @{}
if ($PlanPath) {
    $planDoc = Get-Content $PlanPath -Raw | ConvertFrom-Json
    foreach ($prop in $planDoc.PSObject.Properties) {
        $plan[$prop.Name] = $prop.Value
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

if (-not $OutputDir) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDir = Join-Path $Root "o/run-$($spec.id)-$stamp"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

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
    if ($cgInit.timedOut -or $cgInit.exitCode -ne 0) {
        $setupFailed.codegraph = $true
    }
    $sdEnv = @{ DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false' }
    $restore = Invoke-ComparisonQuery -Tool 'sharpdeps' -FilePath $dotnet `
        -Arguments @('restore', (Join-Path $spec.copies.sharpdeps $spec.sharpdepsTarget),
            '--disable-parallel', '--nologo', "-p:Configuration=$($spec.configuration)") `
        -WorkingDirectory $spec.copies.sharpdeps -Environment $sdEnv `
        -TimeoutSeconds $SetupTimeoutSeconds -OutBase (Join-Path $OutputDir 'setup-sharpdeps-restore')
    $summary.setup.sharpdeps += $restore
    if ($restore.timedOut -or $restore.exitCode -ne 0) {
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
        if ($index.timedOut -or $index.exitCode -ne 0) {
            $setupFailed.sharpdeps = $true
        }
        else {
            $status = Invoke-ComparisonQuery -Tool 'sharpdeps' -FilePath $dotnet `
                -Arguments @('exec', $buildInfo.dll, 'status', '--root', $spec.copies.sharpdeps,
                    '--index', '.sharpdeps/comparison.sqlite', '--quiet') `
                -WorkingDirectory $spec.copies.sharpdeps `
                -TimeoutSeconds $QueryTimeoutSeconds -OutBase (Join-Path $OutputDir 'setup-sharpdeps-status')
            $summary.setup.sharpdeps += $status
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
                if ($accumulated.Contains($requires)) {
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
            durationMs = $result.durationMs
            stdoutBytes = $result.stdoutBytes
            stdoutUtf16Units = $result.stdoutUtf16Units
            stdoutPath = $result.stdoutPath
            stderrPath = $result.stderrPath
        }
        $record.totalBytes += $result.stdoutBytes
        $record.totalUnits += $result.stdoutUtf16Units
        if (Test-Path $result.stdoutPath) {
            $accumulated += [System.IO.File]::ReadAllText($result.stdoutPath, [System.Text.Encoding]::UTF8)
        }
        $callIndex++
        if ($result.timedOut) {
            $record.stopReason = 'timeout'
            break
        }
        if ($result.exitCode -ne 0) {
            $record.stopReason = 'error'
            break
        }
        $stderrText = ''
        if (Test-Path $result.stderrPath) {
            $stderrText = [System.IO.File]::ReadAllText($result.stderrPath, [System.Text.Encoding]::UTF8)
        }
        if ($accumulated -match '(?i)\bunsupported\b' -or $stderrText -match '(?i)\bunsupported\b') {
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
Write-Output $summaryPath
