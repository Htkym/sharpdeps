<#
.SYNOPSIS
    Shared non-TTY process runner for the SharpDeps / CodeGraph comparison.

.DESCRIPTION
    Invoke-ComparisonQuery runs one native command with stdout and stderr
    redirected to separate files (no TTY), enforces a timeout, and records the
    protocol metrics for the captured stdout: raw UTF-8 byte count (the file
    length) and decoded UTF-16 code unit count (the .NET string length of the
    UTF-8 decoding, line endings included). A JSON result file is written next
    to the captures and the same data is returned as an object.

    A result is a valid collection only when the process exited on its own
    and both output streams were fully drained (collectionComplete). A
    timeout or an expired stream grace period (a descendant still holding
    the pipes) makes the collection incomplete: the captures stay on disk
    for diagnosis but must not be treated as a valid result.

    Every started process is registered in $script:ComparisonProcesses and
    is killed if it is still running when the call returns or throws.
    Stop-ComparisonProcesses cleans up anything left registered.
#>

$script:ComparisonProcesses = @()

function Stop-ComparisonProcesses {
    foreach ($entry in $script:ComparisonProcesses) {
        try {
            if (-not $entry.HasExited) {
                $entry.Kill($true)
                $entry.WaitForExit(5000) | Out-Null
            }
        }
        catch {
            # Best effort cleanup; the original outcome stands.
        }
    }
    $script:ComparisonProcesses = @()
}

function Invoke-ComparisonQuery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Tool,
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [int]$TimeoutSeconds = 300,
        [Parameter(Mandatory)] [string]$OutBase
    )
    $stdoutPath = "$OutBase.stdout.txt"
    $stderrPath = "$OutBase.stderr.txt"
    $resultPath = "$OutBase.result.json"
    # Start-Process joins -ArgumentList with bare spaces and mangles paths
    # and question texts containing spaces, so launch through
    # ProcessStartInfo.ArgumentList, which passes each argument intact.
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    foreach ($arg in $Arguments) {
        $psi.ArgumentList.Add($arg)
    }
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    foreach ($key in $Environment.Keys) {
        $psi.Environment[$key] = $Environment[$key]
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $timedOut = $false
    $exitCode = $null
    $streamGraceExpired = $false
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()
    $script:ComparisonProcesses += $process
    try {
        # Children get EOF on stdin immediately; nothing here may prompt.
        $process.StandardInput.Close()
        $outStream = [System.IO.File]::Create($stdoutPath)
        $errStream = [System.IO.File]::Create($stderrPath)
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($outStream)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($errStream)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            $process.Kill($true)
            $process.WaitForExit()
        }
        if (-not $timedOut) {
            $exitCode = $process.ExitCode
        }
        # A CLI may leave a coordinator daemon behind that inherits the pipe
        # handles; the CLI's own output is already flushed at exit, so grant
        # the copies a bounded grace period instead of blocking on the daemon.
        # If the grace expires the collection is incomplete, not valid.
        if (-not [System.Threading.Tasks.Task]::WaitAll(@($outTask, $errTask), 10000)) {
            $streamGraceExpired = $true
        }
        $outStream.Dispose()
        $errStream.Dispose()
    }
    finally {
        $sw.Stop()
        if (-not $process.HasExited) {
            try {
                $process.Kill($true)
                $process.WaitForExit(5000) | Out-Null
            }
            catch {
                # Best effort cleanup; the original outcome stands.
            }
        }
    }
    $stdoutBytes = 0
    $stdoutUnits = 0
    if (Test-Path $stdoutPath) {
        $stdoutBytes = (Get-Item $stdoutPath).Length
        $decoded = [System.IO.File]::ReadAllText($stdoutPath, [System.Text.Encoding]::UTF8)
        $stdoutUnits = $decoded.Length
    }
    $result = [ordered]@{
        tool             = $Tool
        filePath         = $FilePath
        arguments        = $Arguments
        workingDirectory = $WorkingDirectory
        exitCode         = $exitCode
        timedOut         = $timedOut
        streamGraceExpired = $streamGraceExpired
        collectionComplete = (-not $timedOut -and -not $streamGraceExpired)
        durationMs       = [int]$sw.ElapsedMilliseconds
        stdoutBytes      = $stdoutBytes
        stdoutUtf16Units = $stdoutUnits
        stdoutPath       = $stdoutPath
        stderrPath       = $stderrPath
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -Path $resultPath -Encoding utf8
    return [pscustomobject]$result
}
