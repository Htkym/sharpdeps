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
#>

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
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()
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
    # handles; the CLI's own output is already flushed at exit, so grant the
    # copies a bounded grace period instead of blocking on the daemon.
    $streamGraceExpired = $false
    if (-not [System.Threading.Tasks.Task]::WaitAll(@($outTask, $errTask), 10000)) {
        $streamGraceExpired = $true
    }
    $outStream.Dispose()
    $errStream.Dispose()
    $sw.Stop()
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
        durationMs       = [int]$sw.ElapsedMilliseconds
        stdoutBytes      = $stdoutBytes
        stdoutUtf16Units = $stdoutUnits
        stdoutPath       = $stdoutPath
        stderrPath       = $stderrPath
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -Path $resultPath -Encoding utf8
    return [pscustomobject]$result
}
