using Microsoft.Data.Sqlite;

namespace Bag;

internal sealed partial class FindService
{
    /// <summary>Reads exact saved versions in request order, excluding current seals.</summary>
    public FindResult GetMessages(string databasePath, IReadOnlyList<FindMessageKey> ids)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ids.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ids.Count, 50);
        using SealReadLease lease = new SealStore(databasePath).AcquireRead();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        DatabaseCompatibility.Verify(connection, transaction);
        var messages = new List<FindMessage>();
        var allSources = new FindRequest(null, null, null, null, null, true, 0, 50);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT v.version_id FROM message_versions AS v
            WHERE v.provider = $provider AND v.room_id = $room
              AND v.message_id = $message AND v.version_hash = $version
              AND EXISTS (
                  SELECT 1 FROM source_messages AS sm
                  WHERE sm.version_id = v.version_id AND sm.provider = v.provider
                    AND sm.room_id = v.room_id AND sm.message_id = v.message_id
              )
            """;
        command.Parameters.Add("$provider", SqliteType.Text);
        command.Parameters.Add("$room", SqliteType.Text);
        command.Parameters.Add("$message", SqliteType.Text);
        command.Parameters.Add("$version", SqliteType.Text);
        foreach (FindMessageKey key in ids.Distinct())
        {
            // Missing and sealed IDs have the same observable result: no entry.
            if (lease.Snapshot.IsSealed(key.Provider, key.RoomId, key.MessageId))
            {
                continue;
            }
            command.Parameters["$provider"].Value = key.Provider;
            command.Parameters["$room"].Value = key.RoomId;
            command.Parameters["$message"].Value = key.MessageId;
            command.Parameters["$version"].Value = key.VersionHash;
            if (command.ExecuteScalar() is not long versionId)
            {
                continue;
            }
            MessageBuilder message = ReadMessage(connection, transaction, versionId);
            message.IsMatch = true;
            foreach (FindSource source in ReadMatchingSources(connection, transaction, key, versionId, allSources))
            {
                message.AddSource(source);
            }
            messages.Add(message.Build());
        }
        return new FindResult(messages.Count, messages);
    }
}
