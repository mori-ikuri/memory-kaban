using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bag.Tests;

public sealed class ChatGptParentOnlyTests
{
    private readonly SyntheticArchive archive = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingChildrenPreserveBranchesContextAndOriginalNodes(bool mixed)
    {
        Dictionary<string, object?> question = Node("question", "user", "question", "stub");
        if (mixed) question["children"] = new[] { "main", "side" };
        Dictionary<string, object?> room = Room("room", "tail",
            Node("stub", null, ""), question,
            Node("main", "assistant", "selected", "question"),
            Node("side", "assistant", "branch hit", "question"),
            Node("hidden", "system", "SYNTHETIC_HIDDEN", "main"),
            Node("tail", "user", "after hidden", "hidden"));
        string zip = Zip(("conversations.json", new[] { room }));
        byte[] source = File.ReadAllBytes(zip);

        Assert.Equal(new ImportCounts(1, 4, 4, 0), Import(zip));
        Assert.Equal(new[] { "main", "question", "tail" },
            Column("SELECT message_id FROM source_messages WHERE is_mainline=1 ORDER BY message_id"));
        Assert.Equal(new[] { "side" },
            Column("SELECT message_id FROM source_messages WHERE is_mainline=0 ORDER BY message_id"));
        Assert.Equal("main", Scalar<string>("SELECT parent_id FROM message_versions WHERE message_id='tail'"));
        Assert.Equal(new[] { "main", "tail" },
            Find("after hidden", context: 1).Messages.Select(message => message.Key.MessageId).Order());
        Assert.Equal(new[] { "question", "side" },
            Find("branch hit", allBranches: true, context: 1).Messages.Select(message => message.Key.MessageId).Order());
        string[] nodes = Column("SELECT payload_json FROM room_auxiliary WHERE kind LIKE 'node:%'");
        Assert.Equal(6, nodes.Length);
        foreach (string node in nodes)
        {
            using JsonDocument document = JsonDocument.Parse(node);
            JsonElement original = document.RootElement;
            bool expectedChildren = mixed && original.GetProperty("id").GetString() == "question";
            Assert.Equal(expectedChildren, original.TryGetProperty("children", out _));
        }
        Assert.Equal(new ImportCounts(1, 4, 0, 0), Import(zip));
        Assert.Equal(4, Scalar<long>("SELECT count(*) FROM message_versions"));
        Assert.Equal(source, File.ReadAllBytes(zip));
    }

    [Theory]
    [InlineData("missing-parent")]
    [InlineData("cycle")]
    [InlineData("missing-current-node")]
    [InlineData("invalid-current-node")]
    public void MissingChildrenDoNotRepairBrokenGraphsOrGuessSelection(string condition)
    {
        Dictionary<string, object?> first = Node("first", "user", "first");
        Dictionary<string, object?> second = Node("second", "assistant", "second", "first");
        if (condition == "missing-parent") second["parent"] = "absent";
        if (condition == "cycle") first["parent"] = "second";
        Dictionary<string, object?> room = Room("room", "second", first, second);
        if (condition == "missing-current-node") room.Remove("current_node");
        if (condition == "invalid-current-node") room["current_node"] = "absent";

        Assert.Equal(new ImportCounts(1, 2, 2, 1), Import(Zip(("conversations.json", new[] { room }))));
        Assert.Equal(condition, Scalar<string>("SELECT mainline_reason FROM room_snapshots"));
        Assert.Empty(Find().Messages);
        Assert.Equal(2, Find(allBranches: true).Messages.Count);
    }

    [Theory]
    [InlineData("[]", "inconsistent-children")]
    [InlineData("[\"second\",\"second\"]", "inconsistent-children")]
    [InlineData("[\"absent\"]", "missing-child")]
    public void ExplicitChildrenRemainAuthoritativeEvenWhenOtherNodesOmitThem(string children, string reason)
    {
        Dictionary<string, object?> first = Node("first", "user", "first");
        first["children"] = JsonSerializer.Deserialize<JsonElement>(children);
        Dictionary<string, object?> room = Room("room", "second", first,
            Node("second", "assistant", "second", "first"));

        Assert.Equal(new ImportCounts(1, 2, 2, 1), Import(Zip(("conversations.json", new[] { room }))));
        Assert.Equal(reason, Scalar<string>("SELECT mainline_reason FROM room_snapshots"));
        Assert.Empty(Find().Messages);
        Assert.Equal(2, Find(allBranches: true).Messages.Count);
        using JsonDocument original = JsonDocument.Parse(Scalar<string>(
            "SELECT payload_json FROM room_auxiliary WHERE kind='node:first'"));
        Assert.Equal(children, original.RootElement.GetProperty("children").GetRawText());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    [InlineData("[\"\"]")]
    public void ExplicitInvalidChildrenFailAtomicallyWithoutReinterpretingThemAsMissing(string children)
    {
        Dictionary<string, object?> existing = Node("known", "user", "existing");
        existing["children"] = Array.Empty<string>();
        Import(Zip(("conversations.json", new[] { Room("existing", "known", existing) })));
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        Dictionary<string, object?> invalid = Node("invalid", "assistant", "SYNTHETIC_PRIVATE_BODY");
        invalid["children"] = JsonSerializer.Deserialize<JsonElement>(children);
        string zip = Zip(("conversations.json", new[] { Room("new", "invalid", invalid) }));
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal("Unsupported export format." + Environment.NewLine, error.ToString());
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public void ParentOnlyRoomsAcrossNumberedPartsRemainOneSourceAndReimportWithoutNewVersions()
    {
        string zip = Zip(
            ("conversations-001.json", new[] { Room("second", "reply", Node("reply", "assistant", "second")) }),
            ("conversations-000.json", new[] { Room("first", "question", Node("question", "user", "first")) }));
        byte[] source = File.ReadAllBytes(zip);

        Assert.Equal(new ImportCounts(2, 2, 2, 0), Import(zip));
        Assert.Equal(new[] { "first", "second" }, Column("SELECT room_id FROM message_versions ORDER BY version_id"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM source_messages WHERE is_mainline=1"));
        Assert.Equal(new ImportCounts(2, 2, 0, 0), Import(zip));
        Assert.Equal(source, File.ReadAllBytes(zip));
    }

    private ImportCounts Import(string zip) => new ImportService(new ChatGptExportReader()).Import(zip, archive.DatabasePath);

    private FindResult Find(string? text = null, bool allBranches = false, int context = 0) =>
        new FindService().Find(archive.DatabasePath, new FindRequest(null, null, null, null, text, allBranches, context, 50));

    private string Zip(params (string Name, Dictionary<string, object?>[] Rooms)[] parts)
    {
        string path = Path.Combine(archive.DirectoryPath, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach ((string name, Dictionary<string, object?>[] rooms) in parts)
        {
            using Stream entry = zip.CreateEntry(name).Open();
            JsonSerializer.Serialize(entry, rooms);
        }
        return path;
    }

    private static Dictionary<string, object?> Room(string id, string current, params Dictionary<string, object?>[] nodes) => new()
    {
        ["id"] = id, ["title"] = "Synthetic ChatGPT room", ["create_time"] = 0m, ["current_node"] = current,
        ["mapping"] = nodes.ToDictionary(node => (string)node["id"]!, node => node, StringComparer.Ordinal)
    };

    private static Dictionary<string, object?> Node(string id, string? role, string body, string? parent = null) => new()
    {
        ["id"] = id, ["parent"] = parent,
        ["message"] = role is null ? null : new
        {
            id, author = new { role }, create_time = 1m,
            content = new { content_type = "text", parts = new[] { body } }
        }
    };

    private T Scalar<T>(string sql)
    {
        using SqliteConnection connection = archive.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private string[] Column(string sql)
    {
        using SqliteConnection connection = archive.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }
}
