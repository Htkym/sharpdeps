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

    Cleanup: while a call runs, the caller's descendant process ids are
    snapshotted (parent links are only reliable while the parent lives).
    On timeout the process tree is killed and any wait for its exit is
    bounded; when the parent has already exited but descendants linger
    (for example a daemon holding the pipes), the snapshotted descendants
    are killed explicitly. Stop-ComparisonProcesses applies the same
    cleanup to every registered process.
#>

$script:ComparisonProcesses = @()

function Get-DescendantProcessIds {
    param([Parameter(Mandatory)] [int]$RootProcessId)
    $childrenByParent = @{}
    try {
        if ($IsWindows) {
            $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId -ErrorAction Stop
            foreach ($p in $all) {
                $childrenByParent[[int]$p.ParentProcessId] += @([int]$p.ProcessId)
            }
        }
        elseif (Test-Path '/proc') {
            foreach ($dir in Get-ChildItem '/proc' -Directory -ErrorAction SilentlyContinue) {
                if ($dir.Name -notmatch '^\d+$') {
                    continue
                }
                $stat = Get-Content (Join-Path $dir.FullName 'stat') -Raw -ErrorAction SilentlyContinue
                if (-not $stat) {
                    continue
                }
                # Format: pid (comm) state ppid ... ; comm may contain
                # spaces and parentheses, so parse after the last ')'.
                $close = $stat.LastIndexOf(')')
                if ($close -lt 0) {
                    continue
                }
                $fields = $stat.Substring($close + 2).Split(' ')
                if ($fields.Count -ge 2) {
                    $childrenByParent[[int]$fields[1]] += @([int]$dir.Name)
                }
            }
        }
    }
    catch {
        return @()
    }
    $found = @()
    try {
        $frontier = @($RootProcessId)
        while ($frontier.Count -gt 0) {
            $next = @()
            foreach ($parentId in $frontier) {
                # A missing key means a leaf process; indexing it would
                # yield $null and poison the next frontier round.
                if (-not $childrenByParent.ContainsKey($parentId)) {
                    continue
                }
                foreach ($childId in $childrenByParent[$parentId]) {
                    $found += $childId
                    $next += $childId
                }
            }
            $frontier = $next
        }
    }
    catch {
        return @()
    }
    return $found
}

function Stop-ProcessIds {
    param([AllowEmptyCollection()] [int[]]$Ids)
    # Deepest descendants first, so nothing can respawn a parent chain.
    foreach ($id in @($Ids) | Sort-Object -Descending) {
        try {
            $p = Get-Process -Id $id -ErrorAction Stop
            $p.Kill()
            [void]$p.WaitForExit(2000)
        }
        catch {
            # Already gone, or not ours to kill; cleanup is best effort.
        }
    }
}

function Stop-ComparisonProcesses {
    foreach ($entry in $script:ComparisonProcesses) {
        $descendants = @()
        try {
            $descendants = @(Get-DescendantProcessIds $entry.Id)
        }
        catch {
            # Snapshot is best effort; the direct cleanup below still runs.
        }
        try {
            if (-not $entry.HasExited) {
                $entry.Kill($true)
                [void]$entry.WaitForExit(5000)
            }
        }
        catch {
            # Best effort cleanup; the original outcome stands.
        }
        Stop-ProcessIds $descendants
    }
    $script:ComparisonProcesses = @()
}

function Test-CountedCalls {
    param([AllowEmptyCollection()] $Calls)
    # A call counts toward the comparison only when its output was
    # collected completely, the tool exited successfully, and the output
    # is non-empty. Error text on stdout never counts.
    $list = @($Calls)
    $valid = @($list | Where-Object {
            $_.stdoutBytes -gt 0 -and $_.exitCode -eq 0 -and $_.collectionComplete
        })
    return [ordered]@{
        ok     = ($list.Count -gt 0 -and $valid.Count -eq $list.Count)
        detail = "calls=$($list.Count) validCounted=$($valid.Count)"
    }
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
    $descendants = @()
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
        # Wait in slices and keep a fresh descendant snapshot: parent
        # links are only reliable while the parent is alive, and the
        # snapshot is what cleanup uses once the parent has exited.
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $lastSnapshot = [DateTime]::MinValue
        $exited = $false
        while (-not $exited) {
            $remainingMs = ($deadline - [DateTime]::UtcNow).TotalMilliseconds
            if ($remainingMs -le 0) {
                break
            }
            $sliceMs = [Math]::Min(2000, [int]$remainingMs)
            if ($sw.Elapsed.TotalSeconds -lt 5) {
                $sliceMs = [Math]::Min(500, $sliceMs)
            }
            $exited = $process.WaitForExit($sliceMs)
            if (-not $exited -and ([DateTime]::UtcNow - $lastSnapshot).TotalSeconds -ge 2) {
                $descendants = @(Get-DescendantProcessIds $process.Id)
                $lastSnapshot = [DateTime]::UtcNow
            }
        }
        if (-not $exited -and $process.HasExited) {
            # Exited right at the deadline boundary; not a timeout.
            $exited = $true
        }
        if (-not $exited) {
            $timedOut = $true
            $descendants = @(Get-DescendantProcessIds $process.Id)
            $process.Kill($true)
            # Bounded: a killed process that still does not exit must not
            # hang the run; the finally block retries once more, bounded.
            [void]$process.WaitForExit(10000)
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
        if ($streamGraceExpired) {
            # The parent has exited but descendants still hold the pipes;
            # remove them so nothing the run started is left behind.
            Stop-ProcessIds $descendants
        }
    }
    finally {
        $sw.Stop()
        if (-not $process.HasExited) {
            try {
                $process.Kill($true)
                [void]$process.WaitForExit(5000)
            }
            catch {
                # Best effort cleanup; the original outcome stands.
            }
        }
        # Refresh the descendant set now that the parent has finished:
        # on Windows the recorded parent ids still identify orphans of
        # an exited parent, and on every platform this merges in
        # anything the in-wait snapshots captured.
        try {
            $descendants = @($descendants + @(Get-DescendantProcessIds $process.Id) | Select-Object -Unique)
        }
        catch {
            # Snapshot is best effort; the captured set still applies.
        }
        Stop-ProcessIds $descendants
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
