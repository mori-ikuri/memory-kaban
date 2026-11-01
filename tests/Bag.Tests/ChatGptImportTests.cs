using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bag.Tests;

public sealed class ChatGptImportTests
{
    private readonly SyntheticArchive archive = new();

    [Fact]
    public void PreservesLiteralTextOriginalRolesIdentitiesAndFractionalTimes()
    {
        string text = "  日本語🙂e\u0301\r\n次の行\n'\"\t\0末尾  ";
        Dictionary<string, object?> user = Message("export-message-id", "user", text, time: 0.1234567m);
        user["content"] = new { content_type = "text", parts = new[] { text, "", "最後" } };
        user["metadata"] = new { attachments = new[] { new { name = "架空.txt" } }, future = "SYNTHETIC_METADATA" };
        Dictionary<string, object?> response = Message("reply", "assistant", "", time: -0.5m);
        response["content"] = new { content_type = "text", parts = Array.Empty<string>() };
        Dictionary<string, object?> room = Room("reply", Node("stub", null), Node("node-id", user, "stub"),
            Node("reply", response, "node-id"));
        string zip = Zip(room);
        byte[] before = File.ReadAllBytes(zip);

        ImportCounts counts = Import(zip);

        Assert.Equal(1, counts.Rooms);
        Assert.Equal(2, counts.Messages);
        Assert.Equal(2, counts.NewVersions);
        Assert.Equal(0, counts.UnresolvedRooms);
        Assert.Equal(text + "\n\n最後", Scalar<string>("SELECT body FROM message_versions WHERE message_id='node-id'"));
        Assert.Equal("", Scalar<string>("SELECT body FROM message_versions WHERE message_id='reply'"));
        Assert.Equal("user", Scalar<string>("SELECT speaker FROM message_versions WHERE message_id='node-id'"));
        Assert.Equal("assistant", Scalar<string>("SELECT speaker FROM message_versions WHERE message_id='reply'"));
        Assert.Equal("node-id", Scalar<string>("SELECT parent_id FROM message_versions WHERE message_id='reply'"));
        Assert.Equal("chatgpt", Scalar<string>("SELECT provider FROM rooms"));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(1234567),
            FindTime.ParseStored(Scalar<string>("SELECT created_at FROM message_versions WHERE message_id='node-id'")));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(-500),
            FindTime.ParseStored(Scalar<string>("SELECT created_at FROM message_versions WHERE message_id='reply'")));
        string shelf = AuxiliaryText();
        Assert.Contains("export-message-id", shelf);
        Assert.Contains("SYNTHETIC_METADATA", shelf);
        Assert.Contains("0.1234567", shelf);
        Assert.Equal(before, File.ReadAllBytes(zip));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(before)), Scalar<string>("SELECT sha256 FROM sources"));
        Assert.Equal(before.Length, Scalar<long>("SELECT size_bytes FROM sources"));
    }

    [Theory]
    [InlineData("main", "main,question")]
    [InlineData("hidden", "main,question")]
    [InlineData("question", "question")]
    [InlineData("stub", "")]
    public void CurrentNodeSelectsItsAncestryEvenWhenItIsInternalOrHasNoUtterance(string current, string members)
    {
        var hidden = Message("hidden", "system", "SYNTHETIC_SYSTEM");
        Dictionary<string, object?> room = Room(current,
            Node("stub", null), Node("question", Message("question", "user", "question"), "stub"),
            Node("main", Message("main", "assistant", "selected", 10m), "question"),
            Node("side", Message("side", "assistant", "newer side", 999m), "question"),
            Node("hidden", hidden, "main"),
            Node("later", Message("later", "user", "later", 1000m), "hidden"));

        Import(zip: Zip(room));

        Assert.Equal(4, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Equal(members.Length == 0 ? [] : members.Split(','),
            Column("SELECT message_id FROM source_messages WHERE is_mainline=1 ORDER BY message_id"));
        Assert.Equal("main", Scalar<string>("SELECT parent_id FROM message_versions WHERE message_id='later'"));
        Assert.Contains("hidden", AuxiliaryText());
        Assert.Contains("SYNTHETIC_SYSTEM", AuxiliaryText());
    }

    [Theory]
    [InlineData("missing-current")]
    [InlineData("null-current")]
    [InlineData("unknown-current")]
    [InlineData("missing-parent")]
    [InlineData("cycle")]
    public void UnresolvedGraphsPreserveAllMessagesWithoutGuessingAMainline(string condition)
    {
        Dictionary<string, object?> first = Node("first", Message("first", "user", "first"));
        Dictionary<string, object?> second = Node("second", Message("second", "assistant", "second", 999m), "first");
        if (condition == "missing-parent") second["parent"] = "absent";
        if (condition == "cycle") first["parent"] = "second";
        Dictionary<string, object?> room = Room("second", first, second);
        if (condition == "missing-current") room.Remove("current_node");
        if (condition == "null-current") room["current_node"] = null;
        if (condition == "unknown-current") room["current_node"] = "not-in-mapping";

        ImportCounts counts = Import(Zip(room));

        Assert.Equal(2, counts.Messages);
        Assert.Equal(1, counts.UnresolvedRooms);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM source_messages WHERE is_mainline=1"));
        Assert.Equal(2, Find(allBranches: true).Messages.Count);
        Assert.Empty(Find(allBranches: false).Messages);
    }

    [Fact]
    public void NonUtterancesStayOnTheAuxiliaryShelfAndContextBridgesTheirNodes()
    {
        var hidden = Message("hidden", "assistant", "SYNTHETIC_HIDDEN");
        hidden["metadata"] = new { is_visually_hidden_from_conversation = true };
        var analysis = Message("analysis", "assistant", "SYNTHETIC_ANALYSIS");
        analysis["channel"] = "analysis";
        var thoughts = Message("thoughts", "assistant", "unused");
        thoughts["content"] = new { content_type = "thoughts", thoughts = new[] { new { content = "SYNTHETIC_THOUGHT" } } };
        var unknown = Message("future", "assistant", "unused");
        unknown["content"] = new { content_type = "future_kind", parts = new[] { "SYNTHETIC_FUTURE" } };
        var visible = Message("visible", "assistant", "unused");
        visible["content"] = new
        {
            content_type = "multimodal_text",
            parts = new object[] { "text one", new { content_type = "image_asset_pointer", asset_pointer = "SYNTHETIC_IMAGE" }, "", "text two" }
        };
        Dictionary<string, object?> room = Room("visible", Node("stub", null),
            Node("system", Message("system", "system", "SYNTHETIC_SYSTEM"), "stub"),
            Node("question", Message("question", "user", "question"), "system"),
            Node("hidden", hidden, "question"), Node("analysis", analysis, "hidden"),
            Node("thoughts", thoughts, "analysis"), Node("future", unknown, "thoughts"),
            Node("visible", visible, "future"));
        room["future_room_field"] = new { text = "SYNTHETIC_ROOM_METADATA" };

        ImportCounts counts = Import(Zip(room));

        Assert.Equal(2, counts.Messages);
        Assert.Equal(new[] { "question", "visible" }, Column("SELECT message_id FROM messages ORDER BY message_id"));
        Assert.Equal("text one\n\ntext two", Scalar<string>("SELECT body FROM message_versions WHERE message_id='visible'"));
        Assert.Equal("question", Scalar<string>("SELECT parent_id FROM message_versions WHERE message_id='visible'"));
        Assert.Empty(Find(text: "SYNTHETIC_", allBranches: true).Messages);
        string shelf = AuxiliaryText();
        foreach (string marker in new[] { "SYNTHETIC_HIDDEN", "SYNTHETIC_ANALYSIS", "SYNTHETIC_THOUGHT", "SYNTHETIC_FUTURE",
                     "SYNTHETIC_SYSTEM", "SYNTHETIC_IMAGE", "SYNTHETIC_ROOM_METADATA" })
        {
            Assert.Contains(marker, shelf);
        }
        FindResult result = Find(text: "text one", context: 1);
        Assert.Equal(new[] { "question", "visible" }, result.Messages.Select(message => message.Key.MessageId).Order());
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-4000-8000-000000000000")]
    public void ContextTreatsClaudeRootSentinelsAsOrdinaryChatGptNodeIds(string sentinel)
    {
        Import(Zip(Room("answer", Node(sentinel, Message("question", "user", "question")),
            Node("answer", Message("answer", "assistant", "answer"), sentinel))));

        FindResult result = Find(text: "answer", context: 1);

        Assert.Equal(new[] { sentinel, "answer" }, result.Messages.Select(message => message.Key.MessageId).Order());
        Assert.False(result.Messages.Single(message => message.Key.MessageId == sentinel).IsMatch);
    }

    [Fact]
    public void BranchContextContinuesThroughNodeIdsThatAreClaudeRootSentinels()
    {
        const string sentinel = "00000000-0000-0000-0000-000000000000";
        Import(Zip(Room("main", Node("question", Message("question", "user", "question")),
            Node("main", Message("main", "assistant", "selected"), "question"),
            Node("branch", Message("branch", "assistant", "branch hit"), "question"),
            Node(sentinel, Message("middle", "user", "branch middle"), "branch"),
            Node("leaf", Message("leaf", "assistant", "branch leaf"), sentinel))));

        FindResult result = Find(text: "branch hit", allBranches: true, context: 2);

        Assert.Equal(new[] { sentinel, "branch", "leaf", "question" },
            result.Messages.Select(message => message.Key.MessageId).Order());
        Assert.DoesNotContain(result.Messages, message => message.Key.MessageId == "main");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UserSystemFlagSeparatesInjectedInstructionsFromOrdinaryUserUtterances(bool systemFlag)
    {
        Dictionary<string, object?> injected = Message("injection", "user", "SYNTHETIC_CUSTOM_INSTRUCTIONS");
        injected["metadata"] = new { is_user_system_message = systemFlag };
        Import(Zip(Room("answer", Node("question", Message("question", "user", "question")),
            Node("injection", injected, "question"), Node("answer", Message("answer", "assistant", "answer"), "injection"))));

        Assert.Equal(systemFlag ? 2 : 3, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Contains("SYNTHETIC_CUSTOM_INSTRUCTIONS", AuxiliaryText());
        FindResult context = Find(text: "answer", context: 1);
        Assert.Equal(systemFlag ? "question" : "injection",
            Assert.Single(context.Messages, message => !message.IsMatch).Key.MessageId);
        Assert.Equal(systemFlag ? 0 : 1, Find(text: "SYNTHETIC_CUSTOM_INSTRUCTIONS").Messages.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"true\"")]
    public void InvalidUserSystemFlagsFailAtomically(string flag)
    {
        Import(Zip(Room("known", Node("known", Message("known", "user", "existing")))));
        Dictionary<string, object?> message = Message("new", "user", "SYNTHETIC_INVALID_FLAG");
        message["metadata"] = new { is_user_system_message = JsonSerializer.Deserialize<JsonElement>(flag) };
        string zip = Zip(Room("new", Node("new", message)));
        var error = new StringWriter();

        Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], TextWriter.Null, error));
        Assert.Equal("Unsupported export format." + Environment.NewLine, error.ToString());
        Assert.Equal(new[] { "known" }, Column("SELECT message_id FROM messages"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
    }

    [Fact]
    public void ToolCallsAndResultsKeepNamesButNeverPersistArgumentsOrResults()
    {
        var call = Message("call", "assistant", "SECRET_TOOL_ARGUMENT");
        call["recipient"] = "python";
        call["content"] = new { content_type = "code", text = "SECRET_TOOL_ARGUMENT", language = "python" };
        call["metadata"] = new { payload = "SECRET_CALL_METADATA" };
        var result = Message("result", "tool", "SECRET_TOOL_RESULT");
        result["author"] = new { role = "tool", name = "python" };
        result["content"] = new { content_type = "execution_output", text = "SECRET_TOOL_RESULT" };
        result["metadata"] = new { payload = "SECRET_RESULT_METADATA" };
        var answer = Message("answer", "assistant", "visible answer");
        answer["metadata"] = new
        {
            future = new { type = "tool_use", id = "nested", name = "synthetic_tool", input = new { value = "SECRET_NESTED" } }
        };
        Import(Zip(Room("answer", Node("question", Message("question", "user", "question")),
            Node("call", call, "question"), Node("result", result, "call"), Node("answer", answer, "result"))));

        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Contains("python", AuxiliaryText());
        Assert.Contains("synthetic_tool", AuxiliaryText());
        Assert.DoesNotContain("SECRET_", AuxiliaryText());
        Assert.DoesNotContain("SECRET_", Encoding.UTF8.GetString(File.ReadAllBytes(archive.DatabasePath)));
        Assert.Equal("question", Scalar<string>("SELECT parent_id FROM message_versions WHERE message_id='answer'"));
    }

    [Fact]
    public void EquivalentReimportsAndRebuildsPreserveVersionHashesAndOriginalZip()
    {
        Dictionary<string, object?> message = Message("message", "assistant", "日本語\r\n body");
        message["metadata"] = new Dictionary<string, object?> { ["z"] = 1, ["a"] = "preserved" };
        Dictionary<string, object?> node = Node("node", message);
        Dictionary<string, object?> room = Room("node", node);
        string zip = Zip(room);
        byte[] before = File.ReadAllBytes(zip);
        Assert.Equal(1, Import(zip).NewVersions);
        Assert.Equal(0, Import(zip).NewVersions);

        message["metadata"] = new Dictionary<string, object?> { ["a"] = "preserved", ["z"] = 1 };
        node["message"] = message.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        room["title"] = "Renamed synthetic room";
        Dictionary<string, object?> reordered = room.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        string secondZip = Zip(reordered);
        Assert.Equal(0, Import(secondZip).NewVersions);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM message_versions"));
        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM sources"));
        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM source_messages"));
        string rebuilt = Path.Combine(archive.DirectoryPath, "rebuilt.db");
        new ImportService(new ChatGptExportReader()).Import(secondZip, rebuilt);
        new ImportService(new ChatGptExportReader()).Import(zip, rebuilt);
        Assert.Equal(Column("SELECT version_hash FROM message_versions ORDER BY version_hash"),
            Column("SELECT version_hash FROM message_versions ORDER BY version_hash", rebuilt));
        Assert.Equal(Column("SELECT mainline_reason FROM room_snapshots ORDER BY source_sha"),
            Column("SELECT mainline_reason FROM room_snapshots ORDER BY source_sha", rebuilt));
        Assert.Equal(before, File.ReadAllBytes(zip));
        Assert.Empty(Directory.GetFiles(archive.DirectoryPath, "*.json"));
    }

    [Fact]
    public void ChangedTextCreatesRetainedVersionsAndOldSourceCanBeReimported()
    {
        Dictionary<string, object?> message = Message("message", "assistant", "old body");
        Dictionary<string, object?> room = Room("node", Node("node", message));
        string old = Zip(room);
        Import(old);
        message["content"] = new { content_type = "text", parts = new[] { "new body" } };
        Assert.Equal(1, Import(Zip(room)).NewVersions);
        Assert.Equal(0, Import(old).NewVersions);
        Assert.Equal(new[] { "new body", "old body" }, Column("SELECT body FROM message_versions ORDER BY body"));
        Assert.Equal(2, Find().Messages.Count);
    }

    [Fact]
    public void MappingKeysRemainAuthoritativeWhenEmbeddedIdentifiersDifferOrRepeat()
    {
        Dictionary<string, object?> first = Node("first-key", Message("repeated-message-id", "user", "first"));
        Dictionary<string, object?> second = Node("second-key", Message("repeated-message-id", "assistant", "second"), "first-key");
        Dictionary<string, object?> room = Room("second-key", first, second);
        first["id"] = "different-embedded-node-id";

        Import(Zip(room));

        Assert.Equal(new[] { "first-key", "second-key" }, Column("SELECT message_id FROM messages ORDER BY message_id"));
        Assert.Equal("first-key", Scalar<string>("SELECT parent_id FROM message_versions WHERE message_id='second-key'"));
        Assert.Contains("repeated-message-id", AuxiliaryText());
        Assert.Contains("different-embedded-node-id", AuxiliaryText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InconsistentChildLinksArePreservedButDoNotProduceAMainline(bool missingChild)
    {
        Dictionary<string, object?> first = Node("first", Message("first", "user", "first"));
        Dictionary<string, object?> room = Room("second", first,
            Node("second", Message("second", "assistant", "second"), "first"));
        first["children"] = missingChild ? new[] { "second", "absent-child" } : Array.Empty<string>();

        ImportCounts counts = Import(Zip(room));

        Assert.Equal(2, counts.Messages);
        Assert.Equal(1, counts.UnresolvedRooms);
        Assert.Empty(Find().Messages);
        Assert.Equal(2, Find(allBranches: true).Messages.Count);
        if (missingChild) Assert.Contains("absent-child", AuxiliaryText());
    }

    [Theory]
    [InlineData("[] trailing")]
    [InlineData("{\"conversations\":[]}")]
    [InlineData("[{\"id\":\"room\",\"mapping\":[]}]")]
    [InlineData("[{\"id\":\"room\"}]")]
    [InlineData("[{\"id\":\"room\",\"id\":\"duplicate\",\"mapping\":{}}]")]
    [InlineData("[{\"id\":\"room\",\"mapping\":{\"node\":null}}]")]
    [InlineData("[{\"id\":\"room\",\"mapping\":{\"node\":{\"id\":\"node\",\"parent\":null,\"children\":[],\"message\":null},\"node\":{\"id\":\"node\",\"parent\":null,\"children\":[],\"message\":null}}}]")]
    [InlineData("[{\"id\":\"room\",\"mapping\":{\"node\":{\"id\":\"node\",\"parent\":42,\"children\":[],\"message\":null}}}]")]
    [InlineData("[{\"id\":\"room\",\"mapping\":{\"node\":{\"id\":\"node\",\"parent\":null,\"children\":[],\"message\":{\"id\":\"m\",\"author\":{\"role\":\"user\"},\"content\":{\"content_type\":\"text\",\"parts\":42}}}}}]")]
    public void MalformedExportsFailAtomicallyWithFixedPrivateDiagnostics(string json)
    {
        Import(Zip(Room("known", Node("known", Message("known", "user", "existing")))));
        string zip = RawZip(Encoding.UTF8.GetBytes(json));
        byte[] before = File.ReadAllBytes(zip);
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = ImportCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], output, error);

        Assert.Equal(2, exit);
        Assert.Equal("", output.ToString());
        Assert.Equal("Unsupported export format." + Environment.NewLine, error.ToString());
        Assert.Equal(new[] { "known" }, Column("SELECT message_id FROM messages"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
        Assert.Equal(before, File.ReadAllBytes(zip));
    }

    [Fact]
    public void InvalidUtf8InUnknownMetadataFailsWithoutCommittingEarlierRooms()
    {
        string valid = JsonSerializer.Serialize(Room("known", Node("known", Message("known", "user", "valid first room"))));
        byte[] prefix = Encoding.UTF8.GetBytes("[" + valid + ", {\"id\":\"second\",\"mapping\":{},\"future\":\"");
        byte[] suffix = "\"}]"u8.ToArray();
        string zip = RawZip([.. prefix, 0xFF, .. suffix]);
        var error = new StringWriter();

        Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], TextWriter.Null, error));
        Assert.Equal("Unsupported export format." + Environment.NewLine, error.ToString());
        using SqliteConnection connection = archive.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void DuplicateRoomIdsAndConflictingRoomAliasesAreRejected()
    {
        Dictionary<string, object?> room = Room("node", Node("node", Message("node", "user", "body")));
        Assert.Throws<InvalidDataException>(() => Import(RawZip(JsonSerializer.SerializeToUtf8Bytes(new[] { room, room }))));
        room["conversation_id"] = "another-room";
        Assert.Throws<InvalidDataException>(() => Import(Zip(room)));
    }

    [Fact]
    public void ConversationIdAliasAndMetadataOnlyRoomsArePreserved()
    {
        Dictionary<string, object?> room = Room("system", Node("stub", null),
            Node("system", Message("system", "system", "SYNTHETIC_SYSTEM_ONLY"), "stub"));
        room.Remove("id");
        room["conversation_id"] = "alias-room";

        ImportCounts counts = Import(Zip(room));

        Assert.Equal(1, counts.Rooms);
        Assert.Equal(0, counts.Messages);
        Assert.Equal("alias-room", Scalar<string>("SELECT room_id FROM rooms"));
        Assert.Contains("SYNTHETIC_SYSTEM_ONLY", AuxiliaryText());
        Assert.Empty(Find(allBranches: true).Messages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1e100")]
    [InlineData("-1e100")]
    public void MissingAndOutOfRangeEpochTimesStayUnknown(string? epoch)
    {
        Dictionary<string, object?> message = Message("node", "assistant", "body");
        message["create_time"] = epoch is null ? null : JsonSerializer.Deserialize<JsonElement>(epoch);
        Import(Zip(Room("node", Node("node", message))));

        Assert.Null(Assert.Single(Find().Messages).Timestamp);
        if (epoch is not null) Assert.Contains(epoch, AuxiliaryText());
    }

    [Fact]
    public void CliAcceptsExplicitChatGptFormatAndOutputsOnlyCounts()
    {
        string zip = Zip(Room("node", Node("node", Message("node", "assistant", "SYNTHETIC_PRIVATE_BODY"))));
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, ImportCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], output, error));
        Assert.Equal("", error.ToString());
        using JsonDocument counts = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, counts.RootElement.GetProperty("messages").GetInt32());
        Assert.Equal(new[] { "messages", "new_versions", "rooms", "unresolved_rooms" },
            counts.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_BODY", output.ToString());
        Assert.DoesNotContain(zip, output.ToString());
        string other = Path.Combine(archive.DirectoryPath, "default-format.db");
        Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", other], TextWriter.Null, TextWriter.Null));
    }

    private ImportCounts Import(string zip) => new ImportService(new ChatGptExportReader()).Import(zip, archive.DatabasePath);

    private FindResult Find(string? text = null, bool allBranches = false, int context = 0) =>
        new FindService().Find(archive.DatabasePath, new FindRequest(null, null, null, null, text, allBranches, context, 50));

    private string Zip(Dictionary<string, object?> room) => RawZip(JsonSerializer.SerializeToUtf8Bytes(new[] { room }));

    private string RawZip(byte[] bytes)
    {
        string path = Path.Combine(archive.DirectoryPath, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using Stream entry = zip.CreateEntry("conversations.json").Open();
        entry.Write(bytes);
        return path;
    }

    private static Dictionary<string, object?> Room(string? current, params Dictionary<string, object?>[] nodes)
    {
        foreach (Dictionary<string, object?> node in nodes)
        {
            string id = (string)node["id"]!;
            node["children"] = nodes.Where(candidate => Equals(candidate["parent"], id)).Select(candidate => candidate["id"]).ToArray();
        }
        return new Dictionary<string, object?>
        {
            ["id"] = "room", ["title"] = "Synthetic ChatGPT room", ["create_time"] = 0m,
            ["mapping"] = nodes.ToDictionary(node => (string)node["id"]!, node => node, StringComparer.Ordinal),
            ["current_node"] = current
        };
    }

    private static Dictionary<string, object?> Node(string id, Dictionary<string, object?>? message, string? parent = null) => new()
    {
        ["id"] = id, ["message"] = message, ["parent"] = parent, ["children"] = Array.Empty<string>()
    };

    private static Dictionary<string, object?> Message(string id, string role, string body, decimal time = 1m) => new()
    {
        ["id"] = id, ["author"] = new { role, name = (string?)null }, ["create_time"] = time, ["update_time"] = null,
        ["content"] = new { content_type = "text", parts = new[] { body } }, ["recipient"] = "all", ["channel"] = null,
        ["metadata"] = new { }
    };

    private T Scalar<T>(string sql)
    {
        using SqliteConnection connection = archive.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private string[] Column(string sql, string? path = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path ?? archive.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private string AuxiliaryText() => string.Join('\n', Column(
        "SELECT payload_json FROM auxiliary_blocks UNION ALL SELECT payload_json FROM room_auxiliary"));
}
