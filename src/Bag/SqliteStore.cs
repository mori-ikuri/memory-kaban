using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bag;

/// <summary>Persists immutable message versions and export-specific graph membership.</summary>
internal sealed class SqliteStore : IImportSink, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction? transaction;
    private readonly Dictionary<string, SqliteCommand> commands = new(StringComparer.Ordinal);
    private readonly string sourceHash;
    private readonly string provider;
    private readonly List<GraphNode> graph = [];
    private readonly HashSet<string> messageIds = new(StringComparer.Ordinal);
    private RoomInfo? room;
    private long rooms;
    private long messages;
    private long versions;
    private long unresolved;

    public SqliteStore(string path, string hash, long size, string fileName, string providerName)
    {
        sourceHash = hash;
        provider = providerName;
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            transaction = connection.BeginTransaction();
            if (DatabaseCompatibility.Verify(connection, transaction, allowEmpty: true))
            {
                Execute("PRAGMA application_id=1111574321"); // BAG1
                using SqliteCommand schema = connection.CreateCommand();
                schema.Transaction = transaction;
                schema.CommandText = Schema;
                schema.ExecuteNonQuery();
            }
            object? oldVersion = Scalar("SELECT importer_version FROM sources WHERE sha256=$p0", hash);
            if (oldVersion is not null && (string)oldVersion != BagVersion.Current)
            {
                throw BagErrors.InvalidData(FailureKind.DatabaseIncompatible);
            }
            Execute("INSERT OR IGNORE INTO sources VALUES($p0,$p1,$p2,$p3,$p4)",
                hash, size, fileName, DateTimeOffset.Now.ToString("O"), BagVersion.Current);
            Execute("INSERT OR IGNORE INTO source_names VALUES($p0,$p1)", hash, fileName);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void BeginRoom(RoomInfo info)
    {
        room = info;
        graph.Clear();
        messageIds.Clear();
        rooms++;
        Execute("INSERT OR IGNORE INTO rooms VALUES($p0,$p1)", provider, info.Id);
        Execute("""
            INSERT OR IGNORE INTO room_snapshots
            (source_sha,provider,room_id,name,created_at,selected_leaf,mainline_reason,rule_version)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5,'pending',$p6)
            """, sourceHash, provider, info.Id, info.Name, info.CreatedAt, info.SelectedLeaf, MainlineSelector.RuleVersion);
    }

    public void AddRoomBlock(AuxiliaryBlock block)
    {
        Execute("INSERT OR IGNORE INTO room_auxiliary VALUES($p0,$p1,$p2,$p3,$p4)",
            sourceHash, provider, room!.Id, block.Kind, block.Json);
    }

    public void AddMessage(ImportedMessage message)
    {
        if (!messageIds.Add(message.Id))
        {
            throw new InvalidDataException("Duplicate message identifier in one room.");
        }
        messages++;
        graph.Add(new GraphNode(message.Id, message.ParentId, message.CreatedAt));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message))));
        Execute("INSERT OR IGNORE INTO messages VALUES($p0,$p1,$p2)", provider, room!.Id, message.Id);
        int added = Execute("""
            INSERT OR IGNORE INTO message_versions
            (provider,room_id,message_id,version_hash,parent_id,speaker,created_at,updated_at,body)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)
            """, provider, room.Id, message.Id, hash, message.ParentId, message.Speaker,
            message.CreatedAt, message.UpdatedAt, message.Body);
        versions += added;
        long versionId = (long)Scalar("""
            SELECT version_id FROM message_versions
            WHERE provider=$p0 AND room_id=$p1 AND message_id=$p2 AND version_hash=$p3
            """, provider, room.Id, message.Id, hash)!;
        if (added != 0)
        {
            for (int index = 0; index < message.Blocks.Count; index++)
            {
                AuxiliaryBlock block = message.Blocks[index];
                Execute("INSERT INTO auxiliary_blocks VALUES($p0,$p1,$p2,$p3)", versionId, index, block.Kind, block.Json);
            }
        }
        Execute("""
            INSERT OR IGNORE INTO source_messages
            (source_sha,provider,room_id,message_id,version_id,is_mainline)
            VALUES($p0,$p1,$p2,$p3,$p4,0)
            """, sourceHash, provider, room.Id, message.Id, versionId);
    }

    public void EndRoom()
    {
        MainlineResult result = MainlineSelector.Select(graph, room!.SelectedLeaf);
        if (result.LeafId is null && graph.Count != 0)
        {
            unresolved++;
        }
        Execute("UPDATE source_messages SET is_mainline=0 WHERE source_sha=$p0 AND provider=$p1 AND room_id=$p2",
            sourceHash, provider, room.Id);
        foreach (string id in result.Members)
        {
            Execute("""
                UPDATE source_messages SET is_mainline=1
                WHERE source_sha=$p0 AND provider=$p1 AND room_id=$p2 AND message_id=$p3
                """, sourceHash, provider, room.Id, id);
        }
        Execute("""
            UPDATE room_snapshots SET mainline_reason=$p0,mainline_leaf=$p1,rule_version=$p2
            WHERE source_sha=$p3 AND provider=$p4 AND room_id=$p5
            """, result.Reason, result.LeafId, MainlineSelector.RuleVersion, sourceHash, provider, room.Id);
        room = null;
    }

    public ImportCounts Commit()
    {
        transaction!.Commit();
        return new ImportCounts(rooms, messages, versions, unresolved);
    }

    private int Execute(string sql, params object?[] values) => Command(sql, values).ExecuteNonQuery();
    private object? Scalar(string sql, params object?[] values) => Command(sql, values).ExecuteScalar();

    private SqliteCommand Command(string sql, object?[] values)
    {
        if (!commands.TryGetValue(sql, out SqliteCommand? command))
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            for (int index = 0; index < values.Length; index++)
            {
                command.Parameters.AddWithValue("$p" + index, values[index] ?? DBNull.Value);
            }
            commands.Add(sql, command);
        }
        else
        {
            for (int index = 0; index < values.Length; index++)
            {
                command.Parameters[index].Value = values[index] ?? DBNull.Value;
            }
        }
        return command;
    }

    public void Dispose()
    {
        foreach (SqliteCommand command in commands.Values)
        {
            command.Dispose();
        }
        transaction?.Dispose();
        connection.Dispose();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL);
        INSERT INTO schema_info SELECT 2 WHERE NOT EXISTS(SELECT 1 FROM schema_info);
        CREATE TABLE IF NOT EXISTS sources(
            sha256 TEXT PRIMARY KEY, size_bytes INTEGER NOT NULL, file_name TEXT NOT NULL,
            imported_at TEXT NOT NULL, importer_version TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS source_names(
            source_sha TEXT NOT NULL REFERENCES sources(sha256), file_name TEXT NOT NULL,
            PRIMARY KEY(source_sha,file_name));
        CREATE TABLE IF NOT EXISTS rooms(
            provider TEXT NOT NULL, room_id TEXT NOT NULL, PRIMARY KEY(provider,room_id));
        CREATE TABLE IF NOT EXISTS room_snapshots(
            source_sha TEXT NOT NULL REFERENCES sources(sha256), provider TEXT NOT NULL, room_id TEXT NOT NULL,
            name TEXT NOT NULL, created_at TEXT, selected_leaf TEXT, mainline_leaf TEXT,
            mainline_reason TEXT NOT NULL, rule_version TEXT NOT NULL,
            PRIMARY KEY(source_sha,provider,room_id), FOREIGN KEY(provider,room_id) REFERENCES rooms);
        CREATE TABLE IF NOT EXISTS room_auxiliary(
            source_sha TEXT NOT NULL, provider TEXT NOT NULL, room_id TEXT NOT NULL,
            kind TEXT NOT NULL, payload_json TEXT NOT NULL, PRIMARY KEY(source_sha,provider,room_id,kind),
            FOREIGN KEY(source_sha,provider,room_id) REFERENCES room_snapshots);
        CREATE TABLE IF NOT EXISTS messages(
            provider TEXT NOT NULL, room_id TEXT NOT NULL, message_id TEXT NOT NULL,
            PRIMARY KEY(provider,room_id,message_id), FOREIGN KEY(provider,room_id) REFERENCES rooms);
        CREATE TABLE IF NOT EXISTS message_versions(
            version_id INTEGER PRIMARY KEY, provider TEXT NOT NULL, room_id TEXT NOT NULL,
            message_id TEXT NOT NULL, version_hash TEXT NOT NULL, parent_id TEXT, speaker TEXT NOT NULL,
            created_at TEXT, updated_at TEXT, body TEXT NOT NULL,
            UNIQUE(provider,room_id,message_id,version_hash),
            FOREIGN KEY(provider,room_id,message_id) REFERENCES messages);
        CREATE TABLE IF NOT EXISTS auxiliary_blocks(
            version_id INTEGER NOT NULL REFERENCES message_versions, block_index INTEGER NOT NULL,
            kind TEXT NOT NULL, payload_json TEXT NOT NULL, PRIMARY KEY(version_id,block_index));
        CREATE TABLE IF NOT EXISTS source_messages(
            source_sha TEXT NOT NULL, provider TEXT NOT NULL, room_id TEXT NOT NULL, message_id TEXT NOT NULL,
            version_id INTEGER NOT NULL REFERENCES message_versions, is_mainline INTEGER NOT NULL CHECK(is_mainline IN (0,1)),
            PRIMARY KEY(source_sha,provider,room_id,message_id),
            FOREIGN KEY(source_sha,provider,room_id) REFERENCES room_snapshots,
            FOREIGN KEY(provider,room_id,message_id) REFERENCES messages);
        CREATE INDEX IF NOT EXISTS source_message_versions ON source_messages(version_id);
        """;
}
