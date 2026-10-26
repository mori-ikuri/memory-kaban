using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bag.Tests;

/// <summary>Creates only synthetic exports and databases outside the source tree.</summary>
internal sealed class SyntheticArchive
{
    public string DirectoryPath { get; } = Path.Combine(Environment.GetEnvironmentVariable("BAG_TEST_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "bag-tests"),
        Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(DirectoryPath, "synthetic.db");

    public SyntheticArchive()
    {
        Directory.CreateDirectory(DirectoryPath);
    }

    public string Import(params Dictionary<string, object?>[] rooms)
    {
        string zip = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".zip");
        using (FileStream file = File.Create(zip))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (Stream stream = archive.CreateEntry("conversations.json").Open())
        {
            JsonSerializer.Serialize(stream, rooms);
        }
        new ImportService(new ClaudeExportReader()).Import(zip, DatabasePath);
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
    }

    public static Dictionary<string, object?> Room(string id, string name, params Dictionary<string, object?>[] messages)
    {
        return new Dictionary<string, object?>
        {
            ["uuid"] = id,
            ["name"] = name,
            ["created_at"] = "2026-01-01T00:00:00Z",
            ["chat_messages"] = messages
        };
    }

    public static Dictionary<string, object?> Message(string id, string body, string? parent = null,
        string? createdAt = "2026-01-01T00:00:00Z", string speaker = "human")
    {
        return new Dictionary<string, object?>
        {
            ["uuid"] = id,
            ["sender"] = speaker,
            ["parent_message_uuid"] = parent,
            ["created_at"] = createdAt,
            ["text"] = "SYNTHETIC_DISPLAY_ONLY",
            ["content"] = new object[] { new { type = "text", text = body } }
        };
    }

    public SqliteConnection Open(SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Execute(string sql)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
