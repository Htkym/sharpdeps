// SD-003 semantic loading prototype.
//
// Contract with the extension host:
//   stdout : machine-readable progress lines, prefixed with `sharpdeps:progress `
//   stderr : human-readable log lines
//   exit 0 : the probe report was written (partial results are still exit 0 and are
//            described by the report's coverage/limitations)
//   exit 2 : a precondition failed (no MSBuild/SDK); the reason is on stderr and
//            the caller must not treat this as a Quick failure
//   exit 1 : unexpected error

using System.Text.Json;
using SharpDeps.Analysis.Roslyn;

var parsed = CliOptions.Parse(args);
if (parsed is null)
{
    Console.Error.WriteLine(
        "usage: sharpdeps-semantic-host --solution <path> --output <path> "
        + "[--configuration Debug] [--platform <platform>] [--timeout <seconds>]");
    return 2;
}

var outputPath = parsed.Output;
var targetPath = parsed.Solution;
var configuration = parsed.Configuration;
var platform = parsed.Platform;
var timeoutSeconds = parsed.TimeoutSeconds;

var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};

if (!SemanticEnvironment.TryRegister(Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? ".", out var reason))
{
    Console.Error.WriteLine(reason);
    Console.Error.WriteLine(
        "Semantic analysis is unavailable in this environment. Quick analysis does not need MSBuild or an SDK.");
    return 2;
}

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
var stopwatch = System.Diagnostics.Stopwatch.StartNew();

Progress("discover", new { target = targetPath, configuration, platform });

try
{
    var report = await SemanticLoader.LoadAsync(
        new SemanticLoadOptions(targetPath, configuration, platform, timeoutSeconds),
        cancellation.Token);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, json), CancellationToken.None);

    Progress(
        "write",
        new
        {
            variants = report.Variants.Count,
            references = report.References.Count,
            unresolved = report.Coverage.Unresolved,
            diagnostics = report.Diagnostics.Count,
            elapsedMs = stopwatch.ElapsedMilliseconds
        });

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine($"Semantic analysis timed out after {timeoutSeconds}s.");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.ToString());
    return 1;
}

void Progress(string stage, object payload)
{
    var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
    Console.Out.WriteLine($"sharpdeps:progress {{\"stage\":\"{stage}\",\"payload\":{body}}}");
    Console.Out.Flush();
}

internal sealed record CliArguments(
    string Solution,
    string Output,
    string Configuration,
    string? Platform,
    int TimeoutSeconds);

internal static class CliOptions
{
    public static CliArguments? Parse(string[] args)
    {
        string? solution = null;
        string? output = null;
        var configuration = "Debug";
        string? platform = null;
        var timeout = 180;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--solution" when index + 1 < args.Length:
                    solution = args[++index];
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--configuration" when index + 1 < args.Length:
                    configuration = args[++index];
                    break;
                case "--platform" when index + 1 < args.Length:
                    platform = args[++index];
                    break;
                case "--timeout" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], out timeout) || timeout < 1)
                    {
                        return null;
                    }

                    break;
                default:
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(solution) || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return new CliArguments(solution, output, configuration, platform, timeout);
    }
}
