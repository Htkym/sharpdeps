Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ProcessIoObservation.psm1')

function Initialize-ObservationPaths {
    if (-not $IsWindows) {
        throw 'physical-path-unsupported'
    }
    if (-not ('SharpDeps.Observation.ObservationPaths' -as [type])) {
        $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ObservationPaths.cs'))
        $null = Add-Type -TypeDefinition $source -ErrorAction Stop -WarningAction SilentlyContinue
    }
}

function Get-ObservationCanonicalPath([string]$Path) {
    Initialize-ObservationPaths
    [SharpDeps.Observation.ObservationPaths]::Canonicalize($Path)
}

function Get-ObservationSyntaxPath([string]$Path) {
    # Pure syntax only. Never probe a UNC, device, relative or ambiguous path.
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^(?:[a-z]:[\\/]|\\\\\?\\[a-z]:\\|\\\\\?\\Volume\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}\\)') {
        throw 'unsupported-guard-path'
    }
    $rawRoot = [IO.Path]::GetPathRoot($Path)
    foreach ($part in $Path.Substring($rawRoot.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($part -cnotin @('.', '..') -and ($part.EndsWith('.') -or $part.EndsWith(' '))) {
            throw 'ambiguous-guard-path'
        }
    }
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    foreach ($part in $full.Substring($root.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($part.EndsWith('.') -or $part.EndsWith(' ') -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw 'ambiguous-guard-path'
        }
        $stem = $part.Split('.')[0].TrimEnd(' ').ToUpperInvariant()
        if ($stem -cin @('CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$', 'CLOCK$') -or $stem -cmatch '^(?:COM|LPT)[1-9¹²³]$') {
            throw 'device-guard-path'
        }
    }
    $full
}

function Get-ObservationLocalPath([string]$Path) {
    $full = Get-ObservationSyntaxPath $Path
    Initialize-ObservationPaths
    [SharpDeps.Observation.ObservationPaths]::LocalPath($full)
}

function Get-ObservationShortPath([string]$Path) {
    Initialize-ObservationPaths
    [SharpDeps.Observation.ObservationPaths]::ShortPath($Path)
}
function New-ObservationMetric($Value, [string]$Reason = 'available') {
    @{
        value = $Value; reason = $Reason
    }
}

function Get-ObservationFailure($Failure) {
    while ($null -ne $Failure.InnerException -and ($Failure -is [Management.Automation.RuntimeException] -or $Failure -is [Reflection.TargetInvocationException])) {
        $Failure = $Failure.InnerException
    }
    $Failure
}
function Get-ObservationFileSize([string]$Path) {
    if (-not $Path) {
        return New-ObservationMetric $null 'not-configured'
    }
    try {
        $Path = Assert-ObservationPath $Path
        # A direct metadata call preserves missing-file errors that property adapters can wrap.
        $attributes = [IO.File]::GetAttributes($Path)
        if ($attributes -band [IO.FileAttributes]::Directory) {
            return New-ObservationMetric $null 'metadata-unavailable'
        }
        $length = [IO.FileInfo]::new($Path).Length
        if ($null -eq $length -or $length -lt 0) {
            return New-ObservationMetric $null 'metadata-unavailable'
        }
        New-ObservationMetric $length
    } catch {
        $failure = Get-ObservationFailure $_.Exception

        $reason = if ($failure -is [IO.FileNotFoundException] -or $failure -is [IO.DirectoryNotFoundException]) {
            'file-absent'
        } else {
            'metadata-unavailable'
        }
        New-ObservationMetric $null $reason
    }
}
function Get-ObservationProcess($Process, [long]$StartTicks) {
    $unavailable = {
        param($status, $reason)
        @{
            status = $status
            workingSetBytes = (New-ObservationMetric $null $reason)
            osLifetimePeakWorkingSetBytes = (New-ObservationMetric $null $reason)
            processWideCpuAbsoluteMilliseconds = (New-ObservationMetric $null $reason)
        }
    }
    try {
        $Process.Refresh()
        if ($Process.HasExited) {
            return & $unavailable 'exited' 'target-exited'
        }
        if ($Process.StartTime.ToUniversalTime().Ticks -ne $StartTicks) {
            return & $unavailable 'reused' 'target-identity-changed'
        }
    } catch {
        return & $unavailable 'unavailable' 'process-unavailable'
    }
    $result = @{
        status = 'running'
    }
    foreach ($pair in @(
        @('workingSetBytes', 'WorkingSet64'),
        @('osLifetimePeakWorkingSetBytes', 'PeakWorkingSet64'),
        @('processWideCpuAbsoluteMilliseconds', 'TotalProcessorTime')
    )) {
        try {
            $value = $Process.($pair[1])
            if ($pair[1] -eq 'TotalProcessorTime') {
                $value = $value.TotalMilliseconds
            }
            if ($null -eq $value -or $value -lt 0 -or -not [double]::IsFinite([double]$value)) {
                $result[$pair[0]] = New-ObservationMetric $null 'counter-unavailable'
            } else {
                $result[$pair[0]] = New-ObservationMetric $value
            }
        } catch {
            $result[$pair[0]] = New-ObservationMetric $null 'counter-unavailable'
        }
    }
    $result
}

function New-ObservationState {
    @{
        io = (New-ObservationIoState); samples = 0L; firstCpu = $null; lastCpu = $null; lastCpuElapsed = $null
        firstCpuElapsed = $null; cpuRegressed = $false; sampledMax = $null; lifetimeMax = $null
        firstDb = $null; lastDb = $null; firstWal = $null; lastWal = $null; DbSamples = 0L; WalSamples = 0L
    }
}

function Add-ObservationSample($State, $ProcessSample, $Db, $Wal, [double]$Elapsed, [string]$Utc) {
    $delta = New-ObservationMetric $null 'no-previous-cpu-sample'
    $deltaSamplingStartInterval = New-ObservationMetric $null 'no-previous-cpu-sample'
    $cpu = $ProcessSample.processWideCpuAbsoluteMilliseconds.value
    if ($null -ne $cpu) {
        if ($null -eq $State.firstCpu) {
            $State.firstCpu = $cpu; $State.firstCpuElapsed = $Elapsed
        }
        if ($null -ne $State.lastCpu) {
            if ($cpu -ge $State.lastCpu) {
                $delta = New-ObservationMetric ($cpu - $State.lastCpu)
                $deltaSamplingStartInterval = New-ObservationMetric ($Elapsed - $State.lastCpuElapsed)
            } else {
                $State.cpuRegressed = $true
                $delta = New-ObservationMetric $null 'counter-regressed'
                $deltaSamplingStartInterval = New-ObservationMetric $null 'counter-regressed'
            }
        }
        $State.lastCpu = $cpu; $State.lastCpuElapsed = $Elapsed
    } else {
        $delta = New-ObservationMetric $null 'cpu-unavailable'; $deltaSamplingStartInterval = New-ObservationMetric $null 'cpu-unavailable'
    }
    foreach ($pair in @(@('sampledMax', 'workingSetBytes'), @('lifetimeMax', 'osLifetimePeakWorkingSetBytes'))) {
        $value = $ProcessSample.($pair[1]).value
        if ($null -ne $value -and ($null -eq $State[$pair[0]] -or $value -gt $State[$pair[0]])) {
            $State[$pair[0]] = $value
        }
    }
    foreach ($pair in @(@('Db', $Db), @('Wal', $Wal))) {
        if ($null -ne $pair[1].value) {
            if ($null -eq $State['first' + $pair[0]]) {
                $State['first' + $pair[0]] = $pair[1].value
            }
            $State['last' + $pair[0]] = $pair[1].value
            $State[$pair[0] + 'Samples']++
        }
    }
    $io = if ($ProcessSample.ContainsKey('processWideIoCounters')) {
        $ProcessSample.processWideIoCounters
    } else {
        New-ObservationIoUnavailable 'not-collected'
    }
    $ioSample = Add-ObservationIoSample $State.io $io $Elapsed
    $State.samples++
    @{
        type = 'sample'; utc = $Utc; elapsedMilliseconds = $Elapsed; timestampBasis = 'observer-loop-sampling-start'; processStatus = $ProcessSample.status
        workingSetBytes = $ProcessSample.workingSetBytes
        osLifetimePeakWorkingSetBytes = $ProcessSample.osLifetimePeakWorkingSetBytes
        processWideCpuAbsoluteMilliseconds = $ProcessSample.processWideCpuAbsoluteMilliseconds
        processWideCpuDeltaMilliseconds = $delta; cpuDeltaSamplingStartIntervalMilliseconds = $deltaSamplingStartInterval
        processWideIo = $ioSample
        databaseSizeBytes = $Db; walSizeBytes = $Wal
        databaseBytesWritten = (New-ObservationMetric $null 'not-measured')
        walBytesWritten = (New-ObservationMetric $null 'not-measured')
    }
}

function Get-ObservationSummary($State) {
    $summary = @{
        sampleCount = $State.samples; cpuIntervalBasis = 'observer-loop-sampling-start-monotonic'; processWideIo = (Get-ObservationIoSummary $State.io)
    }
    foreach ($pair in @(@('intervalSampledMaxWorkingSetBytes', 'sampledMax'), @('observedOsLifetimePeakWorkingSetBytes', 'lifetimeMax'))) {
        $value = $State[$pair[1]]
        $summary[$pair[0]] = if ($null -eq $value) {
            New-ObservationMetric $null 'no-valid-samples'
        } else {
            New-ObservationMetric $value
        }
    }
    $cpuReason = if ($State.cpuRegressed) {
        'counter-regressed'
    } elseif ($null -eq $State.firstCpu -or $State.lastCpuElapsed -eq $State.firstCpuElapsed) {
        'insufficient-cpu-samples'
    } else {
        'available'
    }
    $summary.processWideCpuDeltaMilliseconds = if ($cpuReason -eq 'available') {
        New-ObservationMetric ($State.lastCpu - $State.firstCpu)
    } else {
        New-ObservationMetric $null $cpuReason
    }
    $summary.cpuObservedSamplingStartIntervalMilliseconds = if ($cpuReason -eq 'available') {
        New-ObservationMetric ($State.lastCpuElapsed - $State.firstCpuElapsed)
    } else {
        New-ObservationMetric $null $cpuReason
    }
    foreach ($pair in @(@('database', 'Db'), @('wal', 'Wal'))) {
        $first = $State['first' + $pair[1]]; $last = $State['last' + $pair[1]]
        $summary[$pair[0] + 'ObservedSizeChangeBytes'] = if ($null -eq $first) {
            New-ObservationMetric $null 'no-valid-samples'
        } elseif ($State[$pair[1] + 'Samples'] -lt 2) {
            New-ObservationMetric $null 'insufficient-size-samples'
        } else {
            New-ObservationMetric ($last - $first)
        }
        $summary[$pair[0] + 'BytesWritten'] = New-ObservationMetric $null 'not-measured'
    }
    $summary
}

function New-ObservationTail([string]$Path, [string]$Source, $Budget = @{
    bytes = 0L; records = 0L
}) {
    @{
        path = $Path; source = $Source; budget = $Budget; offset = 0L; pending = [byte[]]@(); sequence = 0L; runId = $null; utc = $null; ended = $false; failed = $false; missingReported = $false
    }
}

function Get-ObservationUtc([string]$Line, [bool]$Payload) {
    # ConvertFrom-Json can auto-convert ISO strings to local DateTime values.
    # Read the original string token before applying the strict wire timestamp contract.
    $document = [Text.Json.JsonDocument]::Parse($Line)
    try {
        $container = $document.RootElement
        if ($Payload) {
            $container = $container.GetProperty('payload')
        }
        $token = $container.GetProperty('utc')
        if ($token.ValueKind -ne [Text.Json.JsonValueKind]::String) {
            throw 'invalid-utc'
        }
        [DateTimeOffset]::ParseExact($token.GetString(), 'O', [Globalization.CultureInfo]::InvariantCulture)
    } finally {
        $document.Dispose()
    }
}
function ConvertTo-ObservationEvent($Tail, [string]$Line, [string]$ObservedUtc, [double]$Elapsed) {
    $record = ConvertFrom-Json -InputObject $Line -AsHashtable -ErrorAction Stop
    if ($record -isnot [System.Collections.IDictionary]) {
        throw 'invalid-record'
    }
    $sequence = $record.sequence
    if ($sequence -isnot [long] -and $sequence -isnot [int]) {
        throw 'invalid-sequence'
    }
    if ($sequence -ne $Tail.sequence + 1 -or $Tail.ended) {
        throw 'invalid-sequence'
    }
    $Tail.sequence = $sequence
    $event = @{
        type = 'lifecycle'; source = $Tail.source; observedUtc = $ObservedUtc; elapsedMilliseconds = $Elapsed
    }
    if ($Tail.source -eq 'marker') {
        $kinds = @('checkout-start', 'checkout-end', 'acquisition-start', 'acquisition-end', 'restore-start', 'restore-end', 'watch-start', 'watch-stop', 'update-start', 'update-end', 'idle-start', 'idle-end', 'resync-start', 'resync-end', 'cancelled', 'failure')
        if ($record.schema -cne 'sharpdeps-observer-marker/1' -or $record.kind -isnot [string] -or $record.kind -cnotin $kinds) {
            throw 'invalid-marker'
        }
        $time = Get-ObservationUtc $Line $false
        $event.kind = $record.kind; $event.utc = $time.ToUniversalTime().ToString('O'); $event.sequence = $sequence
        return $event
    }
    $payload = $record.payload
    if ($payload -isnot [System.Collections.IDictionary] -or $record.runId -isnot [string] -or $record.runId -cnotmatch '^[a-f0-9]{32}$') {
        throw 'invalid-phase'
    }
    if ($sequence -eq 1) {
        if ($payload.type -cne 'run.start' -or $payload.schema -cne 'sharpdeps-phase-metrics/1') {
            throw 'invalid-start'
        }
        $Tail.runId = $record.runId
        $Tail.utc = Get-ObservationUtc $Line $true
    }
    if ($record.runId -cne $Tail.runId -or $null -eq $Tail.utc) {
        throw 'invalid-run'
    }
    $event.runId = $Tail.runId; $event.sequence = $sequence
    switch -CaseSensitive ($payload.type) {
        'run.start' {
            if ($sequence -ne 1) {
                throw 'duplicate-start'
            }
            $operations = @('index.exclusive', 'index.update', 'index.watch-update', 'watch-cycle', 'explore', 'query')
            $event.kind = 'run-start'; $event.utc = $Tail.utc.ToUniversalTime().ToString('O')
            $event.operation = if ($payload.operation -is [string] -and $payload.operation -cin $operations) {
                $payload.operation
            } else {
                'other'
            }
            return $event
        }
        'run.end' {
            if ($payload.outcome -isnot [string] -or $payload.outcome -cnotin @('completed', 'failed', 'cancelled', 'incomplete')) {
                throw 'invalid-outcome'
            }
            $Tail.ended = $true; $event.kind = 'run-end'; $event.outcome = $payload.outcome
            $event.utc = $null; $event.timestampReason = 'not-emitted-by-producer'
            return $event
        }
        'phase' {
            if ($payload.phase -isnot [string] -or $payload.phase -cnotin @('run', 'watch.settle', 'watch.input-scan', 'watch.update-wait', 'watch.idle-wait')) {
                return
            }
            foreach ($key in @('startedMilliseconds', 'inclusiveMilliseconds')) {
                if ($payload[$key] -isnot [ValueType] -or $payload[$key] -is [bool] -or -not [double]::IsFinite([double]$payload[$key]) -or $payload[$key] -lt 0) {
                    throw 'invalid-time'
                }
            }
            $event.kind = 'span'; $event.phase = $payload.phase
            $event.utc = $Tail.utc.AddMilliseconds($payload.startedMilliseconds).ToUniversalTime().ToString('O')
            $event.inclusiveMilliseconds = $payload.inclusiveMilliseconds
            return $event
        }
        'decision' {
            $allowed = @{
                'watch.reconciliation' = @('full-resync', 'notification')
                'watch.notifications' = @('available', 'unavailable')
                'watch.wakeup' = @('notification', 'periodic-resync')
            }
            if ($payload.stage -is [string] -and $allowed.ContainsKey($payload.stage) -and $payload.reason -is [string] -and $payload.reason -cin $allowed[$payload.stage]) {
                $event.kind = 'decision'; $event.stage = $payload.stage; $event.reason = $payload.reason
                $event.utc = $null; $event.timestampReason = 'not-emitted-by-producer'
                return $event
            }
        }
    }
}

function Read-ObservationTail($Tail, [string]$ObservedUtc, [double]$Elapsed, [bool]$Final = $false) {
    if ($Tail.failed) {
        return
    }
    $stream = $null
    try {
        $physicalPath = Assert-ObservationPath $Tail.path
        $stream = [IO.FileStream]::new($physicalPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        if ($stream.Length -lt $Tail.offset -or $stream.Length -gt 16MB) {
            throw 'input-limit'
        }
        $null = $stream.Seek($Tail.offset, [IO.SeekOrigin]::Begin)
        $remaining = [int]($stream.Length - $Tail.offset)
        if ($Tail.budget.bytes + $remaining -gt 16MB) {
            throw 'input-limit'
        }
        $Tail.budget.bytes += $remaining
        $buffer = [byte[]]::new($Tail.pending.Length + $remaining)
        [Array]::Copy($Tail.pending, $buffer, $Tail.pending.Length)
        $read = 0
        while ($read -lt $remaining) {
            $count = $stream.Read($buffer, $Tail.pending.Length + $read, $remaining - $read)
            if ($count -eq 0) {
                throw 'input-interrupted'
            }
            $read += $count
        }
        $Tail.offset += $read
        $start = 0
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        for ($i = 0; $i -lt $buffer.Length; $i++) {
            if ($buffer[$i] -ne 10) {
                continue
            }
            if ($i - $start -gt 64KB -or $Tail.budget.records -ge 65536) {
                throw 'input-limit'
            }
            $Tail.budget.records++
            $line = $utf8.GetString($buffer, $start, $i - $start).TrimEnd("`r")
            $start = $i + 1
            if (-not $line) {
                throw 'empty-record'
            }
            ConvertTo-ObservationEvent $Tail $line $ObservedUtc $Elapsed
        }
        $Tail.pending = [byte[]]::new($buffer.Length - $start)
        [Array]::Copy($buffer, $start, $Tail.pending, 0, $Tail.pending.Length)
        if ($Tail.pending.Length -gt 64KB) {
            throw 'input-limit'
        }
        if ($Final -and ($Tail.pending.Length -gt 0 -or ($Tail.source -eq 'phase' -and -not $Tail.ended))) {
            @{
                type = 'lifecycle-unavailable'; source = $Tail.source; observedUtc = $ObservedUtc; elapsedMilliseconds = $Elapsed; value = $null; reason = 'unfinished-input'
            }
        }
    } catch {
        $failure = Get-ObservationFailure $_.Exception
        if ($failure -is [IO.FileNotFoundException] -or $failure -is [IO.DirectoryNotFoundException]) {
            if (-not $Tail.missingReported) {
                $Tail.missingReported = $true
                @{ type = 'lifecycle-unavailable'; source = $Tail.source; observedUtc = $ObservedUtc; elapsedMilliseconds = $Elapsed; value = $null; reason = 'input-absent' }
            }
        } else {
            $Tail.failed = $true
            @{ type = 'lifecycle-unavailable'; source = $Tail.source; observedUtc = $ObservedUtc; elapsedMilliseconds = $Elapsed; value = $null; reason = 'input-unavailable' }
        }
    } finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Assert-ObservationPath {
    param(
        [string]$Path,
        [scriptblock]$ReadAttributes = {
            param($candidate)
            [IO.File]::GetAttributes($candidate)
        },
        [scriptblock]$Canonicalizer = {
            param($candidate)
            Get-ObservationCanonicalPath $candidate
        },
        [scriptblock]$Localizer = {
            param($candidate)
            Get-ObservationLocalPath $candidate
        }
    )
    $full = Get-ObservationSyntaxPath $Path
    $local = & $Localizer $full
    if ($local -isnot [string]) {
        throw 'local-volume-unavailable'
    }
    $full = Get-ObservationSyntaxPath $local
    $ancestors = [Collections.Generic.Stack[string]]::new()
    for ($candidate = $full; $candidate; $candidate = [IO.Path]::GetDirectoryName($candidate)) {
        $ancestors.Push($candidate)
    }
    foreach ($candidate in $ancestors) {
        try {
            $attributes = & $ReadAttributes $candidate
            if ($null -eq $attributes -or ($attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'linked-or-unknown-path'
            }
        } catch {
            $failure = Get-ObservationFailure $_.Exception
            if ($failure -isnot [IO.FileNotFoundException] -and $failure -isnot [IO.DirectoryNotFoundException]) {
                throw
            }
            # No child can pre-exist below a missing ancestor. Do not probe deeper.
            break
        }
    }
    $canonical = & $Canonicalizer $full
    if ($canonical -isnot [string] -or -not [IO.Path]::IsPathFullyQualified($canonical)) {
        throw 'physical-path-unavailable'
    }
    Get-ObservationSyntaxPath $canonical
}

function Get-ObservationProtectedRoots($Roles) {
    if ($Roles -isnot [Collections.IDictionary]) {
        throw 'protected-roots-required'
    }
    $required = @('toolPayloadRoots', 'dotnetRoots', 'runtimeRoots', 'sdkRoots', 'nugetRoots', 'packageRoots', 'packageInputs')
    foreach ($key in $required) {
        if (-not $Roles.Contains($key)) {
            throw 'protected-root-role-required'
        }
    }
    foreach ($key in $Roles.Keys) {
        if ($key -cnotin $required) {
            throw 'unknown-protected-root-role'
        }
    }
    if ($Roles.packageInputs -isnot [string] -or $Roles.packageInputs -cnotin @('configured', 'none')) {
        throw 'package-inputs-unknown'
    }
    $roots = [Collections.Generic.List[string]]::new()
    foreach ($role in $required[0..5]) {
        $values = $Roles[$role]
        if ($values -isnot [array]) {
            throw 'protected-root-list-required'
        }
        $packageRole = $role -cin @('nugetRoots', 'packageRoots')
        if ($packageRole -and $Roles.packageInputs -ceq 'none') {
            if ($values.Count -ne 0) {
                throw 'inconsistent-package-inputs'
            }
            continue
        }
        if ($values.Count -eq 0) {
            throw 'protected-root-role-empty'
        }
        foreach ($value in $values) {
            if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or -not [IO.Path]::IsPathFullyQualified($value)) {
                throw 'protected-root-unknown'
            }
            $roots.Add((Get-ObservationSyntaxPath $value))
        }
    }
    $roots.ToArray()
}

function Read-ObservationInputManifest([string]$Path) {
    $Path = Assert-ObservationPath $Path
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -gt 64KB) {
            throw 'manifest-limit'
        }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) {
                throw 'manifest-interrupted'
            }
            $offset += $read
        }
        if ($stream.Length -ne $bytes.Length) {
            throw 'manifest-changed'
        }
        $manifest = ConvertFrom-Json -InputObject ([Text.UTF8Encoding]::new($false, $true).GetString($bytes)) -AsHashtable -ErrorAction Stop
    } finally {
        $stream.Dispose()
    }
    if ($manifest -isnot [Collections.IDictionary] -or -not $manifest.Contains('protectedInputRoots')) {
        throw 'protected-roots-required'
    }
    $null = @(Get-ObservationProtectedRoots $manifest.protectedInputRoots)
    $manifest.protectedInputRoots
}

function Assert-ObservationOutput {
    param(
        [string]$Directory,
        [string[]]$Inputs,
        $ProtectedInputRoots,
        [scriptblock]$ReadAttributes = {
            param($candidate)
            [IO.File]::GetAttributes($candidate)
        },
        [scriptblock]$Canonicalizer = {
            param($candidate)
            Get-ObservationCanonicalPath $candidate
        },
        [scriptblock]$Localizer = {
            param($candidate)
            Get-ObservationLocalPath $candidate
        }
    )
    $roots = @(Get-ObservationProtectedRoots $ProtectedInputRoots)
    $inputPaths = [Collections.Generic.List[string]]::new()
    foreach ($inputPath in $Inputs) {
        if ($inputPath) {
            $inputPaths.Add((Get-ObservationSyntaxPath $inputPath))
        }
    }
    $Directory = Get-ObservationSyntaxPath $Directory
    # Finish syntax validation for every input before invoking any I/O callback.
    $guards = [Collections.Generic.List[string]]::new()
    foreach ($root in $roots) {
        $guards.Add((Assert-ObservationPath $root $ReadAttributes $Canonicalizer $Localizer))
    }
    foreach ($inputPath in $inputPaths) {
        $guards.Add((Assert-ObservationPath $inputPath $ReadAttributes $Canonicalizer $Localizer))
    }
    $output = Assert-ObservationPath $Directory $ReadAttributes $Canonicalizer $Localizer
    if ([IO.Directory]::Exists($output) -or [IO.File]::Exists($output)) {
        throw 'output-exists'
    }
    $separator = [string][IO.Path]::DirectorySeparatorChar
    $outputPrefix = if ($output.EndsWith($separator)) {
        $output
    } else {
        $output + $separator
    }
    foreach ($guard in $guards) {
        $guardPrefix = if ($guard.EndsWith($separator)) {
            $guard
        } else {
            $guard + $separator
        }
        if ($guard.Equals($output, [StringComparison]::OrdinalIgnoreCase) -or
            $guard.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            $output.StartsWith($guardPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'overlapping-output'
        }
    }
    $output
}

function Invoke-ObservationLoop {
    param(
        [Parameter(Mandatory)][scriptblock]$ReadSample,
        [Parameter(Mandatory)][scriptblock]$WriteRecord,
        [Parameter(Mandatory)][double]$DurationMilliseconds,
        [int]$IntervalMilliseconds = 1000,
        [scriptblock]$ReadEvents = {

    },
        [scriptblock]$Cancelled = {
        $false
    },
        [scriptblock]$Elapsed = {
        $clock.Elapsed.TotalMilliseconds
    },
        [scriptblock]$Wait = {
        param($milliseconds) Start-Sleep -Milliseconds $milliseconds
    }
    )
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $state = New-ObservationState
    $reason = 'collector-failed'
    try {
        while ($true) {
            if (& $Cancelled) {
                $reason = 'observer-cancelled'; break
            }
            $elapsedMs = & $Elapsed
            if ($elapsedMs -ge $DurationMilliseconds -and $state.samples -gt 0) {
                $reason = 'duration-elapsed'; break
            }
            $utc = [DateTimeOffset]::UtcNow.ToString('O')
            # Client sampling-start clocks are reference times, not provider counter timestamps.
            $sample = & $ReadSample
            & $WriteRecord (Add-ObservationSample $state $sample.process $sample.database $sample.wal $elapsedMs $utc)
            foreach ($event in @(& $ReadEvents $utc $elapsedMs $false)) {
                & $WriteRecord $event
            }
            if ($sample.process.status -eq 'exited') {
                $reason = 'target-exited'; break
            }
            if ($sample.process.status -eq 'reused') {
                $reason = 'target-identity-changed'; break
            }
            & $Wait $IntervalMilliseconds
        }
    } finally {
        $clock.Stop()
        $utc = [DateTimeOffset]::UtcNow.ToString('O')
        foreach ($event in @(& $ReadEvents $utc (& $Elapsed) $true)) {
            & $WriteRecord $event
        }
        & $WriteRecord @{
            type = 'observer.end'; utc = $utc; reason = $reason; productOutcome = $null; productOutcomeReason = 'not-observed'; summary = (Get-ObservationSummary $state)
        }
    }
}

Export-ModuleMember -Function *-Observation*
