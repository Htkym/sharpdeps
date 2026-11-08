namespace SharpDeps.Analysis.Markdown;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    }

    private sealed record Entry(MarkdownPreparedInput Prepared, long SourceUtf16, long Items, long Stamp);

    internal sealed class Candidate
    {
        private readonly MarkdownPreparedCache owner;
        private readonly Dictionary<CacheKey, Entry> entries;
        private readonly long revision;
        private long stamp;
        private long sourceUtf16;
        private long items;
        private bool finished;

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
                return entry.Prepared;
            }
            hit = false;
            var prepared = adapter.Prepare(request, isTrusted, token);
            var weight = CountItems(prepared, owner.maximumItems, token);
            token.ThrowIfCancellationRequested();
            foreach (var stale in entries.Keys.Where(existing => existing.Workspace == key.Workspace
                && existing.Scope == key.Scope && existing.Source == key.Source).ToArray())
            {
                var removed = entries[stale];
                entries.Remove(stale);
                sourceUtf16 -= removed.SourceUtf16;
                items -= removed.Items;
            }
            var length = Math.Max(1, request.RawText.Length);
            if (length > owner.maximumSourceUtf16 || weight > owner.maximumItems)
                return prepared;
            while (entries.Count >= owner.maximumEntries || sourceUtf16 + length > owner.maximumSourceUtf16
                || items + weight > owner.maximumItems)
            {
                var oldest = entries.MinBy(pair => pair.Value.Stamp);
                entries.Remove(oldest.Key);
                sourceUtf16 -= oldest.Value.SourceUtf16;
                items -= oldest.Value.Items;
            }
            entries.Add(key, new(prepared, length, weight, ++stamp));
            sourceUtf16 += length;
            items += weight;
            return prepared;
        }

        internal bool Adopt()
        {
            if (finished)
                return false;
            finished = true;
            lock (owner.gate)
            {
                if (revision != owner.revision)
                    return false;
                owner.committed = entries;
                owner.stamp = stamp;
                owner.revision++;
                return true;
            }
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
