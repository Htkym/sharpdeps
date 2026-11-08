using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SharpDeps.Analysis.Core.Diagnostics;

namespace SharpDeps.Analysis.Roslyn;

internal sealed class SemanticColdLoad(Workspace? workspace, Solution? solution, SemanticLoadOptions options,
    IReadOnlyList<ProbeDiagnostic> diagnostics, SemanticLoadResult result) : IDisposable
{
    private Workspace? ownedWorkspace = workspace;
    internal Solution? Solution { get; } = solution;
    internal SemanticLoadOptions Options { get; } = options;
    internal IReadOnlyList<ProbeDiagnostic> Diagnostics { get; } = diagnostics;
    internal SemanticLoadResult Result { get; } = result;
    internal Workspace? TakeWorkspace() => Interlocked.Exchange(ref ownedWorkspace, null);
    public void Dispose() => Interlocked.Exchange(ref ownedWorkspace, null)?.Dispose();
}

internal sealed record SemanticSessionProof(string Key, string StructureHash, string DependencyHash,
    string EnvironmentHash, string SdkPath);

// Text is strictly decoded captured input, including any BOM. RawHash describes the original bytes.
internal sealed record SemanticSourceInput(string Text, string RawHash, string EncodingName = "utf8");

internal sealed class SemanticSessionCandidate : IDisposable
{
    private Workspace? ownedWorkspace;
    internal SemanticSessionCandidate(SemanticSessionOwner owner, long revision, Workspace? workspace,
        Solution solution, SemanticLoadOptions options, IReadOnlyList<ProbeDiagnostic> diagnostics,
        SemanticLoadResult result, SemanticSessionProof proof,
        IReadOnlyDictionary<string, SemanticSourceInput> sources, bool warm, IReadOnlySet<string> dirty)
    {
        Owner = owner;
        Revision = revision;
        ownedWorkspace = workspace;
        Solution = solution;
        Options = options;
        Diagnostics = diagnostics;
        Result = result;
        Proof = proof;
        Sources = sources;
        Warm = warm;
        DirtyVariants = dirty;
    }
    internal SemanticSessionOwner Owner { get; }
    internal long Revision { get; }
    internal Solution? Solution { get; private set; }
    internal SemanticLoadOptions Options { get; }
    internal IReadOnlyList<ProbeDiagnostic> Diagnostics { get; }
    internal SemanticLoadResult Result { get; }
    internal SemanticSessionProof Proof { get; }
    internal IReadOnlyDictionary<string, SemanticSourceInput> Sources { get; }
    internal bool Warm { get; }
    internal IReadOnlySet<string> DirtyVariants { get; }
    internal Workspace? TakeWorkspace() => Interlocked.Exchange(ref ownedWorkspace, null);
    public void Dispose()
    {
        Solution = null;
        Interlocked.Exchange(ref ownedWorkspace, null)?.Dispose();
    }
}

/// <summary>Single-writer semantic state. Candidates are adopted only after durable publication.</summary>
internal sealed class SemanticSessionOwner : IDisposable
{
    private const int MaximumLineage = 32;
    private const int MaximumDocuments = 10_000;
    private const long MaximumSourceChars = 32L * 1024 * 1024;
    private const long MaximumLineageSourceChars = 128L * 1024 * 1024;
    private static readonly TimeSpan IdleExpiry = TimeSpan.FromMinutes(5);
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly TimeProvider clock;
    internal SemanticSessionOwner(TimeProvider? timeProvider = null) => clock = timeProvider ?? TimeProvider.System;
    private Workspace? workspace;
    private Solution? committed;
    private SemanticLoadOptions? options;
    private IReadOnlyList<ProbeDiagnostic> diagnostics = [];
    private SemanticSessionProof? proof;
    private IReadOnlyDictionary<string, SemanticSourceInput>? sources;
    private long generation;
    private long revision;
    private int lineage;
    private long lineageSourceChars;
    private long lastAdopted;
    internal bool HasCommitted => committed is not null;

    internal async Task<SemanticSessionCandidate?> PrepareColdAsync(SemanticColdLoad loaded,
        SemanticSessionProof receipt, IReadOnlyDictionary<string, SemanticSourceInput> captured,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var solution = loaded.Solution;
        if (solution is null)
            return null;
        string? reason;
        try
        {
            reason = await UnsupportedReasonAsync(solution, loaded.Result, captured, token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            PhaseMetrics.Decision("semantic.session", "profile-unavailable");
            return null;
        }
        if (reason is not null)
        {
            PhaseMetrics.Decision("semantic.session", reason);
            return null;
        }
        var capturedSources = Copy(captured);
        var resource = loaded.TakeWorkspace();
        if (resource is null)
            return null;
        return new(this, revision, resource, solution, loaded.Options, loaded.Diagnostics,
            loaded.Result, receipt, capturedSources, false, new HashSet<string>(StringComparer.Ordinal));
    }

    internal async Task<SemanticSessionCandidate?> TryUpdateAsync(SemanticSessionProof receipt,
        long baselineGeneration, IReadOnlyDictionary<string, SemanticSourceInput> captured,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var metrics = PhaseMetrics.Measure("semantic.session-update");
        if (committed is null || options is null || sources is null || proof != receipt
            || generation != baselineGeneration || lineage >= MaximumLineage
            || clock.GetElapsedTime(lastAdopted) >= IdleExpiry)
        {
            Evict();
            PhaseMetrics.Decision("semantic.session", "cold-required");
            return null;
        }
        if (captured.Count != sources.Count || !captured.Keys.ToHashSet(Paths).SetEquals(sources.Keys)
            || SourceChars(committed, captured) > Math.Min(MaximumSourceChars, MaximumLineageSourceChars - lineageSourceChars))
        {
            Evict();
            return null;
        }
        var candidate = committed;
        var dirty = new HashSet<ProjectId>();
        var updates = 0;
        foreach (var group in committed.Projects.SelectMany(p => p.Documents)
                     .GroupBy(d => Path.GetFullPath(d.FilePath!), Paths))
        {
            token.ThrowIfCancellationRequested();
            var before = sources[group.Key];
            var after = captured[group.Key];
            if (before.RawHash == after.RawHash)
            {
                if (before.Text != after.Text)
                {
                    Evict();
                    return null;
                }
                continue;
            }
            // Changing the preamble is not a body-only edit; let MSBuild load its encoding again.
            if (before.EncodingName != after.EncodingName
                || before.Text.StartsWith('\uFEFF') != after.Text.StartsWith('\uFEFF'))
            {
                Evict();
                return null;
            }
            foreach (var document in group)
            {
                var oldText = await document.GetTextAsync(token);
                var next = SourceBody(after.Text, oldText.ToString(), before.Text);
                if (next is null)
                {
                    Evict();
                    return null;
                }
                var updated = Replace(oldText, next, token);
                if (!updated.ContentEquals(SourceText.From(next)))
                    throw new InvalidOperationException("Semantic edit mismatch.");
                candidate = candidate.WithDocumentText(document.Id, updated, PreservationMode.PreserveIdentity);
                dirty.Add(document.Project.Id);
                updates++;
            }
        }
        var reverse = committed.GetProjectDependencyGraph();
        foreach (var owner in dirty.ToArray())
            dirty.UnionWith(reverse.GetProjectsThatTransitivelyDependOnThisProject(owner));
        // Evaluation never uses an old SemanticModel or ISymbol with the candidate trees.
        var result = await SemanticLoader.EvaluateAsync(candidate, options, diagnostics, token);
        token.ThrowIfCancellationRequested();
        if (result.Report.Variants.Any(v => v.ErrorDiagnosticCount != 0))
        {
            PhaseMetrics.Decision("semantic.session", "compiler-error-candidate-cold");
            return null;
        }
        var keys = dirty.Select(id => candidate.GetProject(id)!)
            .Select(p => SemanticLoader.VariantKeyFor(p, options)).ToHashSet(StringComparer.Ordinal);
        PhaseMetrics.Count("semantic.syntax-updates-attempted", updates);
        PhaseMetrics.Decision("semantic.session", "candidate-created");
        return new(this, revision, null, candidate, options, diagnostics, result, receipt, Copy(captured), true, keys);
    }

    internal bool Adopt(SemanticSessionCandidate candidate, long committedGeneration)
    {
        if (candidate.Owner != this || candidate.Revision != revision || candidate.Solution is null
            || committedGeneration <= generation || (candidate.Warm && committed is null))
        {
            candidate.Dispose();
            Evict();
            return false;
        }
        var replacement = candidate.TakeWorkspace();
        if (!candidate.Warm)
        {
            var retired = workspace;
            workspace = replacement;
            retired?.Dispose();
            lineage = 0;
            lineageSourceChars = 0;
        }
        committed = candidate.Solution;
        options = candidate.Options;
        diagnostics = candidate.Diagnostics;
        proof = candidate.Proof;
        sources = candidate.Sources;
        generation = committedGeneration;
        lineage++;
        lineageSourceChars += SourceChars(candidate.Solution, candidate.Sources);
        revision++;
        lastAdopted = clock.GetTimestamp();
        return true;
    }

    internal bool AdvanceCommitted(SemanticSessionProof receipt, long baselineGeneration, long nextGeneration)
    {
        if (committed is null || proof != receipt || generation != baselineGeneration
            || nextGeneration <= generation || clock.GetElapsedTime(lastAdopted) >= IdleExpiry)
        {
            Evict();
            return false;
        }
        generation = nextGeneration;
        revision++;
        lastAdopted = clock.GetTimestamp();
        return true;
    }

    internal void Evict()
    {
        revision++;
        committed = null;
        options = null;
        proof = null;
        sources = null;
        diagnostics = [];
        var retired = workspace;
        workspace = null;
        generation = 0;
        lineage = 0;
        lineageSourceChars = 0;
        retired?.Dispose();
    }
    public void Dispose() => Evict();

    private static IReadOnlyDictionary<string, SemanticSourceInput> Copy(IReadOnlyDictionary<string, SemanticSourceInput> captured)
        => captured.ToDictionary(p => Path.GetFullPath(p.Key), p => p.Value, Paths);

    private static async Task<string?> UnsupportedReasonAsync(Solution solution, SemanticLoadResult result,
        IReadOnlyDictionary<string, SemanticSourceInput> captured, CancellationToken token)
    {
        if (result.Report.Variants.Any(v => v.ErrorDiagnosticCount != 0))
            return "compiler-error-baseline-cold";
        if (result.GeneratedDocuments.Count != 0 || result.Report.Diagnostics.Count != 0
            || result.Report.Coverage.Failed != 0 || result.Report.Coverage.Unresolved != 0
            || result.Report.Variants.Any(v => v.LoadState != "loaded" || !v.CompilationObtained)
            || captured.Values.Sum(s => (long)s.Text.Length) > MaximumSourceChars)
            return "incomplete-or-over-budget-profile";
        var actual = new HashSet<string>(Paths);
        foreach (var project in solution.Projects)
        {
            token.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp)
                return "non-csharp-profile";
            if (project.AdditionalDocumentIds.Count != 0)
                return "additional-input-profile";
            if (project.AnalyzerReferences.Any(a => a.GetGenerators(LanguageNames.CSharp).Length != 0))
                return "generator-profile";
            foreach (var document in project.Documents)
            {
                if (document.FilePath is null || !Path.IsPathFullyQualified(document.FilePath))
                    return "non-physical-source";
                var path = Path.GetFullPath(document.FilePath);
                actual.Add(path);
                if (!captured.TryGetValue(path, out var input))
                    return "source-inventory-mismatch";
                var text = (await document.GetTextAsync(token)).ToString();
                if (text != input.Text && !(input.Text.StartsWith('\uFEFF') && text == input.Text[1..]))
                    return "compiler-text-mismatch";
            }
        }
        return actual.SetEquals(captured.Keys)
            && solution.Projects.Sum(p => p.DocumentIds.Count) <= MaximumDocuments
            && SourceChars(solution, captured) <= MaximumSourceChars ? null : "source-inventory-or-budget";
    }

    // Count every owner, since linked inputs have a distinct tree per project/parse profile.
    private static long SourceChars(Solution solution, IReadOnlyDictionary<string, SemanticSourceInput> captured)
        => solution.Projects.SelectMany(p => p.Documents).Sum(d => (long)captured[Path.GetFullPath(d.FilePath!)].Text.Length);

    private static string? SourceBody(string next, string oldBody, string oldCaptured)
        => oldBody == oldCaptured ? next
            : oldCaptured.StartsWith('\uFEFF') && oldBody == oldCaptured[1..] ? next[1..] : null;

    internal static SourceText Replace(SourceText oldText, string next, CancellationToken token)
    {
        var start = 0;
        var limit = Math.Min(oldText.Length, next.Length);
        while (start < limit && oldText[start] == next[start])
        {
            if ((start & 4095) == 0)
                token.ThrowIfCancellationRequested();
            start++;
        }
        var end = oldText.Length;
        var nextEnd = next.Length;
        while (end > start && nextEnd > start && oldText[end - 1] == next[nextEnd - 1])
        {
            if ((end & 4095) == 0)
                token.ThrowIfCancellationRequested();
            end--;
            nextEnd--;
        }
        return oldText.WithChanges(new TextChange(TextSpan.FromBounds(start, end), next[start..nextEnd]));
    }
}
