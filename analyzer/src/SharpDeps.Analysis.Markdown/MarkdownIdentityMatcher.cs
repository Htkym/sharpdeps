namespace SharpDeps.Analysis.Markdown;

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Syntamark;
using SharpDeps.Analysis.Core.Identity;

public sealed record MarkdownDocumentIdentity(Guid DocumentUuid, string RelativePath, string? ExplicitId, string TextHash);
/// <summary>VerifiedPreviousPath is an owner-validated rename, never an untrusted Markdown hint.</summary>
public sealed record MarkdownDocumentMatchInput(string RelativePath, string? ExplicitId, string TextHash, string? VerifiedPreviousPath = null);
public sealed record MarkdownDocumentMatch(string RelativePath, Guid? DocumentUuid, string Reason, IReadOnlyList<Guid> Candidates);
public sealed record MarkdownSectionIdentity(string LocalKey, string Token, string? HeadingKey, string? BodyHash);
public sealed record MarkdownSectionIdentitySnapshot(string DocumentId, string ScopeId, string TextHash,
    string ParserVersion, string ContractVersion, string ProfileId, string OptionsHash, IReadOnlyList<MarkdownSectionIdentity> Sections);
public sealed record MarkdownSectionMatch(string LocalKey, string Token, bool Retained, string Reason, IReadOnlyList<string> Candidates);
public sealed record MarkdownSectionMatchResult(IReadOnlyDictionary<string, string> SectionTokens, string TextHash,
    MarkdownSectionIdentitySnapshot Snapshot, IReadOnlyList<MarkdownSectionMatch> Matches);

/// <summary>Pure matching; allocation/persistence and verification of filesystem moves belong to the owner.</summary>
public static class MarkdownIdentityMatcher
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<MarkdownDocumentMatch> MatchDocuments(IReadOnlyList<MarkdownDocumentIdentity> previous,
        IReadOnlyList<MarkdownDocumentMatchInput> current, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var old = previous.ToArray(); var next = current.ToArray();
        foreach (var entry in old)
        {
            ValidatePath(entry.RelativePath); ValidateHash(entry.TextHash); ValidateExplicitId(entry.ExplicitId);
            if (entry.DocumentUuid == Guid.Empty) throw new ArgumentException("A persisted document UUID is required.");
        }
        foreach (var entry in next)
        {
            ValidatePath(entry.RelativePath); ValidateHash(entry.TextHash); ValidateExplicitId(entry.ExplicitId);
            if (entry.VerifiedPreviousPath is { } path) ValidatePath(path);
        }
        if (old.Select(e => e.DocumentUuid).Distinct().Count() != old.Length
            || old.Select(e => e.RelativePath).Distinct(StringComparer.Ordinal).Count() != old.Length
            || next.Select(e => e.RelativePath).Distinct(StringComparer.Ordinal).Count() != next.Length)
            throw new ArgumentException("Document UUIDs and owner-normalized paths must be unique.");
        var duplicatedIds = old.Where(e => !string.IsNullOrWhiteSpace(e.ExplicitId)).GroupBy(e => e.ExplicitId!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).Concat(next.Where(e => !string.IsNullOrWhiteSpace(e.ExplicitId))
                .GroupBy(e => e.ExplicitId!, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)).ToHashSet(StringComparer.Ordinal);
        var matched = new Dictionary<int, (int Old, string Reason)>(); var used = new HashSet<int>();
        Match(a => a.ExplicitId,b => b.ExplicitId,"explicit-id");
        Match(a => a.RelativePath,b => b.VerifiedPreviousPath,"verified-rename");
        Match(a => a.RelativePath,b => b.RelativePath,"same-path");
        // Uniqueness is across both complete snapshots, before same-path matches consume copies.
        var currentPaths = next.Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal);
        var uniqueOldHashes = old.GroupBy(e => e.TextHash,StringComparer.Ordinal).Where(g => g.Count() == 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var uniqueNewHashes = next.GroupBy(e => e.TextHash,StringComparer.Ordinal).Where(g => g.Count() == 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        Match(a => currentPaths.Contains(a.RelativePath) || !uniqueOldHashes.Contains(a.TextHash) ? null : a.TextHash,
            b => uniqueNewHashes.Contains(b.TextHash) ? b.TextHash : null,"unique-content-move");
        var results = next.Select((entry,i) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ExplicitId is { } explicitId && duplicatedIds.Contains(explicitId))
                return new MarkdownDocumentMatch(entry.RelativePath, null, "duplicate-explicit-id", Freeze(old
                    .Where(e => e.ExplicitId == explicitId).Select(e => e.DocumentUuid)));
            if (matched.TryGetValue(i, out var match))
                return new MarkdownDocumentMatch(entry.RelativePath, old[match.Old].DocumentUuid, match.Reason, Freeze<Guid>([]));
            var candidates = old.Where(e => Compatible(e,entry) && (e.TextHash == entry.TextHash
                || e.RelativePath == entry.VerifiedPreviousPath)).Select(e => e.DocumentUuid).Distinct().ToArray();
            return new MarkdownDocumentMatch(entry.RelativePath, null, candidates.Length == 0 ? "new-document" : "ambiguous-move", Freeze(candidates));
        }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(results);

        void Match(Func<MarkdownDocumentIdentity,string?> oldKey,Func<MarkdownDocumentMatchInput,string?> newKey,string reason)
        {
            var oldGroups = Enumerable.Range(0,old.Length).Where(i => !used.Contains(i)
                && (old[i].ExplicitId is null || !duplicatedIds.Contains(old[i].ExplicitId!)))
                .Select(i => (Index:i,Key:oldKey(old[i]))).Where(p => !string.IsNullOrEmpty(p.Key))
                .GroupBy(p => p.Key!,StringComparer.Ordinal).ToDictionary(g => g.Key,g => g.Select(p => p.Index).ToArray(),StringComparer.Ordinal);
            var groups = Enumerable.Range(0,next.Length).Where(i => !matched.ContainsKey(i)
                && (next[i].ExplicitId is null || !duplicatedIds.Contains(next[i].ExplicitId!)))
                .Select(i => (Index:i,Key:newKey(next[i]))).Where(p => !string.IsNullOrEmpty(p.Key)).GroupBy(p => p.Key!,StringComparer.Ordinal);
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var proposals = group.ToArray();
                if (proposals.Length == 1 && oldGroups.TryGetValue(group.Key,out var prior) && prior.Length == 1
                    && Compatible(old[prior[0]],next[proposals[0].Index]))
                { matched.Add(proposals[0].Index,(prior[0],reason)); used.Add(prior[0]); }
            }
        }
    }

    public static MarkdownSectionMatchResult MatchSections(Guid workspaceUuid, Guid documentUuid, string rawText,
        MarkdownDocument facts, MarkdownSectionIdentitySnapshot? previous, Func<string> allocateSectionToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(allocateSectionToken);
        MarkdownRuntimePin.RequireFacts(facts);
        var documentId = HarnessIdentity.DocumentId(workspaceUuid,documentUuid);
        var hash = Hash(rawText);
        if (facts.TextHash != hash || facts.Status != MarkdownParseStatus.Complete
            || facts.Coverage.RawPositions != MarkdownCoverageState.Complete || facts.Coverage.DecodedMapping != MarkdownCoverageState.Complete)
            throw new ArgumentException("Matching requires complete facts bound to the exact supplied raw text.");
        if (previous is not null && (previous.DocumentId != documentId || previous.ScopeId != facts.ScopeId))
            throw new ArgumentException("Section tokens belong to another document or workspace scope.");
        if (previous is not null) ValidateHash(previous.TextHash);
        var headings = facts.Headings.ToDictionary(h => h.LocalKey,StringComparer.Ordinal);
        var descriptors = facts.Sections.Select(section =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var heading = section.HeadingLocalKey is { } key ? headings.GetValueOrDefault(key) : null;
            var headingKey = heading is null ? "preamble" : heading.Text.Text is { } text ? heading.RawLevel + ":" + text : null;
            string? bodyHash = null;
            if (section.DirectBodySpan is { } span)
            {
                if (span.End > rawText.Length) throw new ArgumentException("Section span exceeds the supplied text.");
                var body = rawText.Substring(span.Start,span.Length);
                if (body.Any(c => !char.IsWhiteSpace(c))) bodyHash = Hash(body);
            }
            return new MarkdownSectionIdentity(section.LocalKey,"",headingKey,bodyHash);
        }).ToArray();
        var compatibleSnapshot = previous is not null && previous.ParserVersion == facts.ParserVersion && previous.ContractVersion == facts.ContractVersion
            && previous.ProfileId == facts.ProfileId && previous.OptionsHash == facts.OptionsHash;
        var prior = compatibleSnapshot ? previous!.Sections.ToArray() : [];
        if (previous is not null && (previous.Sections.Any(s => !ValidToken(s.Token) || string.IsNullOrWhiteSpace(s.LocalKey))
            || previous.Sections.Select(s => s.Token).Distinct(StringComparer.Ordinal).Count() != previous.Sections.Count
            || previous.Sections.Select(s => s.LocalKey).Distinct(StringComparer.Ordinal).Count() != previous.Sections.Count))
            throw new ArgumentException("Previous section tokens and local bindings must be unique.");
        var matched = new Dictionary<int,(int Old,string Reason)>(); var used = new HashSet<int>();
        if (compatibleSnapshot && previous!.TextHash == hash)
        {
            // Snapshot-local keys are safe bindings only with the exact source and parser stamps.
            var byKey = prior.Select((entry,i) => (entry,i)).ToDictionary(p => p.entry.LocalKey,StringComparer.Ordinal);
            if (prior.Length != descriptors.Length || descriptors.Any(entry => !byKey.TryGetValue(entry.LocalKey,out var p)
                || p.entry.HeadingKey != entry.HeadingKey || p.entry.BodyHash != entry.BodyHash))
                throw new ArgumentException("The saved section snapshot does not match its claimed raw text.");
            for (var i=0;i<descriptors.Length;i++)
            { var oldIndex = byKey[descriptors[i].LocalKey].i; matched.Add(i,(oldIndex,"exact-snapshot")); used.Add(oldIndex); }
        }
        Match(a => a.HeadingKey is not null && a.BodyHash is not null ? a.BodyHash + ":" + a.HeadingKey : null,"heading-and-body");
        Match(a => a.HeadingKey,"unique-heading", requireSnapshotUnique: true);
        Match(a => a.BodyHash,"unique-body", requireSnapshotUnique: true);
        var byHeading = prior.Where(p => p.HeadingKey is not null).GroupBy(p => p.HeadingKey!,StringComparer.Ordinal)
            .ToDictionary(g => g.Key,g => g.Select(p => p.Token).ToArray(),StringComparer.Ordinal);
        var byBody = prior.Where(p => p.BodyHash is not null).GroupBy(p => p.BodyHash!,StringComparer.Ordinal)
            .ToDictionary(g => g.Key,g => g.Select(p => p.Token).ToArray(),StringComparer.Ordinal);
        var candidateCache = new Dictionary<(string?,string?),IReadOnlyList<string>>();
        var allocated = (previous?.Sections ?? []).Select(s => s.Token).ToHashSet(StringComparer.Ordinal);
        var entries = new List<MarkdownSectionIdentity>(); var matches = new List<MarkdownSectionMatch>();
        for (var i=0;i<descriptors.Length;i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = descriptors[i];
            if (matched.TryGetValue(i,out var match))
            {
                entry = entry with { Token = prior[match.Old].Token };
                matches.Add(new(entry.LocalKey,entry.Token,true,match.Reason,Freeze<string>([])));
            }
            else
            {
                var token = allocateSectionToken();
                if (!ValidToken(token) || !allocated.Add(token))
                    throw new ArgumentException("The owner must allocate a fresh, nonempty section token.");
                if (!candidateCache.TryGetValue((entry.HeadingKey,entry.BodyHash),out var candidates))
                {
                    candidates = Freeze((entry.HeadingKey is { } headingKey ? byHeading.GetValueOrDefault(headingKey) ?? [] : [])
                        .Concat(entry.BodyHash is { } bodyKey ? byBody.GetValueOrDefault(bodyKey) ?? [] : []).Distinct(StringComparer.Ordinal));
                    candidateCache.Add((entry.HeadingKey,entry.BodyHash),candidates);
                }
                entry = entry with { Token = token };
                matches.Add(new(entry.LocalKey,token,false,candidates.Count == 0 ? "new-section" : "ambiguous-section",candidates));
            }
            entries.Add(entry);
        }
        var snapshot = new MarkdownSectionIdentitySnapshot(documentId,facts.ScopeId,hash,facts.ParserVersion,facts.ContractVersion,
            facts.ProfileId,facts.OptionsHash,Freeze(entries));
        var result = new MarkdownSectionMatchResult(
            new ReadOnlyDictionary<string,string>(entries.ToDictionary(e => e.LocalKey,e => e.Token,StringComparer.Ordinal)),
            hash,snapshot,Freeze(matches));
        cancellationToken.ThrowIfCancellationRequested();
        return result;

        void Match(Func<MarkdownSectionIdentity,string?> key,string reason,bool requireSnapshotUnique = false)
        {
            var ambiguousKeys = requireSnapshotUnique
                ? prior.Select(key).Where(k => k is not null).GroupBy(k => k!,StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)
                    .Concat(descriptors.Select(key).Where(k => k is not null).GroupBy(k => k!,StringComparer.Ordinal)
                        .Where(g => g.Count() > 1).Select(g => g.Key)).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var oldGroups = Enumerable.Range(0,prior.Length).Where(i => !used.Contains(i)).Select(i => (Index:i,Key:key(prior[i])))
                .Where(p => p.Key is not null).GroupBy(p => p.Key!,StringComparer.Ordinal)
                .ToDictionary(g => g.Key,g => g.Select(p => p.Index).ToArray(),StringComparer.Ordinal);
            var groups = Enumerable.Range(0,descriptors.Length).Where(i => !matched.ContainsKey(i)).Select(i => (Index:i,Key:key(descriptors[i])))
                .Where(p => p.Key is not null).GroupBy(p => p.Key!,StringComparer.Ordinal);
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var proposals = group.ToArray();
                if (!ambiguousKeys.Contains(group.Key) && proposals.Length == 1
                    && oldGroups.TryGetValue(group.Key,out var oldMatches) && oldMatches.Length == 1)
                { matched.Add(proposals[0].Index,(oldMatches[0],reason)); used.Add(oldMatches[0]); }
            }
        }
    }

    private static bool Compatible(MarkdownDocumentIdentity a,MarkdownDocumentMatchInput b)
        => string.IsNullOrEmpty(a.ExplicitId) || string.IsNullOrEmpty(b.ExplicitId) || a.ExplicitId == b.ExplicitId;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(text))).ToLowerInvariant();
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private static bool ValidToken(string token) => !string.IsNullOrWhiteSpace(token) && token == token.Trim();
    private static void ValidateExplicitId(string? id)
    {
        if (id is not null && (string.IsNullOrWhiteSpace(id) || id != id.Trim()))
            throw new ArgumentException("An explicit document ID must be nonempty and owner-normalized.");
    }
    private static void ValidateHash(string hash)
    {
        if (hash.Length != 64 || hash.AsSpan().ContainsAnyExcept("0123456789abcdef")) throw new ArgumentException("A complete raw SHA256 is required.");
    }
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Contains('\\')
            || path.Split('/').Any(s => s is "" or "." or "..")) throw new ArgumentException("Use an owner-normalized workspace-relative path.");
    }
}
