using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class ChatGptBoundaryTests
{
    private readonly SyntheticArchive archive = new();

    [Fact]
    public void MixedProviderCliFiltersKeepOriginalRolesAndDistinguishMatchingIds()
    {
        ChatGptBoundaryFixture.ImportMixed(archive);
        JsonElement all = FindJson();
        Assert.Equal(4, all.GetProperty("message_count").GetInt32());
        Assert.Equal(new[] { "assistant", "chatgpt-assistant", "chatgpt-user", "user" },
            all.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("speaker").GetString()).Order());

        foreach ((string label, string provider, string id) in new[]
        {
            ("assistant", "claude", "answer"), ("user", "claude", "prompt"),
            ("chatgpt-assistant", "chatgpt", "answer"), ("chatgpt-user", "chatgpt", "prompt")
        })
        {
            JsonElement message = Assert.Single(FindJson("--speaker", label).GetProperty("messages").EnumerateArray());
            Assert.Equal(provider, message.GetProperty("provider").GetString());
            Assert.Equal(id, message.GetProperty("message_id").GetString());
            Assert.Equal(label, message.GetProperty("speaker").GetString());
        }

        using var connection = archive.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT speaker FROM message_versions WHERE provider='chatgpt' ORDER BY message_id";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("assistant", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("user", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void ConfiguredChatGptAliasesAffectCliJsonAndHumanOutputWithoutChangingStoredVersions()
    {
        ChatGptBoundaryFixture.ImportMixed(archive);
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        File.WriteAllText(Path.Combine(archive.DirectoryPath, "bag.config.json"),
            """{"speaker_names":{"assistant":"Claude guide","chatgpt:assistant":"お姉ちゃん","chatgpt:user":"ChatGPT visitor"}}""");

        JsonElement message = Assert.Single(FindJson("--speaker", "お姉ちゃん").GetProperty("messages").EnumerateArray());
        Assert.Equal("chatgpt", message.GetProperty("provider").GetString());
        Assert.Equal("お姉ちゃん", message.GetProperty("speaker").GetString());
        Assert.Equal("claude", Assert.Single(FindJson("--speaker", "Claude guide").GetProperty("messages").EnumerateArray())
            .GetProperty("provider").GetString());
        (int code, string output, string error) = Run("find", "--speaker", "お姉ちゃん");
        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("お姉ちゃん", output);
        Assert.Contains("CHATGPT_ANSWER", output);
        Assert.DoesNotContain("CLAUDE_ANSWER", output);
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public void ClaudeSenderThatLooksLikeAChatGptMappingKeyIsNotRelabeledOrMatchedAcrossProviders()
    {
        ChatGptBoundaryFixture.ImportMixed(archive);
        archive.Import(Room("literal-room", "Synthetic literal sender",
            Message("literal", "CLAUDE_LITERAL_SENDER", speaker: "chatgpt:assistant")));
        File.WriteAllText(Path.Combine(archive.DirectoryPath, "bag.config.json"),
            """{"speaker_names":{"chatgpt:assistant":"お姉ちゃん"}}""");

        JsonElement literal = Assert.Single(FindJson("--text", "CLAUDE_LITERAL_SENDER")
            .GetProperty("messages").EnumerateArray());
        Assert.Equal("claude", literal.GetProperty("provider").GetString());
        Assert.Equal("chatgpt:assistant", literal.GetProperty("speaker").GetString());
        JsonElement chosen = Assert.Single(FindJson("--speaker", "お姉ちゃん").GetProperty("messages").EnumerateArray());
        Assert.Equal("chatgpt", chosen.GetProperty("provider").GetString());
        Assert.Equal("CHATGPT_ANSWER", chosen.GetProperty("body").GetString());
    }

    [Fact]
    public void ChatGptFormatIsExplicitAndCliImportPreservesTheZipAcrossRepeatedImports()
    {
        string zip = ChatGptBoundaryFixture.WriteZip(archive, "CHATGPT_ANSWER");
        byte[] original = SHA256.HashData(File.ReadAllBytes(zip));
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, BagCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], output, error));
        Assert.Equal("", error.ToString());
        using (JsonDocument counts = JsonDocument.Parse(output.ToString()))
        {
            Assert.Equal(1, counts.RootElement.GetProperty("rooms").GetInt32());
            Assert.Equal(2, counts.RootElement.GetProperty("messages").GetInt32());
            Assert.Equal(2, counts.RootElement.GetProperty("new_versions").GetInt32());
        }
        output.GetStringBuilder().Clear();
        Assert.Equal(0, BagCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], output, error));
        using JsonDocument repeated = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, repeated.RootElement.GetProperty("new_versions").GetInt32());
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(zip)));
    }

    [Fact]
    public void VersionOneSourcesRemainReadableWhileSameSourceReimportRequiresMatchingImporterVersion()
    {
        archive.Import(Room("old-room", "Synthetic v1.0 room", Message("old-message", "ORIGINAL_V1_BODY")));
        string oldZip = Assert.Single(Directory.GetFiles(archive.DirectoryPath, "*.zip"));
        archive.Execute("UPDATE sources SET importer_version='1.0.0'");
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        var service = new FindService();
        FindResult found = service.Find(archive.DatabasePath, new FindRequest(null, null, null, null, null, false, 0, 20));
        FindMessage original = Assert.Single(found.Messages);
        Assert.Equal("ORIGINAL_V1_BODY", original.Body);
        Assert.Equal("old-room", Assert.Single(new RoomListService().List(archive.DatabasePath).Rooms).RoomId);
        JsonElement heads = MessageRetrievalOutput.Heads(found, SpeakerNames.Default);
        Assert.Equal("ORIGINAL_V1_BODY", Assert.Single(heads.GetProperty("messages").EnumerateArray())
            .GetProperty("preview").GetString());
        Assert.Equal("ORIGINAL_V1_BODY", Assert.Single(service.GetMessages(archive.DatabasePath, [original.Key]).Messages).Body);
        Assert.Equal("ORIGINAL_V1_BODY", Assert.Single(FindJson().GetProperty("messages").EnumerateArray())
            .GetProperty("body").GetString());
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
            new ImportService(new ClaudeExportReader()).Import(oldZip, archive.DatabasePath));
        Assert.Equal("Incompatible database.", BagErrors.GetMessage(failure));
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));

        archive.Import(Room("new-room", "Synthetic v1.1 room", Message("new-message", "NEW_V11_BODY")));
        Assert.Equal(2, FindJson().GetProperty("message_count").GetInt32());
        Assert.Equal(original.Key, service.GetMessages(archive.DatabasePath, [original.Key]).Messages[0].Key);
        using var connection = archive.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sources WHERE importer_version='1.0.0'";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderScopedSealAndUnsealCoverAllVersionsWithoutTouchingOtherProvider(bool messageOnly)
    {
        ChatGptBoundaryFixture.ImportMixed(archive);
        ChatGptBoundaryFixture.ImportChatGpt(archive, "CHATGPT_REVISED_ANSWER");
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        string[] target = messageOnly ? ["--message", "answer"] : ["--room", "shared-room"];

        AssertChanged("seal", [.. target, "--provider", "chatgpt"]);
        JsonElement[] visible = FindJson().GetProperty("messages").EnumerateArray().ToArray();
        Assert.DoesNotContain(visible, message => message.GetProperty("provider").GetString() == "chatgpt"
            && (!messageOnly || message.GetProperty("message_id").GetString() == "answer"));
        Assert.Equal(2, visible.Count(message => message.GetProperty("provider").GetString() == "claude"));
        Assert.Equal(5, FindJson("--include-sealed").GetProperty("message_count").GetInt32());

        // Omitting --provider preserves the existing Claude target, even when IDs overlap.
        AssertChanged("seal", target);
        AssertChanged("unseal", [.. target, "--provider", "chatgpt"]);
        JsonElement[] restored = FindJson().GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(3, restored.Count(message => message.GetProperty("provider").GetString() == "chatgpt"));
        Assert.DoesNotContain(restored, message => message.GetProperty("provider").GetString() == "claude"
            && (!messageOnly || message.GetProperty("message_id").GetString() == "answer"));
        AssertChanged("unseal", target);
        Assert.Equal(5, FindJson().GetProperty("message_count").GetInt32());
        Assert.Empty(new SealStore(archive.DatabasePath).Read().Targets);
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
    }

    [Theory]
    [InlineData("seal", false)]
    [InlineData("seal", true)]
    [InlineData("unseal", false)]
    [InlineData("unseal", true)]
    public void InvalidOrRepeatedProvidersFailBeforeWritingSeals(string action, bool repeated)
    {
        ChatGptBoundaryFixture.ImportMixed(archive);
        string[] provider = repeated
            ? ["--provider", "chatgpt", "--provider", "claude"]
            : ["--provider", "SYNTHETIC_PRIVATE_UNSUPPORTED_PROVIDER"];
        (int code, string output, string error) = Run(action, ["--room", "shared-room", .. provider]);

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Equal("Invalid arguments. Use --help." + Environment.NewLine, error);
        Assert.False(File.Exists(Path.Combine(archive.DirectoryPath, "seals.json")));
    }

    private JsonElement FindJson(params string[] options)
    {
        (int code, string output, string error) = Run("find", ["--json", .. options]);
        Assert.Equal(0, code);
        Assert.Equal("", error);
        using JsonDocument document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private void AssertChanged(string action, params string[] options)
    {
        (int code, string output, string error) = Run(action, options);
        Assert.Equal(0, code);
        Assert.Equal("", error);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(1, document.RootElement.GetProperty("changed").GetInt32());
    }

    private (int Code, string Output, string Error) Run(string action, params string[] options)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = BagCommand.Run([action, "--db", archive.DatabasePath, .. options], output, error);
        return (code, output.ToString(), error.ToString());
    }
}

public sealed partial class McpTests
{
    [Fact]
    public async Task MixedProviderMcpFindHeadsAndExactReadsKeepIdenticalIdsDistinct()
    {
        ChatGptBoundaryFixture.ImportMixed(archive);
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);

        foreach ((string label, string provider) in new[] { ("assistant", "claude"), ("chatgpt-assistant", "chatgpt") })
        {
            JsonElement full = Structured(await server.CallAsync("find", new { speaker = label }), "bag-find/1");
            JsonElement match = Assert.Single(full.GetProperty("messages").EnumerateArray());
            Assert.Equal(provider, match.GetProperty("provider").GetString());
            Assert.Equal(label, match.GetProperty("speaker").GetString());
            JsonElement heads = Structured(await server.CallAsync("find_heads", new { speaker = label }), "bag-heads/1");
            JsonElement head = Assert.Single(heads.GetProperty("messages").EnumerateArray());
            Assert.Equal(provider, head.GetProperty("provider").GetString());
            Assert.Equal(label, head.GetProperty("speaker").GetString());
            JsonElement retrieved = Structured(await server.CallAsync("get_messages", new { ids = new[] { MessageId(head) } }),
                "bag-messages/1");
            Assert.Equal(match.GetRawText(), Assert.Single(retrieved.GetProperty("messages").EnumerateArray()).GetRawText());
        }
        JsonElement all = Structured(await server.CallAsync("find_heads", new { text = "ANSWER" }), "bag-heads/1");
        Dictionary<string, string>[] ids = all.GetProperty("messages").EnumerateArray().Select(MessageId).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.All(ids, id => Assert.Equal("answer", id["message_id"]));
        JsonElement both = Structured(await server.CallAsync("get_messages", new { ids }), "bag-messages/1");
        Assert.Equal(new[] { "CHATGPT_ANSWER", "CLAUDE_ANSWER" }, both.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("body").GetString()).Order());
    }

    [Fact]
    public async Task ChatGptConfiguredMcpAliasIsIsolatedFromClaudeAndAdvertisedByBothSearchTools()
    {
        ChatGptBoundaryFixture.ImportMixed(archive);
        File.WriteAllText(Path.Combine(archive.DirectoryPath, "bag.config.json"),
            """{"speaker_names":{"assistant":"Claude guide","chatgpt:assistant":"お姉ちゃん"}}""");
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        JsonElement tools = (await server.RequestAsync("tools/list", new { })).GetProperty("result").GetProperty("tools");
        foreach (string name in new[] { "find", "find_heads" })
        {
            JsonElement tool = tools.EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
            string?[] labels = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("speaker")
                .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Contains("お姉ちゃん", labels);
            Assert.Contains("Claude guide", labels);
            Assert.DoesNotContain("assistant", labels);
            Assert.DoesNotContain("chatgpt-assistant", labels);
            string schema = name == "find" ? "bag-find/1" : "bag-heads/1";
            JsonElement result = Structured(await server.CallAsync(name, new { speaker = "お姉ちゃん" }), schema);
            JsonElement message = Assert.Single(result.GetProperty("messages").EnumerateArray());
            Assert.Equal("chatgpt", message.GetProperty("provider").GetString());
            Assert.Equal("お姉ちゃん", message.GetProperty("speaker").GetString());
            AssertToolError(await server.CallAsync(name, new { speaker = "chatgpt-assistant" }), "Invalid tool arguments.");
        }
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public async Task RunningMcpServerReloadsChatGptSealsAcrossAllVersionsWithoutHidingClaude()
    {
        ChatGptBoundaryFixture.ImportMixed(archive);
        ChatGptBoundaryFixture.ImportChatGpt(archive, "SYNTHETIC_PRIVATE_CHATGPT_REVISION");
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        JsonElement heads = Structured(await server.CallAsync("find_heads", new { }), "bag-heads/1");
        Dictionary<string, string>[] ids = heads.GetProperty("messages").EnumerateArray().Select(MessageId).ToArray();
        Assert.Equal(5, ids.Length);
        Assert.True(new SealService().Set(archive.DatabasePath, "shared-room", null, true, "chatgpt"));

        JsonElement hidden = await server.CallAsync("get_messages", new { ids });
        JsonElement[] visible = Structured(hidden, "bag-messages/1").GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, visible.Length);
        Assert.All(visible, message => Assert.Equal("claude", message.GetProperty("provider").GetString()));
        Assert.DoesNotContain("CHATGPT", hidden.GetRawText());
        JsonElement rooms = Structured(await server.CallAsync("list_rooms", new { }), "bag-rooms/1");
        Assert.Equal("claude", Assert.Single(rooms.GetProperty("rooms").EnumerateArray()).GetProperty("provider").GetString());
        Assert.Empty(Structured(await server.CallAsync("find_heads", new { speaker = "chatgpt-assistant" }), "bag-heads/1")
            .GetProperty("messages").EnumerateArray());
        Assert.Empty(Structured(await server.CallAsync("find", new { speaker = "chatgpt-assistant", context = 50 }), "bag-find/1")
            .GetProperty("messages").EnumerateArray());

        Assert.True(new SealService().Set(archive.DatabasePath, "shared-room", null, false, "chatgpt"));
        JsonElement restored = Structured(await server.CallAsync("get_messages", new { ids }), "bag-messages/1");
        Assert.Equal(5, restored.GetProperty("message_count").GetInt32());
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
    }
}

internal static class ChatGptBoundaryFixture
{
    public static void ImportMixed(SyntheticArchive archive)
    {
        archive.Import(Room("shared-room", "CLAUDE_ROOM",
            Message("prompt", "CLAUDE_PROMPT"),
            Message("answer", "CLAUDE_ANSWER", "prompt", "2026-01-01T00:00:01Z", "assistant")));
        ImportChatGpt(archive, "CHATGPT_ANSWER");
    }

    public static void ImportChatGpt(SyntheticArchive archive, string answer)
    {
        new ImportService(new ChatGptExportReader()).Import(WriteZip(archive, answer), archive.DatabasePath);
    }

    public static string WriteZip(SyntheticArchive archive, string answer)
    {
        string zip = Path.Combine(archive.DirectoryPath, Guid.NewGuid().ToString("N") + ".zip");
        using FileStream file = File.Create(zip);
        using var zipArchive = new ZipArchive(file, ZipArchiveMode.Create);
        using Stream entry = zipArchive.CreateEntry("conversations.json").Open();
        JsonSerializer.Serialize(entry, new[]
        {
            new
            {
                id = "shared-room", title = "CHATGPT_ROOM", create_time = 1767225600,
                current_node = "answer",
                mapping = new Dictionary<string, object>
                {
                    ["prompt"] = Node("prompt", "user", "CHATGPT_PROMPT", null, ["answer"], 1767225600),
                    ["answer"] = Node("answer", "assistant", answer, "prompt", [], 1767225601)
                }
            }
        });
        return zip;
    }

    private static object Node(string id, string role, string body, string? parent, string[] children, long timestamp)
    {
        return new
        {
            id, parent, children,
            message = new
            {
                id, author = new { role, name = (string?)null }, create_time = timestamp, update_time = (long?)null,
                content = new { content_type = "text", parts = new[] { body } },
                recipient = "all", channel = (string?)null, metadata = new { }
            }
        };
    }
}
