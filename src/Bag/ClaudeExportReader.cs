using System.Text.Json;

namespace Bag;

/// <summary>Uses two streaming passes to handle headers and tool names in any order.</summary>
internal sealed class ClaudeExportReader : IExportReader
{
    public string Provider => "claude";

    public void Read(Func<Stream> openJson, IImportSink sink)
    {
        var rooms = new List<RoomDescriptor>();
        var roomIds = new HashSet<string>(StringComparer.Ordinal);
        Walk(openJson, null, (fields, names) =>
        {
            string id = Field(fields, "uuid") ?? throw new InvalidDataException("Missing room identifier.");
            if (string.IsNullOrWhiteSpace(id) || !roomIds.Add(id))
            {
                throw new InvalidDataException("Invalid or duplicate room identifier.");
            }
            string? selected = Field(fields, "current_leaf_message_uuid");
            rooms.Add(new RoomDescriptor(
                new RoomInfo(id, Field(fields, "name") ?? "", Field(fields, "created_at"), selected), names));
        });

        int index = 0;
        Walk(openJson, message => sink.AddMessage(ParseMessage(message, rooms[index].ToolNames)),
            (_, _) => { sink.EndRoom(); index++; },
            () => sink.BeginRoom(rooms[index].Info),
            (name, value) => sink.AddRoomBlock(new AuxiliaryBlock("room:" + name,
                ToolRedactor.Sanitize(value, rooms[index].ToolNames))));
    }

    private static void Walk(Func<Stream> openJson, Action<JsonElement>? message,
        Action<Dictionary<string, JsonElement>, Dictionary<string, string>> endRoom,
        Action? beginRoom = null, Action<string, JsonElement>? roomBlock = null)
    {
        using Stream stream = openJson();
        var json = new JsonStream(stream);
        json.Expect(JsonTokenType.StartArray);
        while (!json.NextIs(JsonTokenType.EndArray))
        {
            json.Expect(JsonTokenType.StartObject);
            beginRoom?.Invoke();
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
            bool hasMessages = false;
            while (!json.NextIs(JsonTokenType.EndObject))
            {
                json.Expect(JsonTokenType.PropertyName);
                string property = json.Text!;
                if (!names.Add(property))
                {
                    throw new InvalidDataException("Duplicate room property.");
                }
                if (property == "chat_messages")
                {
                    hasMessages = true;
                    json.Expect(JsonTokenType.StartArray);
                    while (!json.NextIs(JsonTokenType.EndArray))
                    {
                        using JsonDocument document = json.Value();
                        JsonRules.Validate(document.RootElement);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                        {
                            throw new InvalidDataException("Expected a message object.");
                        }
                        if (message is null)
                        {
                            ToolRedactor.CollectNames(document.RootElement, toolNames);
                        }
                        else
                        {
                            message(document.RootElement);
                        }
                    }
                    json.Expect(JsonTokenType.EndArray);
                }
                else
                {
                    using JsonDocument document = json.Value();
                    JsonRules.Validate(document.RootElement);
                    // Room headers retained here are small scalars; unrelated large values are discarded.
                    if (property is "uuid" or "name" or "created_at" or "current_leaf_message_uuid")
                    {
                        fields.Add(property, document.RootElement.Clone());
                    }
                    else
                    {
                        roomBlock?.Invoke(property, document.RootElement);
                        if (message is null)
                        {
                            ToolRedactor.CollectNames(document.RootElement, toolNames);
                        }
                    }
                }
            }
            json.Expect(JsonTokenType.EndObject);
            if (!hasMessages)
            {
                throw new InvalidDataException("Missing message array.");
            }
            endRoom(fields, toolNames);
        }
        json.Expect(JsonTokenType.EndArray);
        if (json.Read())
        {
            throw new InvalidDataException("Unexpected trailing JSON.");
        }
    }

    private static string? Field(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Invalid room metadata.");
        }
        return value.GetString();
    }

    private static ImportedMessage ParseMessage(JsonElement message, IReadOnlyDictionary<string, string> toolNames)
    {
        string id = JsonRules.Required(message, "uuid");
        string speaker = JsonRules.Required(message, "sender");
        string? parent = JsonRules.String(message, "parent_message_uuid");
        string? alias = JsonRules.String(message, "parent_uuid");
        if (parent is not null && alias is not null && parent != alias)
        {
            throw new InvalidDataException("Conflicting parent identifiers.");
        }
        var texts = new List<string>();
        var blocks = new List<AuxiliaryBlock>();
        if (message.TryGetProperty("content", out JsonElement content) && content.ValueKind != JsonValueKind.Null)
        {
            if (content.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Invalid content array.");
            }
            foreach (JsonElement block in content.EnumerateArray())
            {
                string kind = block.ValueKind == JsonValueKind.Object
                    ? JsonRules.String(block, "type") ?? "unknown" : "unknown";
                if (kind == "text")
                {
                    texts.Add(JsonRules.String(block, "text") ?? throw new InvalidDataException("Missing text block value."));
                }
                blocks.Add(new AuxiliaryBlock("content:" + kind, ToolRedactor.Sanitize(block, toolNames)));
            }
        }
        foreach (JsonProperty property in message.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.Name is "uuid" or "sender" or "parent_message_uuid" or "parent_uuid"
                or "created_at" or "updated_at" or "text" or "content")
            {
                continue;
            }
            blocks.Add(new AuxiliaryBlock("message:" + property.Name, ToolRedactor.Sanitize(property.Value, toolNames)));
        }
        return new ImportedMessage(id, parent ?? alias, speaker, JsonRules.String(message, "created_at"),
            JsonRules.String(message, "updated_at"), string.Join('\n', texts), blocks);
    }

    private sealed record RoomDescriptor(RoomInfo Info, Dictionary<string, string> ToolNames);
}
