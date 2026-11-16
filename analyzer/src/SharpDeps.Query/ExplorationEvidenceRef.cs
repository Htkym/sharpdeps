namespace SharpDeps.Query;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpDeps.Analysis.Contracts.Harness;

public sealed partial class ExplorationService
{
    private static void ValidateEvidenceRefRequest(ExploreRequest request)
    {
        if (request.EvidenceRef is not { } reference) return;
        if (reference.Length != 47 || !reference.StartsWith("er1_", StringComparison.Ordinal)
            || reference.AsSpan(4).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_")
            || request.QueryKind != ExploreQueryKind.Symbol || request.ScopePolicy != ExploreScopePolicy.RequireUnique
            || request.ProjectId is null || request.VariantId is null || request.PathPrefix is not null
            || request.PathTo is not null || request.Query is null || !IsOccurrenceIdentity(request.Query))
            throw new ArgumentException("EvidenceRef requires one exact code occurrence, explicit project/variant and unique scope without path filters.", nameof(request));
    }

    private sealed partial class Run
    {
        // Request-local only. References bind saved declarations, never retrieved text or host context.
        private readonly Dictionary<string, string?> evidenceRefs = new(StringComparer.Ordinal);

        public bool ValidateEvidenceRef()
        {
            if (request.EvidenceRef is null) return true;
            if (roots.Count == 1 && roots[0].Identity == request.Query
                && SavedEvidenceRef(roots[0]) is { } current && current == request.EvidenceRef) return true;
            Error("EVIDENCE_REF_MISMATCH", "The saved selection changed or cannot be guarded; obtain a new reference explicitly. No source returned.");
            return false;
        }

        private string? SavedEvidenceRef(Selected root)
        {
            Check();
            if (evidenceRefs.TryGetValue(root.Identity, out var cached)) return cached;
            string? result = null;
            if (root.Occurrence is { } occurrence && IsOccurrenceIdentity(root.Identity)
                && project is not null && variant is not null && occurrence.VariantId == variant
                && owner.Project(root.Node) == project)
            {
                var locations = (occurrence.Declarations ?? (occurrence.Location is { } primary ? [primary] : []))
                    .Distinct().OrderBy(location => location.SourceId, StringComparer.Ordinal)
                    .ThenBy(location => location.RawSpan?.Start).ThenBy(location => location.RawSpan?.Length)
                    .ThenBy(location => location.ContentHash, StringComparer.Ordinal).ToArray();
                if (locations.Length > 0 && locations.All(location => location.RawSpan is { Length: > 0 }
                    && owner.files.TryGetValue(location.SourceId, out var file)
                    && location.ContentHash == file.ContentHash && !string.IsNullOrWhiteSpace(file.ContentHash)))
                {
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    void Field(string value)
                    {
                        Check();
                        var bytes = Encoding.UTF8.GetBytes(value);
                        Span<byte> length = stackalloc byte[4];
                        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                        digest.AppendData(length); digest.AppendData(bytes);
                    }
                    var graph = owner.saved.Graph;
                    Field("sharpdeps-evidence-ref-v1"); Field(graph.WorkspaceId); Field(graph.SnapshotId);
                    Field(graph.Generation.ToString(CultureInfo.InvariantCulture));
                    Field(project); Field(variant); Field(root.Identity); Field("saved-declaration");
                    Field(locations.Length.ToString(CultureInfo.InvariantCulture));
                    foreach (var location in locations)
                    {
                        var file = owner.files[location.SourceId];
                        Field(location.SourceId); Field(file.RelativePath); Field(file.ContentHash);
                        Field(location.RawSpan!.Value.Start.ToString(CultureInfo.InvariantCulture));
                        Field(location.RawSpan.Value.Length.ToString(CultureInfo.InvariantCulture));
                    }
                    result = "er1_" + Convert.ToBase64String(digest.GetHashAndReset()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                }
            }
            evidenceRefs[root.Identity] = result;
            return result;
        }

        private void AppendEvidenceRefs(StringBuilder text, int detailCount)
        {
            if (!request.IncludeEvidenceRefs || Errors.Count != 0) return;
            text.AppendLine("Evidence refs guard saved selections only; not proof of body possession, authorization or current freshness. Re-fetch normally when needed.");
            // Share the existing shrinkable detail quota. Never pin all discovery candidates outside the wire cap.
            var shown = roots.Take(detailCount).ToArray();
            var issued = 0;
            foreach (var root in shown)
                if (SavedEvidenceRef(root) is { } reference)
                {
                    text.AppendLine($"Evidence ref selector={root.Identity} project={project} variant={variant} evidenceRef={reference}");
                    issued++;
                }
            text.AppendLine(FormattableString.Invariant($"Evidence refs issued={issued}; candidates not shown={roots.Count - shown.Length}; shown without ref={shown.Length - issued}. Only saved code occurrences with complete saved declaration metadata are eligible."));
        }
    }
}
