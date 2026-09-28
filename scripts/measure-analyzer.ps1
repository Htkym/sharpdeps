param([Parameter(Mandatory)][string]$Specification)
$ErrorActionPreference = 'Stop'
$spec = Get-Content -LiteralPath $Specification -Raw | ConvertFrom-Json
$info = [System.Diagnostics.ProcessStartInfo]::new()
$info.FileName = $spec.command
foreach ($argument in $spec.arguments) { $info.ArgumentList.Add([string]$argument) }
$info.WorkingDirectory = $spec.cwd
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $info
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$null = $process.Start()
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$owned = [System.Collections.Generic.HashSet[int]]::new()
$null = $owned.Add($process.Id)
$samples = [System.Collections.Generic.List[object]]::new()
$peak = 0L
while (-not $process.HasExited) {
    foreach ($parent in @($owned)) {
        foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $parent" -ErrorAction SilentlyContinue)) {
            $null = $owned.Add([int]$child.ProcessId)
        }
    }
    $rss = 0L
    foreach ($ownedId in $owned) {
        $live = Get-Process -Id $ownedId -ErrorAction SilentlyContinue
        if ($live) { $rss += $live.WorkingSet64 }
    }
    $peak = [Math]::Max($peak, $rss)
    $samples.Add(@{ elapsedMs = $watch.Elapsed.TotalMilliseconds; workingSetBytes = $rss })
    Start-Sleep -Milliseconds 200
}
$process.WaitForExit()
$watch.Stop()
[System.IO.File]::WriteAllText($spec.output + '.stdout.log', $stdout.GetAwaiter().GetResult())
[System.IO.File]::WriteAllText($spec.output + '.stderr.log', $stderr.GetAwaiter().GetResult())
$remaining = @($owned | Where-Object { $_ -ne $process.Id -and (Get-Process -Id $_ -ErrorAction SilentlyContinue) })
@{ exitCode = $process.ExitCode; wallMs = $watch.Elapsed.TotalMilliseconds; sampledTreePeakBytes = $peak; mainPeakBytes = $process.PeakWorkingSet64;
   sampleIntervalMs = 200; samples = $samples; ownedPids = @($owned); remainingOwnedPids = $remaining } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($spec.output + '.measurement.json') -Encoding utf8
exit $process.ExitCode
