namespace SharpDeps.Query;

using SharpDeps.Analysis.Core.Diagnostics;
using SharpDeps.Index;

internal sealed class ValidatedExplorationSnapshotCache(int maximumEntries = 4, long maximumPayloadBytes = 64 * 1024 * 1024)
{
    internal static readonly ValidatedExplorationSnapshotCache Shared = new();
    private const int DerivedVersion = 1;
    private sealed record Key(string Root, string Index, string Workspace, int IndexSchema, int GraphSchema,
        string Identity, ExplorationSnapshotCache.Fingerprint Payload, int Derived);
    private sealed record Entry(Key Key, ExplorationSnapshot Snapshot);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<Entry> entries = [];
    private long payloadBytes;

    // Only PinnedQueryIndex supplies snapshots already recursively frozen and validated by its current reader.
    internal ExplorationSnapshot GetOrCreate(IndexSnapshot saved, string root, string indexPath, int indexSchema,
        CancellationToken token)
    {
        var fingerprint = ExplorationSnapshotCache.Serialize(saved, Stream.Null, token);
        var key = new Key(Canonical(root), Canonical(indexPath), saved.Graph.WorkspaceId, indexSchema,
            saved.Graph.SchemaVersion, saved.Graph.IdentityVersion, fingerprint, DerivedVersion);
        gate.Wait(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var position = entries.FindIndex(entry => entry.Key == key);
            if (position >= 0)
            {
                var entry = entries[position];
                entries.RemoveAt(position);
                entries.Add(entry);
                PhaseMetrics.Decision("explore.prepare", "validated-cache-hit");
                token.ThrowIfCancellationRequested();
                return entry.Snapshot;
            }
            PhaseMetrics.Decision("explore.prepare", "validated-cache-miss");
            var prepared = new ExplorationSnapshot(saved, token);
            token.ThrowIfCancellationRequested();
            // Capacity is charged in complete serialized payload bytes, not a managed-heap estimate.
            if (maximumEntries > 0 && fingerprint.Bytes <= maximumPayloadBytes)
            {
                token.ThrowIfCancellationRequested();
                while (entries.Count >= maximumEntries || payloadBytes > maximumPayloadBytes - fingerprint.Bytes)
                {
                    payloadBytes -= entries[0].Key.Payload.Bytes;
                    entries.RemoveAt(0);
                    PhaseMetrics.Decision("explore.prepare", "validated-cache-evicted");
                }
                entries.Add(new(key, prepared));
                payloadBytes += fingerprint.Bytes;
            }
            else
                PhaseMetrics.Decision("explore.prepare", "validated-cache-capacity-bypass");
            return prepared;
        }
        finally { gate.Release(); }
    }

    // Preserve case so distinct paths in a case-sensitive Windows directory cannot share an entry.
    private static string Canonical(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
