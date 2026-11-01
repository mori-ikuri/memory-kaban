using System.Text;
using System.Text.Json;

namespace Bag;

/// <summary>Canonicalizes auxiliary JSON and redacts tool objects at every nesting level.</summary>
internal static class ChatGptToolRedactor
{
    public static void CollectNames(JsonElement value, Dictionary<string, string> names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (IsLegacyTool(value))
            {
                ToolRedactor.CollectNames(value, names);
                return;
            }
            if (TryChatGptTool(value, out _, out _))
            {
                return;
            }
            foreach (JsonProperty property in value.EnumerateObject())
            {
                CollectNames(property.Value, names);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                CollectNames(item, names);
            }
        }
    }

    public static string Sanitize(JsonElement value, IReadOnlyDictionary<string, string> names)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            Write(writer, value, names);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, IReadOnlyDictionary<string, string> names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (IsLegacyTool(value))
            {
                writer.WriteRawValue(ToolRedactor.Sanitize(value, names));
                return;
            }
            writer.WriteStartObject();
            if (TryChatGptTool(value, out string? type, out string? name))
            {
                writer.WriteString("type", type);
                writer.WriteString("name", name);
            }
            else
            {
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, names);
                }
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in value.EnumerateArray())
            {
                Write(writer, item, names);
            }
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private static bool IsLegacyTool(JsonElement value) =>
        value.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
        && type.GetString() is "tool_use" or "tool_result";

    private static bool TryChatGptTool(JsonElement value, out string? type, out string? name)
    {
        type = null;
        name = null;
        if (value.TryGetProperty("author", out JsonElement author) && author.ValueKind == JsonValueKind.Object
            && author.TryGetProperty("role", out JsonElement role) && role.ValueKind == JsonValueKind.String)
        {
            if (role.GetString() == "tool")
            {
                type = "tool_result";
                name = JsonRules.String(author, "name");
                return true;
            }
            string? recipient = JsonRules.String(value, "recipient");
            bool code = value.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("content_type", out JsonElement contentType)
                && contentType.ValueKind == JsonValueKind.String && contentType.GetString() == "code";
            if (role.GetString() == "assistant" && (recipient is not null and not "all" || code))
            {
                type = "tool_use";
                name = recipient is null or "all" ? null : recipient;
                return true;
            }
        }
        // Standalone structured tool content can also occur inside unknown metadata.
        if (value.TryGetProperty("content_type", out JsonElement kind) && kind.ValueKind == JsonValueKind.String
            && kind.GetString() is "code" or "execution_output")
        {
            type = kind.GetString() == "code" ? "tool_use" : "tool_result";
            return true;
        }
        return false;
    }
}
