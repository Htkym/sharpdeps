namespace SharpDeps.Index;

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

internal static class IndexDatabase
{
    public const int SchemaVersion = 1;
    private const int ApplicationId = 0x53444831; // SDH1, separate from graph/report versions.

    public static void InspectHeader(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[100];
        if (file.Read(header) != 100 || !header[..16].SequenceEqual("SQLite format 3\0"u8)
            || BinaryPrimitives.ReadInt32BigEndian(header[60..64]) != SchemaVersion
            || BinaryPrimitives.ReadInt32BigEndian(header[68..72]) != ApplicationId)
            throw new IndexStoreException("INDEX_SCHEMA_UNSUPPORTED", "This index schema is not supported; retain it and rebuild into a separate index.");
    }
    public static SqliteConnection Open(string path, bool readOnly)
    {
        IndexPaths.RejectLinks(path + "-wal");
        IndexPaths.RejectLinks(path + "-shm");
        if (readOnly && (!File.Exists(path + "-wal") || !File.Exists(path + "-shm")))
            throw new IndexStoreException("INDEX_RECOVERY_REQUIRED", "A trusted writer must prepare existing WAL sidecars before a saved read.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource=path, Mode=readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache=SqliteCacheMode.Private, Pooling=false, ForeignKeys=true, DefaultTimeout=3 }.ToString());
        try
        {
            connection.Open();
            // Retain sidecars across normal closes so ReadSaved never creates them.
            var persist = 1;
            if (sqlite3_file_control(connection.Handle!.DangerousGetHandle(), "main", 10, ref persist) != 0)
                throw new IndexStoreException("INDEX_WAL_UNAVAILABLE", "The native VFS cannot preserve WAL sidecars.");
            if (readOnly) Execute(connection, null, "PRAGMA query_only=ON;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    [DllImport("e_sqlite3", CallingConvention=CallingConvention.Cdecl)]
    private static extern int sqlite3_file_control(IntPtr database,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string databaseName, int operation, ref int value);

    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText=sql;
        foreach (var (name,value) in values) command.Parameters.AddWithValue(name,value ?? DBNull.Value);
        return command;
    }
    public static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    { using var command=Command(connection,transaction,sql,values);command.ExecuteNonQuery(); }
    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    { using var command=Command(connection,transaction,sql,values);return command.ExecuteScalar(); }
    public static void CheckMetadata(SqliteConnection connection, SqliteTransaction? transaction, string workspace)
    {
        if (Convert.ToInt32(Scalar(connection,transaction,"PRAGMA user_version;")) != SchemaVersion
            || Convert.ToInt32(Scalar(connection,transaction,"PRAGMA application_id;")) != ApplicationId)
            throw new IndexStoreException("INDEX_SCHEMA_UNSUPPORTED", "The index schema is unsupported.");
        if ((string?)Scalar(connection,transaction,"SELECT workspace_id FROM metadata WHERE singleton=1") != workspace)
            throw new IndexStoreException("INDEX_WORKSPACE_MISMATCH", "The persisted workspace UUID differs.");
    }
    public static void Create(SqliteConnection c, string workspace)
    {
        using var transaction=c.BeginTransaction();
        Execute(c,transaction,Schema);
        Execute(c,transaction,"INSERT INTO metadata(singleton,workspace_id,active_generation) VALUES(1,$workspace,NULL)",("$workspace",workspace));
        Execute(c,transaction,$"PRAGMA user_version={SchemaVersion}; PRAGMA application_id={ApplicationId};");
        transaction.Commit();
        // Publish version header before any readonly schema inspection.
        Execute(c,null,"PRAGMA wal_checkpoint(FULL);");
    }
    public static string SearchTable(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        return "search_fts_g" + generation.ToString(CultureInfo.InvariantCulture);
    }
    public static void CreateGenerationSearch(SqliteConnection c, SqliteTransaction transaction, long generation)
    {
        var table = SearchTable(generation);
        // The corpus and external-content view contain exactly one generation, including FTS rebuilds.
        Execute(c, transaction, $"""
            CREATE VIEW {table}_content AS
              SELECT rowid,name,signature,heading,body,path FROM search_text WHERE generation={generation.ToString(CultureInfo.InvariantCulture)};
            CREATE VIRTUAL TABLE {table} USING fts5(name,signature,heading,body,path,
              content='{table}_content',content_rowid='rowid',tokenize='unicode61');
            """);
    }
    private const string Schema = """
        CREATE TABLE generations(generation INTEGER PRIMARY KEY CHECK(generation>=0), snapshot_id TEXT NOT NULL UNIQUE,
          manifest_hash TEXT NOT NULL, coverage TEXT NOT NULL, status TEXT NOT NULL CHECK(status='committed'),
          committed_utc TEXT NOT NULL, graph_json TEXT NOT NULL);
        CREATE TABLE metadata(singleton INTEGER PRIMARY KEY CHECK(singleton=1), workspace_id TEXT NOT NULL,
          writer_version TEXT NOT NULL DEFAULT 'sharpdeps-index/1', canonical_root TEXT,
          active_generation INTEGER REFERENCES generations(generation));
        CREATE TABLE files(generation INTEGER NOT NULL REFERENCES generations, source_id TEXT NOT NULL,
          path TEXT NOT NULL, deleted INTEGER NOT NULL CHECK(deleted IN(0,1)), json TEXT NOT NULL,
          PRIMARY KEY(generation,source_id), UNIQUE(generation,path));
        CREATE TABLE nodes(generation INTEGER NOT NULL REFERENCES generations, id TEXT NOT NULL, kind TEXT NOT NULL,
          parent_id TEXT, json TEXT NOT NULL, PRIMARY KEY(generation,id),
          FOREIGN KEY(generation,parent_id) REFERENCES nodes(generation,id) DEFERRABLE INITIALLY DEFERRED);
        CREATE INDEX nodes_kind ON nodes(generation,kind);
        CREATE TABLE variants(generation INTEGER NOT NULL, id TEXT NOT NULL, project_id TEXT NOT NULL,json TEXT NOT NULL,
          PRIMARY KEY(generation,id), FOREIGN KEY(generation,project_id) REFERENCES nodes(generation,id));
        CREATE TABLE occurrences(generation INTEGER NOT NULL,id TEXT NOT NULL, logical_id TEXT NOT NULL, variant_id TEXT NOT NULL,json TEXT NOT NULL,
          PRIMARY KEY(generation,id), FOREIGN KEY(generation,logical_id) REFERENCES nodes(generation,id),
          FOREIGN KEY(generation,variant_id) REFERENCES variants(generation,id));
        CREATE TABLE edges(generation INTEGER NOT NULL,id TEXT NOT NULL,source_id TEXT NOT NULL,target_id TEXT NOT NULL,
          source_occurrence TEXT,target_occurrence TEXT,variant_id TEXT,json TEXT NOT NULL, PRIMARY KEY(generation,id),
          FOREIGN KEY(generation,source_id) REFERENCES nodes(generation,id), FOREIGN KEY(generation,target_id) REFERENCES nodes(generation,id),
          FOREIGN KEY(generation,source_occurrence) REFERENCES occurrences(generation,id),
          FOREIGN KEY(generation,target_occurrence) REFERENCES occurrences(generation,id),
          FOREIGN KEY(generation,variant_id) REFERENCES variants(generation,id));
        CREATE INDEX edges_source ON edges(generation,source_id);
        CREATE INDEX edges_target ON edges(generation,target_id);
        CREATE TABLE document_identities(generation INTEGER NOT NULL, document_id TEXT NOT NULL,document_uuid TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL,
          PRIMARY KEY(generation,document_id),UNIQUE(generation,document_uuid),UNIQUE(generation,path),
          FOREIGN KEY(generation,document_id) REFERENCES nodes(generation,id));
        CREATE TABLE section_identities(generation INTEGER NOT NULL,document_id TEXT NOT NULL,section_id TEXT NOT NULL,token TEXT NOT NULL,
          PRIMARY KEY(generation,section_id),UNIQUE(generation,document_id,token),
          FOREIGN KEY(generation,document_id) REFERENCES document_identities(generation,document_id),
          FOREIGN KEY(generation,section_id) REFERENCES nodes(generation,id));
        CREATE TABLE aliases(generation INTEGER NOT NULL,kind TEXT NOT NULL,old_id TEXT NOT NULL,new_id TEXT NOT NULL,json TEXT NOT NULL,
          PRIMARY KEY(generation,kind,old_id,new_id), FOREIGN KEY(generation,new_id) REFERENCES nodes(generation,id));
        CREATE TABLE legacy_references(generation INTEGER NOT NULL,schema_version INTEGER NOT NULL,legacy_id TEXT NOT NULL,
          node_id TEXT NOT NULL,occurrence_id TEXT,json TEXT NOT NULL,PRIMARY KEY(generation,schema_version,legacy_id,node_id,occurrence_id),
          FOREIGN KEY(generation,node_id) REFERENCES nodes(generation,id),
          FOREIGN KEY(generation,occurrence_id) REFERENCES occurrences(generation,id));
        CREATE TABLE search_text(rowid INTEGER PRIMARY KEY,generation INTEGER NOT NULL,node_id TEXT NOT NULL,name TEXT NOT NULL,
          signature TEXT NOT NULL,heading TEXT NOT NULL,body TEXT NOT NULL,path TEXT NOT NULL,
          UNIQUE(generation,node_id),FOREIGN KEY(generation,node_id) REFERENCES nodes(generation,id));
        """;
}
