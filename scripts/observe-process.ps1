# Attach-only diagnostics. Run separately from the product command.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$TargetProcessId,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$WorkspaceRoot,
    [Parameter(Mandatory)][string]$InputManifestPath,
    [Parameter(Mandatory)][ValidateRange(1, 604800)][double]$DurationSeconds,
    [ValidateRange(100, 60000)][int]$IntervalMilliseconds = 1000,
    [string]$DatabasePath,
    [string]$PhaseDirectory,
    [string]$MarkerPath,
    [string]$StopFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ProcessObservation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ProcessIoObservation.psm1')
$writer = $null
$target = $null
try {
    $walPath = if ($DatabasePath) {
        $DatabasePath + '-wal'
    } else {
        ''
    }
    $paths = @($WorkspaceRoot, $DatabasePath, $walPath, $PhaseDirectory, $MarkerPath, $StopFile, $InputManifestPath, $OutputDirectory)
    foreach ($path in $paths) {
        if ($path) {
            $null = Get-ObservationSyntaxPath $path
        }
    }
    foreach ($path in $paths) {
        if ($path) {
            $null = Get-ObservationLocalPath $path
        }
    }
    $WorkspaceRoot = Assert-ObservationPath $WorkspaceRoot
    if (-not [IO.Directory]::Exists($WorkspaceRoot)) {
        throw 'invalid-workspace'
    }
    $protectedInputRoots = Read-ObservationInputManifest $InputManifestPath
    $output = Assert-ObservationOutput $OutputDirectory @($WorkspaceRoot, $DatabasePath, $walPath, $PhaseDirectory, $MarkerPath, $StopFile, $InputManifestPath) $protectedInputRoots
    $null = [IO.Directory]::CreateDirectory($output)
    $stream = [IO.FileStream]::new(([IO.Path]::Combine($output, 'observations.jsonl')), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $write = {
        param($record) $writer.WriteLine((ConvertTo-Json -InputObject $record -Depth 12 -Compress))
    }
    & $write @{
        type = 'observer.start'; schema = 'sharpdeps-process-observation/1'; utc = [DateTimeOffset]::UtcNow.ToString('O')
        observerId = [Guid]::NewGuid().ToString('N'); sampleIntervalMilliseconds = $IntervalMilliseconds
        durationLimitMilliseconds = $DurationSeconds * 1000; processScope = 'explicit-target-only'
        phaseEvidence = $(if ($PhaseDirectory) {
            'configured'
        } else {
            'not-configured'
        })
        markerEvidence = $(if ($MarkerPath) {
            'configured'
        } else {
            'not-configured'
        })
        idleResyncInference = 'explicit-evidence-only'; databaseIo = 'metadata-length-only'
        processWideIo = 'win32-process-cim-cumulative-counters'
        sampleTimestampBasis = 'observer-loop-sampling-start'; elapsedClock = 'monotonic-stopwatch'; utcClock = 'observer-utc'
    }
    try {
        $target = [Diagnostics.Process]::GetProcessById($TargetProcessId)
        $startTicks = $target.StartTime.ToUniversalTime().Ticks
    } catch {
        & $write @{
            type = 'observer.end'; utc = [DateTimeOffset]::UtcNow.ToString('O'); reason = 'target-identity-unavailable'; value = $null; productOutcome = $null; productOutcomeReason = 'not-observed'
        }
        return
    }
    $tails = @{}
    $budget = @{
        bytes = 0L; records = 0L
    }
    $eventsState = @{
        directoryFailed = $false; directorySeen = $false; directoryMissingReported = $false
    }
    if ($MarkerPath) {
        $tails.marker = New-ObservationTail $MarkerPath 'marker' $budget
    }
    $readEvents = {
        param($utc, $elapsed, $final)
        if ($PhaseDirectory -and -not $eventsState.directoryFailed) {
            try {
                $physicalPhaseDirectory = Assert-ObservationPath $PhaseDirectory
                $files = @([IO.Directory]::EnumerateFiles($physicalPhaseDirectory, '*.jsonl', [IO.SearchOption]::TopDirectoryOnly) | Select-Object -First 4097)
                if ($files.Count -gt 4096) {
                    throw 'input-limit'
                }
                foreach ($file in $files) {
                    if ([IO.Path]::GetFileName($file) -cnotmatch '^[a-f0-9]{32}\.jsonl$') {
                        continue
                    }
                    $eventsState.directorySeen = $true
                    if (-not $tails.ContainsKey($file)) {
                        if ($tails.Count -ge 4097) {
                            throw 'input-limit'
                        }
                        $tails[$file] = New-ObservationTail $file 'phase' $budget
                    }
                }
            } catch {
                $failure = Get-ObservationFailure $_.Exception
                if ($failure -is [IO.DirectoryNotFoundException]) {
                    if (-not $eventsState.directoryMissingReported) {
                        $eventsState.directoryMissingReported = $true
                        @{ type = 'lifecycle-unavailable'; source = 'phase'; observedUtc = $utc; elapsedMilliseconds = $elapsed; value = $null; reason = 'input-absent' }
                    }
                } else {
                    $eventsState.directoryFailed = $true
                    @{ type = 'lifecycle-unavailable'; source = 'phase'; observedUtc = $utc; elapsedMilliseconds = $elapsed; value = $null; reason = 'input-unavailable' }
                }
            }
        }
        foreach ($tail in @($tails.Values)) {
            Read-ObservationTail $tail $utc $elapsed $final
        }
        if ($final -and $PhaseDirectory -and -not $eventsState.directorySeen -and -not $eventsState.directoryFailed) {
            @{
                type = 'lifecycle-unavailable'; source = 'phase'; observedUtc = $utc; elapsedMilliseconds = $elapsed; value = $null; reason = 'no-phase-evidence'
            }
        }
    }
    $readSample = {
        $process = Get-ObservationProcess $target $startTicks
        $process.processWideIoCounters = if ($process.status -eq 'running') {
            Get-ObservationIoCounters -TargetProcessId $TargetProcessId -StartTicks $startTicks -IdentityCurrent {
                $target.Refresh()
                -not $target.HasExited -and $target.StartTime.ToUniversalTime().Ticks -eq $startTicks
            }
        } else {
            New-ObservationIoUnavailable 'target-unavailable'
        }
        @{
            process = $process
            database = (Get-ObservationFileSize $DatabasePath)
            wal = (Get-ObservationFileSize $(if ($DatabasePath) {
                $DatabasePath + '-wal'
            } else {
                ''
            }))
        }
    }
    $cancelled = {
        $StopFile -and [IO.File]::Exists((Assert-ObservationPath $StopFile))
    }
    Invoke-ObservationLoop -ReadSample $readSample -WriteRecord $write -ReadEvents $readEvents -Cancelled $cancelled -DurationMilliseconds ($DurationSeconds * 1000) -IntervalMilliseconds $IntervalMilliseconds
} catch {
    # Never export exception messages, paths, source contents or environment values.
    [Console]::Error.WriteLine('SharpDeps observer failed; the target process was not changed.')
    exit 2
} finally {
    if ($null -ne $writer) {
        $writer.Dispose()
    }
    if ($null -ne $target) {
        $target.Dispose()
    }
}
