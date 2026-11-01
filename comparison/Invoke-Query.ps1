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

    Cleanup: while a call runs, the caller's descendants are snapshotted
    as ownership identities (PID plus process start time) and union-merged
    across refreshes, because parent links are only reliable while the
    parent lives. On timeout the process tree is killed and any wait for
    its exit is bounded; when the parent has already exited but
    descendants linger (for example a daemon holding the pipes), the
    recorded descendants are killed — but only when the PID's current
    start time still matches the recorded identity, so a reused PID is
    never touched. A descendant that reparents before its first
    observation on Linux can still be missed; no claim of complete
    coverage is made there.
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

function Merge-DescendantIdentities {
    param(
        [Parameter(Mandatory)] [int]$RootProcessId,
        [Parameter(Mandatory)] [hashtable]$Known
    )
    # Union-merge the current descendants into $Known as pid -> process
    # start time (UTC ticks). Once observed, an identity is kept even if
    # a later snapshot no longer lists the process (for example after it
    # was reparented on Linux), so cleanup never loses track of a process
    # it already saw. The start time is the ownership identity: a reused
    # PID has a different start time and is not ours.
    foreach ($id in @(Get-DescendantProcessIds $RootProcessId)) {
        if ($Known.ContainsKey($id)) {
            continue
        }
        try {
            $Known[$id] = (Get-Process -Id $id -ErrorAction Stop).StartTime.ToUniversalTime().Ticks
        }
        catch {
            # Raced with the process exiting; nothing to record.
        }
    }
}

function Stop-ProcessIdentities {
    param([Parameter(Mandatory)] [hashtable]$Known)
    # Deepest descendants first (highest pids are not necessarily the
    # deepest, but killing the recorded set as a group is what matters).
    # A process is terminated only when its current start time still
    # matches the recorded identity; a mismatch means the PID was reused
    # by an unrelated process, which must not be touched.
    foreach ($id in @($Known.Keys | Sort-Object -Descending)) {
        try {
            $p = Get-Process -Id $id -ErrorAction Stop
            if ($p.StartTime.ToUniversalTime().Ticks -ne $Known[$id]) {
                continue
            }
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
        $known = @{}
        try {
            Merge-DescendantIdentities $entry.Id $known
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
        Stop-ProcessIdentities $known
    }
    $script:ComparisonProcesses = @()
}

function Test-CountedCalls {
    param([AllowEmptyCollection()] $Calls)
    # A call counts toward the comparison only when its output was
    # collected completely, the tool exited successfully, the output is
    # non-empty, and the recorded metrics agree with the capture file:
    # the byte count and the UTF-16 code-unit count are recomputed from
    # the actual stdout file, so broken measurement plumbing (or a
    # missing capture) can never be certified.
    $list = @($Calls)
    $valid = @()
    foreach ($call in $list) {
        if (-not ($call.stdoutBytes -gt 0 -and $call.exitCode -eq 0 -and $call.collectionComplete)) {
            continue
        }
        if (-not $call.stdoutPath -or -not (Test-Path $call.stdoutPath)) {
            continue
        }
        $actualBytes = (Get-Item $call.stdoutPath).Length
        $actualUnits = [System.IO.File]::ReadAllText($call.stdoutPath, [System.Text.Encoding]::UTF8).Length
        if ($actualBytes -ne $call.stdoutBytes -or $actualUnits -ne $call.stdoutUtf16Units) {
            continue
        }
        $valid += $call
    }
    return [ordered]@{
        ok     = ($list.Count -gt 0 -and $valid.Count -eq $list.Count)
        detail = "calls=$($list.Count) validCounted=$($valid.Count)"
    }
}

function Test-ExplicitUnsupported {
    param([AllowEmptyString()] [string]$Text)
    # Only an explicit tool diagnostic counts as an unsupported outcome:
    # a JSON status or code field naming an unsupported result, or one of
    # the tools' dashed diagnostic codes (for example
    # "unsupported-symbol-mapping"). The bare word inside returned source
    # (a comment, an identifier, a string literal) is evidence, not a
    # diagnostic, and never classifies the outcome.
    if (-not $Text) {
        return $false
    }
    if ($Text -match '"(?:status|code)"\s*:\s*"unsupported') {
        return $true
    }
    if ($Text -match '\bunsupported-[a-z]') {
        return $true
    }
    return $false
}

function Assert-CopyContent {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ExpectedCommit
    )
    # The copy must be exactly the pinned commit: HEAD equality alone would
    # miss tracked files edited in place, so the working tree is compared
    # against HEAD as well. The only tolerated untracked entries are the
    # two tools' own output directories. Shared by prepare.ps1 (before a
    # copy is reused or created) and run.ps1 (before setup or queries).
    $head = (git -C $Path rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedCommit) {
        throw "Copy $Path is at '$head', expected '$ExpectedCommit'. Refusing to reuse or overwrite it."
    }
    $porcelain = @(git -C $Path status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed in $Path."
    }
    foreach ($line in $porcelain) {
        if ($line.StartsWith('??')) {
            $rel = $line.Substring(3).Trim().Trim('"')
            if (-not ($rel -match '^\.codegraph(/|$)' -or $rel -match '^\.sharpdeps(/|$)')) {
                throw "Unexpected untracked content in $Path : $rel. Refusing to reuse or overwrite it."
            }
        }
        else {
            throw "Tracked content in $Path differs from the pinned commit: $line. Refusing to reuse or overwrite it."
        }
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
    $descendantIdentity = @{}
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
                Merge-DescendantIdentities $process.Id $descendantIdentity
                $lastSnapshot = [DateTime]::UtcNow
            }
        }
        if (-not $exited -and $process.HasExited) {
            # Exited right at the deadline boundary; not a timeout.
            $exited = $true
        }
        if (-not $exited) {
            $timedOut = $true
            Merge-DescendantIdentities $process.Id $descendantIdentity
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
            Stop-ProcessIdentities $descendantIdentity
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
        # an exited parent, and the merge keeps every identity observed
        # while the parent was alive, so nothing already seen is lost.
        try {
            Merge-DescendantIdentities $process.Id $descendantIdentity
        }
        catch {
            # Snapshot is best effort; the captured set still applies.
        }
        Stop-ProcessIdentities $descendantIdentity
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
