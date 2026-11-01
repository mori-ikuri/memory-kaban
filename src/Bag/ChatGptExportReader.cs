using System.Globalization;
using System.Text.Json;

namespace Bag;

/// <summary>Preserves the mapping graph and streams each ChatGPT node in two passes.</summary>
internal sealed class ChatGptExportReader : IExportReader
{
    internal const string RuleVersion = "chatgpt-current-node/1";
    public string Provider => "chatgpt";
    public bool SupportsNumberedConversations => true;

    public void Read(Func<Stream> openJson, IImportSink sink)
    {
        var rooms = new List<RoomDescriptor>();
        var roomIds = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, JsonElement> fields = null!;
        Dictionary<string, ChatGptNode> nodes = null!;
        Dictionary<string, string> toolNames = null!;
        Walk(openJson,
            () =>
            {
                fields = new(StringComparer.Ordinal);
                nodes = new(StringComparer.Ordinal);
                toolNames = new(StringComparer.Ordinal);
            },
            (key, value) =>
            {
                nodes.Add(key, ParseNode(key, value));
                ChatGptToolRedactor.CollectNames(value, toolNames);
            },
            (name, value) =>
            {
                if (name is "id" or "conversation_id" or "title" or "create_time" or "current_node")
                {
                    fields.Add(name, value.Clone());
                }
                ChatGptToolRedactor.CollectNames(value, toolNames);
            },
            () =>
            {
                string? id = StringField(fields, "id");
                string? alias = StringField(fields, "conversation_id");
                if (id is not null && alias is not null && id != alias)
                {
                    throw new InvalidDataException("Conflicting room identifiers.");
                }
                id ??= alias;
                if (string.IsNullOrWhiteSpace(id) || !roomIds.Add(id))
                {
                    throw new InvalidDataException("Invalid or duplicate room identifier.");
                }
                string? current = StringField(fields, "current_node");
                MainlineResult mainline = ChatGptGraph.Select(nodes, current);
                rooms.Add(new RoomDescriptor(new RoomInfo(id, StringField(fields, "title") ?? "",
                    fields.TryGetValue("create_time", out JsonElement time) ? Timestamp(time) : null,
                    current, mainline, RuleVersion), nodes, toolNames));
            });

        int index = 0;
        Walk(openJson,
            () => sink.BeginRoom(rooms[index].Info),
            (key, value) =>
            {
                RoomDescriptor room = rooms[index];
                string retained = ChatGptToolRedactor.Sanitize(value, room.ToolNames);
                sink.AddRoomBlock(new AuxiliaryBlock("node:" + key, retained));
                if (!room.Nodes[key].IsUtterance)
                {
                    return;
                }
                JsonElement message = value.GetProperty("message");
                sink.AddMessage(new ImportedMessage(key, ChatGptGraph.VisibleParent(room.Nodes, key),
                    JsonRules.Required(message.GetProperty("author"), "role"),
                    MessageTimestamp(message, "create_time"), MessageTimestamp(message, "update_time"),
                    Body(message.GetProperty("content")), [new AuxiliaryBlock("node", retained)]));
            },
            (name, value) => sink.AddRoomBlock(new AuxiliaryBlock("room:" + name,
                ChatGptToolRedactor.Sanitize(value, rooms[index].ToolNames))),
            () => { sink.EndRoom(); index++; });
    }

    private static void Walk(Func<Stream> openJson, Action beginRoom, Action<string, JsonElement> node,
        Action<string, JsonElement> roomValue, Action endRoom)
    {
        using Stream stream = openJson();
        var json = new JsonStream(stream);
        json.Expect(JsonTokenType.StartArray);
        while (!json.NextIs(JsonTokenType.EndArray))
        {
            json.Expect(JsonTokenType.StartObject);
            beginRoom();
            var properties = new HashSet<string>(StringComparer.Ordinal);
            bool hasMapping = false;
            while (!json.NextIs(JsonTokenType.EndObject))
            {
                json.Expect(JsonTokenType.PropertyName);
                string property = json.Text!;
                if (!properties.Add(property))
                {
                    throw new InvalidDataException("Duplicate room property.");
                }
                if (property == "mapping")
                {
                    hasMapping = true;
                    json.Expect(JsonTokenType.StartObject);
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    while (!json.NextIs(JsonTokenType.EndObject))
                    {
                        json.Expect(JsonTokenType.PropertyName);
                        string key = json.Text!;
                        if (string.IsNullOrWhiteSpace(key) || !keys.Add(key))
                        {
                            throw new InvalidDataException("Invalid or duplicate mapping key.");
                        }
                        using JsonDocument document = json.Value();
                        JsonRules.Validate(document.RootElement);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                        {
                            throw new InvalidDataException("Expected mapping node object.");
                        }
                        node(key, document.RootElement);
                    }
                    json.Expect(JsonTokenType.EndObject);
                }
                else
                {
                    using JsonDocument document = json.Value();
                    JsonRules.Validate(document.RootElement);
                    roomValue(property, document.RootElement);
                }
            }
            json.Expect(JsonTokenType.EndObject);
            if (!hasMapping)
            {
                throw new InvalidDataException("Missing conversation mapping.");
            }
            endRoom();
        }
        json.Expect(JsonTokenType.EndArray);
        if (json.Read())
        {
            throw new InvalidDataException("Unexpected trailing JSON.");
        }
    }

    private static ChatGptNode ParseNode(string key, JsonElement node)
    {
        if (!node.TryGetProperty("parent", out _)
            || !node.TryGetProperty("children", out JsonElement children) || children.ValueKind != JsonValueKind.Array
            || !node.TryGetProperty("message", out JsonElement message)
            || message.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
        {
            throw new InvalidDataException("Invalid conversation node.");
        }
        string? parent = JsonRules.String(node, "parent");
        _ = JsonRules.String(node, "id"); // The mapping key remains authoritative.
        var childIds = new List<string>();
        foreach (JsonElement child in children.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(child.GetString()))
            {
                throw new InvalidDataException("Invalid child identifier.");
            }
            childIds.Add(child.GetString()!);
        }
        return new ChatGptNode(key, parent, childIds, IsUtterance(message));
    }

    private static bool IsUtterance(JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        if (!message.TryGetProperty("author", out JsonElement author) || author.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Invalid message author.");
        }
        string role = JsonRules.Required(author, "role");
        _ = JsonRules.String(author, "name");
        _ = JsonRules.String(message, "id");
        _ = MessageTimestamp(message, "create_time");
        _ = MessageTimestamp(message, "update_time");
        string? recipient = JsonRules.String(message, "recipient");
        string? channel = JsonRules.String(message, "channel");
        bool hidden = false;
        if (message.TryGetProperty("metadata", out JsonElement metadata) && metadata.ValueKind != JsonValueKind.Null)
        {
            if (metadata.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Invalid message metadata.");
            }
            foreach (string name in new[] { "is_visually_hidden_from_conversation", "is_user_system_message" })
            {
                if (metadata.TryGetProperty(name, out JsonElement flag))
                {
                    if (flag.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    {
                        throw new InvalidDataException("Invalid message visibility.");
                    }
                    hidden |= flag.GetBoolean();
                }
            }
            string? metadataChannel = JsonRules.String(metadata, "channel");
            hidden |= metadataChannel is not null and not "final";
        }
        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        if (content.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Invalid message content.");
        }
        string? contentType = JsonRules.String(content, "content_type");
        if (contentType is "text" or "multimodal_text")
        {
            ValidateParts(content, contentType);
        }
        return role is "user" or "assistant" && !hidden && channel is null or "final"
            && recipient is null or "all" && contentType is "text" or "multimodal_text";
    }

    private static void ValidateParts(JsonElement content, string type)
    {
        if (!content.TryGetProperty("parts", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Invalid text parts.");
        }
        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.String && (type != "multimodal_text" || part.ValueKind != JsonValueKind.Object))
            {
                throw new InvalidDataException("Invalid text part.");
            }
        }
    }

    private static string Body(JsonElement content) => string.Join('\n', content.GetProperty("parts").EnumerateArray()
        .Where(part => part.ValueKind == JsonValueKind.String).Select(part => part.GetString()));

    private static string? MessageTimestamp(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value) ? Timestamp(value) : null;

    private static string? Timestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException("Invalid numeric timestamp.");
        }
        if (!value.TryGetDecimal(out decimal seconds))
        {
            return null;
        }
        try
        {
            decimal ticks = DateTimeOffset.UnixEpoch.Ticks + decimal.Truncate(seconds * TimeSpan.TicksPerSecond);
            if (ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks)
            {
                return null;
            }
            return new DateTimeOffset((long)ticks, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string? StringField(Dictionary<string, JsonElement> fields, string name)
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

    private sealed record RoomDescriptor(RoomInfo Info, Dictionary<string, ChatGptNode> Nodes,
        Dictionary<string, string> ToolNames);
}
