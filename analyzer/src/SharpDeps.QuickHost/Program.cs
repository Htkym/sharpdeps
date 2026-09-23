// Quick analyzer host: SDK-free dependency analysis.
//
// Contract with the extension host:
//   --solution <path>   .sln, .slnx, or a project file
//   --output <path>     where the v1 JSON report is written
//   --max-projects <n>  display budget (default 40)
//   --max-edges <n>     display budget (default 80)
//
// This host must not reference MSBuild or the Roslyn workspace packages: it has to
// run with a .NET runtime and no SDK installed.

using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Cli;
using SharpDeps.Analysis.Quick;

var options = CliOptions.Parse(args);
var outputPath = options.Require("output");
var solutionPath = options.Require("solution");
var maxProjects = options.GetInt("max-projects", 40);
var maxEdges = options.GetInt("max-edges", 80);

var report = await QuickAnalyzer.AnalyzeAsync(solutionPath, maxProjects, maxEdges);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(
    outputPath,
    System.Text.Json.JsonSerializer.Serialize(report, CodeMapJsonContext.Default.CodeMapReport));
