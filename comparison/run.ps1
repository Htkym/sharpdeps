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

    Resume mode (-ResumeFrom <preserved run directory>, requires -PlanPath)
    continues a saved run without re-running setup or initial queries. The
    preserved summary and its captures are only read, never modified; all
    new output goes to a fresh -OutputDir (which must be empty or
    non-existent and different from the preserved directory). Call counts,
    follow-up counts, the total cap and stop states are restored from the
    preserved summary, and only records whose stopReason is
    'evidence-review-required' or 'no-followup-evidence' and which the plan
    names with a candidate for that tool are continued. Terminal states
    (setup-failed, error, timeout, collection-incomplete, unsupported,
    sufficiency, cap, total-cap) are never resumed.
    Corpus pins, questions, initial texts, tool builds and budgets are
    validated against the preserved run; missing captures, count or total
    mismatches, and condition mismatches reject the resume. A re-resume
    from a resume output never re-executes an already-executed call: used
    follow-up selectors are seeded from the preserved records.
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [ValidateSet('small', 'large')]
    [string]$CorpusId = 'small',
    [string]$CorpusSpecPath = '',
    [string]$QuestionsPath = '',
    [string]$PlanPath = '',
    [string]$ResumeFrom = '',
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

if ($ResumeFrom -and $SkipSetup) {
    throw "Cannot combine -ResumeFrom with -SkipSetup; resume never runs setup."
}
if ($ResumeFrom -and $ListOnly) {
    throw "Cannot combine -ResumeFrom with -ListOnly."
}
if ($ResumeFrom -and -not $PlanPath) {
    throw "Resume requires -PlanPath."
}
if ($ResumeFrom) {
    $ResumeFrom = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResumeFrom)
    if (-not (Test-Path (Join-Path $ResumeFrom 'run-summary.json'))) {
        throw "No run-summary.json under resume directory '$ResumeFrom'."
    }
}

function Get-ResumeFileHash {
    param([Parameter(Mandatory)] [string]$Path)
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash
}

function Initialize-ResumeState {
    param(
        [Parameter(Mandatory)] [string]$ResumeDir,
        [Parameter(Mandatory)] $Spec,
        [Parameter(Mandatory)] $CorpusQuestions,
        [Parameter(Mandatory)] $Plan,
        [Parameter(Mandatory)] $Pins,
        [Parameter(Mandatory)] $BuildInfo,
        [Parameter(Mandatory)] [string]$OutputDir,
        [Parameter(Mandatory)] [int]$MaxTotalCalls,
        [Parameter(Mandatory)] [int]$MaxFollowups,
        [Parameter(Mandatory)] [int]$QueryTimeoutSeconds,
        [Parameter(Mandatory)] [int]$SetupTimeoutSeconds,
        [Parameter(Mandatory)] [string]$PlanPath
    )
    $summaryPath = Join-Path $ResumeDir 'run-summary.json'
    $originalSha = Get-ResumeFileHash $summaryPath
    $preserved = Get-Content $summaryPath -Raw | ConvertFrom-Json
    if ($preserved.schemaVersion -ne 1) {
        throw "Unsupported preserved summary schemaVersion '$($preserved.schemaVersion)'; expected 1."
    }
    foreach ($field in 'corpus','environment','setup','questions','totalQueryCalls') {
        if (-not $preserved.PSObject.Properties[$field]) {
            throw "Preserved summary is missing '$field'."
        }
    }
    foreach ($field in 'id','repository','commit','sharpdepsTarget','configuration') {
        if ($preserved.corpus.$field -ne $Spec.$field) {
            throw "Preserved corpus $field '$($preserved.corpus.$field)' does not match current spec '$($Spec.$field)'."
        }
    }
    if ($preserved.environment.codegraphVersion -ne $Pins.codegraph.version) {
        throw "Preserved CodeGraph version '$($preserved.environment.codegraphVersion)' does not match current '$($Pins.codegraph.version)'."
    }
    if ($preserved.environment.sharpdepsSourceCommit -ne $BuildInfo.sourceCommit) {
        throw "Preserved SharpDeps build '$($preserved.environment.sharpdepsSourceCommit)' does not match current '$($BuildInfo.sourceCommit)'."
    }
    if ($preserved.environment.sharpdepsSdk -ne $BuildInfo.sdkVersion) {
        throw "Preserved SharpDeps SDK '$($preserved.environment.sharpdepsSdk)' does not match current '$($BuildInfo.sdkVersion)'."
    }
    if ($preserved.PSObject.Properties['resume'] -and $preserved.resume.PSObject.Properties['budgets']) {
        $b = $preserved.resume.budgets
        if ($b.maxTotalCalls -ne $MaxTotalCalls -or $b.maxFollowups -ne $MaxFollowups -or $b.queryTimeoutSeconds -ne $QueryTimeoutSeconds -or $b.setupTimeoutSeconds -ne $SetupTimeoutSeconds) {
            throw "Resume budgets do not match the preserved run's budgets."
        }
    }
    elseif ($MaxTotalCalls -ne 36 -or $MaxFollowups -ne 2 -or $QueryTimeoutSeconds -ne 300 -or $SetupTimeoutSeconds -ne 1800) {
        throw "Resuming an original summary requires the protocol budgets (36 total calls, 2 follow-ups, 300s query, 1800s setup)."
    }
    $currentIds = @($CorpusQuestions.questions | ForEach-Object { $_.id })
    $preservedIds = @($preserved.questions | ForEach-Object { $_.id })
    if (($currentIds -join ',') -ne ($preservedIds -join ',')) {
        throw "Preserved question ids '$($preservedIds -join ',')' do not match current '$($currentIds -join ',')'."
    }
    $questionById = @{}
    foreach ($q in $CorpusQuestions.questions) { $questionById[$q.id] = $q }
    $setupFailed = @{ codegraph = $false; sharpdeps = $false }
    foreach ($tool in 'codegraph','sharpdeps') {
        foreach ($rec in $preserved.setup.$tool) {
            if ($rec.timedOut -or $rec.exitCode -ne 0 -or -not $rec.collectionComplete) {
                $setupFailed[$tool] = $true
            }
        }
    }
    $stdoutCache = @{}
    $totalCalls = 0
    foreach ($q in $preserved.questions) {
        foreach ($tool in 'sharpdeps','codegraph') {
            $rec = $q.tools.$tool
            if (-not $rec) { throw "Preserved summary is missing record for '$($q.id)/$tool'." }
            $calls = @($rec.calls)
            $totalCalls += $calls.Count
            if ($rec.stopReason -eq 'setup-failed') {
                if ($calls.Count -ne 0 -or $rec.totalBytes -ne 0 -or $rec.totalUnits -ne 0) {
                    throw "Preserved setup-failed record '$($q.id)/$tool' carries calls or totals."
                }
                continue
            }
            if ($calls.Count -eq 0) { throw "Preserved record '$($q.id)/$tool' has no calls but stopReason '$($rec.stopReason)'." }
            if (($calls.Count - 1) -gt $MaxFollowups) { throw "Preserved record '$($q.id)/$tool' exceeds the follow-up cap." }
            $expectedBytes = 0
            $expectedUnits = 0
            $priorEvidence = ''
            for ($i = 0; $i -lt $calls.Count; $i++) {
                $call = $calls[$i]
                if ($call.index -ne $i) { throw "Preserved call index mismatch in '$($q.id)/$tool'." }
                $expectedKind = if ($i -eq 0) { 'initial' } else { 'followup' }
                if ($call.kind -ne $expectedKind) { throw "Preserved call kind mismatch in '$($q.id)/$tool' at index $i." }
                if ($i -eq 0) {
                    $initialText = if ($tool -eq 'sharpdeps') { $questionById[$q.id].initialNativeArguments.sharpdepsTerm } else { $questionById[$q.id].initialNativeArguments.codegraphExplore }
                    if ($call.text -ne $initialText) { throw "Preserved initial text in '$($q.id)/$tool' does not match the current questions file." }
                }
                else {
                    if (-not $priorEvidence.Contains($call.text)) { throw "Preserved follow-up text in '$($q.id)/$tool' at index $i does not appear in prior evidence." }
                }
                foreach ($pathField in 'stdoutPath','stderrPath') {
                    $stored = $call.$pathField
                    if (-not $stored) { throw "Preserved call in '$($q.id)/$tool' is missing $pathField." }
                    $leaf = Split-Path $stored -Leaf
                    if (-not (Test-Path (Join-Path $ResumeDir $leaf))) { throw "Preserved capture '$leaf' for '$($q.id)/$tool' is missing." }
                    $parent = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath((Split-Path $stored -Parent))
                    if ($parent -ne $ResumeDir) { throw "Preserved capture path '$stored' does not live beside the preserved summary." }
                }
                $text = [System.IO.File]::ReadAllText((Join-Path $ResumeDir (Split-Path $call.stdoutPath -Leaf)), [System.Text.Encoding]::UTF8)
                $stdoutCache["$($q.id)|$tool|$i"] = $text
                $isLast = ($i -eq ($calls.Count - 1))
                # Totals are added only for calls that pass every loop check.
                # A terminating timeout/collection-incomplete/error call never
                # contributed; an unsupported call did (its totals are added
                # before the unsupported check), as did every passing call.
                $terminatedWithoutTotals = $isLast -and ($rec.stopReason -in 'timeout','collection-incomplete','error')
                if (-not $terminatedWithoutTotals) {
                    if ($call.exitCode -eq 0 -and $call.collectionComplete -and -not $call.timedOut) {
                        $expectedBytes += $call.stdoutBytes
                        $expectedUnits += $call.stdoutUtf16Units
                        $priorEvidence += $text
                    }
                }
            }
            if ($rec.totalBytes -ne $expectedBytes -or $rec.totalUnits -ne $expectedUnits) {
                throw "Preserved totals mismatch in '$($q.id)/$tool'."
            }
        }
    }
    if ($totalCalls -ne $preserved.totalQueryCalls) { throw "Preserved totalQueryCalls '$($preserved.totalQueryCalls)' does not match the sum of calls '$totalCalls'." }
    if ($preserved.totalQueryCalls -gt $MaxTotalCalls) { throw "Preserved totalQueryCalls exceeds the total cap." }
    $continued = @{}
    foreach ($qid in @($Plan.Keys)) {
        $entry = $Plan[$qid]
        $pq = @($preserved.questions | Where-Object { $_.id -eq $qid })
        if ($pq.Count -ne 1) { throw "Plan question '$qid' is not in the preserved summary." }
        if (-not $entry.PSObject.Properties['followupCandidates'] -or -not $entry.followupCandidates) { continue }
        foreach ($cand in $entry.followupCandidates) {
            $tool = $cand.tool
            if ($tool -notin 'sharpdeps','codegraph') { throw "Plan candidate tool '$tool' is not a comparison tool." }
            $rec = $pq[0].tools.$tool
            if (-not $rec) { throw "Preserved summary is missing record for '$qid/$tool'." }
            if ($setupFailed[$tool]) { throw "Cannot resume '$qid/$tool': its setup failed in the preserved run." }
            if ($rec.stopReason -notin 'evidence-review-required','no-followup-evidence') { throw "Cannot resume '$qid/$tool': stopReason is '$($rec.stopReason)', not a resumable state ('evidence-review-required' or 'no-followup-evidence')." }
            $continued["$qid|$tool"] = $true
        }
    }
    $copied = @()
    foreach ($file in Get-ChildItem $ResumeDir -File | Where-Object { $_.Name -match '\.(stdout\.txt|stderr\.txt|result\.json)$' }) {
        Copy-Item $file.FullName (Join-Path $OutputDir $file.Name) -Force
        $copied += $file.Name
    }
    foreach ($name in $copied) {
        $a = (Get-FileHash (Join-Path $ResumeDir $name) -Algorithm SHA256).Hash
        $b = (Get-FileHash (Join-Path $OutputDir $name) -Algorithm SHA256).Hash
        if ($a -ne $b) { throw "Copied capture '$name' does not match the preserved file." }
    }
    if ((Get-ResumeFileHash $summaryPath) -ne $originalSha) {
        throw "Preserved summary changed during resume preparation; refusing to continue."
    }
    $carried = @{}
    $seeds = @{}
    foreach ($q in $preserved.questions) {
        foreach ($tool in 'sharpdeps','codegraph') {
            $key = "$($q.id)|$tool"
            $rec = $q.tools.$tool
            $copy = [ordered]@{ calls = @(); stopReason = $rec.stopReason; totalBytes = $rec.totalBytes; totalUnits = $rec.totalUnits }
            foreach ($call in @($rec.calls)) {
                $cc = [ordered]@{}
                foreach ($prop in $call.PSObject.Properties) { $cc[$prop.Name] = $prop.Value }
                $cc.stdoutPath = Join-Path $OutputDir (Split-Path $call.stdoutPath -Leaf)
                $cc.stderrPath = Join-Path $OutputDir (Split-Path $call.stderrPath -Leaf)
                $copy.calls += $cc
            }
            $carried[$key] = $copy
            if ($continued.ContainsKey($key)) {
                $evidence = ''
                for ($i = 0; $i -lt @($rec.calls).Count; $i++) { $evidence += $stdoutCache["$key|$i"] }
                $used = @(@($rec.calls) | Where-Object { $_.kind -eq 'followup' } | ForEach-Object { $_.text })
                $seedCalls = @() + $copy.calls
                $seed = [ordered]@{ calls = $seedCalls; stopReason = $null; totalBytes = $rec.totalBytes; totalUnits = $rec.totalUnits }
                $seeds[$key] = @{ Record = $seed; Evidence = $evidence; UsedCandidates = $used; FollowupsUsed = $used.Count; CallIndex = @($rec.calls).Count }
            }
        }
    }
    $planSha = (Get-FileHash $PlanPath -Algorithm SHA256).Hash
    $originalSha2 = $originalSha
    if ($preserved.PSObject.Properties['resume'] -and $preserved.resume.PSObject.Properties['originalSummarySha256']) {
        $originalSha2 = $preserved.resume.originalSummarySha256
    }
    $summaryBase = [ordered]@{
        schemaVersion = 1
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        corpus = $preserved.corpus
        environment = $preserved.environment
        setup = $preserved.setup
        questions = @()
        totalQueryCalls = $preserved.totalQueryCalls
        resume = [ordered]@{
            resumedFromDirectory = $ResumeDir
            resumedFromSummarySha256 = $originalSha
            originalSummarySha256 = $originalSha2
            planFileName = Split-Path $PlanPath -Leaf
            planSha256 = $planSha
            budgets = [ordered]@{ maxTotalCalls = $MaxTotalCalls; maxFollowups = $MaxFollowups; queryTimeoutSeconds = $QueryTimeoutSeconds; setupTimeoutSeconds = $SetupTimeoutSeconds }
            resumedAtUtc = [DateTime]::UtcNow.ToString('o')
        }
    }
    return @{ Summary = $summaryBase; SetupFailed = $setupFailed; Continued = $continued; Seeds = $seeds; Carried = $carried }
}

if (-not $OutputDir) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDir = Join-Path $Root "o/run-$($spec.id)-$stamp"
}
$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
if ($ResumeFrom) {
    if ($OutputDir -eq $ResumeFrom) {
        throw "ResumeFrom and OutputDir must differ; original outputs are never modified."
    }
    if ((Test-Path $OutputDir) -and ((Get-ChildItem $OutputDir -Force | Measure-Object).Count -gt 0)) {
        throw "Resume OutputDir must be empty or non-existent; refusing to mix outputs."
    }
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

$resumeState = $null
if ($ResumeFrom) {
    $resumeState = Initialize-ResumeState -ResumeDir $ResumeFrom -Spec $spec -CorpusQuestions $corpusQuestions -Plan $plan -Pins $pins -BuildInfo $buildInfo -OutputDir $OutputDir -MaxTotalCalls $MaxTotalCalls -MaxFollowups $MaxFollowups -QueryTimeoutSeconds $QueryTimeoutSeconds -SetupTimeoutSeconds $SetupTimeoutSeconds -PlanPath $PlanPath
    $summary = $resumeState.Summary
    $setupFailed = $resumeState.SetupFailed
}
else {
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
}

if (-not $SkipSetup -and -not $ResumeFrom) {
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
        [Parameter(Mandatory)] [string]$Tool,
        $SeedRecord = $null,
        [string]$SeedEvidence = '',
        [string[]]$SeedUsedCandidates = @(),
        [int]$SeedFollowupsUsed = 0,
        [int]$SeedCallIndex = 0
    )
    if ($SeedRecord) {
        $record = $SeedRecord
        $accumulated = $SeedEvidence
        $usedCandidates = @($SeedUsedCandidates)
        $followupsUsed = $SeedFollowupsUsed
        $callIndex = $SeedCallIndex
    }
    else {
        $record = [ordered]@{ calls = @(); stopReason = $null; totalBytes = 0; totalUnits = 0 }
        if ($setupFailed[$Tool]) {
            $record.stopReason = 'setup-failed'
            return $record
        }
        $accumulated = ''
        $usedCandidates = @()
        $followupsUsed = 0
        $callIndex = 0
    }
    $guidance = Get-QuestionGuidance $Question $Tool
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
            # A seeded follow-up count already at the cap must stop before
            # any further call; without this, a re-resume could execute one
            # call past the cap (the end-of-loop check only fires after a
            # call). In a fresh run this branch is unreachable: the
            # end-of-loop cap check breaks before the next iteration.
            if ($followupsUsed -ge $MaxFollowups) {
                $record.stopReason = 'cap'
                break
            }
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
        if ($ResumeFrom) {
            $key = "$($question.id)|$tool"
            if ($resumeState.Continued.ContainsKey($key)) {
                $seed = $resumeState.Seeds[$key]
                $qRecord.tools[$tool] = Invoke-ToolQuestion -Question $question -Tool $tool -SeedRecord $seed.Record -SeedEvidence $seed.Evidence -SeedUsedCandidates $seed.UsedCandidates -SeedFollowupsUsed $seed.FollowupsUsed -SeedCallIndex $seed.CallIndex
            }
            else {
                $qRecord.tools[$tool] = $resumeState.Carried[$key]
            }
        }
        else {
            $qRecord.tools[$tool] = Invoke-ToolQuestion $question $tool
        }
    }
    $summary.questions += $qRecord
}

$summary.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
$summaryPath = Join-Path $OutputDir 'run-summary.json'
$summary | ConvertTo-Json -Depth 10 | Set-Content -Path $summaryPath -Encoding utf8
Stop-ComparisonProcesses
Write-Output $summaryPath
