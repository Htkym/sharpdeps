# Synthetic checks only: no product process, build, restore or corpus measurement.
#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ProcessObservation.psm1') -Force
function Assert($Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try {
        & $Action | Out-Null
    } catch {
        $threw = $true
    }
    Assert $threw $Message
}
function Assert-Overlap([scriptblock]$Action, [string]$Message) {
    $reason = $null
    try {
        & $Action | Out-Null
    } catch {
        $reason = (Get-ObservationFailure $_.Exception).Message
    }
    Assert ($reason -ceq 'overlapping-output') $Message
}
function Sample($Cpu, $Rss = 10, $Peak = 100, $Status = 'running') {
    @{
        status = $Status
        workingSetBytes = (New-ObservationMetric $Rss $(if ($null -eq $Rss) {
            'counter-unavailable'
        } else {
            'available'
        }))
        osLifetimePeakWorkingSetBytes = (New-ObservationMetric $Peak $(if ($null -eq $Peak) {
            'counter-unavailable'
        } else {
            'available'
        }))
        processWideCpuAbsoluteMilliseconds = (New-ObservationMetric $Cpu $(if ($null -eq $Cpu) {
            'counter-unavailable'
        } else {
            'available'
        }))
    }
}
function Line($Value) {
    ConvertTo-Json -InputObject $Value -Depth 10 -Compress
}
$wrappedMissing = [Reflection.TargetInvocationException]::new([IO.FileNotFoundException]::new('PRIVATE_MISSING'))
Assert ((Get-ObservationFailure $wrappedMissing) -is [IO.FileNotFoundException]) 'Unwrap only transparent invocation wrappers for missing-file classification.'
$deniedWithInner = [UnauthorizedAccessException]::new('PRIVATE_DENIED', [IO.FileNotFoundException]::new('PRIVATE_INNER'))
Assert ((Get-ObservationFailure $deniedWithInner) -is [UnauthorizedAccessException]) 'An unrelated outer failure must not become missing solely because of its inner exception.'
$utc = '2026-11-08T00:00:00.0000000+00:00'
$none = New-ObservationMetric $null 'not-configured'
$state = New-ObservationState
$first = Add-ObservationSample $state (Sample 900 20 400) (New-ObservationMetric 100) (New-ObservationMetric 50) 0 $utc
$missing = Add-ObservationSample $state (Sample $null $null $null) $none $none 5 $utc
$second = Add-ObservationSample $state (Sample 940 30 400) (New-ObservationMetric 90) (New-ObservationMetric 0) 10 $utc
$summary = Get-ObservationSummary $state
Assert ($first.processWideCpuAbsoluteMilliseconds.value -eq 900 -and $null -eq $first.processWideCpuDeltaMilliseconds.value) 'CPU baseline must remain absolute, with no invented initial delta.'
Assert ($null -eq $missing.workingSetBytes.value -and $missing.workingSetBytes.reason -eq 'counter-unavailable') 'Unavailable gauges must stay null.'
Assert ($second.processWideCpuDeltaMilliseconds.value -eq 40 -and $second.cpuDeltaSamplingStartIntervalMilliseconds.value -eq 10) 'CPU delta must retain its sampling-start reference interval across missing samples.'
Assert ($summary.intervalSampledMaxWorkingSetBytes.value -eq 30 -and $summary.observedOsLifetimePeakWorkingSetBytes.value -eq 400) 'Sampled interval max must not become OS lifetime peak.'
Assert ($summary.databaseObservedSizeChangeBytes.value -eq -10 -and $summary.walObservedSizeChangeBytes.value -eq -50) 'Gauge changes can be negative.'
Assert ($null -eq $summary.databaseBytesWritten.value -and $summary.databaseBytesWritten.reason -eq 'not-measured') 'File lengths cannot establish bytes written.'
$null = Add-ObservationSample $state (Sample 2) $none $none 20 $utc
Assert ((Get-ObservationSummary $state).processWideCpuDeltaMilliseconds.reason -eq 'counter-regressed') 'CPU counter regressions must never produce negative interval CPU.'

$fake = [pscustomobject]@{
    HasExited = $false; StartTime = [datetime]'2026-11-08T00:00:00Z'; WorkingSet64 = 5L; PeakWorkingSet64 = 20L; TotalProcessorTime = [timespan]::FromMilliseconds(17)
}
$fake | Add-Member -MemberType ScriptMethod -Name Refresh -Value {

}
$pidSample = Get-ObservationProcess $fake $fake.StartTime.ToUniversalTime().Ticks
Assert ($pidSample.status -eq 'running' -and $pidSample.processWideCpuAbsoluteMilliseconds.value -eq 17) 'Only read supplied process counters.'
$reused = Get-ObservationProcess $fake ($fake.StartTime.ToUniversalTime().Ticks + 1)
Assert ($reused.status -eq 'reused' -and $null -eq $reused.workingSetBytes.value) 'PID identity changes must stop attribution.'
$fake.HasExited = $true
Assert ((Get-ObservationProcess $fake 0).status -eq 'exited') 'Exit must not trigger reattachment.'

$runId = '1234567890abcdef1234567890abcdef'
$tail = New-ObservationTail '' 'phase'
$start = ConvertTo-ObservationEvent $tail (Line @{
    runId = $runId; sequence = 1; payload = @{
        type = 'run.start'; schema = 'sharpdeps-phase-metrics/1'; utc = $utc; operation = 'watch-cycle'; source = 'PRIVATE_SOURCE'; path = 'PRIVATE_PATH'
    }
}) $utc 0
Assert ($start.kind -eq 'run-start' -and $start.operation -eq 'watch-cycle') 'Recognize watch cycle evidence.'
$phase = ConvertTo-ObservationEvent $tail (Line @{
    runId = $runId; sequence = 2; payload = @{
        type = 'phase'; phase = 'watch.idle-wait'; startedMilliseconds = 10; inclusiveMilliseconds = 50
    }
}) $utc 100
Assert ($phase.kind -eq 'span' -and $phase.inclusiveMilliseconds -eq 50 -and $phase.phase -eq 'watch.idle-wait') 'Preserve phase timing without summing nested spans.'
$decision = ConvertTo-ObservationEvent $tail (Line @{
    runId = $runId; sequence = 3; payload = @{
        type = 'decision'; stage = 'watch.reconciliation'; reason = 'full-resync'
    }
}) $utc 100
Assert ($decision.reason -eq 'full-resync' -and $null -eq $decision.utc -and $decision.timestampReason -eq 'not-emitted-by-producer') 'Observation time must not be presented as an unavailable producer timestamp.'
$end = ConvertTo-ObservationEvent $tail (Line @{
    runId = $runId; sequence = 4; payload = @{
        type = 'run.end'; outcome = 'cancelled'
    }
}) $utc 100
Assert ($end.outcome -eq 'cancelled') 'Retain failed/cancelled producer outcomes.'
Assert ((Line @($start, $phase, $decision, $end)) -notmatch 'PRIVATE') 'Unrecognized fields must not leak through projection.'
Assert-Throws {
    ConvertTo-ObservationEvent $tail (Line @{
        runId = $runId; sequence = 5; payload = @{
            type = 'run.end'; outcome = 'completed'
        }
    }) $utc 100
} 'Reject records after run end.'
$marker = New-ObservationTail '' 'marker'
$markerEvent = ConvertTo-ObservationEvent $marker (Line @{
    schema = 'sharpdeps-observer-marker/1'; sequence = 1; utc = $utc; kind = 'idle-start'; secret = 'PRIVATE_SECRET'
}) $utc 0
Assert ($markerEvent.kind -eq 'idle-start' -and (Line $markerEvent) -notmatch 'PRIVATE') 'Markers project only allowed fields.'
Assert-Throws {
    ConvertTo-ObservationEvent $marker (Line @{
        schema = 'sharpdeps-observer-marker/1'; sequence = 2; utc = $utc; kind = 'PRIVATE_KIND'
    }) $utc 0
} 'Arbitrary marker names must be rejected.'
$offsetTail = New-ObservationTail '' 'marker'
$offsetEvent = ConvertTo-ObservationEvent $offsetTail (Line @{
    schema = 'sharpdeps-observer-marker/1'; sequence = 1; utc = '2026-11-08T09:00:00.0000000+09:00'; kind = 'watch-start'
}) $utc 0
Assert ($offsetEvent.utc -ceq $utc) 'Preserve the original ISO timestamp token and normalize its explicit offset, independently of host timezone and JSON automatic date conversion.'
Assert-Throws {
    Get-ObservationUtc '{"utc":123}' $false
} 'Timestamp tokens must be strings, never inferred from numeric values.'
Assert-Throws {
    Get-ObservationUtc '{"utc":"11/08/2026 09:00:00"}' $false
} 'Do not replace exact ISO-O validation with a permissive culture parser.'
$setupTail = New-ObservationTail '' 'marker'
$setupSequence = 0
foreach ($kind in @('checkout-start', 'checkout-end', 'acquisition-start', 'acquisition-end', 'restore-start', 'restore-end')) {
    $setupSequence++
    $setupEvent = ConvertTo-ObservationEvent $setupTail (Line @{
        schema = 'sharpdeps-observer-marker/1'; sequence = $setupSequence; utc = $utc; kind = $kind; command = 'PRIVATE_COMMAND'; path = 'PRIVATE_PATH'
    }) $utc 0
    Assert ($setupEvent.source -ceq 'marker' -and $setupEvent.kind -ceq $kind -and (Line $setupEvent) -notmatch 'PRIVATE') 'C0 boundaries must remain explicit caller claims without setup commands or paths.'
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('sharpdeps-observer-test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temp
try {
    $db = Join-Path $temp 'index.sqlite'
    [IO.File]::WriteAllBytes($db, [byte[]]@(1, 2, 3))
    Assert ((Get-ObservationFileSize $db).value -eq 3) 'Read byte length metadata.'
    $localDevice = '\Device\HarddiskVolume7'
    $remoteDevice = '\Device\Mup\server\share'
    Assert ([SharpDeps.Observation.ObservationPaths]::CurrentLocalDevice($localDevice + "`0" + $remoteDevice + "`0`0") -ceq $localDevice) 'Only the first DOS-device mapping is current; historical mappings must not change it.'
    foreach ($mapping in @(($remoteDevice + "`0" + $localDevice + "`0`0"), '\??\D:\local-subst', '\Device\Unknown', '\Device\HarddiskVolume', '\Device\HarddiskVolume7suffix', ("`0" + $localDevice))) {
        Assert-Throws {
            [SharpDeps.Observation.ObservationPaths]::CurrentLocalDevice($mapping)
        } 'Unknown/remote current mappings cannot be rescued by historical local mappings.'
    }
    Assert ((Get-ObservationFileSize ($db + '-wal')).reason -eq 'file-absent') 'Missing WAL is unknown, not zero.'
    Assert ((Get-ObservationFileSize '').reason -eq 'not-configured') 'Unconfigured paths must be explicit.'
    $protected = @{
        toolPayloadRoots = @((Join-Path $temp 'protected-tool'))
        dotnetRoots = @((Join-Path $temp 'protected-dotnet'))
        runtimeRoots = @((Join-Path $temp 'protected-runtime'))
        sdkRoots = @((Join-Path $temp 'protected-sdk'))
        nugetRoots = @((Join-Path $temp 'protected-nuget'))
        packageRoots = @((Join-Path $temp 'protected-packages'))
        packageInputs = 'configured'
    }
    Assert-Throws {
        Assert-ObservationOutput (Join-Path $temp 'new') @()
    } 'Missing protected roots must fail before output creation.'
    Assert-Throws {
        Assert-ObservationOutput (Join-Path $temp 'new') @() @('unrelated-root')
    } 'An unrelated string cannot stand in for named protection roles.'
    $missingRole = $protected.Clone(); $missingRole.Remove('sdkRoots')
    Assert-Throws {
        Assert-ObservationOutput (Join-Path $temp 'new') @() $missingRole
    } 'Missing SDK protection cannot silently become an empty list.'
    $unknownRole = $protected.Clone(); $unknownRole.sdkRoots = @('unknown')
    Assert-Throws {
        Assert-ObservationOutput (Join-Path $temp 'new') @() $unknownRole
    } 'Unknown roots must fail closed.'
    $emptyRole = $protected.Clone(); $emptyRole.sdkRoots = @()
    Assert-Throws {
        Assert-ObservationOutput (Join-Path $temp 'new') @() $emptyRole
    } 'Required protection roles cannot be empty.'
    $noPackages = $protected.Clone(); $noPackages.packageInputs = 'none'; $noPackages.nugetRoots = @(); $noPackages.packageRoots = @()
    Assert (@(Get-ObservationProtectedRoots $noPackages).Count -eq 4) 'No package inputs requires an explicit none assertion and empty package lists.'
    $inconsistent = $protected.Clone(); $inconsistent.packageInputs = 'none'
    Assert-Throws {
        Get-ObservationProtectedRoots $inconsistent
    } 'An explicit none assertion must not coexist with package roots.'
    $manifest = Join-Path $temp 'caller-manifest.json'
    [IO.File]::WriteAllText($manifest, (Line @{
        protectedInputRoots = $protected; unrelated = 'PRIVATE_NOT_EXPORTED'
    }), [Text.UTF8Encoding]::new($false))
    Assert ((Read-ObservationInputManifest $manifest).packageInputs -eq 'configured') 'Read only the bounded manifest protection contract.'
    Assert-Throws {
        Assert-ObservationOutput $temp @() $protected
    } 'Never overwrite an existing output directory.'
    Assert-Throws {
        Assert-ObservationOutput (Join-Path $temp 'new') @($temp) $protected
    } 'Do not write observer results into the observed workspace tree.'
    Assert-Throws {
        Assert-ObservationOutput (Join-Path $temp 'new') @((Join-Path $temp 'new/source.jsonl')) $protected
    } 'Do not place observed inputs inside the output directory.'
    $fresh = Join-Path $temp 'fresh-output'
    Assert ((Assert-ObservationOutput $fresh @() $protected) -ceq (Get-ObservationCanonicalPath $fresh)) 'Disjoint complete guard roots must return the physical output namespace.'
    $identityLocalizer = {
        param($candidate)
        $candidate
    }
    $calls = @{ attributes = 0; canonical = 0; local = 0 }
    $countAttributes = {
        param($candidate)
        $calls.attributes++
        [IO.FileAttributes]::Directory
    }
    $countCanonical = {
        param($candidate)
        $calls.canonical++
        $candidate
    }
    $countLocal = {
        param($candidate)
        $calls.local++
        $candidate
    }
    foreach ($invalidPath in @('\\server.invalid\share\fixture', '//server.invalid/share/fixture', '\\?\UNC\server.invalid\share\fixture', '\\.\pipe\fixture', '\\?\GLOBALROOT\Device\HarddiskVolume1\fixture', 'C:\fixture\CON', 'C:\fixture\aux.txt', 'C:\fixture\CON .txt', 'C:\fixture\name.', 'C:\fixture\name ', 'C:\fixture\file:stream', 'C:relative')) {
        Assert-Throws {
            Assert-ObservationPath $invalidPath $countAttributes $countCanonical $countLocal
        } 'Reject unsupported path syntax before any locality, attribute or canonical callback.'
        Assert-Throws {
            Assert-ObservationOutput $fresh @($invalidPath) $protected $countAttributes $countCanonical $countLocal
        } 'Validate every observed input syntax before touching otherwise valid protection roots.'
        Assert-Throws {
            Assert-ObservationOutput $invalidPath @() $protected $countAttributes $countCanonical $countLocal
        } 'Validate output syntax before any I/O callback.'
        $invalidProtected = $protected.Clone(); $invalidProtected.sdkRoots = @($invalidPath)
        Assert-Throws {
            Assert-ObservationOutput $fresh @() $invalidProtected $countAttributes $countCanonical $countLocal
        } 'Validate all declared protection syntax before any I/O callback.'
    }
    Assert ($calls.attributes -eq 0 -and $calls.canonical -eq 0 -and $calls.local -eq 0) 'UNC/device/ambiguous syntax must cause zero callbacks; no network fixture is accessed.'
    Assert-Throws {
        Assert-ObservationPath 'Z:\fixture' $countAttributes $countCanonical {
            param($candidate)
            $calls.local++
            throw 'local-volume-unavailable'
        }
    } 'Refuse an injected remote/unknown drive before attributes or canonical file opens.'
    Assert ($calls.attributes -eq 0 -and $calls.canonical -eq 0 -and $calls.local -eq 1) 'Locality failure cannot fall through to file probes.'
    $visits = [Collections.Generic.List[string]]::new()
    $driveRoot = [IO.Path]::GetPathRoot($temp)
    $rootLink = {
        param($candidate)
        $visits.Add($candidate)
        [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint
    }
    Assert-Throws {
        Assert-ObservationPath (Join-Path $driveRoot 'never-probe\child') $rootLink $countCanonical $identityLocalizer
    } 'A linked root must reject before any child attribute access.'
    Assert ($visits.Count -eq 1 -and $visits[0] -ceq $driveRoot -and $calls.canonical -eq 0) 'Walk from root to leaf and stop at the first reparse ancestor.'
    $visits.Clear()
    $linkedParent = Join-Path $driveRoot 'mock-link'
    $parentLink = {
        param($candidate)
        $visits.Add($candidate)
        if ($candidate -ceq $linkedParent) {
            [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint
        } else {
            [IO.FileAttributes]::Directory
        }
    }
    Assert-Throws {
        Assert-ObservationPath (Join-Path $linkedParent 'never-probe\child') $parentLink $countCanonical $identityLocalizer
    } 'A linked parent must reject before probing its descendants.'
    Assert ($visits.Count -eq 2 -and $visits[0] -ceq $driveRoot -and $visits[1] -ceq $linkedParent -and $calls.canonical -eq 0) 'Do not probe through a known parent junction even to discover it later.'
    $aliasRoot = Join-Path $temp 'short-alias'
    $physicalRoot = Join-Path $temp 'canonical-tree'
    $ordinaryAttributes = {
        param($candidate)
        [IO.FileAttributes]::Directory
    }
    $canonicalizer = {
        param($candidate)
        if ($candidate.Equals($aliasRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $physicalRoot
        } elseif ($candidate.StartsWith($aliasRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            $physicalRoot + $candidate.Substring($aliasRoot.Length)
        } else {
            $candidate
        }
    }
    $aliasProtected = $protected.Clone(); $aliasProtected.sdkRoots = @($aliasRoot)
    Assert-Overlap {
        Assert-ObservationOutput (Join-Path $physicalRoot 'new-output') @() $aliasProtected $ordinaryAttributes $canonicalizer $identityLocalizer
    } 'Resolve a protected SDK alias into the same physical namespace before comparing.'
    $physicalProtected = $protected.Clone(); $physicalProtected.sdkRoots = @($physicalRoot)
    Assert-Overlap {
        Assert-ObservationOutput (Join-Path $aliasRoot 'new-output') @() $physicalProtected $ordinaryAttributes $canonicalizer $identityLocalizer
    } 'Resolve an output alias before rejecting output beneath a protected physical root.'
    $physicalProtected.sdkRoots = @((Join-Path $physicalRoot 'sdk-child'))
    Assert-Overlap {
        Assert-ObservationOutput $aliasRoot @() $physicalProtected $ordinaryAttributes $canonicalizer $identityLocalizer
    } 'Physical ancestor overlap must also reject an aliased output.'
    Assert-Throws {
        Assert-ObservationOutput $fresh @() $protected $ordinaryAttributes { param($candidate) throw 'PRIVATE_LOOKUP_FAILURE' } $identityLocalizer
    } 'Unknown physical identity must reject output before creation.'
    $driveProtected = $protected.Clone(); $driveProtected.sdkRoots = @([IO.Path]::GetPathRoot($temp))
    Assert-Overlap {
        Assert-ObservationOutput $fresh @() $driveProtected $ordinaryAttributes { param($candidate) $candidate } $identityLocalizer
    } 'A protected volume root must include descendants without duplicating its separator.'
    Assert (-not [IO.Directory]::Exists((Join-Path $physicalRoot 'new-output'))) 'Alias guard checks must not create output directories.'
    foreach ($role in @('toolPayloadRoots', 'dotnetRoots', 'runtimeRoots', 'sdkRoots', 'nugetRoots', 'packageRoots')) {
        $inside = $protected.Clone(); $inside[$role] = @((Join-Path $temp ('root-' + $role)))
        Assert-Throws {
            Assert-ObservationOutput (Join-Path $inside[$role][0] 'observation') @() $inside
        } 'Reject output beneath each protected role.'
        $ancestorOutput = Join-Path $temp ('output-' + $role)
        $outside = $protected.Clone(); $outside[$role] = @((Join-Path $ancestorOutput 'protected-input'))
        Assert-Throws {
            Assert-ObservationOutput $ancestorOutput @() $outside
        } 'Reject output ancestors containing any protected role.'
    }
    $alias = Join-Path $temp 'alias'
    $attributes = {
        param($candidate)
        if ($candidate -ceq $alias) {
            return [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint
        }
        [IO.FileAttributes]::Directory
    }
    Assert-Throws {
        Assert-ObservationOutput $fresh @((Join-Path $alias 'workspace')) $protected $attributes -Localizer $identityLocalizer
    } 'Reject workspace aliases through existing reparse ancestors.'
    Assert-Throws {
        Assert-ObservationOutput $fresh @((Join-Path $alias 'index.sqlite')) $protected $attributes -Localizer $identityLocalizer
    } 'Reject observed-input aliases through existing reparse ancestors.'
    $linkedProtected = $protected.Clone(); $linkedProtected.toolPayloadRoots = @((Join-Path $alias 'payload'))
    Assert-Throws {
        Assert-ObservationOutput $fresh @() $linkedProtected $attributes -Localizer $identityLocalizer
    } 'Reject protected-root aliases through existing reparse ancestors.'
    Assert-Throws {
        Assert-ObservationOutput (Join-Path $alias 'output') @() $protected $attributes -Localizer $identityLocalizer
    } 'Reject output aliases through existing reparse ancestors.'
    foreach ($operation in @('index.exclusive', 'index.update', 'index.watch-update')) {
        $operationTail = New-ObservationTail '' 'phase'
        $operationEvent = ConvertTo-ObservationEvent $operationTail (Line @{
            runId = $runId; sequence = 1; payload = @{
                type = 'run.start'; schema = 'sharpdeps-phase-metrics/1'; utc = $utc; operation = $operation
            }
        }) $utc 0
        Assert ($operationEvent.operation -ceq $operation) 'Keep exact fixed producer operation labels.'
    }
    $lateFile = Join-Path $temp 'late-marker.jsonl'
    $late = New-ObservationTail $lateFile 'marker'
    Assert (@(Read-ObservationTail $late $utc 0)[0].reason -eq 'input-absent' -and -not $late.failed) 'A later-created marker input must not be disabled.'
    [IO.File]::WriteAllText($lateFile, ((Line @{
        schema = 'sharpdeps-observer-marker/1'; sequence = 1; utc = $utc; kind = 'watch-start'
    }) + "`n"), [Text.UTF8Encoding]::new($false))
    Assert (@(Read-ObservationTail $late $utc 1)[0].kind -eq 'watch-start') 'Observe input created after attachment.'
    $file = Join-Path $temp 'phase.jsonl'
    $phaseLine = Line @{
        runId = $runId; sequence = 1; payload = @{
            type = 'run.start'; schema = 'sharpdeps-phase-metrics/1'; utc = $utc; operation = 'index.exclusive'
        }
    }
    [IO.File]::WriteAllText($file, $phaseLine, [Text.UTF8Encoding]::new($false))
    $live = New-ObservationTail $file 'phase'
    Assert (@(Read-ObservationTail $live $utc 0).Count -eq 0) 'Partial lines must wait for their newline.'
    [IO.File]::AppendAllText($file, "`n", [Text.UTF8Encoding]::new($false))
    Assert (@(Read-ObservationTail $live $utc 1).Count -eq 1) 'Process a completed append exactly once.'
    Assert (@(Read-ObservationTail $live $utc 2).Count -eq 0) 'Do not replay already observed records.'
    Assert (@(Read-ObservationTail $live $utc 3 $true)[0].reason -eq 'unfinished-input') 'An unclosed producer run is not success.'
    [IO.File]::AppendAllText($file, "{PRIVATE_BAD_JSON}`n", [Text.UTF8Encoding]::new($false))
    $bad = @(Read-ObservationTail $live $utc 4)
    Assert ($bad.Count -eq 1 -and $bad[0].reason -eq 'input-unavailable' -and (Line $bad[0]) -notmatch 'PRIVATE') 'Malformed input must become a fixed reason, never an exception payload.'
    $bounded = New-ObservationTail $file 'phase' @{
        bytes = 16MB; records = 0L
    }
    Assert (@(Read-ObservationTail $bounded $utc 0)[0].reason -eq 'input-unavailable') 'Bound combined diagnostic bytes before allocation.'
    $shrunk = New-ObservationTail $db 'phase'
    $shrunk.offset = 100
    Assert (@(Read-ObservationTail $shrunk $utc 0)[0].reason -eq 'input-unavailable') 'File truncation must never restart as a new run.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $root = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('sharpdeps-observer-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

$records = [Collections.Generic.List[object]]::new()
$control = @{
    elapsed = 0.0; reads = 0; cancel = $false
}
$reader = {
    $control.reads++; @{
        process = (Sample 30); database = $none; wal = $none
    }
}
$write = {
    param($record) $records.Add($record)
}
$elapsed = {
    $control.elapsed
}
$wait = {
    param($ms) $control.elapsed += $ms; $control.cancel = $true
}
$cancelled = {
    $control.cancel
}
Invoke-ObservationLoop -ReadSample $reader -WriteRecord $write -DurationMilliseconds 1000 -IntervalMilliseconds 100 -Elapsed $elapsed -Wait $wait -Cancelled $cancelled
Assert ($control.reads -eq 1 -and $records[$records.Count - 1].reason -eq 'observer-cancelled') 'Cancel only the observer and flush its final record.'
Assert ($null -eq $records[$records.Count - 1].productOutcome) 'Observer completion must not invent a product result.'
$records.Clear()
$control.cancel = $false; $control.elapsed = 0.0; $control.reads = 0
$wait = {
    param($ms) $control.elapsed += $ms
}
Invoke-ObservationLoop -ReadSample $reader -WriteRecord $write -DurationMilliseconds 200 -IntervalMilliseconds 100 -Elapsed $elapsed -Wait $wait
Assert ($control.reads -eq 2 -and $records[$records.Count - 1].reason -eq 'duration-elapsed') 'A duration limit must stop without controlling the target.'
$records.Clear()
Assert-Throws {
    Invoke-ObservationLoop -ReadSample {
        throw 'PRIVATE_FAILURE'
    } -WriteRecord $write -DurationMilliseconds 100 -Elapsed {
        0
    } -Wait {

    }
} 'Unexpected collection failure must propagate only to the separate observer.'
Assert ($records[$records.Count - 1].reason -eq 'collector-failed' -and (Line $records[$records.Count - 1]) -notmatch 'PRIVATE') 'Diagnostic failure must retain a sanitized final record.'
Write-Output 'Synthetic observer checks passed.'
