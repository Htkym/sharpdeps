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
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Analysis.Roslyn.Symbols;

var parsed = CliOptions.Parse(args);
if (parsed is null)
{
    Console.Error.WriteLine(
        "usage: sharpdeps-semantic-host --solution <path> --output <path> "
        + "[--configuration Debug] [--platform <platform>] [--timeout <seconds>] "
        + "[--analysis-id <an_...>] [--watch-stdin]");
    return 2;
}

var outputPath = parsed.Output;
var targetPath = parsed.Solution;
var configuration = parsed.Configuration;
var platform = parsed.Platform;
var timeoutSeconds = parsed.TimeoutSeconds;
var analysisId = parsed.AnalysisId;

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

// The host cancels cooperatively when the parent closes stdin (the extension host
// stopping the analysis closes the pipe first) or when the timeout elapses.
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
if (args.Contains("--watch-stdin", StringComparer.Ordinal))
{
    // Opt-in: a closed stdin means the caller stopped us.
    _ = WatchForCancellationAsync(cancellation);
}
var stopwatch = System.Diagnostics.Stopwatch.StartNew();

Progress("discover", new { target = targetPath, configuration, platform });

try
{
    var load = await SemanticLoader.LoadAsync(
        new SemanticLoadOptions(targetPath, configuration, platform, timeoutSeconds),
        cancellation.Token);
    // The semantic v2 report writer is not implemented yet (SD-011/SD-013); the probe
    // report is written as-is and the caller's analysis id is not applied here.
    var report = load.Report;
    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
    Directory.CreateDirectory(outputDirectory);

    // v2 model: symbol index + declaration/usage evidence -> AnalysisSnapshot.
    var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? ".";
    var documents = new SourceDocumentRegistry(
        Identity.WorkspaceRootId(solutionDirectory),
        solutionDirectory);

    // Generated documents are known before the index and the collectors run, so evidence
    // and declaration locations inside them are marked generatedSource from the start.
    var generatedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var generated in load.GeneratedDocuments)
    {
        var document = documents.RegisterGenerated(
            generated.FilePath,
            generated.ProjectName,
            generated.HintName,
            generated.ContentHash,
            generated.ByteLength);
        if (generated.Text is null)
        {
            continue;
        }

        // The content lives with the analysis result. Nothing is written into the user's
        // repository; the read-only document provider serves these files.
        var fileName = $"{document.Id}.cs";
        var filePath = Path.Combine(outputDirectory, "generated", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            generated.Text,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CancellationToken.None);
        generatedFiles[document.Id] = fileName;
    }

    var inputs = report.Variants
        .Where(variant => variant.LoadState == "loaded" && load.Compilations.ContainsKey(variant.VariantKey))
        .Select(variant => new SymbolIndexInput(
            variant.VariantKey,
            variant.ProjectName,
            load.Compilations[variant.VariantKey]))
        .ToArray();

    var symbolIndex = SymbolIndexBuilder.Build(documents, inputs, cancellation.Token);
    var resolver = new SymbolResolver(load.DefiningVariantByAssembly);
    var externalTypes = new ExternalTypeRegistry();
    var declarations = new DeclarationDependencyCollector(
        resolver,
        documents,
        symbolIndex,
        report.Profile.ProfileHash,
        externalTypes);
    var operations = new OperationDependencyCollector(
        resolver,
        documents,
        symbolIndex,
        report.Profile.ProfileHash,
        externalTypes);
    var operationResult = operations.Collect(inputs, cancellation.Token);
    var evidence = declarations.Collect(inputs, cancellation.Token)
        .Concat(operationResult.Evidence)
        .ToArray();

    var semantic = SemanticReportWriter.Write(
        load,
        symbolIndex,
        evidence,
        operationResult.Stats,
        DateTimeOffset.UtcNow,
        targetPath,
        analysisId,
        externalTypes.Entries);

    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, json), CancellationToken.None);
    await File.WriteAllTextAsync(
        Path.Combine(outputDirectory, "report-v2.json"),
        JsonSerializer.Serialize(semantic.Snapshot, CodeMapJsonContext.Default.AnalysisSnapshot),
        CancellationToken.None);
    await File.WriteAllTextAsync(
        Path.Combine(outputDirectory, "evidence.ndjson"),
        semantic.EvidenceNdjson,
        CancellationToken.None);

    Progress(
        "write",
        new
        {
            variants = report.Variants.Count,
            relations = semantic.Snapshot.Relations.Count,
            types = semantic.Snapshot.Types.Count,
            references = report.References.Count,
            unresolved = report.Coverage.Unresolved,
            diagnostics = report.Diagnostics.Count,
            compilations = load.Compilations.Count,
            generatedDocuments = load.GeneratedDocuments.Count,
            generatedContentRetained = generatedFiles.Count,
            elapsedMs = stopwatch.ElapsedMilliseconds
        });

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Semantic analysis was cancelled.");
    return 3;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.ToString());
    return 1;
}

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
    int TimeoutSeconds,
    string? AnalysisId);

internal static class CliOptions
{
    public static CliArguments? Parse(string[] args)
    {
        string? solution = null;

        string? output = null;
        var configuration = "Debug";
        string? platform = null;
        var timeout = 180;
        string? analysisId = null;

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

                case "--analysis-id" when index + 1 < args.Length:
                    analysisId = args[++index];
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

        return new CliArguments(solution, output, configuration, platform, timeout, analysisId);
    }
}
