namespace SharpDeps.Mcp.Tests;

using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record ReplayPhaseArtifact(string Path, string OriginalName, long Bytes, string Sha256);
internal sealed record ReplayPhaseSpan(long SpanId, long? ParentSpanId, string Phase, double StartedMilliseconds,
    double InclusiveMilliseconds, double? MeasuredChildExclusiveMilliseconds, bool UnfinishedChildren);
internal sealed record ReplayPhaseCapture(string Status, string? Reason, string RequestOutcome, string? RunId,
    string? RunOutcome, IReadOnlyDictionary<string, double?> PhaseMilliseconds,
    IReadOnlyDictionary<string, string?> MissingReasons, IReadOnlyList<ReplayPhaseArtifact> Artifacts,
    IReadOnlyList<ReplayPhaseSpan> Spans)
{
    public string? PreparationOwnerId { get; init; }
    public ReplayPhaseCapture? RelatedPreparation { get; init; }
}

internal sealed class ReplayPhaseMetrics(string? directory)
{
    internal const string Definition = "Each value is the wall-time union of the listed phase intervals within one host request. "
        + "Nested/repeated intervals are counted once, not added or reported as exclusive time. Groups may overlap and are not additive. "
        + "Only closed, validated spans are evidence; elapsed spans do not establish successful work. "
        + "Search/scope includes discovery scope ranking/selection but excludes remaining discovery orchestration; source verification includes read/decode/hash/freshness, not unit selection. "
        + "Rendering covers explore.render, not transport serialization. Missing phases are null, never inferred zero. "
        + "Related preparation runs use their own clock and are never unioned into request phases; request host waits include waiting, not exclusive preparation work.";
    internal static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>
    {
        ["readerPreparation"] = ["reader.open", "reader.snapshot", "explore.prepare", "explore.prepare-standard", "explore.freeze", "explore.validate",
            "explore.derived-index", "explore.fingerprint", "explore.http-declarations", "explore.lexical-index",
            "explore.host-wait", "explore.host-prepare-wait"],
        ["searchAndScope"] = ["reader.search", "explore.resolve", "explore.search", "explore.expand",
            "explore.discovery-rank", "explore.discovery-select"],
        ["sourceVerification"] = ["source.read", "source.stored", "source.hash", "source.freshness"],
        ["rendering"] = ["explore.render"]
    };
    internal const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumLineBytes = 64 * 1024;
    private const int MaximumRecords = 65536;
    private readonly Dictionary<string, string> known = new(StringComparer.Ordinal);
    private readonly HashSet<string> preparationOwners = new(StringComparer.Ordinal);
    private bool associationLost;
    internal string? DirectoryPath => directory;

    internal void BeginRequest()
    {
        if (directory is null)
            return;
        try
        {
            var files = ReadFiles();
            if (files.Count != known.Count || files.Any(file => !known.TryGetValue(file.Key, out var hash)
                || FixedQueryReplay.Hash(file.Value) != hash))
                associationLost = true;
        }
        catch (Exception error) when (CaptureFailure(error))
        {
            associationLost = true;
        }
    }

    internal ReplayPhaseCapture Capture(string artifactDirectory, string host, string requestOutcome)
    {
        var artifacts = new List<ReplayPhaseArtifact>();
        ReplayPhaseCapture capture;
        try
        {
            if (directory is null)
                capture = Missing("disabled", "not-enabled", requestOutcome, artifacts);
            else
            {
                var files = ReadFiles();
                if (known.Any(file => !files.TryGetValue(file.Key, out var bytes) || FixedQueryReplay.Hash(bytes) != file.Value))
                    associationLost = true;
                var added = files.Where(file => !known.TryGetValue(file.Key, out var hash) || FixedQueryReplay.Hash(file.Value) != hash).ToArray();
                foreach (var file in added)
                {
                    var name = host + ".phase-" + artifacts.Count + ".jsonl";
                    File.WriteAllBytes(Path.Combine(artifactDirectory, name), file.Value);
                    artifacts.Add(new(name, file.Key, file.Value.Length, FixedQueryReplay.Hash(file.Value)));
                }
                known.Clear();
                foreach (var file in files)
                    known.Add(file.Key, FixedQueryReplay.Hash(file.Value));
                capture = associationLost ? Missing("unavailable", "request-association-lost", requestOutcome, artifacts)
                    : added.Length == 0 ? Missing("unavailable", requestOutcome == "rejected"
                        ? "request-rejected-before-admission" : "no-sidecar-setup-not-admitted-or-diagnostics-unavailable", requestOutcome, artifacts)
                    : added.Length != 1 ? Missing("unavailable", "multiple-runs-in-request-boundary", requestOutcome, artifacts)
                    : Parse(added[0].Value, Path.GetFileNameWithoutExtension(added[0].Key), requestOutcome) with { Artifacts = artifacts };
                if (capture.PreparationOwnerId is not null)
                    capture = capture with { RelatedPreparation = CapturePreparation(artifactDirectory, host, capture,
                        MaximumBytes - (int)artifacts.Sum(artifact => artifact.Bytes)) };
            }
        }
        catch (Exception error) when (CaptureFailure(error))
        {
            associationLost = true;
            capture = Missing("unavailable", error is InvalidDataException ? error.Message : "sidecar-read-or-retention-failed", requestOutcome, artifacts);
        }
        FixedQueryReplay.WriteJson(Path.Combine(artifactDirectory, host + ".phase-metrics.json"), capture);
        return capture;
    }

    internal void CaptureOutsideRequest(string artifactDirectory)
    {
        BeginRequest();
        if (associationLost)
            Capture(artifactDirectory, "mcp-unbound", "outside-request-boundary");
    }

    private ReplayPhaseCapture CapturePreparation(string artifactDirectory, string host, ReplayPhaseCapture request, int remainingBytes)
    {
        var ownerId = request.PreparationOwnerId!;
        var artifacts = new List<ReplayPhaseArtifact>();
        try
        {
            Check(preparationOwners.Add(ownerId), "preparation-owner-already-associated");
            var ownerDirectory = Path.GetFullPath(Path.Combine(directory!, "owners", ownerId));
            Check(Path.GetRelativePath(Path.GetFullPath(directory!), ownerDirectory) == Path.Combine("owners", ownerId),
                "preparation-path-invalid");
            var files = ReadFiles(ownerDirectory, remainingBytes, 1);
            if (files.Count == 0)
                return Missing("pending", "preparation-trace-not-available", request.RequestOutcome, artifacts)
                    with { PreparationOwnerId = ownerId };
            var file = files.Single();
            var name = host + ".prepare-phase.jsonl";
            File.WriteAllBytes(Path.Combine(artifactDirectory, name), file.Value);
            artifacts.Add(new(name, Path.Combine("owners", ownerId, file.Key), file.Value.Length, FixedQueryReplay.Hash(file.Value)));
            return Parse(file.Value, Path.GetFileNameWithoutExtension(file.Key), request.RequestOutcome, ownerId)
                with { Artifacts = artifacts, PreparationOwnerId = ownerId };
        }
        catch (Exception error) when (CaptureFailure(error))
        {
            return Missing("unavailable", error is InvalidDataException ? error.Message : "preparation-read-or-retention-failed",
                request.RequestOutcome, artifacts) with { PreparationOwnerId = ownerId };
        }
    }

    private Dictionary<string, byte[]> ReadFiles()
        => ReadFiles(directory!);

    private static Dictionary<string, byte[]> ReadFiles(string sourceDirectory, int byteLimit = MaximumBytes, int fileLimit = 4096)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(sourceDirectory))
            return files;
        for (var ancestor = new DirectoryInfo(sourceDirectory); ancestor is not null; ancestor = ancestor.Parent)
            Check((ancestor.Attributes & FileAttributes.ReparsePoint) == 0, "linked-sidecar-directory");
        var total = 0;
        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            Check(files.Count < fileLimit, "sidecar-file-count-limit");
            var info = new FileInfo(path);
            Check((info.Attributes & FileAttributes.ReparsePoint) == 0, "linked-sidecar-file");
            Check(Regex.IsMatch(info.Name, "\\A[a-f0-9]{32}\\.jsonl\\z", RegexOptions.CultureInvariant), "unexpected-sidecar-filename");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Check(stream.Length <= byteLimit - total, "sidecar-byte-limit");
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                Check(output.Length + read <= byteLimit - total, "sidecar-byte-limit");
                output.Write(buffer, 0, read);
            }
            var bytes = output.ToArray();
            total += bytes.Length;
            files.Add(info.Name, bytes);
        }
        return files;
    }

    internal static ReplayPhaseCapture Parse(byte[] bytes, string expectedRunId, string requestOutcome, string? expectedPreparationOwnerId = null)
    {
        try
        {
            Check(bytes.Length <= MaximumBytes, "sidecar-byte-limit");
            if (expectedPreparationOwnerId is not null && (bytes.Length == 0 || bytes[^1] != (byte)'\n'))
                return Missing("pending", "sidecar-truncated", requestOutcome, []) with { RunId = expectedRunId };
            Check(bytes.Length > 0 && bytes[^1] == (byte)'\n', "sidecar-truncated");
            var spans = new List<ReplayPhaseSpan>();
            var byId = new Dictionary<long, ReplayPhaseSpan>();
            var reads = new Dictionary<long, (long ParentSpanId, string Phase)>();
            string? runOutcome = null;
            string? preparationOwnerId = null;
            var records = 0;
            for (var offset = 0; offset < bytes.Length;)
            {
                var end = Array.IndexOf(bytes, (byte)'\n', offset);
                Check(end - offset <= MaximumLineBytes, "sidecar-line-limit");
                Check(++records <= MaximumRecords, "sidecar-record-limit");
                using var document = JsonDocument.Parse(bytes.AsMemory(offset, end - offset), new() { MaxDepth = 8 });
                offset = end + 1;
                var record = document.RootElement;
                UniqueProperties(record);
                Check(record.GetProperty("runId").GetString() == expectedRunId
                    && record.GetProperty("sequence").GetInt64() == records, "sidecar-run-or-sequence-mismatch");
                var payload = record.GetProperty("payload");
                var type = payload.GetProperty("type").GetString();
                Check(runOutcome is null, "sidecar-record-after-end");
                if (records == 1)
                {
                    Check(type == "run.start" && payload.GetProperty("schema").GetString() == "sharpdeps-phase-metrics/1"
                        && payload.GetProperty("operation").GetString() == (expectedPreparationOwnerId is null ? "explore" : "explore-prepare")
                        && payload.GetProperty("timing").GetString() == "inclusive-and-measured-child-exclusive", "sidecar-start-mismatch");
                }
                else if (type == "run.end")
                {
                    runOutcome = payload.GetProperty("outcome").GetString();
                    Check(runOutcome is "completed" or "failed" or "cancelled" or "incomplete", "sidecar-outcome-invalid");
                }
                else if (type == "phase")
                {
                    var parent = payload.GetProperty("parentSpanId");
                    var exclusive = payload.GetProperty("measuredChildExclusiveMilliseconds");
                    var span = new ReplayPhaseSpan(payload.GetProperty("spanId").GetInt64(),
                        parent.ValueKind == JsonValueKind.Null ? null : parent.GetInt64(), payload.GetProperty("phase").GetString()!,
                        payload.GetProperty("startedMilliseconds").GetDouble(), payload.GetProperty("inclusiveMilliseconds").GetDouble(),
                        exclusive.ValueKind == JsonValueKind.Null ? null : exclusive.GetDouble(), payload.GetProperty("unfinishedChildren").GetBoolean());
                    Check(span.SpanId > 0 && span.Phase is not null && Label(span.Phase) && double.IsFinite(span.StartedMilliseconds) && span.StartedMilliseconds >= 0
                        && double.IsFinite(span.InclusiveMilliseconds) && span.InclusiveMilliseconds >= 0
                        && double.IsFinite(span.StartedMilliseconds + span.InclusiveMilliseconds)
                        && (span.MeasuredChildExclusiveMilliseconds is { } value
                            ? double.IsFinite(value) && value >= 0 && value <= span.InclusiveMilliseconds && !span.UnfinishedChildren
                            : span.UnfinishedChildren), "sidecar-span-invalid");
                    _ = payload.GetProperty("overlappingChildren").GetBoolean();
                    byId.Add(span.SpanId, span);
                    spans.Add(span);
                }
                else if (type == "read")
                {
                    Properties(payload, "type", "phase", "spanId", "parentSpanId", "category", "bytes", "reason");
                    var category = payload.GetProperty("category").GetString();
                    var phase = payload.GetProperty("phase").GetString();
                    var id = payload.GetProperty("spanId").GetInt64();
                    var parent = payload.GetProperty("parentSpanId").GetInt64();
                    var count = payload.GetProperty("bytes");
                    var reason = payload.GetProperty("reason").GetString();
                    Check(category is "workspace" or "sdk" or "git" or "package" or "tool"
                        && phase == "input.read-" + category && id > 0 && parent > 0 && parent < id
                        && (count.ValueKind == JsonValueKind.Null
                            ? reason is "stream-position-unavailable" or "invalid-stream-position"
                            : count.TryGetInt64(out var value) && value >= 0 && reason is null), "sidecar-read-invalid");
                    Check(!byId.ContainsKey(id) && !byId.ContainsKey(parent) && reads.TryAdd(id, (parent, "input.read-" + category)), "sidecar-read-span-invalid");
                }
                else if (type == "reported-duration")
                {
                    Properties(payload, "type", "source", "operation", "elapsedMilliseconds", "reason", "timing");
                    var source = payload.GetProperty("source").GetString();
                    var operation = payload.GetProperty("operation").GetString();
                    var elapsed = payload.GetProperty("elapsedMilliseconds");
                    var reason = payload.GetProperty("reason").GetString();
                    Check(source is not null && Label(source) && operation is not null && Label(operation)
                        && payload.GetProperty("timing").GetString() == "reported-operation-elapsed"
                        && (elapsed.ValueKind == JsonValueKind.Null
                            ? reason is "no-progress-event" or "invalid-elapsed" or "unsupported-progress-operation"
                            : double.IsFinite(elapsed.GetDouble()) && elapsed.GetDouble() >= 0 && reason is null),
                        "sidecar-reported-duration-invalid");
                }
                else if (type == "decision" && payload.TryGetProperty("stage", out var stage)
                    && stage.GetString() == "explore.host-owner")
                {
                    Properties(payload, "type", "stage", "reason");
                    var ownerId = payload.GetProperty("reason").GetString();
                    Check(preparationOwnerId is null && ownerId is not null
                        && Regex.IsMatch(ownerId, "\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant), "preparation-owner-link-invalid");
                    preparationOwnerId = ownerId;
                    Check(expectedPreparationOwnerId is null || preparationOwnerId == expectedPreparationOwnerId, "preparation-owner-mismatch");
                }
                else
                    Check(type is "count" or "decision", "sidecar-event-invalid");
            }
            if (runOutcome is null && expectedPreparationOwnerId is not null)
                return Missing("pending", "sidecar-run-end-missing", requestOutcome, [])
                    with { RunId = expectedRunId, PreparationOwnerId = preparationOwnerId };
            Check(runOutcome is not null, "sidecar-run-end-missing");
            Check(expectedPreparationOwnerId is null || preparationOwnerId == expectedPreparationOwnerId, "preparation-owner-mismatch");
            foreach (var read in reads)
                Check(byId.TryGetValue(read.Key, out var span) && span.ParentSpanId == read.Value.ParentSpanId
                    && span.Phase == read.Value.Phase, "sidecar-read-span-invalid");
            var root = spans.Single(span => span.ParentSpanId is null);
            Check(root.Phase == "run", "sidecar-root-invalid");
            foreach (var span in spans.Where(span => span.ParentSpanId is not null))
            {
                Check(byId.TryGetValue(span.ParentSpanId!.Value, out var parent) && parent.SpanId < span.SpanId
                    && span.StartedMilliseconds >= parent.StartedMilliseconds - 0.001
                    && span.StartedMilliseconds + span.InclusiveMilliseconds <= parent.StartedMilliseconds + parent.InclusiveMilliseconds + 0.001,
                    "sidecar-parent-invalid");
            }
            Check(!spans.Any(span => span.UnfinishedChildren), "sidecar-unfinished-spans");
            Check(runOutcome != "incomplete", "sidecar-run-incomplete");
            var times = new Dictionary<string, double?>();
            var reasons = new Dictionary<string, string?>();
            foreach (var group in Groups)
            {
                var intervals = spans.Where(span => group.Value.Contains(span.Phase, StringComparer.Ordinal))
                    .OrderBy(span => span.StartedMilliseconds).ToArray();
                double union = 0, previousEnd = 0;
                foreach (var span in intervals)
                {
                    var end = span.StartedMilliseconds + span.InclusiveMilliseconds;
                    union += Math.Max(0, end - Math.Max(previousEnd, span.StartedMilliseconds));
                    previousEnd = Math.Max(previousEnd, end);
                }
                times.Add(group.Key, intervals.Length == 0 ? null : union);
                reasons.Add(group.Key, intervals.Length == 0 ? "phase-not-reached" : null);
            }
            return new("recorded", null, requestOutcome, expectedRunId, runOutcome, times, reasons, [], spans)
                { PreparationOwnerId = preparationOwnerId };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or InvalidDataException or FormatException or OverflowException)
        {
            return Missing("unavailable", error is InvalidDataException ? error.Message : "sidecar-invalid", requestOutcome, []);
        }
    }

    private static ReplayPhaseCapture Missing(string status, string reason, string outcome, IReadOnlyList<ReplayPhaseArtifact> artifacts)
        => new(status, reason, outcome, null, null, Groups.ToDictionary(group => group.Key, _ => (double?)null),
            Groups.ToDictionary(group => group.Key, _ => (string?)reason), artifacts, []);
    private static void Properties(JsonElement payload, params string[] names)
        => Check(payload.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names),
            "sidecar-event-shape-invalid");
    private static bool Label(string value) => value.Length is > 0 and <= 96
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    private static bool CaptureFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Check(names.Add(property.Name), "sidecar-duplicate-property");
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                UniqueProperties(item);
    }
    private static void Check(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidDataException(reason);
    }
}
