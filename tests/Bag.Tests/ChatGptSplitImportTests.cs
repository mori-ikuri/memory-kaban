using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bag.Tests;

public sealed class ChatGptSplitImportTests
{
    private readonly SyntheticArchive archive = new();

    [Fact]
    public void ImportsAllPartsInNumericOrderAsOneImmutableSourceAndReimportsWithoutNewVersions()
    {
        (string Name, byte[] Bytes)[] entries = Enumerable.Range(0, 12).Reverse()
            .Select(index => ($"nested/conversations-{index}.json", JsonRooms(Room($"room-{index:D2}", $"body-{index:D2}"))))
            .ToArray();
        string zip = Zip(entries);
        byte[] before = File.ReadAllBytes(zip);
        string[] files = Directory.GetFiles(archive.DirectoryPath).Order().ToArray();

        ImportCounts counts = Import(zip);

        Assert.Equal(12, counts.Rooms);
        Assert.Equal(12, counts.Messages);
        Assert.Equal(12, counts.NewVersions);
        Assert.Equal(0, counts.UnresolvedRooms);
        Assert.Equal(Enumerable.Range(0, 12).Select(index => $"room-{index:D2}"),
            Column("SELECT room_id FROM message_versions ORDER BY version_id"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
        Assert.Equal(1, Scalar<long>("SELECT count(DISTINCT source_sha) FROM source_messages"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(before)), Scalar<string>("SELECT sha256 FROM sources"));
        Assert.Equal(before.Length, Scalar<long>("SELECT size_bytes FROM sources"));
        Assert.Equal(Path.GetFileName(zip), Scalar<string>("SELECT file_name FROM sources"));
        Assert.Equal(before, File.ReadAllBytes(zip));
        Assert.Equal(files.Append(archive.DatabasePath).Order(), Directory.GetFiles(archive.DirectoryPath).Order());
        Assert.Equal(0, Import(zip).NewVersions);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));

        string secondZip = Zip(entries.Reverse().ToArray());
        Assert.Equal(0, Import(secondZip).NewVersions);
        Assert.Equal(12, Scalar<long>("SELECT count(*) FROM message_versions"));
        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM sources"));
        Assert.Equal(24, Scalar<long>("SELECT count(*) FROM source_messages"));
        string rebuilt = Path.Combine(archive.DirectoryPath, "rebuilt.db");
        new ImportService(new ChatGptExportReader()).Import(secondZip, rebuilt);
        Assert.Equal(Column("SELECT version_hash FROM message_versions ORDER BY version_hash"),
            Column("SELECT version_hash FROM message_versions ORDER BY version_hash", rebuilt));
        Assert.Empty(Directory.GetFiles(archive.DirectoryPath, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void EmptyPartsAndUnrelatedEntriesDoNotSkipSubsequentRooms()
    {
        string zip = Zip(
            ("conversations-002.json", JsonRooms(Room("last", "last body"))),
            ("user.json", "not conversation JSON"u8.ToArray()),
            ("conversations-000.json", JsonRooms(Room("first", "first body"))),
            ("conversations-001.json", "[]"u8.ToArray()),
            ("attachments/private.dat", [0xFF, 0xFE]));

        ImportCounts counts = Import(zip);

        Assert.Equal(2, counts.Rooms);
        Assert.Equal(new[] { "first", "last" }, Column("SELECT room_id FROM message_versions ORDER BY version_id"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
    }

    [Fact]
    public void OneNumberedPartAndEntirelyEmptyPartSetsAreSupported()
    {
        string first = Zip(("conversations-000.json", JsonRooms(Room("only", "body"))));
        Assert.Equal(1, Import(first).Messages);
        string empty = Zip(("conversations-000.json", "[]"u8.ToArray()), ("conversations-001.json", "[]"u8.ToArray()));
        Assert.Equal(new ImportCounts(0, 0, 0, 0), Import(empty));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM messages"));
        Assert.Equal(2, Scalar<long>("SELECT count(*) FROM sources"));
    }

    [Theory]
    [InlineData("conversations-001.json")]
    [InlineData("conversations-000.json", "conversations-002.json")]
    [InlineData("conversations-0.json", "conversations-000.json")]
    [InlineData("conversations-000.json", "nested/conversations-000.json")]
    [InlineData("conversations-000.json", "conversations-000.json")]
    [InlineData("conversations-000.json", "nested/conversations-001.json")]
    [InlineData("conversations.json", "conversations-000.json")]
    [InlineData("nested/conversations.json", "conversations-000.json")]
    [InlineData("conversations-000.json", "conversations-bad.json")]
    [InlineData("conversations-000.json", "conversations-001.JSON")]
    [InlineData("conversations-000.json", "Conversations-001.json")]
    [InlineData("conversations-000.json", "conversations-.json")]
    [InlineData("conversations-000.json", "conversations--1.json")]
    [InlineData("conversations-000.json", "conversations-+1.json")]
    [InlineData("conversations-000.json", "conversations-１.json")]
    [InlineData("conversations-000.json", "conversations-2147483648.json")]
    [InlineData("conversations-000.json", "conversations-001.json.bak")]
    [InlineData("conversations.json", "conversations_001.json")]
    public void AmbiguousIncompleteOrMalformedPartNamesFailBeforeDatabaseCreation(params string[] names)
    {
        string zip = Zip(names.Select(name => (name, "[]"u8.ToArray())).ToArray());
        byte[] before = File.ReadAllBytes(zip);
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = ImportCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], output, error);

        Assert.Equal(2, exit);
        Assert.Equal("", output.ToString());
        Assert.Equal("Unsupported export format." + Environment.NewLine, error.ToString());
        Assert.False(File.Exists(archive.DatabasePath));
        Assert.Equal(before, File.ReadAllBytes(zip));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DuplicateRoomIdsAcrossPartsRejectIdenticalConflictingAndMetadataOnlyRooms(bool metadataOnly, bool conflict)
    {
        Import(Zip(("conversations.json", JsonRooms(Room("existing", "retained body")))));
        Dictionary<string, object?> first = Room("duplicate", "original", metadataOnly);
        Dictionary<string, object?> second = conflict ? Room("duplicate", "changed", metadataOnly) : first;
        string zip = Zip(("conversations-000.json", JsonRooms(first)), ("conversations-001.json", JsonRooms(second)));
        byte[] database = File.ReadAllBytes(archive.DatabasePath);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => Import(zip));

        Assert.Equal("Unsupported export format.", BagErrors.GetMessage(failure));
        Assert.Equal(database, File.ReadAllBytes(archive.DatabasePath));
        Assert.Equal(new[] { "existing" }, Column("SELECT room_id FROM rooms"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
    }

    [Theory]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    [InlineData(false, "utf8")]
    [InlineData(true, "utf8")]
    [InlineData(false, "shape")]
    [InlineData(true, "shape")]
    [InlineData(false, "trailing")]
    [InlineData(true, "trailing")]
    public void LateInvalidPartsRollBackTheEntireZipForNewAndExistingDatabases(bool existing, string kind)
    {
        if (existing)
        {
            Import(Zip(("conversations.json", JsonRooms(Room("existing", "retained body")))));
        }
        byte[]? before = existing ? File.ReadAllBytes(archive.DatabasePath) : null;
        byte[] invalid = kind switch
        {
            "utf8" => [.. "[{\"id\":\"bad\",\"mapping\":{},\"future\":\""u8.ToArray(), 0xFF, .. "\"}]"u8.ToArray()],
            "shape" => "[{\"id\":\"bad\",\"mapping\":[]}]"u8.ToArray(),
            "trailing" => "[] []"u8.ToArray(),
            _ => "[{\"id\":\"SYNTHETIC_PRIVATE_BROKEN"u8.ToArray()
        };
        string zip = Zip(("conversations-000.json", JsonRooms(Room("first-new", "new body"))),
            ("conversations-001.json", JsonRooms(Room("second-new", "other new body"))),
            ("conversations-002.json", invalid));
        byte[] source = File.ReadAllBytes(zip);
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(2, ImportCommand.Run(["import", zip, "--db", archive.DatabasePath, "--format", "chatgpt"], output, error));

        Assert.Equal("", output.ToString());
        Assert.Equal("Unsupported export format." + Environment.NewLine, error.ToString());
        Assert.Equal(source, File.ReadAllBytes(zip));
        if (existing)
        {
            Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
            Assert.Equal(new[] { "existing" }, Column("SELECT room_id FROM rooms"));
            Assert.Equal(1, Scalar<long>("SELECT count(*) FROM sources"));
        }
        else
        {
            Assert.Equal(0, Scalar<long>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
        }
        Assert.Empty(Directory.GetFiles(archive.DirectoryPath, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void ClaudeStillIgnoresNumberedNamesAndRequiresExactlyOneLegacyEntry()
    {
        var claude = new ImportService(new ClaudeExportReader());
        string splitOnly = Zip(("conversations-000.json", "[]"u8.ToArray()));
        InvalidDataException missing = Assert.Throws<InvalidDataException>(() => claude.Import(splitOnly, archive.DatabasePath));
        Assert.Equal("conversations.json not found.", BagErrors.GetMessage(missing));
        Assert.False(File.Exists(archive.DatabasePath));
        byte[] room = JsonSerializer.SerializeToUtf8Bytes(new[] { SyntheticArchive.Room("claude", "room",
            SyntheticArchive.Message("message", "body")) });
        string singleAndIgnored = Zip(("conversations.json", room), ("conversations-000.json", [0xFF]));
        Assert.Equal(1, claude.Import(singleAndIgnored, archive.DatabasePath).Messages);
        string duplicated = Zip(("conversations.json", room), ("nested/conversations.json", room));
        InvalidDataException multiple = Assert.Throws<InvalidDataException>(() => claude.Import(duplicated, archive.DatabasePath));
        Assert.Equal("Multiple conversations.json entries.", BagErrors.GetMessage(multiple));
    }

    [Fact]
    public void ChatGptLegacyDuplicateAndMissingFileDiagnosticsRemainUnchanged()
    {
        string duplicate = Zip(("conversations.json", "[]"u8.ToArray()), ("nested/conversations.json", "[]"u8.ToArray()));
        Assert.Equal("Multiple conversations.json entries.", BagErrors.GetMessage(Assert.Throws<InvalidDataException>(() => Import(duplicate))));
        string missing = Zip(("user.json", "{}"u8.ToArray()));
        Assert.Equal("conversations.json not found.", BagErrors.GetMessage(Assert.Throws<InvalidDataException>(() => Import(missing))));
        Assert.False(File.Exists(archive.DatabasePath));
    }

    private ImportCounts Import(string zip) => new ImportService(new ChatGptExportReader()).Import(zip, archive.DatabasePath);

    private string Zip(params (string Name, byte[] Bytes)[] entries)
    {
        string path = Path.Combine(archive.DirectoryPath, Guid.NewGuid().ToString("N") + ".zip");
        using FileStream file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach ((string name, byte[] bytes) in entries)
        {
            using Stream entry = zip.CreateEntry(name).Open();
            entry.Write(bytes);
        }
        return path;
    }

    private static byte[] JsonRooms(params Dictionary<string, object?>[] rooms) => JsonSerializer.SerializeToUtf8Bytes(rooms);

    private static Dictionary<string, object?> Room(string id, string body, bool metadataOnly = false)
    {
        var message = new
        {
            id = "export-message", author = new { role = metadataOnly ? "system" : "assistant" }, create_time = 1m,
            content = new { content_type = "text", parts = new[] { body } }, recipient = "all", channel = (string?)null
        };
        return new Dictionary<string, object?>
        {
            ["id"] = id, ["title"] = id, ["create_time"] = 0m, ["current_node"] = "node",
            ["mapping"] = new Dictionary<string, object?>
            {
                ["node"] = new { id = "node", parent = (string?)null, children = Array.Empty<string>(), message }
            }
        };
    }

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
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    }
}
