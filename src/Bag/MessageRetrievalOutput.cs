using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bag;

internal static class MessageRetrievalOutput
{
    public const int PreviewLength = 80;

    public static JsonElement Heads(FindResult result, SpeakerNames names)
    {
        return JsonOutput.ToElement(new
        {
            schema = "bag-heads/1",
            match_count = result.MatchCount,
            message_count = result.Messages.Count,
            messages = result.Messages.Select(message =>
            {
                (string preview, bool truncated) = Preview(message.Body);
                return new
                {
                    provider = message.Key.Provider,
                    room_id = message.Key.RoomId,
                    message_id = message.Key.MessageId,
                    version_hash = message.Key.VersionHash,
                    created_at = message.CreatedAt,
                    timestamp_utc = message.Timestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    timestamp_jst = message.Timestamp is DateTimeOffset timestamp ? FindTime.JapanIso(timestamp) : null,
                    speaker = names.GetDisplayName(message.Speaker, message.Key.Provider),
                    preview,
                    preview_truncated = truncated,
                    sources = message.Sources.Select(source => new
                    {
                        source_sha256 = source.SourceSha256,
                        room_name = source.RoomName,
                        is_mainline = source.IsMainline
                    })
                };
            })
        });
    }

    public static JsonElement Messages(FindResult result, SpeakerNames names)
    {
        // Share the existing full-message representation without changing find's envelope.
        JsonElement find = FindOutput.ToJsonElement(result, names);
        return JsonOutput.ToElement(new
        {
            schema = "bag-messages/1",
            message_count = result.Messages.Count,
            messages = find.GetProperty("messages")
        });
    }

    private static (string Text, bool Truncated) Preview(string body)
    {
        int scalars = 0;
        int length = 0;
        foreach (Rune rune in body.EnumerateRunes())
        {
            if (scalars == PreviewLength)
            {
                return (body[..length], true);
            }
            scalars++;
            length += rune.Utf16SequenceLength;
        }
        return (body, false);
    }
}
