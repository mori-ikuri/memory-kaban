using System.Text.Encodings.Web;
using System.Text.Json;

namespace Bag;

/// <summary>Serializes readable JSON for command output and MCP messages.</summary>
internal static class JsonOutput
{
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions();
    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(indented: true);

    public static string Serialize<T>(T value, bool indented = false)
    {
        return JsonSerializer.Serialize(value, indented ? IndentedOptions : CompactOptions);
    }

    public static JsonElement ToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, CompactOptions);
    }

    public static JsonSerializerOptions CreateOptions(
        JsonSerializerOptions? defaults = null, bool indented = false)
    {
        var options = defaults is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(defaults);

        // This output is UTF-8 JSON, never HTML or an embedded script.
        // Relaxed escaping keeps ordinary Unicode readable while JSON syntax
        // and control characters remain escaped. Do not embed it in HTML.
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.WriteIndented = indented;
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
