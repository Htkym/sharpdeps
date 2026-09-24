// Quick analyzer host: SDK-free dependency analysis.
//
// Contract with the extension host:
//   --solution <path>   .sln, .slnx, or a project file
//   --output <path>     where the v1 JSON report is written
//   --max-projects <n>  display budget (default 40)
//   --max-edges <n>     display budget (default 80)
//   --analysis-id <id>  fixed analysis id so the caller can correlate progress
//   --watch-stdin       cancel cooperatively when the caller closes stdin
//
// Besides the v1 report (still consumed by the extension), the host writes the v2
// model next to it:
//   <output directory>/report-v2.json   AnalysisSnapshot (schemas/report-v2.schema.json)
//   <output directory>/evidence.ndjson  one evidence record per line
//
// This host must not reference MSBuild or the Roslyn workspace packages: it has to
// run with a .NET runtime and no SDK installed.

using System.Text.Json;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Cli;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Quick;

// Cooperative cancellation: stdin EOF means the caller stopped us.
async Task WatchForCancellationAsync(CancellationTokenSource source)
{
    try
    {
        using var reader = new StreamReader(Console.OpenStandardInput());
        while (await reader.ReadLineAsync() is not null)
        {
        }
    }
    catch
    {
        // Falling through to cancel is the safe behaviour.
    }

    source.Cancel();
}

var options = CliOptions.Parse(args);
var outputPath = options.Require("output");
var solutionPath = options.Require("solution");
var maxProjects = options.GetInt("max-projects", 40);
var maxEdges = options.GetInt("max-edges", 80);
var analysisId = options.GetOptional("analysis-id");

// Cooperative cancellation is opt-in: only a caller that keeps stdin open and passes
// --watch-stdin (the extension host) wants a closed stdin to mean "stop".
using var cancellation = new CancellationTokenSource();
if (options.GetOptional("watch-stdin") is not null)
{
    _ = WatchForCancellationAsync(cancellation);
}

var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";
var collector = new QuickSourceIndexCollector(
    Identity.WorkspaceRootId(solutionDirectory),
    solutionDirectory);

CodeMapReport report;
try
{
    report = await QuickAnalyzer.AnalyzeAsync(solutionPath, maxProjects, maxEdges, collector, cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Quick analysis was cancelled.");
    return 3;
}
var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
Directory.CreateDirectory(outputDirectory);

await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(report, CodeMapJsonContext.Default.CodeMapReport));

var v2 = QuickV2Mapper.Map(report, collector.Build(), DateTimeOffset.UtcNow, analysisId: analysisId);
await File.WriteAllTextAsync(
    Path.Combine(outputDirectory, "report-v2.json"),
    JsonSerializer.Serialize(v2.Snapshot, CodeMapJsonContext.Default.AnalysisSnapshot));
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "evidence.ndjson"), v2.EvidenceNdjson);

return 0;
