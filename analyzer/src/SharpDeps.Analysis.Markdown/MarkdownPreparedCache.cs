namespace SharpDeps.Analysis.Markdown;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Core.Diagnostics;
using Syntamark;

internal sealed class MarkdownPreparedCache
{
    private readonly object gate = new();
    private readonly int maximumEntries;
    private readonly long maximumSourceUtf16;
    private readonly long maximumItems;
    private Dictionary<CacheKey, Entry> committed = [];
    private long revision;
    private long stamp;

    internal MarkdownPreparedCache(int maximumEntries = 64, long maximumSourceUtf16 = 4 * 1024 * 1024,
        long maximumItems = 262144)
    {
        if (maximumEntries < 1 || maximumSourceUtf16 < 1 || maximumItems < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        this.maximumEntries = maximumEntries;
        this.maximumSourceUtf16 = maximumSourceUtf16;
        this.maximumItems = maximumItems;
    }

    internal Candidate BeginUpdate()
    {
        lock (gate)
            return Candidate.Create(this);
    }

    internal void Clear()
    {
        lock (gate)
        {
            committed = [];
            revision++;
        }
    }

    private readonly record struct CacheKey(Guid Workspace, string Scope, string Source, string? Version,
        string TextHash, int TextLength, string Parser, string RuntimePin, string Options)
    {
        internal static CacheKey Create(MarkdownGraphRequest request, MarkdownParseOptions options)
            => new(request.WorkspaceUuid, request.ScopeId, request.SourceId, request.SourceVersion,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RawText))), request.RawText.Length,
                request.ExpectedParserVersion,
                MarkdownRuntimePin.ComponentVersion + "/" + MarkdownRuntimePin.CanonicalSourceHash + "/"
                    + MarkdownRuntimePin.RuntimeAssemblySha256 + "/" + MarkdownRuntimePin.ContractVersion,
                JsonSerializer.Serialize(options));

        internal bool SameLineage(CacheKey other) => Workspace == other.Workspace && Scope == other.Scope
            && Source == other.Source && Parser == other.Parser && RuntimePin == other.RuntimePin && Options == other.Options;
    }

    private sealed record Entry(MarkdownPreparedInput Prepared,
#if SHARPDEPS_MARKDOWN_SESSION_CANDIDATE
        MarkdownParseSession? Session,
#endif
        long SourceUtf16, long Items, long Stamp);

    internal sealed class Candidate : IDisposable
    {
        private readonly MarkdownPreparedCache owner;
        private Dictionary<CacheKey, Entry> entries;
        private readonly long revision;
        private long stamp;
        private long sourceUtf16;
        private long items;
        private bool finished;

        internal int SessionUpdates { get; private set; }
        internal int FullPreparations { get; private set; }

        internal static Candidate Create(MarkdownPreparedCache owner)
            => new(owner, new(owner.committed), owner.revision, owner.stamp);

        private Candidate(MarkdownPreparedCache owner, Dictionary<CacheKey, Entry> entries, long revision, long stamp)
        {
            this.owner = owner;
            this.entries = entries;
            this.revision = revision;
            this.stamp = stamp;
            sourceUtf16 = entries.Values.Sum(entry => entry.SourceUtf16);
            items = entries.Values.Sum(entry => entry.Items);
        }

        internal MarkdownPreparedInput Prepare(LithoSharpMarkdownAdapter adapter, MarkdownGraphRequest request,
            bool isTrusted, out bool hit, CancellationToken token = default)
        {
            if (finished)
                throw new InvalidOperationException("The prepared cache candidate is already closed.");
            var options = LithoSharpMarkdownAdapter.ValidateRequest(request, isTrusted, token);
            var key = CacheKey.Create(request, options);
            if (entries.TryGetValue(key, out var entry))
            {
                MarkdownRuntimePin.RequireLoaded(request.ExpectedParserVersion);
                entry.Prepared.RequireMatches(request, options);
                token.ThrowIfCancellationRequested();
                entries[key] = entry with { Stamp = ++stamp };
                hit = true;
                PhaseMetrics.Decision("markdown.prepare.mode", "cache");
                return entry.Prepared;
            }
            hit = false;
            MarkdownPreparedInput prepared;
#if SHARPDEPS_MARKDOWN_SESSION_CANDIDATE
            MarkdownParseSession? session = null;
            if ((long)request.RawText.Length * 2 <= owner.maximumSourceUtf16)
            {
                var previous = entries.FirstOrDefault(pair => pair.Key.SameLineage(key) && pair.Value.Session is not null);
                if (previous.Value is { } old)
                {
                    var oldRequest = request with { RawText = old.Prepared.RawText, SourceVersion = previous.Key.Version };
                    old.Prepared.RequireMatches(oldRequest, options);
                    if (CacheKey.Create(oldRequest, options) != previous.Key
                        || !previous.Key.TextHash.Equals(old.Prepared.Facts.TextHash, StringComparison.OrdinalIgnoreCase)
                        || old.Session!.Document.TextHash != old.Prepared.Facts.TextHash
                        || old.Session.Document.SourceVersion != previous.Key.Version
                        || old.Session.Document.ScopeId != previous.Key.Scope || old.Session.Document.SourceId != previous.Key.Source
                        || old.Session.Document.OptionsHash != old.Prepared.Facts.OptionsHash)
                        throw new InvalidOperationException("The previous Markdown session lineage cannot be verified.");
                    MarkdownRuntimePin.RequireFacts(old.Session.Document);
                }
                prepared = adapter.PrepareSession(request, options, previous.Value?.Prepared, previous.Value?.Session, out session, token);
            }
            else
                prepared = adapter.Prepare(request, isTrusted, token);
            if (session?.Mode == MarkdownSessionMode.Delta)
            {
                SessionUpdates++;
                PhaseMetrics.Decision("markdown.prepare.mode", "session-delta");
                PhaseMetrics.Count("markdown.session.reused-blocks", session.ReusedBlocks);
                PhaseMetrics.Count("markdown.session.rebased-blocks", session.RebasedBlocks);
                PhaseMetrics.Count("markdown.session.parsed-blocks", session.ParsedBlocks);
            }
            else
            {
                FullPreparations++;
                PhaseMetrics.Decision("markdown.prepare.mode", "full");
            }
#else
            prepared = adapter.Prepare(request, isTrusted, token);
            FullPreparations++;
            PhaseMetrics.Decision("markdown.prepare.mode", "full");
#endif
            prepared.RequireMatches(request, options);
            if (prepared.Facts.TextHash is { } parsedHash && !parsedHash.Equals(key.TextHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Markdown session does not describe the captured raw input.");
            var weight = CountItems(prepared, owner.maximumItems, token);
            var length = Math.Max(1, request.RawText.Length);
            long retainedLength = length;
#if SHARPDEPS_MARKDOWN_SESSION_CANDIDATE
            if (session is not null)
            {
                var sessionWeight = weight + session.OutputItems + 2L * (session.ParsedBlocks + session.ReusedBlocks);
                if (session.Document.Status == MarkdownParseStatus.Complete && session.ParsedBlocks + session.ReusedBlocks > 0
                    && sessionWeight <= owner.maximumItems && 2L * length <= owner.maximumSourceUtf16)
                {
                    // Charge both the portable core and public projection; these are logical bounds, not a heap cap.
                    weight = sessionWeight;
                    retainedLength = 2L * length;
                }
                else
                    session = null;
            }
#endif
            token.ThrowIfCancellationRequested();
            foreach (var stale in entries.Keys.Where(existing => existing.Workspace == key.Workspace
                && existing.Scope == key.Scope && existing.Source == key.Source).ToArray())
            {
                var removed = entries[stale];
                entries.Remove(stale);
                sourceUtf16 -= removed.SourceUtf16;
                items -= removed.Items;
            }
            if (retainedLength > owner.maximumSourceUtf16 || weight > owner.maximumItems)
                return prepared;
            while (entries.Count >= owner.maximumEntries || sourceUtf16 + retainedLength > owner.maximumSourceUtf16
                || items + weight > owner.maximumItems)
            {
                var oldest = entries.MinBy(pair => pair.Value.Stamp);
                entries.Remove(oldest.Key);
                sourceUtf16 -= oldest.Value.SourceUtf16;
                items -= oldest.Value.Items;
            }
            entries.Add(key, new(prepared,
#if SHARPDEPS_MARKDOWN_SESSION_CANDIDATE
                session,
#endif
                retainedLength, weight, ++stamp));
            sourceUtf16 += retainedLength;
            items += weight;
            return prepared;
        }

        internal bool Adopt()
        {
            if (finished)
                return false;
            finished = true;
            var adopted = entries;
            entries = [];
            lock (owner.gate)
            {
                if (revision != owner.revision)
                    return false;
                owner.committed = adopted;
                owner.stamp = stamp;
                owner.revision++;
                return true;
            }
        }

        public void Dispose()
        {
            finished = true;
            entries = [];
        }

        private static long CountItems(MarkdownPreparedInput prepared, long maximum, CancellationToken token)
        {
            var facts = prepared.Facts;
            long count = facts.Headings.Count + (long)facts.Sections.Count + facts.Links.Count
                + facts.Fences.Count + facts.TextRegions.Count + facts.Diagnostics.Count
                + prepared.Attributes.Count + prepared.IgnoredMetadataKeys.Count;
            if (prepared.SyntaxRoot is { } root)
                foreach (var _ in root.DescendantsAndSelf(token))
                {
                    if (++count > maximum)
                        return count;
                }
            var pending = new Stack<MarkdownYamlNode>();
            if (facts.FrontMatter.Root is { } metadata)
                pending.Push(metadata);
            while (pending.TryPop(out var node))
            {
                token.ThrowIfCancellationRequested();
                if (++count > maximum)
                    return count;
                foreach (var child in node.Children)
                    pending.Push(child);
            }
            return count;
        }
    }
}
