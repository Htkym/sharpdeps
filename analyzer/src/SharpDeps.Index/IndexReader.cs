namespace SharpDeps.Index;

using SharpDeps.Analysis.Core.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;

/// <summary>One reader transaction pins one committed, recursively read-only snapshot. Dispose promptly; not thread-safe.</summary>
public sealed class IndexReader : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly HarnessGraphEnvelope header;
    private readonly int schemaVersion;
    private readonly bool utf8Rows;
    private IndexSnapshot? cachedSnapshot;
    private bool disposed;
    private IndexReader(SqliteConnection connection,SqliteTransaction transaction,HarnessGraphEnvelope header,string hash,int schemaVersion,string indexPath,bool utf8Rows)
    { this.connection=connection;this.transaction=transaction;this.header=header;ManifestHash=hash;this.schemaVersion=schemaVersion;IndexPath=indexPath;this.utf8Rows=utf8Rows; }
    public string ManifestHash { get; }
    public string IndexPath { get; }
    public int SchemaVersion => schemaVersion;
    public static IndexReader Open(string indexPath,Guid workspaceUuid,CancellationToken token=default)
    {
        using var metricsScope = PhaseMetrics.Measure("reader.open");
        token.ThrowIfCancellationRequested();
        var workspace=HarnessIdentity.WorkspaceId(workspaceUuid);var path=IndexPaths.Absolute(indexPath);
        if(!File.Exists(path))throw new IndexStoreException("INDEX_NOT_FOUND", "No saved index exists.");
        IndexDatabase.InspectHeader(path);
        token.ThrowIfCancellationRequested();
        var connection=IndexDatabase.Open(path,readOnly:true);SqliteTransaction? transaction=null;
        try
        {
            using var progress=new ReadCancellation(connection,token);
            token.ThrowIfCancellationRequested();
            transaction=connection.BeginTransaction(deferred:true);
            var schemaVersion=IndexDatabase.CheckMetadata(connection,transaction,workspace);
            // CAST(TEXT AS BLOB) follows the database encoding; retain the string path for UTF-16 databases.
            var utf8Rows=string.Equals((string?)IndexDatabase.Scalar(connection,transaction,"PRAGMA encoding;"),"UTF-8",StringComparison.OrdinalIgnoreCase);
            token.ThrowIfCancellationRequested();
            // Rows are the validated graph source; project only header fields from legacy full-graph JSON.
            using var command=IndexDatabase.Command(connection,transaction,"""
                SELECT g.generation,g.snapshot_id,g.manifest_hash,
                    CASE WHEN json_type(g.graph_json,'$.nodes')='array'
                        AND json_type(g.graph_json,'$.variants')='array'
                        AND json_type(g.graph_json,'$.symbolOccurrences')='array'
                        AND json_type(g.graph_json,'$.edges')='array'
                        AND json_type(g.graph_json,'$.legacyReferences')='array'
                    THEN json_set(g.graph_json,'$.nodes',json('[]'),'$.variants',json('[]'),
                        '$.symbolOccurrences',json('[]'),'$.edges',json('[]'),'$.legacyReferences',json('[]'))
                    ELSE g.graph_json END
                FROM generations g JOIN metadata m ON m.active_generation=g.generation
                WHERE m.singleton=1 AND g.status='committed'
                """);
            using var row=command.ExecuteReader();
            token.ThrowIfCancellationRequested();
            if(!ReadRow(row,token))throw new IndexStoreException("INDEX_NO_SNAPSHOT", "No committed generation exists.");
            var header=HarnessGraphContract.Read(row.GetString(3));
            token.ThrowIfCancellationRequested();
            if(header.Generation!=row.GetInt64(0)||header.SnapshotId!=row.GetString(1)||header.WorkspaceId!=workspace)
                throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Saved generation metadata disagrees with the graph.");
            var result=new IndexReader(connection,transaction,header,row.GetString(2),schemaVersion,path,utf8Rows);row.Close();
            _=result.Snapshot(token);token.ThrowIfCancellationRequested();return result;
        }
        catch(SqliteException error) when(token.IsCancellationRequested && error.SqliteErrorCode==SQLitePCL.raw.SQLITE_INTERRUPT)
        {transaction?.Dispose();connection.Dispose();throw new OperationCanceledException("Saved index read was cancelled.",error,token);}
        catch{transaction?.Dispose();connection.Dispose();throw;}
    }
    public IndexSnapshot Snapshot(CancellationToken token=default)
    {
        using var metricsScope = PhaseMetrics.Measure("reader.snapshot");
        ObjectDisposedException.ThrowIf(disposed,this);
        token.ThrowIfCancellationRequested();
        if (cachedSnapshot is not null)
        {
            PhaseMetrics.Decision("reader.snapshot", "cache-hit");
            return cachedSnapshot;
        }
        using var progress=new ReadCancellation(connection,token);
        try
        {
            var graph=header with {Nodes=Read<HarnessNode>("nodes",token),Variants=Read<HarnessVariant>("variants",token),
                SymbolOccurrences=Read<HarnessSymbolOccurrence>("occurrences",token),Edges=Read<HarnessEdge>("edges",token),LegacyReferences=Read<HarnessLegacyReference>("legacy_references",token)};
            var search=new List<IndexSearchText>();
            using(var command=IndexDatabase.Command(connection,transaction,"SELECT node_id,name,signature,heading,body,path FROM search_text WHERE generation=$g ORDER BY node_id",("$g",header.Generation)))
            using(var rows=command.ExecuteReader())
                while(ReadRow(rows,token))search.Add(new IndexSearchText(rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.GetString(5)));
            var bodies = schemaVersion >= 2 ? ReadGeneratedBodies(token) : [];
            var packages = schemaVersion >= 3 ? ReadPackageBodies(token) : [];
            var snapshot=new IndexSnapshot(graph,Read<IndexFile>("files",token),Read<IndexDocumentIdentity>("document_identities",token),Read<IndexAlias>("aliases",token),search.AsReadOnly(),bodies,packages);
            using (PhaseMetrics.Measure("reader.validate"))
                IndexSnapshotValidator.Validate(snapshot,token);
            token.ThrowIfCancellationRequested();
            var manifestHash=IndexStage.ComputeManifestHash(snapshot.Files);
            token.ThrowIfCancellationRequested();
            if(manifestHash!=ManifestHash)
                throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Saved input manifest hash differs.");
            snapshot = snapshot with
            {
                Graph = graph with
                {
                    SymbolOccurrences = Copy(graph.SymbolOccurrences, token, occurrence => occurrence with
                        { Declarations = occurrence.Declarations is null ? null : Copy(occurrence.Declarations, token) }),
                    Diagnostics = graph.Diagnostics is null ? null : Copy(graph.Diagnostics, token)
                },
                Files = Copy(snapshot.Files, token, file => Freeze(file, token)),
                Documents = Copy(snapshot.Documents, token, document => document with
                    { Sections = Copy(document.Sections, token) })
            };
            token.ThrowIfCancellationRequested();
            return cachedSnapshot=snapshot;
        }
        catch(SqliteException error) when(token.IsCancellationRequested && error.SqliteErrorCode==SQLitePCL.raw.SQLITE_INTERRUPT)
        {throw new OperationCanceledException("Saved index read was cancelled.",error,token);}
    }
    private IReadOnlyList<IndexGeneratedBody> ReadGeneratedBodies(CancellationToken token)
    {
        using var metricsScope = PhaseMetrics.Measure("reader.generated-bodies");
        token.ThrowIfCancellationRequested();
        var result=new List<IndexGeneratedBody>();
        using var command=IndexDatabase.Command(connection,transaction,"SELECT source_id,body FROM generated_bodies WHERE generation=$g ORDER BY source_id",("$g",header.Generation));
        using var rows=command.ExecuteReader();
        while(ReadRow(rows,token)) result.Add(new IndexGeneratedBody(rows.GetString(0),rows.GetString(1)));
        return result.AsReadOnly();
    }
    private IReadOnlyList<IndexPackageBody> ReadPackageBodies(CancellationToken token)
    {
        using var metricsScope = PhaseMetrics.Measure("reader.package-bodies");
        token.ThrowIfCancellationRequested();
        var result=new List<IndexPackageBody>();
        using var command=IndexDatabase.Command(connection,transaction,"SELECT source_id,body FROM package_bodies WHERE generation=$g ORDER BY source_id",("$g",header.Generation));
        using var rows=command.ExecuteReader();
        while(ReadRow(rows,token)) result.Add(new IndexPackageBody(rows.GetString(0),rows.GetString(1)));
        return result.AsReadOnly();
    }
    private static IndexFile Freeze(IndexFile file, CancellationToken token)
    {
        if (file.Reuse is not { } reuse) return file;
        return file with { Reuse = reuse with
        {
            Catalog = Copy(reuse.Catalog, token),
            Projects = reuse.Projects is not { } projects ? null : projects with
            {
                Variants = Copy(projects.Variants, token, project => project with
                {
                    SourceIds = Copy(project.SourceIds, token),
                    References = Copy(project.References, token)
                })
            }
        } };
    }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> items, CancellationToken token, Func<T,T>? freeze = null)
    {
        var copy = new T[items.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            copy[i] = freeze is null ? items[i] : freeze(items[i]);
        }
        return Array.AsReadOnly(copy);
    }

    private IReadOnlyList<T> Read<T>(string table,CancellationToken token)
    {
        using var metricsScope = PhaseMetrics.Measure("reader.rows-" + table.Replace('_', '-'));
        token.ThrowIfCancellationRequested();
        var result=new List<T>();
        var typeInfo=(JsonTypeInfo<T>)IndexReadJsonContext.Default.GetTypeInfo(typeof(T))!;
        var projection=utf8Rows ? "CAST(json AS BLOB)" : "json";
        using var command=IndexDatabase.Command(connection,transaction,$"SELECT {projection} FROM {table} WHERE generation=$g ORDER BY rowid",("$g",header.Generation));
        using var rows=command.ExecuteReader();
        byte[] buffer=[];
        while(ReadRow(rows,token))
        {
            T? item;
            if(utf8Rows)
            {
                var length=rows.GetBytes(0,0,null,0,0);
                if(length>int.MaxValue)throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Saved row exceeds the readable size.");
                if(buffer.Length<length)buffer=new byte[Math.Max(4096,(int)length)];
                if(rows.GetBytes(0,0,buffer,0,(int)length)!=length)
                    throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Saved row could not be read completely.");
                item=JsonSerializer.Deserialize(buffer.AsSpan(0,(int)length),typeInfo);
            }
            else item=JsonSerializer.Deserialize(rows.GetString(0),typeInfo);
            if(item is null)throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Saved rows cannot be null.");
            token.ThrowIfCancellationRequested();
            result.Add(item);
        }
        return result.AsReadOnly();
    }
    public IReadOnlyList<IndexSearchHit> Search(string term,int limit=100,CancellationToken token=default)
    {
        using var metricsScope = PhaseMetrics.Measure("reader.search");
        ObjectDisposedException.ThrowIf(disposed,this);
        token.ThrowIfCancellationRequested();
        if(string.IsNullOrWhiteSpace(term)||term.Length>128||term.Any(char.IsControl)||limit<1||limit>500)
            throw new ArgumentException("Search requires a short literal term and 1..500 results.");
        using var progress=new ReadCancellation(connection,token);
        try
        {
            var results=new List<IndexSearchHit>();
            var query="\""+term.Replace("\"","\"\"",StringComparison.Ordinal)+"\"";
            var searchTable=IndexDatabase.SearchTable(header.Generation);
            using(var command=IndexDatabase.Command(connection,transaction,$"SELECT t.node_id,bm25({searchTable},10,5,8,1,2) FROM {searchTable} JOIN search_text t ON t.rowid={searchTable}.rowid WHERE {searchTable} MATCH $query AND t.generation=$g ORDER BY 2,t.node_id LIMIT $limit",("$query",query),("$g",header.Generation),("$limit",limit)))
            using(var rows=command.ExecuteReader())
                while(ReadRow(rows,token))results.Add(new IndexSearchHit(rows.GetString(0),rows.GetDouble(1),"fts5-unicode61"));
            // Literal substring handles Japanese segmentation and identifier substrings without another tokenizer.
            if(results.Count<limit)
            {
                using var command=IndexDatabase.Command(connection,transaction,"SELECT node_id FROM search_text WHERE generation=$g AND (instr(lower(name),lower($term))>0 OR instr(lower(signature),lower($term))>0 OR instr(lower(heading),lower($term))>0 OR instr(lower(body),lower($term))>0 OR instr(lower(path),lower($term))>0) ORDER BY node_id LIMIT $limit",("$g",header.Generation),("$term",term),("$limit",limit));
                using var rows=command.ExecuteReader();var ids=results.Select(h=>h.NodeId).ToHashSet(StringComparer.Ordinal);
                while(results.Count<limit && ReadRow(rows,token))if(ids.Add(rows.GetString(0)))results.Add(new IndexSearchHit(rows.GetString(0),0,"literal-substring"));
            }
            token.ThrowIfCancellationRequested();
            return results.AsReadOnly();
        }
        catch(SqliteException error) when(token.IsCancellationRequested && error.SqliteErrorCode==SQLitePCL.raw.SQLITE_INTERRUPT)
        {throw new OperationCanceledException("Saved index search was cancelled.",error,token);}
    }
    private static bool ReadRow(SqliteDataReader rows,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result=rows.Read();
        token.ThrowIfCancellationRequested();
        return result;
    }
    private sealed class ReadCancellation : IDisposable
    {
        private readonly SQLitePCL.sqlite3? handle;
        public ReadCancellation(SqliteConnection connection,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(!token.CanBeCanceled)return;
            handle=connection.Handle!;
            SQLitePCL.raw.sqlite3_progress_handler(handle,1000,static state=>((CancellationToken)state).IsCancellationRequested?1:0,token);
        }
        public void Dispose()
        {
            if(handle is not null)SQLitePCL.raw.sqlite3_progress_handler(handle,0,null!,null!);
        }
    }
    public void Dispose()
    {if(disposed)return;disposed=true;try{transaction.Dispose();}finally{connection.Dispose();}}
}
