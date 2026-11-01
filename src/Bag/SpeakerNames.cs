using System.Text;
using System.Text.Json;

namespace Bag;

/// <summary>Maps original sender values to configurable labels at the application boundary.</summary>
internal sealed class SpeakerNames
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<string, string> displayNames;
    private readonly Dictionary<string, string> originalNames;

    public static SpeakerNames Default { get; } = new(DefaultMappings());
    public IReadOnlyList<string> Labels { get; }

    private SpeakerNames(Dictionary<string, string> mappings)
    {
        displayNames = mappings;
        originalNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string original, string display) in mappings)
        {
            if (!IsValidName(original) || !IsValidName(display) || !originalNames.TryAdd(display, original))
            {
                throw BagErrors.InvalidData(FailureKind.ConfigurationInvalid);
            }
        }
        Labels = Array.AsReadOnly(mappings.Values.ToArray());
    }

    public string GetDisplayName(string original, string? provider = null)
    {
        // Reserved provider keys must not relabel a literal Claude sender with that name.
        if (provider != "chatgpt" && original.StartsWith("chatgpt:", StringComparison.Ordinal))
        {
            return original;
        }
        string key = MappingKey(provider, original);
        return displayNames.TryGetValue(key, out string? display) ? display : key;
    }

    // Provider scope belongs at the boundary: stored roles and version hashes stay original.
    public static string MappingKey(string? provider, string original) =>
        provider == "chatgpt" ? "chatgpt:" + original : original;

    public bool TryResolve(string display, out string original)
    {
        if (originalNames.TryGetValue(display, out string? value))
        {
            original = value;
            return true;
        }
        original = string.Empty;
        return false;
    }

    public static SpeakerNames Read(string databasePath)
    {
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(Path.Combine(directory, "bag.config.json"));
            }
            catch (FileNotFoundException)
            {
                return Default;
            }
            catch (DirectoryNotFoundException)
            {
                return Default;
            }

            // Accept UTF-8 with or without a BOM, including files made by Windows PowerShell.
            ReadOnlySpan<byte> content = bytes;
            if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                content = content[3..];
            }
            using JsonDocument document = JsonDocument.Parse(StrictUtf8.GetString(content));
            JsonElement root = document.RootElement;
            JsonRules.Validate(root);
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("speaker_names", out JsonElement names)
                || names.ValueKind != JsonValueKind.Object)
            {
                throw BagErrors.InvalidData(FailureKind.ConfigurationInvalid);
            }

            Dictionary<string, string> mappings = DefaultMappings();
            foreach (JsonProperty property in names.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw BagErrors.InvalidData(FailureKind.ConfigurationInvalid);
                }
                mappings[property.Name] = property.Value.GetString()!;
            }
            return new SpeakerNames(mappings);
        }
        catch (Exception exception) when (BagErrors.IsHandled(exception))
        {
            BagErrors.Tag(exception, FailureKind.ConfigurationInvalid);
            throw;
        }
    }

    private static bool IsValidName(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    }

    private static Dictionary<string, string> DefaultMappings()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["human"] = "user",
            ["assistant"] = "assistant",
            ["chatgpt:user"] = "chatgpt-user",
            ["chatgpt:assistant"] = "chatgpt-assistant"
        };
    }
}
