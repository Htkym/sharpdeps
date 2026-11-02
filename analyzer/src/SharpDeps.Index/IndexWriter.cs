namespace SharpDeps.Index;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;

/// <summary>Acquire before extraction. One lease covers analysis, staging and commit; never delete its lock file.</summary>
public sealed class IndexWriter : IDisposable
{
    private readonly FileStream lease;
    private readonly SqliteConnection connection;
    private readonly int schemaVersion;
    private readonly object gate = new();
    private bool disposed;
    private IndexWriter(string root, string path, Guid workspace, FileStream lease, SqliteConnection connection, int schemaVersion)
    { CanonicalRoot=root;IndexPath=path;WorkspaceUuid=workspace;this.lease=lease;this.connection=connection;this.schemaVersion=schemaVersion; }
    public string CanonicalRoot { get; }
    public string IndexPath { get; }
    public Guid WorkspaceUuid { get; }
    /// <summary>The schema of the opened index: 1 for an existing index, the current schema for a new one.</summary>
    public int SchemaVersion => schemaVersion;
    internal Action? BeforePublish { get; set; }

    public static IndexWriter Open(string canonicalRoot, string indexPath, Guid workspaceUuid, bool isTrusted)
    {
        RequireTrust(isTrusted);
        var workspace=HarnessIdentity.WorkspaceId(workspaceUuid);
        var root=Path.TrimEndingDirectorySeparator(IndexPaths.Absolute(canonicalRoot));
        if (!Directory.Exists(root)) throw new IndexStoreException("INDEX_INVALID_PATH", "The workspace directory must exist.");
        var path=IndexPaths.Absolute(indexPath); var directory=Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        { Directory.CreateDirectory(directory); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute); }
        IndexPaths.RejectLinks(path + ".writer.lock");
        FileStream lease;
        try { lease=new FileStream(path+".writer.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch (IOException error) { throw new IndexStoreException("INDEX_BUSY", "Another storage owner holds this index lease.",error); }
        SqliteConnection? connection=null;
        try
        {
            using var process=Process.GetCurrentProcess();
            var existing=File.Exists(path);
            var schemaVersion=existing ? IndexDatabase.InspectHeader(path) : IndexDatabase.CurrentSchemaVersion;
            var owner=new IndexWriterOwner(Environment.ProcessId,process.StartTime.ToUniversalTime().ToString("o"),root,path,schemaVersion);
            lease.SetLength(0); JsonSerializer.Serialize(lease,owner,IndexStage.Json);lease.Flush(true);
            if (existing)
            {
                // Without SHM, opening a WAL database could recover an unsupported schema before validation.
                if (!File.Exists(path+"-wal") || !File.Exists(path+"-shm"))
                    throw new IndexStoreException("INDEX_RECOVERY_REQUIRED", "Keep the existing WAL index intact and rebuild into a separate index.");
                // A newer schema can be in WAL even before its header is checkpointed.
                using var probe=IndexDatabase.Open(path,readOnly:true);
                schemaVersion=IndexDatabase.CheckMetadata(probe,null,workspace);
            }
            connection=IndexDatabase.Open(path,readOnly:false);
            if (existing) schemaVersion=IndexDatabase.CheckMetadata(connection,null,workspace);
            if (!string.Equals((string?)IndexDatabase.Scalar(connection,null,"PRAGMA journal_mode=WAL;"),"wal",StringComparison.OrdinalIgnoreCase))
                throw new IndexStoreException("INDEX_WAL_UNAVAILABLE", "This index requires WAL mode on a local filesystem.");
            IndexDatabase.Execute(connection,null,"PRAGMA synchronous=FULL; PRAGMA busy_timeout=3000;");
            if (!existing) IndexDatabase.Create(connection,workspace,schemaVersion);
            if (!OperatingSystem.IsWindows())
                foreach (var file in new[]{path,path+"-wal",path+"-shm",path+".writer.lock"})
                    if (File.Exists(file)) File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            return new IndexWriter(root,path,workspaceUuid,lease,connection,schemaVersion);
        }
        catch { connection?.Dispose();lease.Dispose();throw; }
    }

    public void Commit(IndexStage stage, CancellationToken cancellationToken=default, Action? verifySelection=null)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (!Monitor.TryEnter(gate)) throw new IndexStoreException("INDEX_BUSY", "This writer already has an operation in progress.");
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var snapshot=stage.Materialize(); var graph=snapshot.Graph; var generation=graph.Generation;
            IndexSnapshotValidator.Validate(snapshot);
            var generatedBodies=snapshot.StoredGeneratedBodies.ToDictionary(body=>body.SourceId,body=>body.Body,StringComparer.Ordinal);
            if (snapshot.StoredGeneratedBodies.Count != 0 && schemaVersion < IndexDatabase.CurrentSchemaVersion)
                throw new IndexStoreException("INDEX_SCHEMA_MIGRATION_REQUIRED", "This schema 1 index cannot store generated document bodies; retain it and rebuild into a separate index.");
            if (graph.WorkspaceId!=HarnessIdentity.WorkspaceId(WorkspaceUuid))
                throw new IndexStoreException("INDEX_WORKSPACE_MISMATCH", "Staging belongs to a different workspace.");
            IndexPaths.VerifyInputs(CanonicalRoot,snapshot.Files,cancellationToken,generatedBodies);
            using var transaction=connection.BeginTransaction();
            var active=IndexDatabase.Scalar(connection,transaction,"SELECT active_generation FROM metadata WHERE singleton=1");
            if (active is long prior && generation<=prior)
                throw new IndexStoreException("INDEX_GENERATION_CONFLICT", "A new commit must advance the active generation.");
            Execute("INSERT INTO generations VALUES($g,$snapshot,$manifest,$coverage,'committed',$utc,$json)",
                ("$snapshot",graph.SnapshotId),("$manifest",stage.ManifestHash),("$coverage",graph.Coverage.ToString()),
                ("$utc",DateTimeOffset.UtcNow.ToString("o")),("$json",JsonSerializer.Serialize(graph,HarnessGraphJsonContext.Default.HarnessGraphEnvelope)));
            foreach (var file in snapshot.Files) Execute("INSERT INTO files VALUES($g,$id,$path,$deleted,$json)",("$id",file.SourceId),("$path",file.RelativePath),("$deleted",file.Deleted?1:0),("$json",Serialize(file)));
            foreach (var body in snapshot.StoredGeneratedBodies) Execute("INSERT INTO generated_bodies VALUES($g,$id,$body)",("$id",body.SourceId),("$body",body.Body));
            foreach (var node in graph.Nodes) Execute("INSERT INTO nodes VALUES($g,$id,$kind,$parent,$json)",("$id",node.Id),("$kind",node.Kind.ToString()),("$parent",node.ParentId),("$json",Serialize(node)));
            foreach (var variant in graph.Variants) Execute("INSERT INTO variants VALUES($g,$id,$project,$json)",("$id",variant.Id),("$project",variant.ProjectId),("$json",Serialize(variant)));
            foreach (var occurrence in graph.SymbolOccurrences) Execute("INSERT INTO occurrences VALUES($g,$id,$logical,$variant,$json)",("$id",occurrence.Id),("$logical",occurrence.LogicalSymbolId),("$variant",occurrence.VariantId),("$json",Serialize(occurrence)));
            foreach (var edge in graph.Edges) Execute("INSERT INTO edges VALUES($g,$id,$source,$target,$so,$to,$variant,$json)",("$id",edge.Id),("$source",edge.SourceNodeId),("$target",edge.TargetNodeId),("$so",edge.SourceOccurrenceId),("$to",edge.TargetOccurrenceId),("$variant",edge.VariantId),("$json",Serialize(edge)));
            foreach (var document in snapshot.Documents)
            {
                var id=HarnessIdentity.DocumentId(WorkspaceUuid,document.DocumentUuid);
                Execute("INSERT INTO document_identities VALUES($g,$id,$uuid,$path,$json)",("$id",id),("$uuid",document.DocumentUuid.ToString("N")),("$path",document.RelativePath),("$json",Serialize(document)));
                foreach (var section in document.Sections)
                    Execute("INSERT INTO section_identities VALUES($g,$doc,$section,$token)",("$doc",id),("$section",HarnessIdentity.SectionId(WorkspaceUuid,id,section.Token)),("$token",section.Token));
            }
            foreach (var alias in snapshot.Aliases) Execute("INSERT INTO aliases VALUES($g,$kind,$old,$new,$json)",("$kind",alias.Kind),("$old",alias.OldId),("$new",alias.NewId),("$json",Serialize(alias)));
            foreach (var legacy in graph.LegacyReferences) Execute("INSERT INTO legacy_references VALUES($g,$schema,$old,$node,$occurrence,$json)",("$schema",legacy.SchemaVersion),("$old",legacy.LegacyId),("$node",legacy.NodeId),("$occurrence",legacy.OccurrenceId),("$json",Serialize(legacy)));
            IndexDatabase.CreateGenerationSearch(connection,transaction,generation);
            var searchTable=IndexDatabase.SearchTable(generation);
            foreach (var text in snapshot.SearchText)
            {
                Execute("INSERT INTO search_text(generation,node_id,name,signature,heading,body,path) VALUES($g,$id,$name,$signature,$heading,$body,$path)",("$id",text.NodeId),("$name",text.Name),("$signature",text.Signature),("$heading",text.Heading),("$body",text.Body),("$path",text.Path));
                Execute($"INSERT INTO {searchTable}(rowid,name,signature,heading,body,path) SELECT rowid,name,signature,heading,body,path FROM search_text WHERE generation=$g AND node_id=$id",("$id",text.NodeId));
            }
            // Test fault injection uses the real boundary after all rows and FTS, before manifest/active publication.
            BeforePublish?.Invoke();
            IndexPaths.VerifyInputs(CanonicalRoot,snapshot.Files,cancellationToken,generatedBodies);
            // The extraction owner can also verify negative inventory and non-root dependencies.
            // Failures roll back rows/FTS before the active generation changes.
            if (verifySelection is not null)
            {
                verifySelection();
                IndexPaths.VerifyInputs(CanonicalRoot,snapshot.Files,cancellationToken,generatedBodies);
            }
            Execute("UPDATE metadata SET active_generation=$g,canonical_root=$root,writer_version='sharpdeps-index/1' WHERE singleton=1",("$root",CanonicalRoot));
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();

            void Execute(string sql, params (string Name,object? Value)[] values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexDatabase.Execute(connection,transaction,sql,[("$g",generation),..values]);
            }
        }
        catch (SqliteException error) { throw new IndexStoreException(error.SqliteErrorCode is 5 or 6 ? "INDEX_BUSY" : "INDEX_WRITE_FAILED", "The staged index transaction was not published.",error); }
        finally { Monitor.Exit(gate); }
    }

    public void BackupTo(string destination, bool isTrusted)
    {
        RequireTrust(isTrusted);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var path=IndexPaths.Absolute(destination);
            IndexPaths.RejectLinks(path+".writer.lock");
            if (File.Exists(path)) throw new IndexStoreException("INDEX_BACKUP_EXISTS", "Backups never overwrite an existing index.");
            using var targetLease=new FileStream(path+".writer.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if (File.Exists(path)) throw new IndexStoreException("INDEX_BACKUP_EXISTS", "The backup destination now exists.");
            using var target=IndexDatabase.Open(path,readOnly:false);
            connection.BackupDatabase(target);
            IndexDatabase.Execute(target,null,"PRAGMA journal_mode=WAL;");
            // Create readable persistent sidecars, and ensure the backup schema header is checkpointed.
            IndexDatabase.Execute(target,null,"BEGIN IMMEDIATE; UPDATE metadata SET singleton=singleton; COMMIT; PRAGMA wal_checkpoint(FULL);");
            if (!OperatingSystem.IsWindows())
                foreach(var file in new[]{path,path+"-wal",path+"-shm",path+".writer.lock"})
                    if(File.Exists(file)) File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        }
    }
    private static string Serialize<T>(T value)=>JsonSerializer.Serialize(value,IndexStage.Json);
    private static void RequireTrust(bool trusted)
    { if(!HarnessTrustPolicy.Allows(HarnessOperation.WriteStore,trusted)) throw new IndexStoreException("INDEX_UNTRUSTED", "Storage writes require workspace trust."); }
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;
            try {connection.Dispose();} finally {lease.Dispose();}
        }
    }
}
