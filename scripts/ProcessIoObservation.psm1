Set-StrictMode -Version Latest

function Get-ObservationIoNames {
    @('readOperations', 'writeOperations', 'otherOperations', 'readTransferBytes', 'writeTransferBytes', 'otherTransferBytes')
}

function New-ObservationIoUnavailable([string]$Reason) {
    $result = @{}
    foreach ($name in Get-ObservationIoNames) {
        $result[$name] = @{
            value = $null; reason = $Reason
        }
    }
    $result
}

function Get-ObservationIoCounters {
    param(
        [int]$TargetProcessId,
        [long]$StartTicks,
        [Parameter(Mandatory)][scriptblock]$IdentityCurrent,
        [bool]$Supported = $IsWindows,
        [scriptblock]$Reader = {
            param($targetId)
            Get-CimInstance -Namespace root/cimv2 -ClassName Win32_Process -Filter ('ProcessId = ' + $targetId) `
                -Property CreationDate,ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount `
                -OperationTimeoutSec 1 -ErrorAction Stop
        }
    )
    if (-not $Supported) {
        return New-ObservationIoUnavailable 'platform-unsupported'
    }
    if ($TargetProcessId -le 0 -or $StartTicks -le 0) {
        return New-ObservationIoUnavailable 'invalid-target-identity'
    }
    try {
        if (-not (& $IdentityCurrent)) {
            return New-ObservationIoUnavailable 'target-identity-changed'
        }
        $records = @(& $Reader $TargetProcessId)
        if (-not (& $IdentityCurrent)) {
            return New-ObservationIoUnavailable 'target-identity-changed'
        }
        if ($records.Count -ne 1) {
            return New-ObservationIoUnavailable 'target-unavailable'
        }
        $record = $records[0]
        $created = $record.CreationDate
        if ($created -isnot [datetime] -and $created -isnot [datetimeoffset]) {
            return New-ObservationIoUnavailable 'identity-unavailable'
        }
        $ticks = $created.ToUniversalTime().Ticks
        # CIM dates have microsecond precision; do not broaden identity to wall-clock windows.
        if (($ticks - ($ticks % 10)) -ne ($StartTicks - ($StartTicks % 10))) {
            return New-ObservationIoUnavailable 'target-identity-changed'
        }
        $fields = @{
            readOperations = 'ReadOperationCount'; writeOperations = 'WriteOperationCount'; otherOperations = 'OtherOperationCount'
            readTransferBytes = 'ReadTransferCount'; writeTransferBytes = 'WriteTransferCount'; otherTransferBytes = 'OtherTransferCount'
        }
        $result = @{}
        foreach ($name in Get-ObservationIoNames) {
            $value = 0UL
            $property = $record.PSObject.Properties[$fields[$name]]
            $raw = if ($null -eq $property) {
                $null
            } else {
                $property.Value
            }
            if ($null -eq $raw -or -not [ulong]::TryParse([string]$raw, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
                $result[$name] = @{
                    value = $null; reason = 'counter-unavailable'
                }
            } else {
                $result[$name] = @{
                    value = $value; reason = 'available'
                }
            }
        }
        $result
    } catch {
        New-ObservationIoUnavailable 'io-counters-unavailable'
    }
}

function New-ObservationIoState {
    $state = @{}
    foreach ($name in Get-ObservationIoNames) {
        $state[$name] = @{
            first = $null; last = $null; firstElapsed = $null; lastElapsed = $null; regressed = $false
        }
    }
    $state
}

function Add-ObservationIoSample($State, $Counters, [double]$Elapsed) {
    $absolute = @{}; $delta = @{}; $windows = @{}
    foreach ($name in Get-ObservationIoNames) {
        $sample = $Counters[$name]; $prior = $State[$name]
        $absolute[$name] = $sample
        $reason = 'no-previous-io-sample'
        $change = $null; $window = $null
        if ($null -ne $sample.value) {
            if ($null -eq $prior.first) {
                $prior.first = $sample.value; $prior.firstElapsed = $Elapsed
            }
            if ($null -ne $prior.last) {
                if ($sample.value -ge $prior.last) {
                    $reason = 'available'; $change = [decimal]$sample.value - [decimal]$prior.last
                    $window = $Elapsed - $prior.lastElapsed
                } else {
                    $reason = 'counter-regressed'; $prior.regressed = $true
                }
            }
            $prior.last = $sample.value; $prior.lastElapsed = $Elapsed
        } else {
            $reason = $sample.reason
        }
        $delta[$name] = @{
            value = $change; reason = $reason
        }
        $windows[$name] = @{
            value = $window; reason = $reason
        }
    }
    @{
        scope = 'explicit-process-only'; source = 'win32-process-cim'; intervalBasis = 'observer-loop-sampling-start-monotonic'
        counterTimestamp = @{ value = $null; reason = 'not-provided-by-source' }
        absolute = $absolute; delta = $delta; deltaSamplingStartIntervalMilliseconds = $windows
    }
}

function Get-ObservationIoSummary($State) {
    $delta = @{}; $windows = @{}
    foreach ($name in Get-ObservationIoNames) {
        $prior = $State[$name]
        $reason = if ($prior.regressed) {
            'counter-regressed'
        } elseif ($null -eq $prior.first -or $prior.firstElapsed -eq $prior.lastElapsed) {
            'insufficient-io-samples'
        } else {
            'available'
        }
        $change = if ($reason -eq 'available') {
            [decimal]$prior.last - [decimal]$prior.first
        } else {
            $null
        }
        $window = if ($reason -eq 'available') {
            $prior.lastElapsed - $prior.firstElapsed
        } else {
            $null
        }
        $delta[$name] = @{
            value = $change; reason = $reason
        }
        $windows[$name] = @{
            value = $window; reason = $reason
        }
    }
    @{
        scope = 'explicit-process-only'; source = 'win32-process-cim'; intervalBasis = 'observer-loop-sampling-start-monotonic'
        counterTimestamp = @{ value = $null; reason = 'not-provided-by-source' }
        delta = $delta; deltaSamplingStartIntervalMilliseconds = $windows
    }
}

Export-ModuleMember -Function *-ObservationIo*
