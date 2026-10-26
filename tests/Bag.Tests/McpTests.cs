using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class McpTests
{
    private readonly SyntheticArchive archive = new();

    [Fact]
    public async Task AdvertisesOnlyTheTwoReadToolsAndRejectsWriteTools()
    {
        archive.Import(Room("room", "Synthetic room", Message("message", "Synthetic body")));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);

        JsonElement response = await server.RequestAsync("tools/list", new { });
        JsonElement[] tools = response.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(new[] { "find", "list_rooms" }, tools.Select(tool => tool.GetProperty("name").GetString()).Order());
        JsonElement find = tools.Single(tool => tool.GetProperty("name").GetString() == "find");
        string[] findArguments = find.GetProperty("inputSchema").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(new[] { "all_branches", "context", "from", "limit", "room", "speaker", "text", "to" }, findArguments);
        Assert.Equal(new[] { "assistant", "user" }, find.GetProperty("inputSchema").GetProperty("properties")
            .GetProperty("speaker").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Order());

        foreach (string name in new[] { "seal", "unseal", "import", "write" })
        {
            JsonElement rejected = await server.CallAsync(name, new { room = "SYNTHETIC_PRIVATE_TARGET" });
            Assert.Equal("Invalid request.", rejected.GetProperty("error").GetProperty("message").GetString());
            Assert.DoesNotContain("SYNTHETIC_PRIVATE_TARGET", rejected.GetRawText());
        }
    }

    [Fact]
    public async Task FindReturnsTheStructuredEnvelopeWithExactBodyAndJapanTime()
    {
        const string body = " \t鞄🙂e\u0301 café Ελληνικά 한국어\r\n次の行\n'\"\\literal \\u3042\0末尾  ";
        archive.Import(Room("room", "Synthetic research room",
            Message("before", "different body", createdAt: "2026-10-26T03:00:00Z"),
            Message("match", body, "before", "2026-10-26T04:00:00Z", "assistant"),
            Message("after", "鞄 other speaker", "match", "2026-10-26T05:00:00Z")));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);

        JsonElement response = await server.CallAsync("find", new
        {
            from = "2026-10-26 13:00",
            to = "2026-10-26T13:00:00+09:00",
            room = "research",
            speaker = "assistant",
            text = "鞄"
        });
        JsonElement result = Structured(response, "bag-find/1");

        Assert.Equal(1, result.GetProperty("match_count").GetInt32());
        JsonElement message = Assert.Single(result.GetProperty("messages").EnumerateArray());
        Assert.Equal("match", message.GetProperty("message_id").GetString());
        Assert.Equal(body, message.GetProperty("body").GetString());
        string rawBody = message.GetProperty("body").GetRawText();
        Assert.Contains("鞄", rawBody);
        Assert.Contains("café", rawBody);
        Assert.Contains("Ελληνικά", rawBody);
        Assert.Contains("한국어", rawBody);
        Assert.DoesNotContain("\\u9784", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("assistant", message.GetProperty("speaker").GetString());
        Assert.Equal("2026-10-26T13:00:00.0000000+09:00", message.GetProperty("timestamp_jst").GetString());
        Assert.True(message.GetProperty("is_match").GetBoolean());
        string textContent = Assert.Single(response.GetProperty("result").GetProperty("content").EnumerateArray())
            .GetProperty("text").GetString()!;
        using JsonDocument textDocument = JsonDocument.Parse(textContent);
        Assert.Equal(body, Assert.Single(textDocument.RootElement.GetProperty("messages").EnumerateArray())
            .GetProperty("body").GetString());
    }

    [Fact]
    public async Task FindUsesTheDefaultLimitAndClampsMatchesAndTotalMessagesToFifty()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Dictionary<string, object?>[] messages = Enumerable.Range(0, 110)
            .Select(index => Message("m" + index, index == 55 ? "needle-special" : "needle",
                index == 0 ? null : "m" + (index - 1), start.AddMinutes(index).ToString("O", CultureInfo.InvariantCulture)))
            .ToArray();
        archive.Import(Room("room", "Synthetic room", messages));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);

        JsonElement defaults = Structured(await server.CallAsync("find", new { }), "bag-find/1");
        Assert.Equal(20, defaults.GetProperty("match_count").GetInt32());
        Assert.Equal(20, defaults.GetProperty("messages").GetArrayLength());

        JsonElement manyMatches = Structured(await server.CallAsync("find", new { limit = 1000, context = 1000 }), "bag-find/1");
        Assert.Equal(50, manyMatches.GetProperty("match_count").GetInt32());
        Assert.Equal(50, manyMatches.GetProperty("messages").GetArrayLength());

        JsonElement context = Structured(await server.CallAsync("find", new { text = "needle-special", context = 1000 }), "bag-find/1");
        Assert.Equal(1, context.GetProperty("match_count").GetInt32());
        Assert.InRange(context.GetProperty("messages").GetArrayLength(), 2, 50);
        JsonElement match = Assert.Single(context.GetProperty("messages").EnumerateArray(),
            message => message.GetProperty("is_match").GetBoolean());
        Assert.Equal("m55", match.GetProperty("message_id").GetString());
    }

    [Fact]
    public async Task IntegerCountsBeyondInt64AreAcceptedAndClampedToFifty()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Dictionary<string, object?>[] messages = Enumerable.Range(0, 110)
            .Select(index => Message("m" + index, index == 55 ? "needle-special" : "needle",
                index == 0 ? null : "m" + (index - 1), start.AddMinutes(index).ToString("O", CultureInfo.InvariantCulture)))
            .ToArray();
        archive.Import(Room("room", "Synthetic room", messages));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        using JsonDocument largeCounts = JsonDocument.Parse("""
            {"limit":92233720368547758081234567890,"context":92233720368547758081234567890}
            """);
        using JsonDocument largeContext = JsonDocument.Parse("""
            {"text":"needle-special","context":92233720368547758081234567890}
            """);

        JsonElement matches = Structured(await server.CallAsync("find", largeCounts.RootElement), "bag-find/1");
        Assert.Equal(50, matches.GetProperty("match_count").GetInt32());
        Assert.Equal(50, matches.GetProperty("message_count").GetInt32());
        Assert.Equal(50, matches.GetProperty("messages").GetArrayLength());

        JsonElement context = Structured(await server.CallAsync("find", largeContext.RootElement), "bag-find/1");
        Assert.Equal(1, context.GetProperty("match_count").GetInt32());
        Assert.InRange(context.GetProperty("messages").GetArrayLength(), 2, 50);
        JsonElement hit = Assert.Single(context.GetProperty("messages").EnumerateArray(),
            message => message.GetProperty("is_match").GetBoolean());
        Assert.Equal("m55", hit.GetProperty("message_id").GetString());
    }

    [Fact]
    public async Task InvalidRpcParameterTypesReturnSanitizedProtocolErrors()
    {
        archive.Import(Room("room", "Synthetic room", Message("message", "Synthetic body")));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        object[] invalidParameters =
        [
            new { name = "find", arguments = "SYNTHETIC_PRIVATE_INVALID_ARGUMENTS" },
            new { name = "find", arguments = new[] { "SYNTHETIC_PRIVATE_INVALID_ARGUMENTS" } },
            new { name = new[] { "SYNTHETIC_PRIVATE_INVALID_TOOL_NAME" }, arguments = new { } }
        ];

        foreach (object parameters in invalidParameters)
        {
            JsonElement response = await server.RequestAsync("tools/call", parameters);
            Assert.False(response.TryGetProperty("result", out _));
            JsonElement error = response.GetProperty("error");
            Assert.Equal("Invalid request.", error.GetProperty("message").GetString());
            Assert.False(error.TryGetProperty("data", out JsonElement data) && data.ValueKind != JsonValueKind.Null);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE", response.GetRawText());
        }

        Structured(await server.CallAsync("find", new { }), "bag-find/1");
    }

    [Fact]
    public async Task RoomsDeduplicateLogicalMessagesAndExcludeSealedOrEmptyRooms()
    {
        Dictionary<string, object?> first = Room("visible-room", "First visible name",
            Message("revised", "first version"),
            Message("sealed-message", "SYNTHETIC_PRIVATE_MESSAGE", "revised", "2026-01-01T01:00:00Z"),
            Message("side", "visible branch", "revised", "2026-01-01T02:00:00Z"));
        first["created_at"] = "2026-01-01T00:00:00Z";
        string firstSource = archive.Import(first);
        Dictionary<string, object?> second = Room("visible-room", "Second visible name",
            Message("revised", "second version"),
            Message("sealed-message", "SYNTHETIC_PRIVATE_MESSAGE", "revised", "2026-01-01T01:00:00Z"),
            Message("side", "visible branch", "revised", "2026-01-01T02:00:00Z"));
        second["created_at"] = "2026-01-02T09:00:00+09:00";
        string secondSource = archive.Import(second,
            Room("sealed-room", "SYNTHETIC_PRIVATE_ROOM_NAME", Message("hidden", "SYNTHETIC_PRIVATE_ROOM_BODY")),
            Room("all-messages-sealed", "SYNTHETIC_PRIVATE_EMPTY_ROOM", Message("only", "SYNTHETIC_PRIVATE_ONLY_BODY")),
            Room("empty-room", "SYNTHETIC_PRIVATE_ZERO_MESSAGES"));
        Seal("visible-room", "sealed-message");
        Seal("sealed-room");
        Seal("all-messages-sealed", "only");
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);

        JsonElement response = await server.CallAsync("list_rooms", new { });
        JsonElement result = Structured(response, "bag-rooms/1");

        JsonElement room = Assert.Single(result.GetProperty("rooms").EnumerateArray());
        Assert.Equal("claude", room.GetProperty("provider").GetString());
        Assert.Equal("visible-room", room.GetProperty("room_id").GetString());
        Assert.Equal(2, room.GetProperty("message_count").GetInt32());
        JsonElement[] sources = room.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(new[] { firstSource, secondSource }.Order(),
            sources.Select(source => source.GetProperty("source_sha256").GetString()).Order());
        Assert.Equal(new[] { "First visible name", "Second visible name" },
            sources.Select(source => source.GetProperty("name").GetString()).Order());
        Assert.All(sources, source =>
        {
            Assert.Equal(JsonValueKind.String, source.GetProperty("created_at").ValueKind);
            Assert.Equal(JsonValueKind.String, source.GetProperty("timestamp_utc").ValueKind);
            Assert.EndsWith("+09:00", source.GetProperty("timestamp_jst").GetString());
        });
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", response.GetRawText());
        Assert.DoesNotContain("sealed-room", response.GetRawText());
        Assert.DoesNotContain("all-messages-sealed", response.GetRawText());
    }

    [Fact]
    public async Task RunningServerReloadsSealsAndNeverReturnsSealedHitsOrContext()
    {
        archive.Import(Room("room", "SYNTHETIC_PRIVATE_ROOM_NAME",
            Message("SYNTHETIC_PRIVATE_PARENT_ID", "SYNTHETIC_PRIVATE_PARENT_BODY", createdAt: "2026-01-01T00:00:00Z"),
            Message("SYNTHETIC_PRIVATE_HIT_ID", "needle", "SYNTHETIC_PRIVATE_PARENT_ID", "2026-01-01T01:00:00Z"),
            Message("child", "child body", "SYNTHETIC_PRIVATE_HIT_ID", "2026-01-01T02:00:00Z")));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        JsonElement initial = Structured(await server.CallAsync("find", new { text = "needle", context = 2 }), "bag-find/1");
        Assert.Equal(3, initial.GetProperty("messages").GetArrayLength());

        Seal("room", "SYNTHETIC_PRIVATE_PARENT_ID");
        JsonElement parentHiddenResponse = await server.CallAsync("find", new { text = "needle", context = 2 });
        JsonElement parentHidden = Structured(parentHiddenResponse, "bag-find/1");
        Assert.Equal(2, parentHidden.GetProperty("messages").GetArrayLength());
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_PARENT", parentHiddenResponse.GetRawText());
        JsonElement roomsAfterParent = Structured(await server.CallAsync("list_rooms", new { }), "bag-rooms/1");
        Assert.Equal(2, Assert.Single(roomsAfterParent.GetProperty("rooms").EnumerateArray())
            .GetProperty("message_count").GetInt32());

        Seal("room", "SYNTHETIC_PRIVATE_HIT_ID");
        JsonElement hitHiddenResponse = await server.CallAsync("find", new { text = "needle", context = 2 });
        Assert.Empty(Structured(hitHiddenResponse, "bag-find/1").GetProperty("messages").EnumerateArray());
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_HIT_ID", hitHiddenResponse.GetRawText());

        Seal("room");
        JsonElement allHidden = await server.CallAsync("find", new { all_branches = true, context = 2 });
        Assert.Empty(Structured(allHidden, "bag-find/1").GetProperty("messages").EnumerateArray());
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", allHidden.GetRawText());
        JsonElement hiddenRooms = await server.CallAsync("list_rooms", new { });
        Assert.Empty(Structured(hiddenRooms, "bag-rooms/1").GetProperty("rooms").EnumerateArray());
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", hiddenRooms.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerReadsNeverChangeTheDatabaseOrSidecar(bool hasSeals)
    {
        archive.Import(Room("room", "Synthetic room",
            Message("visible", "needle"), Message("hidden", "hidden", "visible", "2026-01-01T01:00:00Z")));
        if (hasSeals)
        {
            Seal("room", "hidden");
        }
        string sealsPath = Path.Combine(archive.DirectoryPath, "seals.json");
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);
        byte[]? sealsBefore = hasSeals ? File.ReadAllBytes(sealsPath) : null;
        string[] filesBefore = Directory.GetFiles(archive.DirectoryPath).Order().ToArray();

        await using (McpProcess server = await McpProcess.StartAsync(archive.DatabasePath))
        {
            Structured(await server.CallAsync("find", new { context = 2 }), "bag-find/1");
            Structured(await server.CallAsync("list_rooms", new { }), "bag-rooms/1");
        }

        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
        Assert.Equal(filesBefore, Directory.GetFiles(archive.DirectoryPath).Order().ToArray());
        if (hasSeals)
        {
            Assert.Equal(sealsBefore, File.ReadAllBytes(sealsPath));
        }
        else
        {
            Assert.False(File.Exists(sealsPath));
        }
    }

    [Fact]
    public async Task ToolArgumentsCannotOverrideSealsOrDatabaseAndErrorsDoNotEchoInput()
    {
        archive.Import(Room("room", "SYNTHETIC_PRIVATE_ROOM", Message("message", "SYNTHETIC_PRIVATE_BODY")));
        Seal("room");
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        object[] invalidArguments =
        [
            new { include_sealed = true },
            new { include_sealed = false },
            new { db = "SYNTHETIC_PRIVATE_DATABASE_PATH" },
            new { unexpected = "SYNTHETIC_PRIVATE_ARGUMENT" },
            new { text = (string?)null },
            new { from = "SYNTHETIC_PRIVATE_INVALID_TIME" },
            new { from = "2026-01-02", to = "2026-01-01" },
            new { speaker = "SYNTHETIC_PRIVATE_SPEAKER" },
            new { limit = 0 },
            new { limit = 1.5 },
            new { context = -1 },
            new { all_branches = "true" }
        ];

        foreach (object arguments in invalidArguments)
        {
            AssertToolError(await server.CallAsync("find", arguments), "Invalid tool arguments.");
        }
        AssertToolError(await server.CallAsync("list_rooms", new { include_sealed = true }), "Invalid tool arguments.");
        AssertToolError(await server.CallAsync("list_rooms", new { limit = 1 }), "Invalid tool arguments.");
        Assert.Empty(Structured(await server.CallAsync("find", new { }), "bag-find/1")
            .GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task CorruptSealsFailClosedForBothToolsWhileTheServerKeepsRunning()
    {
        archive.Import(Room("room", "SYNTHETIC_PRIVATE_ROOM", Message("message", "SYNTHETIC_PRIVATE_BODY")));
        await using McpProcess server = await McpProcess.StartAsync(archive.DatabasePath);
        Structured(await server.CallAsync("find", new { }), "bag-find/1");
        File.WriteAllText(Path.Combine(archive.DirectoryPath, "seals.json"), "{SYNTHETIC_PRIVATE_BROKEN_SEALS");

        AssertToolError(await server.CallAsync("find", new { }), "Invalid seal file.");
        AssertToolError(await server.CallAsync("list_rooms", new { }), "Invalid seal file.");
    }

    [Fact]
    public async Task ConfiguredSpeakerLabelsControlTheMcpEnumAndReloadOnlyAfterRestart()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("human-message", "human body"),
            Message("assistant-message", "assistant body", "human-message", "2026-01-01T01:00:00Z", "assistant"),
            Message("other-message", "other body", "assistant-message", "2026-01-01T02:00:00Z", "moderator")));
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);
        string configuration = Path.Combine(archive.DirectoryPath, "bag.config.json");
        File.WriteAllText(configuration,
            """{"speaker_names":{"human":"Visitor","assistant":"Guide","moderator":"Observer"}}""");

        await using (McpProcess server = await McpProcess.StartAsync(archive.DatabasePath))
        {
            Assert.Equal(new[] { "Guide", "Observer", "Visitor" }, await SpeakerLabelsAsync(server));
            JsonElement first = Structured(await server.CallAsync("find", new { speaker = "Visitor" }), "bag-find/1");
            JsonElement human = Assert.Single(first.GetProperty("messages").EnumerateArray());
            Assert.Equal("human-message", human.GetProperty("message_id").GetString());
            Assert.Equal("Visitor", human.GetProperty("speaker").GetString());
            JsonElement extra = Structured(await server.CallAsync("find", new { speaker = "Observer" }), "bag-find/1");
            Assert.Equal("other-message", Assert.Single(extra.GetProperty("messages").EnumerateArray())
                .GetProperty("message_id").GetString());
            AssertToolError(await server.CallAsync("find", new { speaker = "human" }), "Invalid tool arguments.");

            File.WriteAllText(configuration,
                """{"speaker_names":{"human":"Guest","assistant":"Helper","moderator":"Narrator"}}""");

            Assert.Equal(new[] { "Guide", "Observer", "Visitor" }, await SpeakerLabelsAsync(server));
            JsonElement unchanged = Structured(await server.CallAsync("find", new { speaker = "Visitor" }), "bag-find/1");
            Assert.Equal("Visitor", Assert.Single(unchanged.GetProperty("messages").EnumerateArray())
                .GetProperty("speaker").GetString());
            AssertToolError(await server.CallAsync("find", new { speaker = "Guest" }), "Invalid tool arguments.");
        }

        await using (McpProcess restarted = await McpProcess.StartAsync(archive.DatabasePath))
        {
            Assert.Equal(new[] { "Guest", "Helper", "Narrator" }, await SpeakerLabelsAsync(restarted));
            JsonElement current = Structured(await restarted.CallAsync("find", new { speaker = "Guest" }), "bag-find/1");
            JsonElement human = Assert.Single(current.GetProperty("messages").EnumerateArray());
            Assert.Equal("human-message", human.GetProperty("message_id").GetString());
            Assert.Equal("Guest", human.GetProperty("speaker").GetString());
            AssertToolError(await restarted.CallAsync("find", new { speaker = "Visitor" }), "Invalid tool arguments.");
        }

        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public async Task InvalidSpeakerConfigurationStopsBeforeTheMcpProtocolStarts()
    {
        archive.Import(Room("room", "SYNTHETIC_PRIVATE_ROOM", Message("message", "SYNTHETIC_PRIVATE_BODY")));
        File.WriteAllText(Path.Combine(archive.DirectoryPath, "bag.config.json"), "{SYNTHETIC_PRIVATE_INVALID_CONFIGURATION");
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bag.dll"));
        start.ArgumentList.Add("mcp");
        start.ArgumentList.Add("--db");
        start.ArgumentList.Add(archive.DatabasePath);
        using Process process = Process.Start(start)!;
        try
        {
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(2, process.ExitCode);
            Assert.Equal("", await output);
            Assert.Equal("Invalid configuration." + Environment.NewLine, await error);
            Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    private static async Task<string[]> SpeakerLabelsAsync(McpProcess server)
    {
        JsonElement response = await server.RequestAsync("tools/list", new { });
        JsonElement find = Assert.Single(response.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "find");
        return find.GetProperty("inputSchema").GetProperty("properties").GetProperty("speaker")
            .GetProperty("enum").EnumerateArray().Select(label => label.GetString()!).Order().ToArray();
    }

    private void Seal(string roomId, string? messageId = null)
    {
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", roomId, messageId), true);
    }

    private static JsonElement Structured(JsonElement response, string schema)
    {
        Assert.False(response.TryGetProperty("error", out _));
        JsonElement result = response.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out JsonElement error) && error.GetBoolean());
        JsonElement content = result.GetProperty("structuredContent");
        Assert.Equal(schema, content.GetProperty("schema").GetString());
        return content;
    }

    private static void AssertToolError(JsonElement response, string expected)
    {
        Assert.False(response.TryGetProperty("error", out _));
        JsonElement result = response.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.False(result.TryGetProperty("structuredContent", out JsonElement content) && content.ValueKind != JsonValueKind.Null);
        Assert.Equal(expected, Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", response.GetRawText());
    }

    private sealed class McpProcess : IAsyncDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private readonly Process process;
        private readonly string databasePath;
        private readonly Task<string> standardError;
        private int nextId;
        private bool disposed;

        private McpProcess(Process process, string databasePath)
        {
            this.process = process;
            this.databasePath = databasePath;
            standardError = process.StandardError.ReadToEndAsync();
        }

        public static async Task<McpProcess> StartAsync(string databasePath)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bag.dll"));
            start.ArgumentList.Add("mcp");
            start.ArgumentList.Add("--db");
            start.ArgumentList.Add(databasePath);
            var server = new McpProcess(Process.Start(start)!, databasePath);
            try
            {
                JsonElement initialized = await server.RequestAsync("initialize", new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "bag-synthetic-tests", version = "1.0" }
                });
                Assert.Equal("2025-11-25", initialized.GetProperty("result").GetProperty("protocolVersion").GetString());
                await server.WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public Task<JsonElement> CallAsync(string name, object arguments)
        {
            return RequestAsync("tools/call", new { name, arguments });
        }

        public async Task<JsonElement> RequestAsync(string method, object parameters)
        {
            int id = ++nextId;
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters });
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(Timeout);
                Assert.NotNull(line);
                JsonElement response = ParseProtocolLine(line);
                if (response.TryGetProperty("id", out JsonElement responseId)
                    && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
                {
                    return response;
                }
                Assert.True(response.TryGetProperty("method", out _), "Unexpected MCP response identifier.");
            }
        }

        private async Task WriteAsync(object message)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)).WaitAsync(Timeout);
            await process.StandardInput.FlushAsync().WaitAsync(Timeout);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            try
            {
                process.StandardInput.Close();
                Task<string> remainingOutput = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(Timeout);
                string remaining = await remainingOutput.WaitAsync(Timeout);
                using var lines = new StringReader(remaining);
                while (lines.ReadLine() is string line)
                {
                    ParseProtocolLine(line);
                }
                Assert.Equal(0, process.ExitCode);
                string error = await standardError.WaitAsync(Timeout);
                Assert.DoesNotContain("SYNTHETIC_PRIVATE", error);
                Assert.DoesNotContain(databasePath, error);
                Assert.DoesNotContain(Path.GetDirectoryName(databasePath)!, error);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(Timeout);
                }
                process.Dispose();
            }
        }

        private static JsonElement ParseProtocolLine(string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
            return document.RootElement.Clone();
        }
    }
}
