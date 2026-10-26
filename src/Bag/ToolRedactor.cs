using System.Text;
using System.Text.Json;

namespace Bag;

/// <summary>Reduces every nested tool block to its type and resolved name.</summary>
internal static class ToolRedactor
{
    public static void CollectNames(JsonElement element, Dictionary<string, string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (IsTool(element) && JsonRules.String(element, "type") == "tool_use")
            {
                string? id = JsonRules.String(element, "id");
                string? name = JsonRules.String(element, "name");
                if (id is not null && name is not null)
                {
                    if (names.TryGetValue(id, out string? existing) && existing != name)
                    {
                        throw new InvalidDataException("Conflicting tool names.");
                    }
                    names[id] = name;
                }
            }
            if (IsTool(element))
            {
                return; // Arguments and results are not interpreted as archive data.
            }
            foreach (JsonProperty property in element.EnumerateObject())
            {
                CollectNames(property.Value, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                CollectNames(child, names);
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

    private static bool IsTool(JsonElement value) =>
        value.TryGetProperty("type", out JsonElement type) &&
        type.ValueKind == JsonValueKind.String && type.GetString() is "tool_use" or "tool_result";

    private static void Write(Utf8JsonWriter writer, JsonElement value, IReadOnlyDictionary<string, string> names)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                if (IsTool(value))
                {
                    string? name = JsonRules.String(value, "name");
                    string? toolId = JsonRules.String(value, "tool_use_id");
                    if (name is null && toolId is not null)
                    {
                        names.TryGetValue(toolId, out name);
                    }
                    writer.WriteString("type", JsonRules.String(value, "type"));
                    writer.WriteString("name", name);
                }
                else
                {
                    // Stable ordering makes semantic object-property reordering idempotent.
                    foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(writer, property.Value, names);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    Write(writer, item, names);
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
