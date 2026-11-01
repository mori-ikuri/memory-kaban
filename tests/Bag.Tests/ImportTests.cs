using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bag.Tests;

public sealed class ImportTests
{
    private readonly string directory = Path.Combine(Environment.GetEnvironmentVariable("BAG_TEST_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "bag-tests"),
        Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "archive.db");

    public ImportTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void PreservesCharactersBlocksEmptyMessagesAndMetadata()
    {
        string original = "  日本語🙂e\u0301\r\n次の行\n'\"\t\u0000末尾  ";
        Dictionary<string, object?> first = Message("m1", original);
        first["content"] = new object[] { Text(original), Text(""), Text("末尾") };
        first["flag"] = new { hidden = true };
        first["attachments"] = new[] { new { name = "架空.txt", content = "架空添付\n内容" } };
        var second = Message("m2", "", "m1");
        second["sender"] = "assistant";
        second["content"] = new object[]
        {
            new { type = "thinking", thinking = "架空の考え", is_truncated = true, is_hidden = true },
            new { type = "injected_prompt_block", text = "架空の注入" },
            new { type = "future_kind", payload = new[] { "未定義", "種類" } }
        };
        var third = Message("m3", "", "m2");
        third.Remove("content");
        Dictionary<string, object?> room = Room(first, second, third);
        room["flag"] = new { future_room_flag = "保持" };
        string zip = Zip(room);
        byte[] before = SHA256.HashData(File.ReadAllBytes(zip));

        ImportCounts counts = Import(zip);

        Assert.Equal(3, counts.Messages);
        Assert.Equal(original + "\n\n末尾", Scalar<string>("SELECT body FROM message_versions WHERE message_id='m1'"));
        Assert.Equal("", Scalar<string>("SELECT body FROM message_versions WHERE message_id='m2'"));
        Assert.Equal("", Scalar<string>("SELECT body FROM message_versions WHERE message_id='m3'"));
        Assert.Equal("human", Scalar<string>("SELECT speaker FROM message_versions WHERE message_id='m1'"));
        Assert.Equal("assistant", Scalar<string>("SELECT speaker FROM message_versions WHERE message_id='m2'"));
        Assert.Equal("m1", Scalar<string>("SELECT parent_id FROM message_versions WHERE message_id='m2'"));
        Assert.Equal("架空の部屋", Scalar<string>("SELECT name FROM room_snapshots"));
        Assert.Equal("claude", Scalar<string>("SELECT provider FROM rooms"));
        Assert.Contains("is_truncated", Scalar<string>("SELECT payload_json FROM auxiliary_blocks WHERE kind='content:thinking'"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM auxiliary_blocks WHERE kind='content:future_kind'"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM auxiliary_blocks WHERE kind='message:attachments'"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM room_auxiliary WHERE kind='room:flag'"));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM message_versions WHERE instr(body,'DISPLAY_TEMPLATE_DECOY')>0"));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(zip)));
        Assert.Equal(new FileInfo(zip).Length, Scalar<long>("SELECT size_bytes FROM sources"));
        Assert.Equal(Convert.ToHexString(before), Scalar<string>("SELECT sha256 FROM sources"));
        Assert.Equal(Path.GetFileName(zip), Scalar<string>("SELECT file_name FROM sources"));
        Assert.Equal(ImportService.Version, Scalar<string>("SELECT importer_version FROM sources"));
    }

    [Fact]
    public void KeepsOnlyToolNamesIncludingForwardReferencesAndNestedBlocks()
    {
        var message = Message("tools", "");
        message["content"] = new object[]
        {
            new { type = "tool_result", tool_use_id = "call1", content = "SECRET_RESULT", extra = "SECRET_EXTRA" },
            new { type = "future_kind", nested = new { type = "tool_use", id = "call1", name = "synthetic_tool", input = new { token = "SECRET_ARGUMENT" } } },
            new { type = "tool_use", id = "call2", name = "second_tool", arguments = "SECRET_ARGUMENT_2" },
            new { type = "tool_result", tool_use_id = "missing", content = "SECRET_UNRESOLVED" }
        };
        Import(Zip(Room(message)));

        string[] payloads = Column("SELECT payload_json FROM auxiliary_blocks ORDER BY block_index");
        Assert.Contains("synthetic_tool", payloads[0]);
        Assert.Contains("synthetic_tool", payloads[1]);
        Assert.Contains("second_tool", payloads[2]);
        Assert.Contains("\"name\":null", payloads[3]);
        Assert.DoesNotContain("SECRET", string.Join("", payloads));
        Assert.DoesNotContain("call1", string.Join("", payloads));
        Assert.DoesNotContain("SECRET", Encoding.UTF8.GetString(File.ReadAllBytes(Database)));
    }

    [Fact]
    public void SelectsLatestLeafAndRetainsOtherBranches()
    {
        string zip = Zip(Room(Message("root", "root"), Message("older", "older", "root", "2026-01-01T01:00:00Z"),
            Message("newer", "newer", "root", "2026-01-01T11:00:00+09:00")));
        Import(zip);
        Assert.Equal(new[] { "newer", "root" }, Column("SELECT message_id FROM source_messages WHERE is_mainline=1 ORDER BY message_id"));
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Equal("inferred-latest-created-at", Scalar<string>("SELECT mainline_reason FROM room_snapshots"));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-4000-8000-000000000000")]
    public void RootMarkerImportsPreserveParentBranchesAndVersions(string parent)
    {
        // There is deliberately no current_leaf_message_uuid field in this fixture.
        string zip = Zip(Room(Message("newer", "新しい枝", "root", "2026-01-02T00:00:00Z"),
            Message("root", "根", parent), Message("older", "古い枝", "root", "2026-01-01T00:00:00Z")));
        byte[] before = SHA256.HashData(File.ReadAllBytes(zip));

        ImportCounts first = Import(zip);
        Assert.Equal(1, first.Rooms);
        Assert.Equal(3, first.Messages);
        Assert.Equal(3, first.NewVersions);
        Assert.Equal(0, first.UnresolvedRooms);
        Assert.Equal(parent, Scalar<string>("SELECT parent_id FROM message_versions WHERE message_id='root'"));
        Assert.Equal(new[] { "newer", "root" }, Column("SELECT message_id FROM source_messages WHERE is_mainline=1 ORDER BY message_id"));
        Assert.Equal("newer", Scalar<string>("SELECT mainline_leaf FROM room_snapshots"));
        Assert.Equal("inferred-latest-created-at", Scalar<string>("SELECT mainline_reason FROM room_snapshots"));
        Assert.Equal(MainlineSelector.RuleVersion, Scalar<string>("SELECT rule_version FROM room_snapshots"));

        ImportCounts repeated = Import(zip);
        Assert.Equal(0, repeated.NewVersions);
        Assert.Equal(0, repeated.UnresolvedRooms);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM message_versions"));
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM source_messages"));
        Assert.Equal(parent, Scalar<string>("SELECT parent_id FROM message_versions WHERE message_id='root'"));
        Assert.Equal(new[] { "newer", "root" }, Column("SELECT message_id FROM source_messages WHERE is_mainline=1 ORDER BY message_id"));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(zip)));
    }

    [Fact]
    public void UsesExplicitLeafOverLatestTimestamp()
    {
        Dictionary<string, object?> room = Room(Message("root", ""), Message("old", "", "root", "2026-01-01T00:00:00Z"),
            Message("new", "", "root", "2026-02-01T00:00:00Z"));
        room["current_leaf_message_uuid"] = "old";
        Import(Zip(room));
        Assert.Equal("old", Scalar<string>("SELECT mainline_leaf FROM room_snapshots"));
        Assert.Equal("explicit", Scalar<string>("SELECT mainline_reason FROM room_snapshots"));
    }

    [Fact]
    public void RepeatedZipAndEquivalentMessagesDoNotGrowVersions()
    {
        Dictionary<string, object?> room = Room(Message("m", "保存"));
        string first = Zip(room);
        Assert.Equal(1, Import(first).NewVersions);
        string time = Scalar<string>("SELECT imported_at FROM sources");
        Assert.Equal(0, Import(first).NewVersions);
        Assert.Equal(time, Scalar<string>("SELECT imported_at FROM sources"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));

        // A different ZIP with the same message and reordered object properties.
        room["name"] = "別の名前";
        var msg = Message("m", "保存");
        room["chat_messages"] = new[] { msg.Reverse().ToDictionary(p => p.Key, p => p.Value) };
        Assert.Equal(0, Import(Zip(room)).NewVersions);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM message_versions"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM auxiliary_blocks"));
        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM source_messages"));
        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM room_snapshots"));
    }

    [Fact]
    public void KeepsChangedVersionsAndIndependentSourceGraphsAfterOldReimport()
    {
        string old = Zip(Room(Message("m", "旧版")));
        string newer = Zip(Room(Message("m", "新版"), Message("branch", "新しい枝", "m")));
        Import(old);
        Import(newer);
        Assert.Equal(0, Import(old).NewVersions);
        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM message_versions"));
        Assert.Equal(new[] { "旧版", "新版" }.Order(), Column("SELECT body FROM message_versions WHERE message_id='m'").Order());
        Assert.Equal(new[] { "branch", "m" }, Column("SELECT mainline_leaf FROM room_snapshots ORDER BY mainline_leaf"));
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("date")]
    [InlineData("metadata")]
    public void ChangesOutsideBodyCreateVersions(string change)
    {
        Dictionary<string, object?> message = Message("m", "本文");
        Import(Zip(Room(message)));
        if (change == "parent") message["parent_message_uuid"] = "absent";
        if (change == "date") message["created_at"] = "2026-02-01T00:00:00Z";
        if (change == "metadata") message["flag"] = new { hidden = true };
        Import(Zip(Room(message)));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM message_versions"));
    }

    [Fact]
    public void RecalculatesMainlineOnSameZip()
    {
        string zip = Zip(Room(Message("m", "本文")));
        Import(zip);
        using (SqliteConnection connection = Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE source_messages SET is_mainline=0; UPDATE room_snapshots SET mainline_reason='obsolete'";
            command.ExecuteNonQuery();
        }
        Assert.Equal(0, Import(zip).NewVersions);
        Assert.Equal(1, Scalar<long>("SELECT is_mainline FROM source_messages"));
        Assert.Equal("single-leaf", Scalar<string>("SELECT mainline_reason FROM room_snapshots"));
    }

    [Fact]
    public void FailedLaterMessageRollsBackAllNewData()
    {
        Import(Zip(Room(Message("known", "既存"))));
        string bad = Zip(Room(Message("new", "追加"), Message("new", "重複")));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(bad));
        Assert.Throws<InvalidDataException>(() => Import(bad));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
        Assert.Equal(new[] { "known" }, Column("SELECT message_id FROM messages"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM message_versions"));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(bad)));
    }

    [Theory]
    [InlineData("[] trailing")]
    [InlineData("[{\"uuid\":\"r\",\"chat_messages\":[")]
    [InlineData("[{\"uuid\":\"r\",\"uuid\":\"duplicate\",\"chat_messages\":[]}]")]
    [InlineData("[{\"uuid\":\"r\"}]")]
    [InlineData("{\"conversations\":[]}")]
    [InlineData("[{\"uuid\":\"r\",\"chat_messages\":[{\"uuid\":\"m\",\"sender\":\"human\",\"content\":42}]}]")]
    public void MalformedInputsFailWithoutLeakingInput(string json)
    {
        string zip = RawZip(Encoding.UTF8.GetBytes(json));
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", Database], output, error));
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain(json, error.ToString());
    }

    [Fact]
    public void RejectsInvalidUtf8EvenInIgnoredDisplayText()
    {
        byte[] prefix = Encoding.UTF8.GetBytes("[{\"uuid\":\"r\",\"chat_messages\":[{\"uuid\":\"m\",\"sender\":\"human\",\"text\":\"");
        byte[] suffix = Encoding.UTF8.GetBytes("\",\"content\":[]}]}]");
        string zip = RawZip([.. prefix, 0xFF, .. suffix]);
        Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", Database], TextWriter.Null, TextWriter.Null));
    }

    [Fact]
    public void RejectsMissingOrDuplicateEntriesAndDuplicateRooms()
    {
        foreach (string[] entries in new[] { new[] { "other.json" }, new[] { "conversations.json", "sub/conversations.json" } })
        {
            string zip = RawZip("[]"u8.ToArray(), entries);
            Assert.Throws<InvalidDataException>(() => Import(zip));
        }
        string duplicate = RawZip(JsonSerializer.SerializeToUtf8Bytes(new[] { Room(), Room() }));
        Assert.Throws<InvalidDataException>(() => Import(duplicate));
    }

    [Fact]
    public void SourceCannotBeDatabaseAndUnsupportedFormatsAreRejected()
    {
        string zip = Zip(Room(Message("m", "本文")));
        byte[] before = File.ReadAllBytes(zip);
        Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", zip], TextWriter.Null, TextWriter.Null));
        Assert.Equal(1, ImportCommand.Run(["import", zip, "--format", "unsupported-provider"], TextWriter.Null, TextWriter.Null));
        Assert.Equal(before, File.ReadAllBytes(zip));
    }

    [Fact]
    public void CliOutputsCountsOnlyAndRebuildMatches()
    {
        string zip = Zip(Room(Message("m", "SHOULD_NOT_PRINT")));
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, ImportCommand.Run(["import", zip, "--db", Database], output, error));
        Assert.DoesNotContain("SHOULD_NOT_PRINT", output.ToString());
        Assert.DoesNotContain("架空", output.ToString());
        Assert.Equal("", error.ToString());
        using JsonDocument counts = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, counts.RootElement.GetProperty("messages").GetInt32());
        string rebuilt = Path.Combine(directory, "rebuilt.db");
        new ImportService(new ClaudeExportReader()).Import(zip, rebuilt);
        Assert.Equal(Column("SELECT version_hash FROM message_versions"), Column("SELECT version_hash FROM message_versions", rebuilt));
        Assert.Equal(Column("SELECT mainline_reason FROM room_snapshots"), Column("SELECT mainline_reason FROM room_snapshots", rebuilt));
        Assert.Equal(2, Directory.GetFiles(directory, "*.db").Length);
        Assert.Empty(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public void RejectsUnrelatedDatabaseWithoutChangingIt()
    {
        using (SqliteConnection connection = Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('preserve')";
            command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(Database);
        Assert.Throws<InvalidDataException>(() => Import(Zip(Room(Message("m", "本文")))));
        Assert.Equal(before, File.ReadAllBytes(Database));
        Assert.Equal("preserve", Scalar<string>("SELECT value FROM unrelated"));
    }

    [Fact]
    public void FailedTransactionStartReleasesConnection()
    {
        string zip = Zip(Room(Message("m", "本文")));
        Import(zip);
        using (SqliteConnection connection = Open())
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", Database], TextWriter.Null, TextWriter.Null));
        }
        using (var exclusive = new FileStream(Database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(exclusive.Length > 0);
        }
        Assert.Equal(0, Import(zip).NewVersions);
    }

    [Fact]
    [Trait("Category", "Large")]
    public async Task StreamsOneLargeConversationInSeparateProcess()
    {
        string zip = Path.Combine(directory, "large-synthetic.zip");
        const int count = 5120;
        using (FileStream file = File.Create(zip))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (Stream entry = archive.CreateEntry("conversations.json", CompressionLevel.Fastest).Open())
        using (var writer = new Utf8JsonWriter(entry))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("uuid", "large");
            writer.WritePropertyName("chat_messages");
            writer.WriteStartArray();
            string body = new('x', 32768);
            for (int index = 0; index < count; index++)
            {
                JsonSerializer.Serialize(writer, Message("m" + index, body, index == 0 ? null : "m" + (index - 1)));
                writer.Flush();
            }
            writer.WriteEndArray();
            writer.WriteString("name", "Large synthetic room with header after messages");
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        long jsonBytes;
        using (ZipArchive archive = ZipFile.OpenRead(zip)) jsonBytes = archive.Entries[0].Length;
        Assert.True(jsonBytes > 150L * 1024 * 1024);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string app = Path.Combine(AppContext.BaseDirectory, "bag.dll");
        start.ArgumentList.Add(app);
        foreach (string argument in new[] { "import", zip, "--db", Database }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        long peak = 0;
        var stopwatch = Stopwatch.StartNew();
        while (!process.WaitForExit(50))
        {
            try
            {
                process.Refresh();
                peak = Math.Max(peak, process.PeakWorkingSet64);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // Exit can race the sampling after WaitForExit returned false.
                break;
            }
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(3), "Synthetic import exceeded three minutes.");
        }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await stderr);
        Assert.Contains("\"messages\":5120", await stdout);
        Assert.Equal(count, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Equal(count, Scalar<long>("SELECT count(*) FROM source_messages WHERE is_mainline=1"));
        // The whole decoded conversation alone would exceed this bound.
        Assert.True(peak > 0 && peak < 150L * 1024 * 1024, $"Peak working set was {peak} bytes.");
        File.WriteAllText(Path.Combine(directory, "metrics.json"), JsonSerializer.Serialize(new { json_bytes = jsonBytes, peak_working_set_bytes = peak, elapsed_seconds = stopwatch.Elapsed.TotalSeconds }));
    }

    private ImportCounts Import(string zip) => new ImportService(new ClaudeExportReader()).Import(zip, Database);
    private string Zip(Dictionary<string, object?> room) => RawZip(JsonSerializer.SerializeToUtf8Bytes(new[] { room }));

    private string RawZip(byte[] bytes, string[]? entries = null)
    {
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (string name in entries ?? ["conversations.json"])
        {
            using Stream stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private static Dictionary<string, object?> Room(params Dictionary<string, object?>[] messages) => new()
    {
        ["uuid"] = "room1", ["name"] = "架空の部屋", ["created_at"] = "2026-01-01T00:00:00Z", ["chat_messages"] = messages
    };

    private static Dictionary<string, object?> Message(string id, string body, string? parent = null, string? date = "2026-01-01T00:00:00Z") => new()
    {
        ["uuid"] = id, ["sender"] = "human", ["parent_message_uuid"] = parent, ["created_at"] = date,
        ["text"] = "DISPLAY_TEMPLATE_DECOY", ["content"] = new[] { Text(body) }
    };

    private static object Text(string value) => new { type = "text", text = value };

    private SqliteConnection Open(string? path = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? Database, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private T Scalar<T>(string sql)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private string[] Column(string sql, string? path = null)
    {
        using SqliteConnection connection = Open(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }
}
