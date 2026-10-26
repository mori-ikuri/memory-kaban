using System.Globalization;
using System.Text.Json;

namespace Bag;

internal static class FindOutput
{
    public static void Write(
        FindResult result, bool json, TextWriter output, SpeakerNames? names = null)
    {
        names ??= SpeakerNames.Default;
        if (json)
        {
            WriteJson(result, output, names);
            return;
        }
        foreach (FindMessage message in result.Messages)
        {
            string time = message.Timestamp is DateTimeOffset timestamp
                ? FindTime.FormatJapan(timestamp)
                : "日時不明";
            string speaker = names.GetDisplayName(message.Speaker);
            output.WriteLine($"[{time}] {speaker} ({(message.IsMatch ? "match" : "context")})");
            output.WriteLine($"Room ID: {message.Key.RoomId}; message: {message.Key.MessageId}; version: {message.Key.VersionHash}");
            foreach (FindSource source in message.Sources)
            {
                output.WriteLine($"Room: {source.RoomName}; source: {source.SourceSha256}; {(source.IsMainline ? "mainline" : "branch")}");
                foreach (FindMessageKey match in source.ContextFor)
                {
                    output.WriteLine($"Context for: {match.MessageId}; version: {match.VersionHash}");
                }
            }
            output.WriteLine("Body:");
            // Framing stays outside the body. Never trim, replace newlines, or shorten it.
            output.Write(message.Body);
            output.WriteLine();
            output.WriteLine();
        }
        if (result.Messages.Count == 0)
        {
            output.WriteLine("No matches.");
        }
    }

    private static void WriteJson(FindResult result, TextWriter output, SpeakerNames names)
    {
        output.WriteLine(JsonOutput.Serialize(ToJsonElement(result, names), indented: true));
    }

    public static JsonElement ToJsonElement(FindResult result, SpeakerNames? names = null)
    {
        names ??= SpeakerNames.Default;
        var document = new
        {
            schema = "bag-find/1",
            match_count = result.MatchCount,
            message_count = result.Messages.Count,
            context_truncated = result.ContextTruncated,
            messages = result.Messages.Select(message => new
            {
                provider = message.Key.Provider,
                room_id = message.Key.RoomId,
                message_id = message.Key.MessageId,
                version_hash = message.Key.VersionHash,
                created_at = message.CreatedAt,
                timestamp_utc = message.Timestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                timestamp_jst = message.Timestamp is DateTimeOffset timestamp ? FindTime.JapanIso(timestamp) : null,
                speaker = names.GetDisplayName(message.Speaker),
                body = message.Body,
                is_match = message.IsMatch,
                sources = message.Sources.Select(source => new
                {
                    source_sha256 = source.SourceSha256,
                    room_name = source.RoomName,
                    is_mainline = source.IsMainline,
                    context_for = source.ContextFor.Select(key => new
                    {
                        provider = key.Provider,
                        room_id = key.RoomId,
                        message_id = key.MessageId,
                        version_hash = key.VersionHash
                    })
                })
            })
        };
        return JsonOutput.ToElement(document);
    }
}
