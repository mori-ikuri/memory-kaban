using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bag.Tests;

public sealed class ChatGptDepthTests
{
    private readonly SyntheticArchive archive = new();

    [Theory]
    [InlineData(85)]
    [InlineData(128)]
    public void ChatGptRetainsDeepAuxiliaryMetadataWithinItsBound(int depth)
    {
        string json = ChatGptJson(depth);
        string zip = Zip(("conversations.json", json));
        byte[] original = File.ReadAllBytes(zip);

        Assert.Equal(depth, MaximumDepth(json));
        Assert.Equal(new ImportCounts(1, 1, 1, 0),
            new ImportService(new ChatGptExportReader()).Import(zip, archive.DatabasePath));
        using JsonDocument node = JsonDocument.Parse(
            Scalar<string>("SELECT payload_json FROM room_auxiliary WHERE kind='node:node'"),
            new JsonDocumentOptions { MaxDepth = 128 });
        Assert.Equal(NestedValue(depth - 4), node.RootElement.GetProperty("future").GetRawText());
        Assert.Equal("SYNTHETIC_BODY", Scalar<string>("SELECT body FROM message_versions"));
        Assert.Equal(original, File.ReadAllBytes(zip));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChatGptDepthBeyond128InALaterPartRollsBackWithFixedDiagnostics(bool existing)
    {
        if (existing)
        {
            new ImportService(new ChatGptExportReader()).Import(
                Zip(("conversations.json", ChatGptJson(8, "existing"))), archive.DatabasePath);
        }
        byte[]? before = existing ? File.ReadAllBytes(archive.DatabasePath) : null;
        string invalid = ChatGptJson(129, "too-deep");
        string zip = Zip(("conversations-000.json", ChatGptJson(8, "first-new")),
            ("conversations-001.json", invalid));
        byte[] original = File.ReadAllBytes(zip);
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(129, MaximumDepth(invalid));
        Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal("Unsupported export format." + Environment.NewLine, error.ToString());
        Assert.Equal(original, File.ReadAllBytes(zip));
        if (existing)
        {
            Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
            Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
            Assert.Equal("existing", Scalar<string>("SELECT room_id FROM rooms"));
        }
        else
        {
            Assert.Equal(0, Scalar<long>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
        }
    }

    [Fact]
    public void ClaudeRetainsItsExisting64LevelLimit()
    {
        string accepted = ClaudeJson(64);
        string rejected = ClaudeJson(65);
        Assert.Equal(64, MaximumDepth(accepted));
        Assert.Equal(65, MaximumDepth(rejected));
        Assert.Equal(new ImportCounts(1, 1, 1, 0), new ImportService(new ClaudeExportReader()).Import(
            Zip(("conversations.json", accepted)), archive.DatabasePath));
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(2, ImportCommand.Run(["import", Zip(("conversations.json", rejected)), "--db", archive.DatabasePath], output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal("Unsupported export format." + Environment.NewLine, error.ToString());
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
    }

    private static string ChatGptJson(int depth, string room = "room") =>
        """
        [{"id":"__ROOM__","current_node":"node","mapping":{"node":{"id":"node","parent":null,"message":{"id":"message","author":{"role":"user"},"create_time":1,"content":{"content_type":"text","parts":["SYNTHETIC_BODY"]}},"future":__DEEP__}}}]
        """.Replace("__ROOM__", room).Replace("__DEEP__", NestedValue(depth - 4));

    private static string ClaudeJson(int depth) =>
        """
        [{"uuid":"room","name":"Synthetic room","chat_messages":[{"uuid":"message","sender":"human","parent_message_uuid":null,"created_at":"2026-01-01T00:00:00Z","content":[{"type":"text","text":"SYNTHETIC_BODY"}],"future":__DEEP__}]}]
        """.Replace("__DEEP__", NestedValue(depth - 4));

    private static string NestedValue(int levels) => new string('[', levels) + "\"SYNTHETIC_METADATA\"" + new string(']', levels);

    private static int MaximumDepth(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 256 });
        int maximum = 0;
        while (reader.Read())
        {
            maximum = Math.Max(maximum, reader.CurrentDepth
                + (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject ? 1 : 0));
        }
        return maximum;
    }

    private string Zip(params (string Name, string Json)[] parts)
    {
        string path = Path.Combine(archive.DirectoryPath, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach ((string name, string json) in parts)
        {
            using Stream entry = zip.CreateEntry(name).Open();
            entry.Write(Encoding.UTF8.GetBytes(json));
        }
        return path;
    }

    private T Scalar<T>(string sql)
    {
        using SqliteConnection connection = archive.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }
}
