using System.Text.Json;

namespace Bag;

internal static class JsonRules
{
    public static string? String(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Expected a JSON string.");
        }
        return value.GetString();
    }

    public static string Required(JsonElement element, string name)
    {
        string? value = String(element, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("Missing required identifier.");
        }
        return value;
    }

    public static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Duplicate JSON property.");
                }
                Validate(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                Validate(item);
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            // Force strict decoding even for fields that will be discarded.
            _ = value.GetString();
        }
    }
}
