namespace SharpDeps.Mcp.Tests;

using System.Text;
using System.Text.Json;
using Xunit;

public sealed class ReplayPhaseMetricsTests
{
    private const string RunId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void RepeatedNestedAndOverlappingSpansUseIntervalUnionAndKeepMissingStagesNull()
    {
        var bytes = Sidecar();
        var capture = ReplayPhaseMetrics.Parse(bytes, RunId, "completed");
        Assert.Equal("recorded", capture.Status);
        Assert.Equal("completed", capture.RunOutcome);
        Assert.Equal(15, capture.PhaseMilliseconds["readerPreparation"]);
        Assert.Equal(6, capture.PhaseMilliseconds["searchAndScope"]);
        Assert.Null(capture.PhaseMilliseconds["sourceVerification"]);
        Assert.Equal("phase-not-reached", capture.MissingReasons["sourceVerification"]);
        Assert.Equal(3, capture.PhaseMilliseconds["rendering"]);
        Assert.Equal(7, capture.Spans.Count);
        Assert.Equal("cancelled", ReplayPhaseMetrics.Parse(Sidecar("cancelled"), RunId, "cancelled").RunOutcome);
        Assert.Equal("failed", ReplayPhaseMetrics.Parse(Sidecar("failed"), RunId, "timeout").RunOutcome);
        Assert.Equal("timeout", ReplayPhaseMetrics.Parse(Sidecar("failed"), RunId, "timeout").RequestOutcome);
    }

    [Fact]
    public void MalformedTruncatedForeignOrUnfinishedRunsNeverBecomeTimings()
    {
        var text = Encoding.UTF8.GetString(Sidecar());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var invalid = new[]
        {
            text[..^1],
            string.Join('\n', lines[..^1]) + "\n",
            text.Replace("\"sequence\":2", "\"sequence\":1", StringComparison.Ordinal),
            text.Replace("\"spanId\":3", "\"spanId\":2", StringComparison.Ordinal),
            text.Replace("\"parentSpanId\":2", "\"parentSpanId\":99", StringComparison.Ordinal),
            text.Replace("\"phase\":\"reader.snapshot\"", "\"phase\":null", StringComparison.Ordinal),
            text.Replace("\"startedMilliseconds\":2", "\"startedMilliseconds\":-2", StringComparison.Ordinal),
            text.Replace("\"startedMilliseconds\":2", "\"startedMilliseconds\":1e999", StringComparison.Ordinal),
            text.Replace("\"startedMilliseconds\":2", "\"startedMilliseconds\":2,\"startedMilliseconds\":2", StringComparison.Ordinal),
            text.Replace("\"schema\":\"sharpdeps-phase-metrics/1\"", "\"schema\":\"unknown\"", StringComparison.Ordinal),
            text.Replace("\"outcome\":\"completed\"", "\"outcome\":\"incomplete\"", StringComparison.Ordinal),
            text.Replace("\"runId\":\"" + RunId + "\",\"sequence\":2", "\"runId\":\"ffffffffffffffffffffffffffffffff\",\"sequence\":2", StringComparison.Ordinal),
            text.Replace("\"unfinishedChildren\":false", "\"unfinishedChildren\":true", StringComparison.Ordinal),
            text + lines[^1] + "\n",
            new string(' ', 65537) + text
        };
        foreach (var value in invalid)
        {
            var capture = ReplayPhaseMetrics.Parse(Encoding.UTF8.GetBytes(value), RunId, "capture-aborted");
            Assert.Equal("unavailable", capture.Status);
            Assert.NotNull(capture.Reason);
            Assert.All(capture.PhaseMilliseconds.Values, Assert.Null);
        }
        var oversized = new byte[ReplayPhaseMetrics.MaximumBytes + 1];
        Assert.Equal("sidecar-byte-limit", ReplayPhaseMetrics.Parse(oversized, RunId, "completed").Reason);
    }

    [Fact]
    public void SequentialBoundariesRetainRawHashesAndRejectMultipleLateOrModifiedRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpdeps-replay-metrics-" + Guid.NewGuid().ToString("N"));
        var sink = Path.Combine(root, "sink");
        var receipts = Path.Combine(root, "receipts");
        Directory.CreateDirectory(receipts);
        try
        {
            var collector = new ReplayPhaseMetrics(sink);
            collector.BeginRequest();
            Directory.CreateDirectory(sink);
            var path = Path.Combine(sink, RunId + ".jsonl");
            var bytes = Sidecar();
            File.WriteAllBytes(path, bytes);
            var first = collector.Capture(receipts, "first", "completed");
            Assert.Equal("recorded", first.Status);
            var artifact = Assert.Single(first.Artifacts);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(receipts, artifact.Path)));
            Assert.Equal(FixedQueryReplay.Hash(bytes), artifact.Sha256);
            Assert.Equal(RunId + ".jsonl", artifact.OriginalName);
            collector.BeginRequest();
            var rejected = collector.Capture(receipts, "rejected", "rejected");
            Assert.Equal("request-rejected-before-admission", rejected.Reason);
            Assert.Empty(rejected.Artifacts);
            Assert.All(rejected.PhaseMilliseconds.Values, Assert.Null);
            collector.BeginRequest();
            var missing = collector.Capture(receipts, "missing", "timeout");
            Assert.Equal("no-sidecar-setup-not-admitted-or-diagnostics-unavailable", missing.Reason);
            Assert.Equal("timeout", missing.RequestOutcome);
            collector.BeginRequest();
            var interrupted = bytes[..^1];
            File.WriteAllBytes(Path.Combine(sink, new string('c', 32) + ".jsonl"), interrupted);
            var cancelled = collector.Capture(receipts, "cancelled", "cancelled");
            Assert.Equal("sidecar-truncated", cancelled.Reason);
            Assert.Equal("cancelled", cancelled.RequestOutcome);
            Assert.Equal(FixedQueryReplay.Hash(interrupted), Assert.Single(cancelled.Artifacts).Sha256);
            Assert.All(cancelled.PhaseMilliseconds.Values, Assert.Null);
            collector.BeginRequest();
            foreach (var id in new[] { new string('a', 32), new string('b', 32) })
                File.WriteAllBytes(Path.Combine(sink, id + ".jsonl"), Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(RunId, id, StringComparison.Ordinal)));
            var multiple = collector.Capture(receipts, "multiple", "completed");
            Assert.Equal("multiple-runs-in-request-boundary", multiple.Reason);
            Assert.Equal(2, multiple.Artifacts.Count);
            File.AppendAllText(path, "late\n");
            collector.BeginRequest();
            var modified = collector.Capture(receipts, "modified", "completed");
            Assert.Equal("request-association-lost", modified.Reason);
            Assert.Equal(FixedQueryReplay.Hash(File.ReadAllBytes(path)), Assert.Single(modified.Artifacts).Sha256);
            Assert.All(modified.PhaseMilliseconds.Values, Assert.Null);
            var late = new ReplayPhaseMetrics(sink);
            late.BeginRequest();
            Assert.Equal("request-association-lost", late.Capture(receipts, "late", "completed").Reason);
            Assert.Equal("disabled", new ReplayPhaseMetrics(null).Capture(receipts, "disabled", "completed").Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Sidecar(string outcome = "completed")
    {
        object Phase(long id, long? parent, string name, double start, double duration, double? exclusive = null, bool overlaps = false)
            => new
            {
                type = "phase",
                phase = name,
                spanId = id,
                parentSpanId = parent,
                startedMilliseconds = start,
                inclusiveMilliseconds = duration,
                measuredChildExclusiveMilliseconds = exclusive ?? duration,
                overlappingChildren = overlaps,
                unfinishedChildren = false
            };
        object[] payloads =
        [
            new { type = "run.start", schema = "sharpdeps-phase-metrics/1", operation = "explore", timing = "inclusive-and-measured-child-exclusive" },
            Phase(3, 2, "reader.snapshot", 2, 4), Phase(2, 1, "reader.open", 1, 10, 6),
            Phase(4, 1, "reader.snapshot", 9, 7), Phase(5, 1, "explore.search", 16, 4),
            Phase(6, 1, "explore.resolve", 17, 5), Phase(7, 1, "explore.render", 24, 3), Phase(1, null, "run", 0, 30, 6, overlaps: true),
            new { type = "run.end", outcome }
        ];
        return Encoding.UTF8.GetBytes(string.Join('\n', payloads.Select((payload, index)
            => JsonSerializer.Serialize(new { runId = RunId, sequence = index + 1, payload }))) + "\n");
    }
}
