using System.Globalization;
using System.Text.Json;

namespace Bag;

internal static class RoomListOutput
{
    public static JsonElement ToJsonElement(RoomListResult result)
    {
        return JsonOutput.ToElement(new
        {
            schema = "bag-rooms/1",
            rooms = result.Rooms.Select(room => new
            {
                provider = room.Provider,
                room_id = room.RoomId,
                message_count = room.MessageCount,
                sources = room.Sources.Select(Source)
            })
        });
    }

    private static object Source(RoomListSnapshot snapshot)
    {
        DateTimeOffset? timestamp = FindTime.ParseStored(snapshot.CreatedAt);
        return new
        {
            source_sha256 = snapshot.SourceSha256,
            name = snapshot.Name,
            created_at = snapshot.CreatedAt,
            timestamp_utc = timestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            timestamp_jst = timestamp is DateTimeOffset time ? FindTime.JapanIso(time) : null
        };
    }
}
