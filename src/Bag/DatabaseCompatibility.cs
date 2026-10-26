using Microsoft.Data.Sqlite;

namespace Bag;

/// <summary>Checks schema identity without changing the database.</summary>
internal static class DatabaseCompatibility
{
    public const long ApplicationId = 1111574321; // BAG1

    private static readonly IReadOnlyDictionary<string, string> Columns = new Dictionary<string, string>
    {
        ["schema_info"] = "version:INTEGER",
        ["sources"] = "sha256:TEXT,size_bytes:INTEGER,file_name:TEXT,imported_at:TEXT,importer_version:TEXT",
        ["source_names"] = "source_sha:TEXT,file_name:TEXT",
        ["rooms"] = "provider:TEXT,room_id:TEXT",
        ["room_snapshots"] = "source_sha:TEXT,provider:TEXT,room_id:TEXT,name:TEXT,created_at:TEXT,selected_leaf:TEXT,mainline_leaf:TEXT,mainline_reason:TEXT,rule_version:TEXT",
        ["room_auxiliary"] = "source_sha:TEXT,provider:TEXT,room_id:TEXT,kind:TEXT,payload_json:TEXT",
        ["messages"] = "provider:TEXT,room_id:TEXT,message_id:TEXT",
        ["message_versions"] = "version_id:INTEGER,provider:TEXT,room_id:TEXT,message_id:TEXT,version_hash:TEXT,parent_id:TEXT,speaker:TEXT,created_at:TEXT,updated_at:TEXT,body:TEXT",
        ["auxiliary_blocks"] = "version_id:INTEGER,block_index:INTEGER,kind:TEXT,payload_json:TEXT",
        ["source_messages"] = "source_sha:TEXT,provider:TEXT,room_id:TEXT,message_id:TEXT,version_id:INTEGER,is_mainline:INTEGER"
    };

    /// <returns>True only for an empty database that the importer may initialize.</returns>
    public static bool Verify(SqliteConnection connection, SqliteTransaction? transaction = null, bool allowEmpty = false)
    {
        long applicationId = Scalar(connection, transaction, "PRAGMA application_id");
        long objectCount = Scalar(connection, transaction,
            "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'");
        if (allowEmpty && objectCount == 0 && applicationId == 0)
        {
            return true;
        }

        long tableCount = Scalar(connection, transaction,
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
        if (applicationId != ApplicationId || tableCount != Columns.Count)
        {
            throw BagErrors.InvalidData(FailureKind.DatabaseIncompatible);
        }

        foreach ((string table, string expectedColumns) in Columns)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT name, type FROM pragma_table_info($table) ORDER BY cid";
            command.Parameters.AddWithValue("$table", table);
            using SqliteDataReader reader = command.ExecuteReader();
            var actualColumns = new List<string>();
            while (reader.Read())
            {
                actualColumns.Add(reader.GetString(0) + ":" + reader.GetString(1).ToUpperInvariant());
            }
            if (string.Join(',', actualColumns) != expectedColumns)
            {
                throw BagErrors.InvalidData(FailureKind.DatabaseIncompatible);
            }
        }

        if (Scalar(connection, transaction, "SELECT count(*) FROM schema_info") != 1
            || Scalar(connection, transaction, "SELECT count(*) FROM schema_info WHERE version=2") != 1)
        {
            throw BagErrors.InvalidData(FailureKind.DatabaseIncompatible);
        }
        return false;
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
