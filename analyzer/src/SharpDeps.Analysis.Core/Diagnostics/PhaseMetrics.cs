namespace SharpDeps.Analysis.Core.Diagnostics;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

/// <summary>Optional process-local diagnostic runs. Never put paths, source text or environment values in labels.</summary>
internal static class PhaseMetrics
{
    private static readonly AsyncLocal<Scope?> Ambient = new();
    public static bool Enabled => Ambient.Value is not null;

    public static Run? TryStart(string? directory, string operation, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        try
        {
            var output = IsolatedDirectory(directory, workspaceRoot, DependencyRoots());
            if (output is null)
                return null;
            return StartCore(output, operation);
        }
        catch (Exception error) when (IsDiagnosticFailure(error))
        {
            return null;
        }
    }

    internal static bool IsOutputIsolated(string directory, string workspaceRoot, IEnumerable<string> dependencyRoots)
    {
        try
        {
            return IsolatedDirectory(directory, workspaceRoot, dependencyRoots) is not null;
        }
        catch (Exception error) when (IsDiagnosticFailure(error))
        {
            return false;
        }
    }

    private static string? IsolatedDirectory(string directory, string workspaceRoot, IEnumerable<string> dependencyRoots)
    {
        var output = MetricsPath.Canonicalize(directory);
        var workspace = MetricsPath.Canonicalize(workspaceRoot);
        var relative = Path.GetRelativePath(workspace, output);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0].Equals(".sharpdeps", StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var dependency in dependencyRoots)
        {
            var input = MetricsPath.Canonicalize(dependency);
            if (Contains(input, output) || Contains(output, input))
                return null;
        }
        return output;
    }

    private static bool Contains(string root, string path)
    {
        // Case-insensitive comparison is conservative on case-sensitive filesystems.
        var parent = Path.TrimEndingDirectorySeparator(root);
        var child = Path.TrimEndingDirectorySeparator(path);
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> DependencyRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured)
            yield return configured;
        foreach (var home in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("HOME") })
            if (!string.IsNullOrWhiteSpace(home))
                yield return Path.Combine(home, ".nuget", "packages");
    }

    public static Run? Start(string directory, string operation)
    {
        try
        {
            return Label(operation) ? StartCore(MetricsPath.Canonicalize(directory), operation) : null;
        }
        catch (Exception error) when (IsDiagnosticFailure(error))
        {
            return null;
        }
    }

    private static Run? StartCore(string directory, string operation)
    {
        if (!Label(operation))
            return null;
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var stream = new FileStream(Path.Combine(directory, id + ".jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return new Run(id, operation, new StreamWriter(stream, new UTF8Encoding(false)));
    }

    public static Scope? Measure(string phase)
        => Ambient.Value is { } parent && Label(phase) ? new Scope(parent.Owner, parent, phase) : null;

    public static void Count(string name, long value)
    {
        if (Ambient.Value is { } scope && Label(name))
            scope.Owner.Write(new { type = "count", phase = scope.Phase, name, value });
    }

    public static void Decision(string stage, string reason)
    {
        if (Ambient.Value is { } scope && Label(stage) && Label(reason))
            scope.Owner.Write(new { type = "decision", stage, reason });
    }

    internal enum ReadCategory { Workspace, Sdk, Git, Package, Tool }

    public static ReadObservation? ObserveRead(ReadCategory category, Stream input)
    {
        if (Ambient.Value is null)
            return null;
        var label = category switch
        {
            ReadCategory.Workspace => "workspace",
            ReadCategory.Sdk => "sdk",
            ReadCategory.Git => "git",
            ReadCategory.Package => "package",
            ReadCategory.Tool => "tool",
            _ => null
        };
        if (label is null)
            return null;
        long? position = null;
        try
        {
            position = input.Position;
        }
        catch (Exception error) when (IsDiagnosticFailure(error))
        {
        }
        return new ReadObservation(label, input, position);
    }

    public static DurationReporter? ReportDurations(string source)
        => Ambient.Value is { } scope && Label(source) ? new(scope.Owner, source) : null;

    public sealed class DurationReporter(Run owner, string source)
    {
        public void Record(string operation, double? milliseconds, string? reason = null)
        {
            if (!Label(operation) || reason is not null && !Label(reason))
                return;
            if (milliseconds is { } value && (!double.IsFinite(value) || value < 0))
            {
                milliseconds = null;
                reason = "invalid-elapsed";
            }
            owner.Write(new { type = "reported-duration", source, operation, elapsedMilliseconds = milliseconds,
                reason, timing = "reported-operation-elapsed" });
        }
    }

    public sealed class ReadObservation : IDisposable
    {
        private readonly string category;
        private readonly Stream input;
        private readonly long? start;
        private readonly Scope scope;
        private bool disposed;

        internal ReadObservation(string category, Stream input, long? start)
        {
            this.category = category;
            this.input = input;
            this.start = start;
            scope = Measure("input.read-" + category)!;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            long? bytes = null;
            string? reason = null;
            try
            {
                var position = input.Position;
                if (start is null)
                    reason = "stream-position-unavailable";
                else if (position < start.Value)
                    reason = "invalid-stream-position";
                else
                    bytes = position - start.Value;
            }
            catch (Exception error) when (IsDiagnosticFailure(error))
            {
                reason = "stream-position-unavailable";
            }
            scope.Owner.Write(new { type = "read", phase = scope.Phase, spanId = scope.SpanId, parentSpanId = scope.ParentSpanId, category, bytes, reason });
            scope.Dispose();
        }
    }

    private static bool Label(string value) => value.Length is > 0 and <= 96
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static bool IsDiagnosticFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException or InvalidOperationException
        or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    public sealed class Run : IDisposable
    {
        private readonly object gate = new();
        private readonly StreamWriter writer;
        private readonly Scope root;
        private readonly long allocated = GC.GetTotalAllocatedBytes();
        private readonly int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        private readonly TimeSpan? cpu = Cpu();
        private long sequence;
        private long scopeId;
        internal long StartTicks { get; } = Stopwatch.GetTimestamp();
        internal long NextScopeId() => Interlocked.Increment(ref scopeId);
        private bool failed;
        private bool disposed;
        private string outcome = "incomplete";
        public string RunId { get; }

        internal Run(string id, string operation, StreamWriter writer)
        {
            RunId = id;
            this.writer = writer;
            Write(new
            {
                type = "run.start",
                schema = "sharpdeps-phase-metrics/1",
                operation,
                utc = DateTimeOffset.UtcNow.ToString("O"),
                timing = "inclusive-and-measured-child-exclusive"
            });
            root = new Scope(this, Ambient.Value, "run", isRoot: true);
        }

        public void Complete() => outcome = "completed";
        public void Fail(bool cancelled) => outcome = cancelled ? "cancelled" : "failed";

        internal void Write(object payload)
        {
            lock (gate)
            {
                if (failed || disposed)
                    return;
                try
                {
                    writer.WriteLine(JsonSerializer.Serialize(new { runId = RunId, sequence = ++sequence, payload }));
                }
                catch (Exception error) when (IsDiagnosticFailure(error))
                {
                    failed = true;
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            root.Dispose();
            var endCpu = Cpu();
            Write(new
            {
                type = "run.end",
                outcome,
                processWideCpuMilliseconds = cpu is { } start && endCpu is { } end ? (double?)(end - start).TotalMilliseconds : null,
                processWideAllocatedBytesDelta = GC.GetTotalAllocatedBytes() - allocated,
                processWideGen0Collections = GC.CollectionCount(0) - collections[0],
                processWideGen1Collections = GC.CollectionCount(1) - collections[1],
                processWideGen2Collections = GC.CollectionCount(2) - collections[2]
            });
            lock (gate)
            {
                disposed = true;
                try
                {
                    writer.Dispose();
                }
                catch (Exception error) when (IsDiagnosticFailure(error))
                {
                    failed = true;
                }
            }
        }

        private static TimeSpan? Cpu()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.TotalProcessorTime;
            }
            catch (Exception error) when (IsDiagnosticFailure(error))
            {
                return null;
            }
        }
    }

    public sealed class Scope : IDisposable
    {
        internal Run Owner { get; }
        internal string Phase { get; }
        internal long SpanId => id;
        internal long? ParentSpanId => isRoot ? null : parent?.id;
        private readonly Scope? parent;
        private readonly long start = Stopwatch.GetTimestamp();
        private readonly bool isRoot;
        private readonly List<(long Start, long End)> children = [];
        private int activeChildren;
        private readonly long id;
        private bool disposed;
        internal Scope(Run owner, Scope? parent, string phase, bool isRoot = false)
        {
            Owner = owner;
            this.parent = parent;
            Phase = phase;
            this.isRoot = isRoot;
            id = owner.NextScopeId();
            if (!isRoot && parent is not null && ReferenceEquals(parent.Owner, owner))
                Interlocked.Increment(ref parent.activeChildren);
            Ambient.Value = this;
        }
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            var end = Stopwatch.GetTimestamp();
            var elapsed = end - start;
            if (!isRoot && parent is not null && ReferenceEquals(parent.Owner, Owner))
            {
                lock (parent.children)
                    parent.children.Add((start, end));
                Interlocked.Decrement(ref parent.activeChildren);
            }
            Ambient.Value = parent;
            long union = 0, sum = 0, lastEnd = start;
            lock (children)
                foreach (var interval in children.OrderBy(child => child.Start))
                {
                    var clippedStart = Math.Clamp(interval.Start, start, end);
                    var clippedEnd = Math.Clamp(interval.End, clippedStart, end);
                    sum += clippedEnd - clippedStart;
                    union += Math.Max(0, clippedEnd - Math.Max(lastEnd, clippedStart));
                    lastEnd = Math.Max(lastEnd, clippedEnd);
                }
            var unfinished = Volatile.Read(ref activeChildren) != 0;
            Owner.Write(new
            {
                type = "phase",
                phase = Phase,
                spanId = SpanId,
                parentSpanId = ParentSpanId,
                startedMilliseconds = (start - Owner.StartTicks) * 1000d / Stopwatch.Frequency,
                inclusiveMilliseconds = elapsed * 1000d / Stopwatch.Frequency,
                measuredChildExclusiveMilliseconds = unfinished ? (double?)null : (elapsed - union) * 1000d / Stopwatch.Frequency,
                overlappingChildren = sum > union,
                unfinishedChildren = unfinished
            });
        }
    }
}
