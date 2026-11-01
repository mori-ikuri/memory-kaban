using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Bag;

/// <summary>Validates the fixed MCP argument surface before any archive access.</summary>
internal static class McpArguments
{
    private static readonly HashSet<string> FindNames = new(StringComparer.Ordinal)
    {
        "from", "to", "room", "speaker", "text", "all_branches", "context", "limit"
    };

    public static bool TryReadFind(
        IDictionary<string, JsonElement>? arguments, out FindRequest? request, SpeakerNames? names = null)
    {
        request = null;
        arguments ??= new Dictionary<string, JsonElement>();
        if (arguments.Keys.Any(name => !FindNames.Contains(name)))
        {
            return false;
        }
        if (!TryReadTime(arguments, "from", out DateTimeOffset? from)
            || !TryReadTime(arguments, "to", out DateTimeOffset? to)
            || !TryReadText(arguments, "room", out string? room)
            || !TryReadText(arguments, "speaker", out string? speaker)
            || !TryReadText(arguments, "text", out string? text)
            || !TryReadBoolean(arguments, "all_branches", out bool allBranches)
            || !TryReadCount(arguments, "context", defaultValue: 0, minimum: 0, out int context)
            || !TryReadCount(arguments, "limit", defaultValue: 20, minimum: 1, out int limit))
        {
            return false;
        }
        if (from is not null && to is not null && from > to)
        {
            return false;
        }
        string? originalSpeaker = null;
        if (speaker is not null)
        {
            if (!(names ?? SpeakerNames.Default).TryResolve(speaker, out string resolved))
            {
                return false;
            }
            originalSpeaker = resolved;
        }
        request = new FindRequest(from, to, room, originalSpeaker, text, allBranches, context, limit);
        return true;
    }

    public static bool TryReadHeads(
        IDictionary<string, JsonElement>? arguments, out FindRequest? request, SpeakerNames? names = null)
    {
        request = null;
        return arguments?.ContainsKey("context") != true && TryReadFind(arguments, out request, names);
    }

    public static bool TryReadMessageIds(
        IDictionary<string, JsonElement>? arguments, out IReadOnlyList<FindMessageKey>? ids)
    {
        ids = null;
        if (arguments is not { Count: 1 } || !arguments.TryGetValue("ids", out JsonElement value)
            || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 50)
        {
            return false;
        }
        var keys = new List<FindMessageKey>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (property.Name is not "provider" and not "room_id" and not "message_id" and not "version_hash"
                    || property.Value.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(property.Value.GetString())
                    || !fields.TryAdd(property.Name, property.Value.GetString()!))
                {
                    return false;
                }
            }
            if (fields.Count != 4)
            {
                return false;
            }
            keys.Add(new FindMessageKey(fields["provider"], fields["room_id"], fields["message_id"], fields["version_hash"]));
        }
        ids = keys;
        return true;
    }

    private static bool TryReadTime(
        IDictionary<string, JsonElement> arguments, string name, out DateTimeOffset? result)
    {
        result = null;
        if (!TryReadText(arguments, name, out string? text))
        {
            return false;
        }
        if (text is null)
        {
            return true;
        }
        if (!FindTime.TryParseInput(text, out DateTimeOffset timestamp))
        {
            return false;
        }
        result = timestamp;
        return true;
    }

    private static bool TryReadText(
        IDictionary<string, JsonElement> arguments, string name, out string? result)
    {
        result = null;
        if (!arguments.TryGetValue(name, out JsonElement value))
        {
            return true;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        result = value.GetString();
        return !string.IsNullOrEmpty(result);
    }

    private static bool TryReadBoolean(
        IDictionary<string, JsonElement> arguments, string name, out bool result)
    {
        result = false;
        if (!arguments.TryGetValue(name, out JsonElement value))
        {
            return true;
        }
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }
        result = value.GetBoolean();
        return true;
    }

    private static bool TryReadCount(
        IDictionary<string, JsonElement> arguments, string name, int defaultValue, int minimum, out int result)
    {
        result = defaultValue;
        if (!arguments.TryGetValue(name, out JsonElement value))
        {
            return true;
        }
        if (value.ValueKind != JsonValueKind.Number
            || !BigInteger.TryParse(value.GetRawText(), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out BigInteger number)
            || number < minimum)
        {
            return false;
        }
        result = number > 50 ? 50 : (int)number;
        return true;
    }
}
