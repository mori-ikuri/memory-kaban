using System.Text.Json;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed partial class McpTests
{
    [Fact]
    public async Task HeadsSearchFullBodiesButReturnOnlyLiteralPrefixesThenGetExactText()
    {
        string prefix = new string('鞄', 78) + "🙂\0";
        string body = prefix + "SYNTHETIC_PRIVATE_TAIL\r\n e\u0301 \t";
        archive.Import(Room("room", "Synthetic head room",
            Message("before", "before", createdAt: "2026-10-26T03:00:00Z"),
            Message("hit", body, "before", "2026-10-26T04:00:00Z", "assistant")));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        var filter = new
        {
            from = "2026-10-26 13:00", to = "2026-10-26 13:00", room = "head",
            speaker = "assistant", text = "SYNTHETIC_PRIVATE_TAIL"
        };

        JsonElement originalFind = Structured(await server.CallAsync("find", filter), "bag-find/1");
        JsonElement response = await server.CallAsync("find_heads", filter);
        JsonElement heads = Structured(response, "bag-heads/1");
        Assert.Equal(1, heads.GetProperty("match_count").GetInt32());
        JsonElement head = Assert.Single(heads.GetProperty("messages").EnumerateArray());
        Assert.Equal(prefix, head.GetProperty("preview").GetString());
        Assert.True(head.GetProperty("preview_truncated").GetBoolean());
        Assert.Equal("assistant", head.GetProperty("speaker").GetString());
        Assert.Equal("2026-10-26T13:00:00.0000000+09:00", head.GetProperty("timestamp_jst").GetString());
        Assert.Equal("Synthetic head room", Assert.Single(head.GetProperty("sources").EnumerateArray())
            .GetProperty("room_name").GetString());
        Assert.False(head.TryGetProperty("body", out _));
        Assert.False(head.TryGetProperty("is_match", out _));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_TAIL", response.GetRawText());
        Assert.Equal(heads.GetRawText(), Assert.Single(response.GetProperty("result").GetProperty("content").EnumerateArray())
            .GetProperty("text").GetString());

        JsonElement full = Structured(await server.CallAsync("get_messages", new { ids = new[] { MessageId(head) } }),
            "bag-messages/1");
        JsonElement message = Assert.Single(full.GetProperty("messages").EnumerateArray());
        Assert.Equal(body, message.GetProperty("body").GetString());
        Assert.Equal(Assert.Single(originalFind.GetProperty("messages").EnumerateArray()).GetRawText(), message.GetRawText());
        Assert.Equal(originalFind.GetRawText(), Structured(await server.CallAsync("find", filter), "bag-find/1").GetRawText());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(79)]
    [InlineData(80)]
    [InlineData(81)]
    public void HeadPrefixesCountUnicodeScalarsWithoutSplittingSurrogates(int length)
    {
        string body = string.Concat(Enumerable.Repeat("🙂", length));
        var message = new FindMessage(new FindMessageKey("claude", "room", "id", "version"), "human",
            body, null, null, true, [new FindSource("source", "room", true, [])]);
        JsonElement heads = MessageRetrievalOutput.Heads(new FindResult(1, [message]), SpeakerNames.Default);
        JsonElement head = Assert.Single(heads.GetProperty("messages").EnumerateArray());

        Assert.Equal(string.Concat(Enumerable.Repeat("🙂", Math.Min(length, 80))), head.GetProperty("preview").GetString());
        Assert.Equal(length > 80, head.GetProperty("preview_truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, head.GetProperty("timestamp_utc").ValueKind);
        Assert.Equal(JsonValueKind.Null, head.GetProperty("timestamp_jst").ValueKind);
    }

    [Fact]
    public async Task FullReadsKeepExactVersionsRoomIdentityRequestOrderAndAllProvenance()
    {
        archive.Import(Room("room", "First name", Message("shared", "old body")),
            Room("other-room", "Other room", Message("shared", "other room body")));
        archive.Import(Room("room", "Second name", Message("shared", "new body")));
        archive.Import(Room("room", "Third name", Message("shared", "new body")));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        JsonElement heads = Structured(await server.CallAsync("find_heads", new { }), "bag-heads/1");
        JsonElement[] entries = heads.GetProperty("messages").EnumerateArray().ToArray();
        JsonElement old = entries.Single(entry => entry.GetProperty("preview").GetString() == "old body");
        JsonElement newest = entries.Single(entry => entry.GetProperty("preview").GetString() == "new body");
        JsonElement other = entries.Single(entry => entry.GetProperty("preview").GetString() == "other room body");
        Dictionary<string, string> missing = MessageId(old);
        missing["provider"] = "different-provider";

        JsonElement full = Structured(await server.CallAsync("get_messages", new
        {
            ids = new[] { MessageId(newest), missing, MessageId(other), MessageId(old), MessageId(newest) }
        }), "bag-messages/1");
        JsonElement[] messages = full.GetProperty("messages").EnumerateArray().ToArray();

        Assert.Equal(3, full.GetProperty("message_count").GetInt32());
        Assert.Equal(new[] { "new body", "other room body", "old body" },
            messages.Select(message => message.GetProperty("body").GetString()));
        Assert.Equal(new[] { "Second name", "Third name" }, messages[0].GetProperty("sources").EnumerateArray()
            .Select(source => source.GetProperty("room_name").GetString()).Order());
        Assert.All(messages, message => Assert.True(message.GetProperty("is_match").GetBoolean()));
        Assert.All(messages.SelectMany(message => message.GetProperty("sources").EnumerateArray()),
            source => Assert.Empty(source.GetProperty("context_for").EnumerateArray()));
    }

    [Fact]
    public async Task HeadsRespectMainlineSelectionAndExactReadsCanFetchBranchVersions()
    {
        Dictionary<string, object?> room = Room("room", "Branch room",
            Message("root", "root"),
            Message("main", "selected", "root", "2026-01-01T01:00:00Z"),
            Message("side", "branch", "root", "2026-01-01T02:00:00Z"));
        room["current_leaf_message_uuid"] = "main";
        archive.Import(room);
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        JsonElement ordinary = Structured(await server.CallAsync("find_heads", new { text = "branch" }), "bag-heads/1");
        Assert.Empty(ordinary.GetProperty("messages").EnumerateArray());
        JsonElement branches = Structured(await server.CallAsync("find_heads", new { text = "branch", all_branches = true }),
            "bag-heads/1");
        JsonElement head = Assert.Single(branches.GetProperty("messages").EnumerateArray());
        JsonElement full = Structured(await server.CallAsync("get_messages", new { ids = new[] { MessageId(head) } }),
            "bag-messages/1");

        JsonElement message = Assert.Single(full.GetProperty("messages").EnumerateArray());
        Assert.Equal("branch", message.GetProperty("body").GetString());
        Assert.False(Assert.Single(message.GetProperty("sources").EnumerateArray()).GetProperty("is_mainline").GetBoolean());
    }

    [Fact]
    public async Task NewReadsReloadSealsAndMissingAndSealedIdsAreIndistinguishable()
    {
        archive.Import(Room("room", "Synthetic room", Message("hit", "SYNTHETIC_PRIVATE_BODY")));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        JsonElement initial = Structured(await server.CallAsync("find_heads", new { }), "bag-heads/1");
        Dictionary<string, string> id = MessageId(Assert.Single(initial.GetProperty("messages").EnumerateArray()));
        Seal("room", "hit");
        JsonElement sealedResult = Structured(await server.CallAsync("get_messages", new { ids = new[] { id } }),
            "bag-messages/1");
        Dictionary<string, string> missing = new(id, StringComparer.Ordinal) { ["message_id"] = "missing" };
        JsonElement missingResult = Structured(await server.CallAsync("get_messages", new { ids = new[] { missing } }),
            "bag-messages/1");
        Assert.Equal(missingResult.GetRawText(), sealedResult.GetRawText());
        Assert.Empty(sealedResult.GetProperty("messages").EnumerateArray());
        Assert.Empty(Structured(await server.CallAsync("find_heads", new { }), "bag-heads/1")
            .GetProperty("messages").EnumerateArray());

        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "room", "hit"), false);
        Seal("room");
        Assert.Equal(sealedResult.GetRawText(), Structured(await server.CallAsync("get_messages", new { ids = new[] { id } }),
            "bag-messages/1").GetRawText());
        File.WriteAllText(Path.Combine(archive.DirectoryPath, "seals.json"), "{SYNTHETIC_PRIVATE_BROKEN");
        AssertToolError(await server.CallAsync("find_heads", new { }), "Invalid seal file.");
        AssertToolError(await server.CallAsync("get_messages", new { ids = new[] { missing } }), "Invalid seal file.");
    }

    [Fact]
    public async Task NewReadsLeaveDatabaseAndSidecarUnchangedAndUseConfiguredSpeakers()
    {
        archive.Import(Room("room", "Synthetic room", Message("hit", "body")));
        File.WriteAllText(Path.Combine(archive.DirectoryPath, "bag.config.json"), """{"speaker_names":{"human":"Visitor"}}""");
        Seal("other-room");
        string sealsPath = Path.Combine(archive.DirectoryPath, "seals.json");
        byte[] database = File.ReadAllBytes(archive.DatabasePath);
        byte[] seals = File.ReadAllBytes(sealsPath);
        string[] files = Directory.GetFiles(archive.DirectoryPath).Order().ToArray();
        await using (McpProcess server = await McpProcess.StartAsync(archive.DatabasePath))
        {
            JsonElement heads = Structured(await server.CallAsync("find_heads", new { speaker = "Visitor" }), "bag-heads/1");
            JsonElement head = Assert.Single(heads.GetProperty("messages").EnumerateArray());
            Assert.Equal("Visitor", head.GetProperty("speaker").GetString());
            JsonElement full = Structured(await server.CallAsync("get_messages", new { ids = new[] { MessageId(head) } }),
                "bag-messages/1");
            Assert.Equal("Visitor", Assert.Single(full.GetProperty("messages").EnumerateArray()).GetProperty("speaker").GetString());
        }
        Assert.Equal(database, File.ReadAllBytes(archive.DatabasePath));
        Assert.Equal(seals, File.ReadAllBytes(sealsPath));
        Assert.Equal(files, Directory.GetFiles(archive.DirectoryPath).Order().ToArray());
    }

    [Fact]
    public async Task NewToolSchemasLimitsAndErrorsMatchTheFixedArgumentSurface()
    {
        Dictionary<string, object?>[] messages = Enumerable.Range(0, 55)
            .Select(index => Message("m" + index, "body", index == 0 ? null : "m" + (index - 1))).ToArray();
        archive.Import(Room("room", "Synthetic room", messages));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        JsonElement tools = (await server.RequestAsync("tools/list", new { })).GetProperty("result").GetProperty("tools");
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.False(tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        }
        JsonElement headsTool = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "find_heads");
        Assert.Equal(new[] { "all_branches", "from", "limit", "room", "speaker", "text", "to" },
            headsTool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name).Order());
        JsonElement getTool = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "get_messages");
        Assert.Equal("ids", Assert.Single(getTool.GetProperty("inputSchema").GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal(50, getTool.GetProperty("inputSchema").GetProperty("properties").GetProperty("ids")
            .GetProperty("maxItems").GetInt32());
        Assert.Equal(20, Structured(await server.CallAsync("find_heads", new { }), "bag-heads/1").GetProperty("message_count").GetInt32());
        JsonElement heads = Structured(await server.CallAsync("find_heads", new { limit = 500 }), "bag-heads/1");
        Assert.Equal(50, heads.GetProperty("message_count").GetInt32());
        Dictionary<string, string>[] ids = heads.GetProperty("messages").EnumerateArray().Select(MessageId).ToArray();
        Assert.Equal(50, Structured(await server.CallAsync("get_messages", new { ids }), "bag-messages/1")
            .GetProperty("message_count").GetInt32());
        AssertToolError(await server.CallAsync("get_messages", new { ids = ids.Append(ids[0]).ToArray() }), "Invalid tool arguments.");
        AssertToolError(await server.CallAsync("find_heads", new { context = 0 }), "Invalid tool arguments.");
        AssertToolError(await server.CallAsync("find_heads", new { include_sealed = true }), "Invalid tool arguments.");
        AssertToolError(await server.CallAsync("get_messages", new { ids, include_sealed = true }), "Invalid tool arguments.");
        AssertToolError(await server.CallAsync("get_messages", new { ids = new[] { "SYNTHETIC_PRIVATE_ARGUMENT" } }), "Invalid tool arguments.");
    }

    private static Dictionary<string, string> MessageId(JsonElement message)
    {
        return new[] { "provider", "room_id", "message_id", "version_hash" }
            .ToDictionary(name => name, name => message.GetProperty(name).GetString()!, StringComparer.Ordinal);
    }
}

public sealed class MessageRetrievalTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":null}")]
    [InlineData("{\"ids\":[]}")]
    [InlineData("{\"ids\":[null]}")]
    [InlineData("{\"ids\":[\"id\"]}")]
    [InlineData("{\"ids\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"message_id\":\"id\"}]}")]
    [InlineData("{\"ids\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"message_id\":\"id\",\"version_hash\":null}]}")]
    [InlineData("{\"ids\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"message_id\":\"id\",\"version_hash\":\"\"}]}")]
    [InlineData("{\"ids\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"message_id\":\"id\",\"version_hash\":3}]}")]
    [InlineData("{\"ids\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"message_id\":\"id\",\"version_hash\":\"hash\",\"body\":\"extra\"}]}")]
    [InlineData("{\"ids\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"message_id\":\"id\",\"version_hash\":\"hash\",\"message_id\":\"other\"}]}")]
    public void InvalidSelectorsAreRejectedBeforeArchiveAccess(string json)
    {
        Dictionary<string, JsonElement> arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.False(McpArguments.TryReadMessageIds(arguments, out _));
    }

    [Fact]
    public void FullReadsDoNotCreateMissingDatabases()
    {
        var archive = new SyntheticArchive();
        Exception? failure = Record.Exception(() => new FindService().GetMessages(archive.DatabasePath,
            [new FindMessageKey("claude", "room", "id", "version")]));
        Assert.NotNull(failure);
        Assert.True(BagErrors.IsHandled(failure));
        Assert.False(File.Exists(archive.DatabasePath));
        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
    }

    [Fact]
    public void FullReadsRejectIncompatibleDatabasesWithoutModifyingThem()
    {
        var archive = new SyntheticArchive();
        archive.Import(Room("room", "room", Message("id", "body")));
        archive.Execute("UPDATE schema_info SET version=1");
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        Exception? failure = Record.Exception(() => new FindService().GetMessages(archive.DatabasePath,
            [new FindMessageKey("claude", "room", "id", "version")]));
        Assert.NotNull(failure);
        Assert.Equal("Incompatible database.", BagErrors.GetMessage(failure));
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
    }
}
