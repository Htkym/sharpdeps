// Quick analyzer host: SDK-free dependency analysis.
//
// Contract with the extension host:
//   --solution <path>   .sln, .slnx, or a project file
//   --output <path>     where the v1 JSON report is written
//   --max-projects <n>  display budget (default 40)
//   --max-edges <n>     display budget (default 80)
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

var options = CliOptions.Parse(args);
var outputPath = options.Require("output");
var solutionPath = options.Require("solution");
var maxProjects = options.GetInt("max-projects", 40);
var maxEdges = options.GetInt("max-edges", 80);

var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";
var collector = new QuickSourceIndexCollector(
    Identity.WorkspaceRootId(solutionDirectory),
    solutionDirectory);

var report = await QuickAnalyzer.AnalyzeAsync(solutionPath, maxProjects, maxEdges, collector);
var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
Directory.CreateDirectory(outputDirectory);

await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(report, CodeMapJsonContext.Default.CodeMapReport));

var v2 = QuickV2Mapper.Map(report, collector.Build(), DateTimeOffset.UtcNow);
await File.WriteAllTextAsync(
    Path.Combine(outputDirectory, "report-v2.json"),
    JsonSerializer.Serialize(v2.Snapshot, CodeMapJsonContext.Default.AnalysisSnapshot));
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "evidence.ndjson"), v2.EvidenceNdjson);
