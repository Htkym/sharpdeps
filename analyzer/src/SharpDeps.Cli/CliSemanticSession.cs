namespace SharpDeps.Cli;

using SharpDeps.Analysis.Core.Diagnostics;
using SharpDeps.Analysis.Roslyn;

internal sealed class CliSemanticSession : IDisposable
{
    private readonly SemanticSessionOwner owner = new();
    private Update? pending;

    internal Update BeginUpdate(bool full)
    {
        pending?.Discard();
        if (full)
            owner.Evict();
        return pending = new Update(owner);
    }

    public void Dispose()
    {
        pending?.Discard();
        pending = null;
        owner.Dispose();
    }

    internal sealed class Update(SemanticSessionOwner owner) : IDisposable
    {
        private SemanticSessionCandidate? candidate;
        private SemanticSessionProof? retained;
        private long baseline;
        private bool detached;
        private bool finished;
        internal bool Warm => candidate?.Warm == true;
        internal bool CanUpdate => !finished && owner.HasCommitted;

        internal async Task<SemanticLoadResult?> TryUpdateAsync(SemanticSessionProof proof, long generation,
            IReadOnlyDictionary<string, SemanticSourceInput> sources, CancellationToken token)
        {
            if (sources.Values.Any(s => s.Text.StartsWith('\uFEFF')))
            {
                owner.Evict();
                PhaseMetrics.Decision("semantic.session", "preamble-profile-cold");
                return null;
            }
            try
            {
                candidate = await owner.TryUpdateAsync(proof, generation, sources, token);
                return candidate?.Result;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                try
                {
                    owner.Evict();
                }
                catch (Exception) { }
                PhaseMetrics.Decision("semantic.session", "evaluation-failed-cold");
                return null;
            }
        }

        internal async Task PrepareColdAsync(SemanticColdLoad cold, SemanticSessionProof proof,
            IReadOnlyDictionary<string, SemanticSourceInput> sources, CancellationToken token)
        {
            // shortcut: CLI BOM warm updates stay cold until the MSBuild source-coordinate profile is verified.
            if (!sources.Values.Any(s => s.Text.StartsWith('\uFEFF')))
                candidate = await owner.PrepareColdAsync(cold, proof, sources, token);
        }

        internal void Retain(SemanticSessionProof proof, long generation)
        {
            retained = proof;
            baseline = generation;
        }

        internal void Detach() => detached = true;

        internal void Adopt(long generation)
        {
            if (finished)
                return;
            finished = true;
            try
            {
                if (candidate is not null)
                    owner.Adopt(candidate, generation);
                else if (retained is not null)
                    owner.AdvanceCommitted(retained, baseline, generation);
                else
                    owner.Evict();
            }
            catch (Exception)
            {
                // A disposable acceleration cache cannot undo an already durable DB commit.
                try
                {
                    owner.Evict();
                }
                catch (Exception) { }
                PhaseMetrics.Decision("semantic.session", "adoption-failed-cold");
            }
            finally
            {
                ReleaseCandidate();
            }
        }

        internal void Discard()
        {
            if (finished)
                return;
            finished = true;
            ReleaseCandidate();
        }

        private void ReleaseCandidate()
        {
            var abandoned = candidate;
            candidate = null;
            try
            {
                abandoned?.Dispose();
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (!detached)
                Discard();
        }
    }
}
