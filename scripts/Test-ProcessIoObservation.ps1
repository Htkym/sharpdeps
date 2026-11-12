# Synthetic values only; no OS counter read or product process is started.
#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ProcessIoObservation.psm1') -Force
function Assert($Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}
$created = [datetime]::new(2026, 11, 8, 0, 0, 0, [DateTimeKind]::Utc)
$record = [pscustomobject]@{
    CreationDate = $created; ReadOperationCount = 0UL; WriteOperationCount = 10UL; OtherOperationCount = 20UL; ReadTransferCount = 30UL; WriteTransferCount = 40UL; OtherTransferCount = 50UL; CommandLine = 'PRIVATE_SECRET'
}
$reader = {
    param($targetId) if ($targetId -ne 123) {
        throw 'PRIVATE_ID'
    }; $record
}
$first = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent {
    $true
} -Supported $true -Reader $reader
Assert ($first.readOperations.value -eq 0 -and $first.readOperations.reason -eq 'available') 'Observed zero must remain zero.'
$precision = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks ($created.Ticks + 9) -IdentityCurrent {
    $true
} -Supported $true -Reader $reader
Assert ($precision.writeOperations.value -eq 10) 'Compare CIM creation time at microsecond precision, preserving a valid sub-microsecond FILETIME remainder.'
$identity = @{
    reads = 0; checks = 0; current = $true
}
$guard = {
    $identity.checks++; $identity.current
}
$replacingReader = {
    param($targetId) $identity.reads++; $identity.current = $false; $record
}
$lost = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent $guard -Supported $true -Reader $replacingReader
Assert ($lost.writeOperations.reason -eq 'target-identity-changed' -and $identity.checks -eq 2 -and $identity.reads -eq 1) 'Recheck the retained target identity after querying; exit/reuse must discard counters.'
$identity.reads = 0
$alreadyLost = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent $guard -Supported $true -Reader $replacingReader
Assert ($alreadyLost.writeOperations.reason -eq 'target-identity-changed' -and $identity.reads -eq 0) 'A failed pre-query identity guard must not query any PID.'
$state = New-ObservationIoState
$sample1 = Add-ObservationIoSample $state $first 0
Assert ($null -eq $sample1.delta.writeTransferBytes.value -and $sample1.delta.writeTransferBytes.reason -eq 'no-previous-io-sample') 'Lifetime absolute counters must not become attachment deltas.'
$missing = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent {
    $true
} -Supported $true -Reader {
    throw 'PRIVATE_FAILURE'
}
$null = Add-ObservationIoSample $state $missing 5
$record.WriteTransferCount = 100UL
$second = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent {
    $true
} -Supported $true -Reader $reader
$sample2 = Add-ObservationIoSample $state $second 10
Assert ($sample2.delta.writeTransferBytes.value -eq 60 -and $sample2.deltaSamplingStartIntervalMilliseconds.writeTransferBytes.value -eq 10) 'Retain exact counter delta and its sampling-start reference interval across missing samples.'
$summary = Get-ObservationIoSummary $state
Assert ($summary.scope -eq 'explicit-process-only' -and $summary.delta.writeTransferBytes.value -eq 60) 'Never attribute process I/O to database or WAL.'
$changed = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks ($created.Ticks + 10) -IdentityCurrent {
    $true
} -Supported $true -Reader $reader
Assert ($null -eq $changed.writeOperations.value -and $changed.writeOperations.reason -eq 'target-identity-changed') 'Reject reused identity, without relaxing to a time window.'
$unsupported = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent {
    $true
} -Supported $false -Reader {
    throw 'must-not-call'
}
Assert ($unsupported.readOperations.reason -eq 'platform-unsupported') 'Unsupported collection must not query.'
$record.ReadOperationCount = $null
$partial = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent {
    $true
} -Supported $true -Reader $reader
Assert ($null -eq $partial.readOperations.value -and $partial.writeOperations.value -eq 10) 'One missing counter must not erase other available fields.'
$record.WriteTransferCount = 1UL
$regressed = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent {
    $true
} -Supported $true -Reader $reader
$null = Add-ObservationIoSample $state $regressed 20
Assert ((Get-ObservationIoSummary $state).delta.writeTransferBytes.reason -eq 'counter-regressed') 'Regressions must not be summed or hidden.'
$largeState = New-ObservationIoState
$record.WriteTransferCount = [ulong]::Parse('18446744073709551610')
$large1 = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent {
    $true
} -Supported $true -Reader $reader
$null = Add-ObservationIoSample $largeState $large1 0
$record.WriteTransferCount = [ulong]::Parse('18446744073709551611')
$large2 = Get-ObservationIoCounters -TargetProcessId 123 -StartTicks $created.Ticks -IdentityCurrent {
    $true
} -Supported $true -Reader $reader
$exact = Add-ObservationIoSample $largeState $large2 1
Assert ($exact.delta.writeTransferBytes.value -eq [decimal]1) 'UInt64 counters must not lose low bits through floating-point conversion.'
Import-Module (Join-Path $PSScriptRoot 'ProcessObservation.psm1') -Force
$outerState = New-ObservationState
$outerProcess = @{
    status = 'running'; workingSetBytes = (New-ObservationMetric 1); osLifetimePeakWorkingSetBytes = (New-ObservationMetric 2)
    processWideCpuAbsoluteMilliseconds = (New-ObservationMetric 3); processWideIoCounters = $first
}
$noFile = New-ObservationMetric $null 'not-configured'
$null = Add-ObservationSample $outerState $outerProcess $noFile $noFile 0 '2026-11-08T00:00:00Z'
$outerProcess.processWideIoCounters = $second
$outerSample = Add-ObservationSample $outerState $outerProcess $noFile $noFile 10 '2026-11-08T00:00:01Z'
$outerSummary = Get-ObservationSummary $outerState
Assert ($outerSample.processWideIo.delta.writeTransferBytes.value -eq 60 -and $outerSummary.processWideIo.delta.writeTransferBytes.value -eq 60) 'Integrate counters into outer samples and summary without a provider read.'
Assert ($null -eq $outerSummary.databaseBytesWritten.value -and $null -eq $outerSummary.walBytesWritten.value) 'Process counters must leave file-specific write metrics unknown.'
$json = ConvertTo-Json -InputObject @($sample1, $sample2, $missing, $changed, $unsupported, $partial, $exact) -Depth 12 -Compress
Assert ($json -notmatch 'PRIVATE|CommandLine|"(?:targetProcessId|processId|database[^"]*|wal[^"]*)"|:\s*123(?=[,}])') 'Export only allowlisted process metrics and fixed reasons.'
$records = [Collections.Generic.List[object]]::new()
$control = @{ elapsed = 0.0; reads = 0; starts = @(); finishes = @() }
$delays = @(1000, 0, 250, 0)
$delayedReader = {
    $index = $control.reads
    $control.starts += $control.elapsed
    $control.elapsed += $delays[$index]
    $control.finishes += $control.elapsed
    $control.reads++
    $io = New-ObservationIoUnavailable 'counter-unavailable'
    if ($index -ne 1) {
        foreach ($name in Get-ObservationIoNames) {
            $io[$name] = New-ObservationMetric ([ulong](10 * ($index + 1)))
        }
    }
    @{
        process = @{
            status = 'running'; workingSetBytes = (New-ObservationMetric 1); osLifetimePeakWorkingSetBytes = (New-ObservationMetric 2)
            processWideCpuAbsoluteMilliseconds = (New-ObservationMetric $(if ($index -eq 1) { $null } else { 10 * ($index + 1) }))
            processWideIoCounters = $io
        }
        database = $noFile; wal = $noFile
    }
}
Invoke-ObservationLoop -ReadSample $delayedReader -WriteRecord { param($record) $records.Add($record) } -DurationMilliseconds 1600 -IntervalMilliseconds 100 -Elapsed { $control.elapsed } -Wait { param($ms) $control.elapsed += $ms }
$samples = @($records | Where-Object type -EQ 'sample')
$delayedSummary = $records[$records.Count - 1].summary
Assert ($samples.Count -eq 4 -and ($control.starts -join ',') -ceq '0,1100,1200,1550' -and ($control.finishes -join ',') -ceq '1000,1100,1450,1550') 'Exercise unequal collection delays without a provider read or real sleep.'
Assert ($samples[2].elapsedMilliseconds -eq 1200 -and $samples[2].processWideIo.deltaSamplingStartIntervalMilliseconds.writeTransferBytes.value -eq 1200) 'The reference interval spans available loop sampling starts, not read completion or a provider timestamp.'
Assert ($samples[3].processWideIo.deltaSamplingStartIntervalMilliseconds.writeTransferBytes.value -eq 350 -and $delayedSummary.processWideIo.deltaSamplingStartIntervalMilliseconds.writeTransferBytes.value -eq 1550) 'Adjacent and summary reference intervals must keep collection delays and missing-sample gaps.'
Assert ($samples[2].cpuDeltaSamplingStartIntervalMilliseconds.value -eq 1200 -and $delayedSummary.cpuObservedSamplingStartIntervalMilliseconds.value -eq 1550) 'CPU reference intervals use the same monotonic sampling starts, not UTC.'
Assert ($null -eq $samples[1].processWideIo.deltaSamplingStartIntervalMilliseconds.writeTransferBytes.value -and $samples[1].processWideIo.deltaSamplingStartIntervalMilliseconds.writeTransferBytes.reason -eq 'counter-unavailable') 'Missing counters must not invent a reference interval.'
foreach ($record in @($samples.processWideIo) + @($delayedSummary.processWideIo)) {
    Assert ($record.intervalBasis -ceq 'observer-loop-sampling-start-monotonic' -and $null -eq $record.counterTimestamp.value -and $record.counterTimestamp.reason -ceq 'not-provided-by-source') 'Every I/O delta and summary must name the reference basis and leave provider counter time unknown.'
    Assert (-not $record.ContainsKey('deltaWallMilliseconds')) 'Do not label client sampling-start references as actual counter wall windows.'
}
Assert ($samples[0].timestampBasis -ceq 'observer-loop-sampling-start' -and $null -eq $samples[0].processWideIo.deltaSamplingStartIntervalMilliseconds.writeTransferBytes.value) 'Retain sampling-start labels and the unknown initial interval.'
$utcState = New-ObservationState
$null = Add-ObservationSample $utcState $outerProcess $noFile $noFile 0 '2026-11-08T01:00:00.0000000+00:00'
$backwardsUtc = Add-ObservationSample $utcState $outerProcess $noFile $noFile 25 '2026-11-08T00:00:00.0000000+00:00'
Assert ($backwardsUtc.cpuDeltaSamplingStartIntervalMilliseconds.value -eq 25 -and $backwardsUtc.processWideIo.deltaSamplingStartIntervalMilliseconds.writeTransferBytes.value -eq 25) 'A backward UTC adjustment must not alter monotonic reference intervals.'
Write-Output 'Synthetic process-I/O checks passed.'
