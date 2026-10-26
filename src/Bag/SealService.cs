using Microsoft.Data.Sqlite;

namespace Bag;

internal sealed class SealService
{
    private const string Provider = "claude";

    public bool Set(string databasePath, string? roomId, string? messageId, bool seal)
    {
        var store = new SealStore(databasePath);
        SealTarget target;
        if (!seal && roomId is not null)
        {
            // A judgment can be removed even when a rebuilt DB omits that target.
            target = new SealTarget(Provider, roomId, messageId);
        }
        else if (!seal && messageId is not null)
        {
            List<SealTarget> existing = store.Read().Targets
                .Where(item => item.Provider == Provider && item.MessageId == messageId)
                .Take(2)
                .ToList();
            if (existing.Count > 1)
            {
                throw BagErrors.InvalidData(FailureKind.TargetAmbiguous);
            }
            target = existing.Count == 1 ? existing[0] : Resolve(databasePath, roomId, messageId);
        }
        else
        {
            target = Resolve(databasePath, roomId, messageId);
        }
        return store.Set(target, seal);
    }

    private static SealTarget Resolve(string databasePath, string? roomId, string? messageId)
    {
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
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        if (messageId is null)
        {
            command.CommandText = "SELECT room_id FROM rooms WHERE provider=$provider AND room_id=$room LIMIT 2";
        }
        else
        {
            command.CommandText = """
                SELECT room_id FROM messages
                WHERE provider=$provider AND message_id=$message
                  AND ($room IS NULL OR room_id=$room)
                LIMIT 2
                """;
            command.Parameters.AddWithValue("$message", messageId);
        }
        command.Parameters.AddWithValue("$provider", Provider);
        command.Parameters.AddWithValue("$room", (object?)roomId ?? DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw BagErrors.InvalidData(FailureKind.TargetMissing);
        }
        string resolvedRoom = reader.GetString(0);
        if (reader.Read())
        {
            throw BagErrors.InvalidData(FailureKind.TargetAmbiguous);
        }
        return new SealTarget(Provider, resolvedRoom, messageId);
    }
}
