namespace SharpDeps.Analysis.Roslyn;

using Microsoft.CodeAnalysis.MSBuild;
using SharpDeps.Analysis.Core.Diagnostics;

internal sealed class SemanticLoadMetrics(PhaseMetrics.DurationReporter reporter) : IProgress<ProjectLoadProgress>, IDisposable
{
    private readonly object gate = new();
    private int observed;
    private bool disposed;

    internal static SemanticLoadMetrics? TryCreate()
        => PhaseMetrics.ReportDurations("semantic.msbuild-progress") is { } reporter ? new(reporter) : null;

    public void Report(ProjectLoadProgress value) => Report(value.Operation, value.ElapsedTime);

    internal void Report(ProjectLoadOperation operation, TimeSpan elapsed)
    {
        var (name, bit) = operation switch
        {
            ProjectLoadOperation.Evaluate => ("evaluate", 1),
            ProjectLoadOperation.Build => ("build", 2),
            ProjectLoadOperation.Resolve => ("resolve", 4),
            _ => ("unknown", 0)
        };
        lock (gate)
        {
            if (disposed)
                return;
            observed |= bit;
            reporter.Record(name, bit == 0 ? null : elapsed.TotalMilliseconds,
                bit == 0 ? "unsupported-progress-operation" : null);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var (name, bit) in new[] { ("evaluate", 1), ("build", 2), ("resolve", 4) })
                if ((observed & bit) == 0)
                    reporter.Record(name, null, "no-progress-event");
        }
    }
}
