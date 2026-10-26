using Microsoft.Data.Sqlite;

namespace Bag;

internal sealed record RoomListSnapshot(string SourceSha256, string Name, string? CreatedAt);
internal sealed record RoomListItem(
    string Provider, string RoomId, long MessageCount, IReadOnlyList<RoomListSnapshot> Sources);
internal sealed record RoomListResult(IReadOnlyList<RoomListItem> Rooms);

/// <summary>Lists visible logical rooms and their original source metadata.</summary>
internal sealed class RoomListService
{
    public RoomListResult List(string databasePath)
    {
        using SealReadLease lease = new SealStore(databasePath).AcquireRead();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        connection.CreateFunction<string, string, string, bool>("bag_visible",
            (provider, room, message) => !lease.Snapshot.IsSealed(provider, room, message), isDeterministic: true);
        connection.CreateCollation("bag_ordinal", (left, right) => string.Compare(left, right, StringComparison.Ordinal));
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        DatabaseCompatibility.Verify(connection, transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT provider, room_id, count(*) FROM messages
            WHERE bag_visible(provider, room_id, message_id)
            GROUP BY provider, room_id
            ORDER BY provider COLLATE bag_ordinal, room_id COLLATE bag_ordinal
            """;
        var identities = new List<(string Provider, string Room, long Count)>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                identities.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }
        var rooms = new List<RoomListItem>();
        foreach ((string provider, string room, long count) in identities)
        {
            rooms.Add(new RoomListItem(provider, room, count, ReadSources(connection, transaction, provider, room)));
        }
        return new RoomListResult(rooms);
    }

    private static IReadOnlyList<RoomListSnapshot> ReadSources(
        SqliteConnection connection, SqliteTransaction transaction, string provider, string room)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rs.source_sha, rs.name, rs.created_at FROM room_snapshots AS rs
            WHERE rs.provider=$provider AND rs.room_id=$room
              AND EXISTS (
                  SELECT 1 FROM source_messages AS sm
                  WHERE sm.source_sha=rs.source_sha AND sm.provider=rs.provider AND sm.room_id=rs.room_id
                    AND bag_visible(sm.provider, sm.room_id, sm.message_id)
              )
            ORDER BY rs.source_sha COLLATE BINARY
            """;
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$room", room);
        using SqliteDataReader reader = command.ExecuteReader();
        var snapshots = new List<RoomListSnapshot>();
        while (reader.Read())
        {
            snapshots.Add(new RoomListSnapshot(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return snapshots;
    }
}
