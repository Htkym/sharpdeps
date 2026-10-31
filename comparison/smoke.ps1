<#
.SYNOPSIS
    Exercises the comparison plumbing on a minimal local fixture.

.DESCRIPTION
    Builds a two-file C# fixture, creates one isolated copy per tool, writes a
    fixture questions file, and runs run.ps1 against it through the same code
    path the real comparison will use. The fixture questions carry machine
    sufficiency markers and evidence-gated follow-up candidates so the smoke
    verifies the collection path, the byte/character counting and the stop
    conditions (sufficiency, follow-up cap) without running any fixed
    comparison question or indexing a real corpus.

    Results land in <Root>/o/smoke-<timestamp>/smoke-summary.json. A tool
    whose setup cannot run (for example a CodeGraph init mode that is not
    unattended-capable) is reported as a concrete finding in the summary;
    the smoke fails only when neither tool completes the path.
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [string[]]$CodeGraphInitArguments = @('init')
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    if ($env:SCG_ROOT) {
        $Root = $env:SCG_ROOT
    }
    elseif ($IsWindows -and (Test-Path 'D:\')) {
        $Root = 'D:\scg'
    }
    else {
        $Root = Join-Path (Split-Path $PSScriptRoot -Parent) '.scg'
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$smokeDir = Join-Path $Root "o/smoke-$stamp"
$srcDir = Join-Path $smokeDir 'fixture-src'
$cgCopy = Join-Path $smokeDir 'fixture-codegraph'
$sdCopy = Join-Path $smokeDir 'fixture-sharpdeps'
foreach ($dir in $srcDir, $cgCopy, $sdCopy) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@ | Set-Content -Path (Join-Path $srcDir 'Smoke.csproj') -Encoding utf8

@'
<Solution>
  <Project Path="Smoke.csproj" />
</Solution>
'@ | Set-Content -Path (Join-Path $srcDir 'Smoke.slnx') -Encoding utf8

@'
namespace Smoke;

public sealed class Greeter
{
    public string Greet(string name)
    {
        return $"Hello, {name}!";
    }

    public string Greet(string name, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Greet(name);
        return $"Hello, {title} {name}!";
    }
}
'@ | Set-Content -Path (Join-Path $srcDir 'Greeter.cs') -Encoding utf8

@'
using System;

namespace Smoke;

internal static class Program
{
    private static void Main()
    {
        var greeter = new Greeter();
        Console.WriteLine(greeter.Greet("World", "Dr."));
    }
}
'@ | Set-Content -Path (Join-Path $srcDir 'Program.cs') -Encoding utf8

Copy-Item -Path (Join-Path $srcDir '*') -Destination $cgCopy -Recurse
Copy-Item -Path (Join-Path $srcDir '*') -Destination $sdCopy -Recurse

$fixtureQuestions = [ordered]@{
    scope   = 'smoke fixture only; not a comparison corpus'
    corpora = @(
        [ordered]@{
            id        = 'fixture'
            questions = @(
                [ordered]@{
                    id = 'fq-overloads'
                    question = 'Show the Greet overloads declared by Greeter and what each returns.'
                    initialNativeArguments = [ordered]@{
                        codegraphExplore = 'Show the Greet overloads declared by Greeter and what each returns.'
                        sharpdepsTerm    = 'Greeter Greet'
                    }
                    sufficiencyMarkers = @('Greet(string name, string title)')
                    followupCandidates = @(
                        [ordered]@{ tool = 'sharpdeps'; text = 'Greeter'; requires = 'Greeter' }
                        [ordered]@{ tool = 'codegraph'; text = 'Greeter Greet'; requires = 'Greeter' }
                    )
                },
                [ordered]@{
                    id = 'fq-cap'
                    question = 'Show the three-argument Greet overload declared by Greeter.'
                    initialNativeArguments = [ordered]@{
                        codegraphExplore = 'Show the Greet overloads declared by Greeter.'
                        sharpdepsTerm    = 'Greeter'
                    }
                    sufficiencyMarkers = @('Greet(string name, string title, string suffix)')
                    followupCandidates = @(
                        [ordered]@{ tool = 'sharpdeps'; text = 'Greeter Greet'; requires = 'Greeter' }
                        [ordered]@{ tool = 'sharpdeps'; text = 'Greet'; requires = 'Greet' }
                        [ordered]@{ tool = 'codegraph'; text = 'Greeter'; requires = 'Greeter' }
                        [ordered]@{ tool = 'codegraph'; text = 'Greet'; requires = 'Greet' }
                    )
                }
            )
        }
    )
}
$questionsPath = Join-Path $smokeDir 'questions-fixture.json'
$fixtureQuestions | ConvertTo-Json -Depth 8 | Set-Content -Path $questionsPath -Encoding utf8

$spec = [ordered]@{
    id              = 'fixture'
    repository      = 'local-fixture'
    commit          = 'none'
    sharpdepsTarget = 'Smoke.slnx'
    configuration   = 'Release'
    copies          = [ordered]@{ codegraph = $cgCopy; sharpdeps = $sdCopy }
}
$specPath = Join-Path $smokeDir 'spec-fixture.json'
$spec | ConvertTo-Json -Depth 5 | Set-Content -Path $specPath -Encoding utf8

$runOutputDir = Join-Path $smokeDir 'run'
$runScript = Join-Path $PSScriptRoot 'run.ps1'
$summaryPath = & $runScript -Root $Root -CorpusSpecPath $specPath -QuestionsPath $questionsPath `
    -OutputDir $runOutputDir -CodeGraphInitArguments $CodeGraphInitArguments | Select-Object -Last 1
$runSummary = Get-Content $summaryPath -Raw | ConvertFrom-Json

$checks = @()
function Add-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    $script:checks += [ordered]@{ name = $Name; ok = $Ok; detail = $Detail }
}

Add-Check 'run-summary-readable' $true $summaryPath
foreach ($tool in 'sharpdeps', 'codegraph') {
    $setup = @($runSummary.setup.$tool)
    $setupOk = $setup.Count -gt 0 -and -not ($setup | Where-Object { $_.timedOut -or ($null -ne $_.exitCode -and $_.exitCode -ne 0) })
    $setupDetail = ($setup | ForEach-Object { "exit=$($_.exitCode) timedOut=$($_.timedOut)" }) -join '; '
    Add-Check "setup-$tool" $setupOk $setupDetail
    $allCalls = @()
    foreach ($q in $runSummary.questions) {
        $allCalls += @($q.tools.$tool.calls)
    }
    $counted = @($allCalls | Where-Object { $_.stdoutBytes -gt 0 })
    Add-Check "query-counting-$tool" ($counted.Count -gt 0) "calls=$($allCalls.Count) countedWithBytes=$($counted.Count)"
}
$fqOverloads = $runSummary.questions | Where-Object { $_.id -eq 'fq-overloads' }
$fqCap = $runSummary.questions | Where-Object { $_.id -eq 'fq-cap' }
Add-Check 'stop-sufficiency-sharpdeps' ($fqOverloads.tools.sharpdeps.stopReason -eq 'sufficiency') "actual=$($fqOverloads.tools.sharpdeps.stopReason)"
Add-Check 'stop-cap-sharpdeps' ($fqCap.tools.sharpdeps.stopReason -eq 'cap' -and @($fqCap.tools.sharpdeps.calls).Count -eq 3) "actual=$($fqCap.tools.sharpdeps.stopReason) calls=$(@($fqCap.tools.sharpdeps.calls).Count)"

$anyToolPathOk = ($checks | Where-Object { $_.name -eq 'setup-sharpdeps' }).ok -or ($checks | Where-Object { $_.name -eq 'setup-codegraph' }).ok
$smokeSummary = [ordered]@{
    schemaVersion = 1
    smokeDir      = $smokeDir
    runSummary    = $summaryPath
    checks        = $checks
    pathExercised = $anyToolPathOk
    finishedAtUtc = [DateTime]::UtcNow.ToString('o')
}
$smokeSummaryPath = Join-Path $smokeDir 'smoke-summary.json'
$smokeSummary | ConvertTo-Json -Depth 6 | Set-Content -Path $smokeSummaryPath -Encoding utf8
Write-Output $smokeSummaryPath
if (-not $anyToolPathOk) {
    throw 'Smoke failed: neither tool completed setup. See smoke-summary.json.'
}
