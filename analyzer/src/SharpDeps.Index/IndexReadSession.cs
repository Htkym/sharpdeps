namespace SharpDeps.Index;

using Microsoft.Data.Sqlite;
using SharpDeps.Analysis.Core.Diagnostics;

/// <summary>One connection, exclusive short-transaction leases, and one validated frozen snapshot.</summary>
internal sealed class IndexReadSession : IDisposable
{
    private readonly string indexPath;
    private readonly Guid workspaceUuid;
    private readonly Func<SqliteConnection, string, bool> physicalIdentity;
    private readonly bool nativeIdentitySupported;
    private readonly SemaphoreSlim gate = new(1, 1);
    private SqliteConnection? connection;
    private IndexReader.ValidatedState? ready;
    private long? readyDataVersion;
    private int disposed;

    internal IndexReadSession(string indexPath, Guid workspaceUuid,
        Func<SqliteConnection, string, bool>? physicalIdentity = null)
    {
        this.indexPath = IndexPaths.Absolute(indexPath);
        this.workspaceUuid = workspaceUuid;
        this.physicalIdentity = physicalIdentity ?? IndexPhysicalIdentity.IsCurrent;
        nativeIdentitySupported = physicalIdentity is not null || IndexPhysicalIdentity.Supported;
    }

    // Deterministic race injection; production callers leave this unset.
    internal Action? BeforePin { get; init; }

    internal IndexReader Acquire(CancellationToken token = default)
    {
        gate.Wait(token);
        IndexReader? reader = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            using var metricsScope = PhaseMetrics.Measure("reader.open");
            token.ThrowIfCancellationRequested();
            IndexPaths.Absolute(indexPath);
            if (!File.Exists(indexPath))
                throw new IndexStoreException("INDEX_NOT_FOUND", "No saved index exists.");
            IndexDatabase.InspectHeader(indexPath);
            IndexDatabase.CheckSidecars(indexPath, readOnly: true);
            if (!nativeIdentitySupported)
                return OpenFresh(token);
            if (connection is not null && !ProvesCurrent(connection))
                Reset();
            connection ??= IndexDatabase.Open(indexPath, readOnly: true);
            if (!ProvesCurrent(connection))
            {
                Reset();
                return OpenFresh(token);
            }
            BeforePin?.Invoke();
            reader = IndexReader.Begin(connection, indexPath, workspaceUuid, ownsConnection: false, token);
            // Metadata/header reads above established this transaction's snapshot before the marker.
            if (!TryDataVersion(connection, reader.Transaction, out var pinnedDataVersion) || !ProvesCurrent(connection))
            {
                reader.Dispose();
                reader = null;
                Reset();
                return OpenFresh(token);
            }
            if (ready is not null && readyDataVersion == pinnedDataVersion && reader.RestoreValidatedState(ready))
                PhaseMetrics.Decision("reader.session", "validated-snapshot-hit");
            else
            {
                PhaseMetrics.Decision("reader.session", "validated-snapshot-miss");
                _ = reader.Snapshot(token);
            }
            token.ThrowIfCancellationRequested();
            var lease = reader;
            reader.ReleaseLeaseWith(() => Release(lease, pinnedDataVersion));
            return reader;
        }
        catch
        {
            try { reader?.Dispose(); }
            finally { try { Reset(); } finally { gate.Release(); } }
            throw;
        }
    }

    private IndexReader OpenFresh(CancellationToken token)
    {
        BeforePin?.Invoke();
        var reader = IndexReader.Open(indexPath, workspaceUuid, token);
        reader.ReleaseLeaseWith(() => gate.Release());
        PhaseMetrics.Decision("reader.session", "full-validation-fallback");
        return reader;
    }

    private static bool TryDataVersion(SqliteConnection connection, SqliteTransaction? transaction, out long version)
    {
        try
        {
            if (IndexDatabase.Scalar(connection, transaction, "PRAGMA data_version;") is long value && value >= 0)
            { version = value; return true; }
        }
        catch (Exception error) when (error is SqliteException or InvalidOperationException or NotSupportedException) { }
        version = 0;
        return false;
    }

    private bool ProvesCurrent(SqliteConnection candidate)
    {
        try { return physicalIdentity(candidate, indexPath); }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        { return false; }
    }

    private void Release(IndexReader reader, long pinnedDataVersion)
    {
        try
        {
            // Never attach a post-release marker to an older snapshot. The autocommit probe
            // may detect a commit made during the lease, so publish only on exact equality.
            if (reader.LeaseClosedCleanly && connection is not null
                && ProvesCurrent(connection) && TryDataVersion(connection, null, out var afterVersion)
                && afterVersion == pinnedDataVersion)
            {
                ready = reader.CaptureValidatedState();
                readyDataVersion = pinnedDataVersion;
            }
            else Reset();
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException)
        { Reset(); }
        finally { gate.Release(); }
    }

    private void Reset()
    {
        ready = null;
        readyDataVersion = null;
        var previous = connection;
        connection = null;
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gate.Wait();
        try { Reset(); }
        finally { gate.Release(); }
    }
}
