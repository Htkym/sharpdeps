namespace SharpDeps.Query;

using SharpDeps.Analysis.Core.Diagnostics;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Index;

internal sealed record ExplorationSearchMetadata(HarnessNode Node, string Label, string SimpleName, string BaseName,
    IReadOnlyList<string> Aliases, IReadOnlyList<string> NameWords, IReadOnlyList<string> SignatureWords, IReadOnlyList<string> ContextWords);

internal sealed class ExplorationSnapshot
{
    internal readonly IndexSnapshot saved;
    internal readonly IReadOnlyDictionary<string, HarnessNode> nodes;
    internal readonly IReadOnlyDictionary<string, HarnessSymbolOccurrence> occurrences;
    internal readonly IReadOnlyDictionary<string, IReadOnlyList<HarnessSymbolOccurrence>> bySymbol;
    internal readonly IReadOnlyDictionary<string, IndexFile> files;
    internal readonly IReadOnlyDictionary<string, IReadOnlyList<HarnessEdge>> outgoing;
    internal readonly IReadOnlyDictionary<string, IReadOnlyList<HarnessEdge>> incoming;
    internal readonly IReadOnlyDictionary<string, ExplorationSearchMetadata> search;
    internal readonly IReadOnlyDictionary<string, IReadOnlyList<HarnessNode>> names;
    internal readonly IReadOnlyDictionary<string, IReadOnlyList<HarnessNode>> words;
    internal readonly IReadOnlyDictionary<string, IReadOnlyList<HarnessNode>> paths;
    internal readonly FrozenSet<string> savedPaths;
    private IReadOnlyDictionary<string, IReadOnlyList<HarnessEdge>>? httpGetDeclarations;
    private SemaphoreSlim? httpGetDeclarationsGate;

    internal ExplorationSnapshot(IndexSnapshot frozen, CancellationToken token)
    {
        using var metricsScope = PhaseMetrics.Measure("explore.derived-index");
        saved = frozen;
        nodes = Lookup(frozen.Graph.Nodes, node => node.Id, token);
        occurrences = Lookup(frozen.Graph.SymbolOccurrences, occurrence => occurrence.Id, token);
        files = Lookup(frozen.Files, file => file.SourceId, token);
        bySymbol = Group(frozen.Graph.SymbolOccurrences, occurrence => occurrence.LogicalSymbolId, occurrence => occurrence.Id, token);
        outgoing = Group(frozen.Graph.Edges, edge => edge.SourceNodeId, edge => edge.Id, token);
        incoming = Group(frozen.Graph.Edges, edge => edge.TargetNodeId, edge => edge.Id, token);
        var searchBuilder = new Dictionary<string, ExplorationSearchMetadata>(StringComparer.Ordinal);
        foreach (var node in frozen.Graph.Nodes)
        {
            token.ThrowIfCancellationRequested();
            var label = Label(node);
            var simple = ShortName(node.Name);
            var baseName = Regex.Replace(simple, "[<`].*$", "", RegexOptions.CultureInvariant);
            var context = node.Kind == HarnessNodeKind.Member && node.ParentId is { } parent
                ? nodes[parent].Name : node.Name[..(node.Name.Length - simple.Length)];
            var contextWords = Components(context, token);
            var signature = node.Signature ?? "";
            var declaration = node.Kind == HarnessNodeKind.Member ? label : node.Name;
            var declarationStart = signature.IndexOf(declaration, StringComparison.Ordinal);
            while (declarationStart >= 0 && declarationStart + declaration.Length < signature.Length
                && (char.IsLetterOrDigit(signature[declarationStart + declaration.Length]) || signature[declarationStart + declaration.Length] == '_'))
            {
                token.ThrowIfCancellationRequested();
                declarationStart = signature.IndexOf(declaration, declarationStart + 1, StringComparison.Ordinal);
            }
            // Mask only the declaring prefix; parameter types may legitimately repeat its words.
            string[] signatureWords = context.Length > 0 && declarationStart >= 0
                ? [.. Components(signature[..declarationStart], token), "", .. Components(signature[(declarationStart + context.Length)..], token)]
                : Components(signature, token);
            searchBuilder.Add(node.Id, new(node, label, simple, baseName,
                Array.AsReadOnly(new[] { node.Name, simple, baseName, label }.Distinct(StringComparer.Ordinal).ToArray()),
                Array.AsReadOnly(Components(baseName, token)), Array.AsReadOnly(signatureWords), Array.AsReadOnly(contextWords)));
        }
        search = new ReadOnlyDictionary<string, ExplorationSearchMetadata>(searchBuilder);
        names = SearchLookup(item => item.Aliases.Concat(item.Node.Signature is { } signature ? [signature] : []),
            StringComparer.OrdinalIgnoreCase, token);
        words = SearchLookup(item => item.NameWords.Concat(item.SignatureWords), StringComparer.OrdinalIgnoreCase, token);
        paths = SearchLookup(item => Locations(item.Node, null).Where(location => files.ContainsKey(location.SourceId))
            .SelectMany(location => new[] { files[location.SourceId].RelativePath, Path.GetFileName(files[location.SourceId].RelativePath) }),
            StringComparer.Ordinal, token);
        var savedPathBuilder = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in frozen.Files)
        {
            token.ThrowIfCancellationRequested();
            savedPathBuilder.Add(file.RelativePath);
            savedPathBuilder.Add(Path.GetFileName(file.RelativePath));
        }
        savedPaths = savedPathBuilder.ToFrozenSet(StringComparer.Ordinal);
        token.ThrowIfCancellationRequested();
    }

    internal IReadOnlyList<HarnessEdge> GetHttpGetDeclarations(string template, CancellationToken token)
    {
        using var metricsScope = PhaseMetrics.Measure("explore.http-declarations");
        ArgumentNullException.ThrowIfNull(template);
        token.ThrowIfCancellationRequested();
        var cached = Volatile.Read(ref httpGetDeclarations);
        if (cached is null)
        {
            var gate = Volatile.Read(ref httpGetDeclarationsGate);
            if (gate is null)
            {
                var created = new SemaphoreSlim(1, 1);
                gate = Interlocked.CompareExchange(ref httpGetDeclarationsGate, created, null) ?? created;
                if (!ReferenceEquals(gate, created)) created.Dispose();
            }
            gate.Wait(token);
            try
            {
                token.ThrowIfCancellationRequested();
                cached = Volatile.Read(ref httpGetDeclarations);
                if (cached is null)
                {
                    var grouped = Group(saved.Graph.Edges.Where(edge =>
                    {
                        // Rejected edges must also observe the request deadline.
                        token.ThrowIfCancellationRequested();
                        return edge.HttpGetDeclaration is { State: HarnessHttpGetDeclarationState.Saved, Template: not null }
                            && edge.Kind == "attribute" && edge.Certainty == HarnessCertainty.Resolved
                            && edge.Producer == "roslyn-declaration" && edge.SourceOccurrenceId is { } source
                            && occurrences.ContainsKey(source);
                    }), edge => edge.HttpGetDeclaration!.Template!, edge => edge.Id, token);
                    var prepared = new Dictionary<string, IReadOnlyList<HarnessEdge>>(StringComparer.Ordinal);
                    foreach (var (key, edges) in grouped)
                    {
                        token.ThrowIfCancellationRequested();
                        prepared.Add(key, edges);
                    }
                    var immutable = new ReadOnlyDictionary<string, IReadOnlyList<HarnessEdge>>(prepared);
                    token.ThrowIfCancellationRequested();
                    Volatile.Write(ref httpGetDeclarations, immutable);
                    cached = immutable;
                }
            }
            finally { gate.Release(); }
        }
        token.ThrowIfCancellationRequested();
        return cached.TryGetValue(template, out var declarations) ? declarations : Array.Empty<HarnessEdge>();
    }

    private IReadOnlyDictionary<string, IReadOnlyList<HarnessNode>> SearchLookup(Func<ExplorationSearchMetadata, IEnumerable<string>> keys,
        StringComparer comparer, CancellationToken token)
    {
        var groups = new Dictionary<string, List<HarnessNode>>(comparer);
        foreach (var item in search.Values)
            foreach (var key in keys(item).Where(key => key.Length != 0).Distinct(comparer))
            {
                token.ThrowIfCancellationRequested();
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = []);
                group.Add(item.Node);
            }
        var result = new Dictionary<string, IReadOnlyList<HarnessNode>>(comparer);
        foreach (var (key, group) in groups)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                group.Sort((left, right) =>
                {
                    token.ThrowIfCancellationRequested();
                    return StringComparer.Ordinal.Compare(left.Id, right.Id);
                });
            }
            catch (InvalidOperationException error) when (error.InnerException is OperationCanceledException)
            { throw new OperationCanceledException("Snapshot preparation was cancelled.", error, token); }
            result.Add(key, Array.AsReadOnly(group.ToArray()));
        }
        return new ReadOnlyDictionary<string, IReadOnlyList<HarnessNode>>(result);
    }

    private static string ShortName(string name)
    {
        var depth = 0; var start = 0;
        for (var index = 0; index < name.Length; index++)
        {
            if (name[index] is '<' or '[' or '(') depth++;
            else if (name[index] is '>' or ']' or ')') depth--;
            else if (name[index] == '.' && depth == 0) start = index + 1;
        }
        return name[start..];
    }

    internal static string[] Components(string text, CancellationToken token = default)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (index == text.Length || !char.IsLetterOrDigit(text[index]))
            {
                if (index > start) result.Add(text[start..index]);
                start = index + 1;
            }
            else if (index > start && char.IsUpper(text[index])
                && (char.IsLower(text[index - 1]) || char.IsDigit(text[index - 1])
                    || char.IsUpper(text[index - 1]) && index + 1 < text.Length && char.IsLower(text[index + 1])))
            {
                result.Add(text[start..index]);
                start = index;
            }
        }
        return result.ToArray();
    }

    private static IReadOnlyDictionary<string, T> Lookup<T>(IEnumerable<T> values, Func<T, string> key, CancellationToken token)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            result.Add(key(value), value);
        }
        return new ReadOnlyDictionary<string, T>(result);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<T>> Group<T>(IEnumerable<T> values, Func<T, string> groupKey,
        Func<T, string> orderKey, CancellationToken token)
    {
        var groups = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            var key = groupKey(value);
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = []);
            group.Add(value);
        }
        var result = new Dictionary<string, IReadOnlyList<T>>(StringComparer.Ordinal);
        foreach (var (key, group) in groups)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                group.Sort((left, right) =>
                {
                    token.ThrowIfCancellationRequested();
                    return StringComparer.Ordinal.Compare(orderKey(left), orderKey(right));
                });
            }
            catch (InvalidOperationException error) when (error.InnerException is OperationCanceledException)
            { throw new OperationCanceledException("Snapshot preparation was cancelled.", error, token); }
            token.ThrowIfCancellationRequested();
            result.Add(key, Array.AsReadOnly(group.ToArray()));
        }
        return new ReadOnlyDictionary<string, IReadOnlyList<T>>(result);
    }

    internal string? Project(HarnessNode node)
    {
        var current = node;
        while (true)
        {
            if (current.Kind == HarnessNodeKind.Project) return current.Id;
            if (current.ParentId is not { } parent || !nodes.TryGetValue(parent, out var next)) return null;
            current = next;
        }
    }

    internal string Label(HarnessNode node) => node.Kind == HarnessNodeKind.Member && node.ParentId is { } parent
        && nodes.TryGetValue(parent, out var owner) ? owner.Name + "." + node.Name : node.Name;

    internal IEnumerable<HarnessLocation> Locations(HarnessNode node, string? variant)
    {
        if (bySymbol.TryGetValue(node.Id, out var declarations))
        {
            foreach (var occurrence in declarations)
                if (variant is null || occurrence.VariantId == variant)
                    foreach (var location in occurrence.Declarations ?? (occurrence.Location is null ? [] : [occurrence.Location]))
                        yield return location;
        }
        else if (node.Location is { } location) yield return location;
    }
}

internal sealed class ExplorationSnapshotCache
{
    private static readonly ExplorationSnapshotCache Shared = new();
    private sealed record Entry(Fingerprint Key, ExplorationSnapshot Snapshot);
    internal readonly record struct Fingerprint(string Sha256, long Bytes);
    private readonly SemaphoreSlim Gate = new(1, 1);
    private Entry? latest;

    internal static ExplorationSnapshot Get(IndexSnapshot current, CancellationToken token)
        => Shared.GetOrCreate(current, token);

    internal ExplorationSnapshot GetOrCreate(IndexSnapshot current, CancellationToken token)
    {
        using var metricsScope = PhaseMetrics.Measure("explore.prepare");
        ArgumentNullException.ThrowIfNull(current);
        token.ThrowIfCancellationRequested();
        var cached = Volatile.Read(ref latest);
        Fingerprint? wanted = null;
        // An empty cache cannot hit. Hash the clone bytes below once, rather than
        // walking the entire payload twice on the first saved exploration.
        // A populated cache still checks the exact payload without allocating clone bytes.
        if (cached is not null)
        {
            wanted = Serialize(current, Stream.Null, token);
            if (cached.Key == wanted)
            {
                token.ThrowIfCancellationRequested();
                PhaseMetrics.Decision("explore.prepare", "payload-cache-hit");
                return cached.Snapshot;
            }
        }
        Gate.Wait(token);
        try
        {
            token.ThrowIfCancellationRequested();
            cached = Volatile.Read(ref latest);
            if (wanted is { } fingerprint && cached is not null && cached.Key == fingerprint)
            {
                PhaseMetrics.Decision("explore.prepare", "payload-cache-hit-after-wait");
                return cached.Snapshot;
            }

            // Only a miss holds clone bytes; the key is computed from those exact bytes.
            using var bytes = new MemoryStream();
            var actual = Serialize(current, bytes, token);
            cached = Volatile.Read(ref latest);
            if (cached is not null && cached.Key == actual)
            {
                token.ThrowIfCancellationRequested();
                PhaseMetrics.Decision("explore.prepare", "payload-cache-hit");
                return cached.Snapshot;
            }
            PhaseMetrics.Decision("explore.prepare", "payload-cache-miss");
            bytes.Position = 0;
            using var input = new CheckedStream(bytes, token);
            IndexSnapshot frozen;
            using (PhaseMetrics.Measure("explore.freeze"))
                frozen = JsonSerializer.Deserialize(input, QuerySnapshotJsonContext.Default.IndexSnapshot)
                    ?? throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Snapshot data cannot be null.");
            token.ThrowIfCancellationRequested();
            using (PhaseMetrics.Measure("explore.validate"))
                IndexSnapshotValidator.Validate(frozen, token);
            var prepared = new ExplorationSnapshot(frozen, token);
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref latest, new Entry(actual, prepared));
            return prepared;
        }
        finally { Gate.Release(); }
    }

    internal static Fingerprint Serialize(IndexSnapshot snapshot, Stream destination, CancellationToken token)
    {
        using var metricsScope = PhaseMetrics.Measure("explore.fingerprint");
        token.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output = new CheckedStream(destination, token, hash);
        JsonSerializer.Serialize(output, snapshot, QuerySnapshotJsonContext.Default.IndexSnapshot);
        token.ThrowIfCancellationRequested();
        return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), output.BytesWritten);
    }

    private sealed class CheckedStream(Stream inner, CancellationToken token, IncrementalHash? hash = null) : Stream
    {
        internal long BytesWritten { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
            token.ThrowIfCancellationRequested();
            inner.Flush();
            token.ThrowIfCancellationRequested();
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            var count = inner.Read(buffer);
            token.ThrowIfCancellationRequested();
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            inner.Write(buffer);
            hash?.AppendData(buffer);
            BytesWritten = checked(BytesWritten + buffer.Length);
            token.ThrowIfCancellationRequested();
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
