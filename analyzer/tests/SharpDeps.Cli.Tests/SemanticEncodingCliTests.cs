namespace SharpDeps.Cli.Tests;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Text;
using SharpDeps.Index;
using SharpDeps.Query;
using Xunit;
using Xunit.Abstractions;

[Collection("markdown-index")]
public sealed class SemanticEncodingCliTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealCliUpdatesMatchIndependentFullAndExactSavedSourceAcrossAllFourEncodings()
    {
        var runRoot = Environment.GetEnvironmentVariable("SHARPDEPS_ENCODING_ARTIFACT_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "sde-cli", Guid.NewGuid().ToString("N"));
        if (!Path.IsPathFullyQualified(runRoot) || Directory.Exists(runRoot) && Directory.EnumerateFileSystemEntries(runRoot).Any())
            throw new InvalidOperationException("The encoding fixture requires a new absolute artifact directory.");
        Directory.CreateDirectory(runRoot);
        output.WriteLine("Semantic encoding CLI artifacts: " + runRoot);
        var limit = TimeSpan.FromMinutes(10);
        if (Environment.GetEnvironmentVariable("SHARPDEPS_ENCODING_DEADLINE_UTC") is { } configured)
        {
            if (!DateTimeOffset.TryParseExact(configured, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var stop) || stop <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("The encoding fixture deadline must be a future round-trip timestamp.");
            var remaining = stop - DateTimeOffset.UtcNow;
            if (remaining < limit)
                limit = remaining;
        }
        using var deadline = new CancellationTokenSource(limit);
        await OracleProcess.ExecuteAsync(runRoot, [typeof(OracleProcess).Assembly.Location, "--semantic-encoding-oracle", runRoot],
            deadline.Token, packageRoot: Path.Combine(runRoot, "packages"));
        var observations = Directory.GetFiles(runRoot, "*.warm.json");
        Assert.Equal(16, observations.Length);
        output.WriteLine("16 real CLI publications: four formats, 12 warm edits, three encoding-change cold fallbacks, initial cold.");
    }

    internal static async Task<int> RunFixtureAsync(string runRoot)
    {
        var root = Path.Combine(runRoot, "workspace");
        Directory.CreateDirectory(Path.Combine(root, "Nested"));
        var sdkRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "sdk"));
        var sdk = Directory.EnumerateDirectories(sdkRoot).Select(Path.GetFileName)
            .Where(s => s is not null && s.StartsWith("10.", StringComparison.Ordinal) && Version.TryParse(s, out _))
            .OrderByDescending(s => Version.Parse(s!)).First()!;
        Write("global.json", JsonSerializer.Serialize(new { sdk = new { version = sdk, rollForward = "disable" } }));
        Write("Oracle.slnx", "<Solution><Project Path=\"App.csproj\" /><Project Path=\"Nested/Other.csproj\" /></Solution>");
        foreach (var project in new[] { "App.csproj", "Nested/Other.csproj" })
            Write(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
                "<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences><EnableNETAnalyzers>false</EnableNETAnalyzers>" +
                "<GenerateAssemblyInfo>false</GenerateAssemblyInfo><GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>" +
                "<DefineConstants>" + (project == "App.csproj" ? "FIRST" : "SECOND") + "</DefineConstants></PropertyGroup></Project>");
        const string initial = "namespace System { public class Object {} public class ValueType {} public struct Void {} public struct Int32 {} }\r\n" +
            "namespace Demo { public class Service {\r\n// \U0001f642 日本語\r\npublic const int Value = 1;\r\n" +
            "public int Stable() => Value;\r\n#if FIRST\r\npublic int Mode() => 1;\r\n#else\r\npublic int Mode() => 2;\r\n#endif\r\n} }\r\n";
        var source = Path.Combine(root, "Nested", "Shared.cs");
        File.WriteAllText(source, initial, new UTF8Encoding(false, true));
        var config = Path.Combine(runRoot, "restore.config");
        File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        await OracleProcess.ExecuteAsync(root, ["restore", "Oracle.slnx", "--configfile", config, "--verbosity", "quiet"], deadline.Token,
            packageRoot: Path.Combine(runRoot, "packages"));
        File.Delete(Path.Combine(root, "global.json"));
        var state = CliWorkspaceState.Open(root, create: true);
        var index = Path.Combine(root, ".sharpdeps", "index.sqlite");
        var options = new CliIndexOptions(root, Path.Combine(root, "Oracle.slnx"), index, state.WorkspaceUuid);
        string[] terms = ["Service", "Stable", "Mode", "missing-term"];
        using var writer = IndexWriter.Open(root, index, state.WorkspaceUuid, true);
        await using var owner = new CliCoordinator(writer);
        OracleObservation? previous = null;
        var formats = new (string Name, Encoding Encoding)[]
        {
            ("utf8", new UTF8Encoding(false, true)), ("utf8bom", new UTF8Encoding(true, true)),
            ("utf16le", new UnicodeEncoding(false, true, true)), ("utf16be", new UnicodeEncoding(true, true, true))
        };
        foreach (var (name, encoding) in formats)
        {
            await Step(name + "-baseline", initial, encoding, warm: false, preserveTime: false);
            await Step(name + "-edit", initial.Replace("Value = 1", "Value = 2", StringComparison.Ordinal), encoding, warm: true, preserveTime: true);
            await Step(name + "-undo", initial, encoding, warm: true, preserveTime: true);
            await Step(name + "-unicode-lf", initial.Replace("\U0001f642", "\U0001f680", StringComparison.Ordinal)
                .Replace("\r\n", "\n", StringComparison.Ordinal), encoding, warm: true, preserveTime: false);
        }
        return 0;

        async Task Step(string label, string body, Encoding encoding, bool warm, bool preserveTime)
        {
            using var pinned = previous is null ? null : IndexReader.Open(index, state.WorkspaceUuid);
            var oldSnapshot = pinned?.Snapshot();
            var timestamp = File.GetLastWriteTimeUtc(source);
            var length = new FileInfo(source).Length;
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(body)).ToArray();
            File.WriteAllBytes(source, bytes);
            if (preserveTime)
            {
                Assert.Equal(length, bytes.LongLength);
                File.SetLastWriteTimeUtc(source, timestamp);
                Assert.Equal(timestamp, File.GetLastWriteTimeUtc(source));
            }
            var metrics = Path.Combine(root, ".sharpdeps", "metrics", label);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            string[] arguments = [previous is null ? "index" : "update", "--root", root, "--target", "Oracle.slnx", "--trusted",
                "--metrics-directory", metrics, "--timeout-seconds", "300"];
            var environmentBefore = Environment.GetEnvironmentVariables();
            var exit = await CliApplication.RunAsync(arguments, stdout, stderr, deadline.Token);
            OracleProcess.WriteProofDiagnostic(Path.Combine(runRoot, label + ".proof-diagnostic.json"), root, environmentBefore, []);
            File.WriteAllText(Path.Combine(runRoot, label + ".cli.stdout.json"), stdout.ToString());
            File.WriteAllText(Path.Combine(runRoot, label + ".cli.stderr.log"), stderr.ToString());
            Assert.True(exit == 0, label + ": CLI exit " + exit + ": " + stdout + "\n" + stderr);
            var actual = OracleProcess.Observe(index, state.WorkspaceUuid, terms);
            File.WriteAllText(Path.Combine(runRoot, label + ".warm.json"), JsonSerializer.Serialize(actual, QueryJson.Options));
            Assert.Equal((previous?.Snapshot.Graph.Generation ?? 0) + 1, actual.Snapshot.Graph.Generation);
            Assert.Equal(warm ? 1 : 0, Count(actual.Snapshot, "cli.semanticWorkspaceReused"));
            Assert.Equal(0, Count(actual.Snapshot, "cli.semanticGraphReused"));
            Assert.Equal(2, Count(actual.Snapshot, "cli.projectCollectorsExtracted"));
            Assert.Equal(0, Count(actual.Snapshot, "cli.projectCollectorsReused"));
            Assert.Equal(0, Count(actual.Snapshot, "cli.compilerErrors"));
            Assert.NotNull(Assert.Single(actual.Snapshot.Files, file => file.RelativePath == "Oracle.slnx").Reuse);
            var events = File.ReadLines(Assert.Single(Directory.GetFiles(metrics, "*.jsonl")))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line).GetProperty("payload")).ToArray();
            var opens = events.Count(item => item.GetProperty("type").GetString() == "phase"
                && item.GetProperty("phase").GetString() == "semantic.msbuild-open");
            Assert.Equal(warm ? 0 : 1, opens);
            var updates = events.Where(item => item.GetProperty("type").GetString() == "count"
                && item.GetProperty("name").GetString() == "semantic.syntax-updates-attempted").ToArray();
            if (warm)
                Assert.Equal(2, Assert.Single(updates).GetProperty("value").GetInt32());
            else
                Assert.Empty(updates);
            AssertSavedSource(actual.Snapshot, body, bytes);
            var fullOptions = options with { IndexPath = Path.Combine(root, ".sharpdeps", label + ".full.sqlite"), AllowSemanticReuse = false };
            var full = await OracleProcess.RunAsync(new(fullOptions, previous?.Snapshot, terms), runRoot, label + "-full", deadline.Token);
            Assert.NotEqual(actual.ProcessId, full.ProcessId);
            Assert.Equal(0, Count(full.Snapshot, "cli.semanticWorkspaceReused"));
            Assert.Equal(2, Count(full.Snapshot, "cli.projectCollectorsExtracted"));
            AssertSavedSource(full.Snapshot, body, bytes);
            var differences = FullBuildOracle.Differences(full, actual);
            File.WriteAllLines(Path.Combine(runRoot, label + ".differences.txt"), differences);
            Assert.True(differences.Count == 0, label + ": " + string.Join("\n", differences.Take(20)));
            Assert.Equal(bytes, File.ReadAllBytes(source));
            if (pinned is not null)
            {
                Assert.Equal(oldSnapshot!.Graph.Generation, pinned.Snapshot().Graph.Generation);
                Assert.Equal(JsonSerializer.Serialize(oldSnapshot, QueryJson.Options), JsonSerializer.Serialize(pinned.Snapshot(), QueryJson.Options));
                var stale = Assert.Throws<QuerySourceException>(() => new QueryWorkspace(root).Read(
                    Assert.Single(oldSnapshot.Files, file => file.RelativePath == "Nested/Shared.cs")));
                Assert.Equal("SOURCE_CHANGED_SINCE_SNAPSHOT", stale.Code);
            }
            previous = actual;
        }

        void AssertSavedSource(IndexSnapshot snapshot, string body, byte[] bytes)
        {
            var file = Assert.Single(snapshot.Files, item => item.RelativePath == "Nested/Shared.cs");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.ContentHash);
            var raw = SourceTextEncoding.Decode(new QueryWorkspace(root).Read(file));
            Assert.Equal((bytes[0] == 0xef || bytes[0] is 0xff or 0xfe ? "\uFEFF" : "") + body, raw);
            Assert.Equal(raw.Length, file.Utf16Length);
            const string declaration = "public int Stable() => Value;";
            var nodes = snapshot.Graph.Nodes.Where(item => item.Kind == HarnessNodeKind.Member && item.Name == "Stable").ToArray();
            Assert.Equal(2, nodes.Length);
            var ids = nodes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var occurrences = snapshot.Graph.SymbolOccurrences.Where(item => ids.Contains(item.LogicalSymbolId)).ToArray();
            Assert.Equal(2, occurrences.Length);
            Assert.Equal(2, occurrences.Select(item => item.VariantId).Distinct().Count());
            foreach (var occurrence in occurrences)
            {
                var location = Assert.Single(occurrence.Declarations!);
                var span = Assert.IsType<HarnessRawSpan>(location.RawSpan);
                Assert.Equal(file.SourceId, location.SourceId);
                Assert.Equal(file.ContentHash, location.ContentHash);
                Assert.Equal(raw.IndexOf(declaration, StringComparison.Ordinal), span.Start);
                Assert.Equal(declaration.Length, span.Length);
                Assert.Equal(declaration, raw.Substring(span.Start, span.Length));
            }
        }
        void Write(string relative, string text) => File.WriteAllText(Path.Combine(root, relative), text, new UTF8Encoding(false, true));
    }

    private static int Count(IndexSnapshot snapshot, string code)
        => snapshot.Graph.Diagnostics?.SingleOrDefault(item => item.Code == code)?.Count ?? 0;
}
