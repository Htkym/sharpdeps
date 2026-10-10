namespace SharpDeps.Index;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;

/// <summary>One reader transaction pins one committed generation. Dispose promptly; not thread-safe.</summary>
public sealed class IndexReader : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly HarnessGraphEnvelope header;
    private bool disposed;
    private IndexReader(SqliteConnection connection,SqliteTransaction transaction,HarnessGraphEnvelope header,string hash)
    { this.connection=connection;this.transaction=transaction;this.header=header;ManifestHash=hash; }
    public string ManifestHash { get; }
    public static IndexReader Open(string indexPath,Guid workspaceUuid)
    {
        var workspace=HarnessIdentity.WorkspaceId(workspaceUuid);var path=IndexPaths.Absolute(indexPath);
        if(!File.Exists(path))throw new IndexStoreException("INDEX_NOT_FOUND", "No saved index exists.");
        IndexDatabase.InspectHeader(path);
        var connection=IndexDatabase.Open(path,readOnly:true);SqliteTransaction? transaction=null;
        try
        {
            transaction=connection.BeginTransaction(deferred:true);
            IndexDatabase.CheckMetadata(connection,transaction,workspace);
            using var command=IndexDatabase.Command(connection,transaction,"SELECT g.generation,g.snapshot_id,g.manifest_hash,g.graph_json FROM generations g JOIN metadata m ON m.active_generation=g.generation WHERE m.singleton=1 AND g.status='committed'");
            using var row=command.ExecuteReader();
            if(!row.Read())throw new IndexStoreException("INDEX_NO_SNAPSHOT", "No committed generation exists.");
            var header=HarnessGraphContract.Read(row.GetString(3));
            if(header.Generation!=row.GetInt64(0)||header.SnapshotId!=row.GetString(1)||header.WorkspaceId!=workspace)
                throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Saved generation metadata disagrees with the graph.");
            var result=new IndexReader(connection,transaction,header,row.GetString(2));row.Close();
            _=result.Snapshot();return result;
        }
        catch{transaction?.Dispose();connection.Dispose();throw;}
    }
    public IndexSnapshot Snapshot()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var graph=header with {Nodes=Read<HarnessNode>("nodes"),Variants=Read<HarnessVariant>("variants"),
            SymbolOccurrences=Read<HarnessSymbolOccurrence>("occurrences"),Edges=Read<HarnessEdge>("edges"),LegacyReferences=Read<HarnessLegacyReference>("legacy_references")};
        var search=new List<IndexSearchText>();
        using(var command=IndexDatabase.Command(connection,transaction,"SELECT node_id,name,signature,heading,body,path FROM search_text WHERE generation=$g ORDER BY node_id",("$g",header.Generation)))
        using(var rows=command.ExecuteReader())
            while(rows.Read())search.Add(new IndexSearchText(rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.GetString(5)));
        var snapshot=new IndexSnapshot(graph,Read<IndexFile>("files"),Read<IndexDocumentIdentity>("document_identities"),Read<IndexAlias>("aliases"),search.AsReadOnly());
        IndexSnapshotValidator.Validate(snapshot);
        if(IndexStage.ComputeManifestHash(snapshot.Files)!=ManifestHash)
            throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Saved input manifest hash differs.");
        return snapshot;
    }
    private IReadOnlyList<T> Read<T>(string table)
    {
        var result=new List<T>();
        using var command=IndexDatabase.Command(connection,transaction,$"SELECT json FROM {table} WHERE generation=$g ORDER BY rowid",("$g",header.Generation));
        using var rows=command.ExecuteReader();
        while(rows.Read())result.Add(JsonSerializer.Deserialize<T>(rows.GetString(0),IndexStage.Json)
            ?? throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Saved rows cannot be null."));
        return result.AsReadOnly();
    }
    public IReadOnlyList<IndexSearchHit> Search(string term,int limit=100)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(string.IsNullOrWhiteSpace(term)||term.Length>128||term.Any(char.IsControl)||limit<1||limit>500)
            throw new ArgumentException("Search requires a short literal term and 1..500 results.");
        var results=new List<IndexSearchHit>();
        var query="\""+term.Replace("\"","\"\"",StringComparison.Ordinal)+"\"";
        var searchTable=IndexDatabase.SearchTable(header.Generation);
        using(var command=IndexDatabase.Command(connection,transaction,$"SELECT t.node_id,bm25({searchTable},10,5,8,1,2) FROM {searchTable} JOIN search_text t ON t.rowid={searchTable}.rowid WHERE {searchTable} MATCH $query AND t.generation=$g ORDER BY 2,t.node_id LIMIT $limit",("$query",query),("$g",header.Generation),("$limit",limit)))
        using(var rows=command.ExecuteReader())
            while(rows.Read())results.Add(new IndexSearchHit(rows.GetString(0),rows.GetDouble(1),"fts5-unicode61"));
        // Literal substring handles Japanese segmentation and identifier substrings without another tokenizer.
        if(results.Count<limit)
        {
            using var command=IndexDatabase.Command(connection,transaction,"SELECT node_id FROM search_text WHERE generation=$g AND (instr(lower(name),lower($term))>0 OR instr(lower(signature),lower($term))>0 OR instr(lower(heading),lower($term))>0 OR instr(lower(body),lower($term))>0 OR instr(lower(path),lower($term))>0) ORDER BY node_id LIMIT $limit",("$g",header.Generation),("$term",term),("$limit",limit));
            using var rows=command.ExecuteReader();var ids=results.Select(h=>h.NodeId).ToHashSet(StringComparer.Ordinal);
            while(rows.Read()&&results.Count<limit)if(ids.Add(rows.GetString(0)))results.Add(new IndexSearchHit(rows.GetString(0),0,"literal-substring"));
        }
        return results.AsReadOnly();
    }
    public void Dispose()
    {if(disposed)return;disposed=true;try{transaction.Dispose();}finally{connection.Dispose();}}
}
