using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Bag.Tests;

public sealed class ErrorTests
{
    private readonly string directory = Path.Combine(Environment.GetEnvironmentVariable("BAG_TEST_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "bag-tests"),
        Guid.NewGuid().ToString("N"));

    private string Database => Path.Combine(directory, "synthetic-private-database.db");

    public ErrorTests()
    {
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void MissingZipHasItsOwnMessage()
    {
        AssertFailure(Path.Combine(directory, "missing-private-export.zip"), "ZIP not found.");
    }

    [Fact]
    public void NonZipHasItsOwnMessage()
    {
        string path = Path.Combine(directory, "not-a-zip.zip");
        File.WriteAllText(path, "SYNTHETIC_PRIVATE_CONTENT");
        AssertFailure(path, "Not a valid ZIP.");
    }

    [Fact]
    public void MissingConversationsHasItsOwnMessage()
    {
        AssertFailure(Zip("[]", "unrelated.json"), "conversations.json not found.");
    }

    [Fact]
    public void MultipleConversationsHasItsOwnMessage()
    {
        AssertFailure(Zip("[]", "conversations.json", "nested/conversations.json"),
            "Multiple conversations.json entries.");
    }

    [Theory]
    [InlineData("\"SYNTHETIC_PRIVATE_CONTENT\"")]
    [InlineData("[{\"uuid\":\"SYNTHETIC_PRIVATE_CONTENT\"}]")]
    [InlineData("[{\"uuid\":\"room\",\"chat_messages\":[")]
    public void UnsupportedExportHasItsOwnMessage(string json)
    {
        AssertFailure(Zip(json), "Unsupported export format.");
    }

    [Fact]
    public void InvalidUtf8IsAnExportFailure()
    {
        string zip = ZipBytes([0xFF, 0xFE]);
        AssertFailure(zip, "Unsupported export format.");
    }

    [Fact]
    public void InvalidUtf8InsideAJsonStringIsAnExportFailure()
    {
        byte[] prefix = Encoding.UTF8.GetBytes("[{\"uuid\":\"room\",\"name\":\"");
        byte[] suffix = Encoding.UTF8.GetBytes("\",\"chat_messages\":[]}]");
        string zip = ZipBytes([.. prefix, 0xFF, .. suffix]);
        AssertFailure(zip, "Unsupported export format.");
    }

    [Fact]
    public void UnrelatedDatabaseHasItsOwnMessageAndIsUnchanged()
    {
        Execute("CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('synthetic')");
        byte[] before = File.ReadAllBytes(Database);
        AssertFailure(Zip("[]"), "Incompatible database.");
        Assert.Equal(before, File.ReadAllBytes(Database));
    }

    [Fact]
    public void NonSqliteDatabaseHasItsOwnMessageAndIsUnchanged()
    {
        File.WriteAllText(Database, "SYNTHETIC_PRIVATE_CONTENT");
        byte[] before = File.ReadAllBytes(Database);
        AssertFailure(Zip("[]"), "Incompatible database.");
        Assert.Equal(before, File.ReadAllBytes(Database));
    }

    [Theory]
    [InlineData("PRAGMA application_id=42")]
    [InlineData("UPDATE schema_info SET version=1")]
    [InlineData("INSERT INTO schema_info VALUES(1)")]
    [InlineData("ALTER TABLE sources RENAME COLUMN importer_version TO unsupported_column")]
    [InlineData("ALTER TABLE source_messages RENAME TO unsupported_table")]
    public void IncompatibleSchemaIsRejectedWithoutRepair(string mutation)
    {
        string zip = Zip("[]");
        Import(zip);
        Execute(mutation);
        byte[] before = File.ReadAllBytes(Database);
        AssertFailure(zip, "Incompatible database.");
        Assert.Equal(before, File.ReadAllBytes(Database));
    }

    [Fact]
    public void DifferentImporterVersionForSameSourceIsRejected()
    {
        string zip = Zip("[]");
        Import(zip);
        Execute("UPDATE sources SET importer_version='synthetic-old-version'");
        byte[] before = File.ReadAllBytes(Database);
        AssertFailure(zip, "Incompatible database.");
        Assert.Equal(before, File.ReadAllBytes(Database));
    }

    [Fact]
    public void BusyDatabaseHasItsOwnMessageAndReleasesItsConnection()
    {
        string zip = Zip("[]");
        Import(zip);
        using (SqliteConnection connection = Open())
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            AssertFailure(zip, "Database is in use.");
        }
        using var exclusive = new FileStream(Database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public void DatabaseAccessFailureIsNotReportedAsMissingZip()
    {
        AssertFailure(Zip("[]"), "File access failed.", Path.Combine(directory, "absent", "archive.db"));
    }

    [Fact]
    public void LockedSourceIsAFileAccessFailure()
    {
        string zip = Zip("[]");
        using var locked = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.None);
        AssertFailure(zip, "File access failed.");
    }

    [Fact]
    public void SourceDestinationConflictHasItsOwnMessage()
    {
        string zip = Zip("[]");
        byte[] before = File.ReadAllBytes(zip);
        AssertFailure(zip, "Source and database must differ.", zip);
        Assert.Equal(before, File.ReadAllBytes(zip));
    }

    [Fact]
    public void CompatibilityCheckCanUseAReadOnlyConnectionWithoutChangingDatabase()
    {
        Import(Zip("[]"));
        byte[] before = SHA256.HashData(File.ReadAllBytes(Database));
        using (SqliteConnection connection = Open(SqliteOpenMode.ReadOnly))
        {
            Assert.False(DatabaseCompatibility.Verify(connection));
        }
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(Database)));
    }

    [Fact]
    public void EmptyDatabaseIsAcceptedOnlyForInitialization()
    {
        using SqliteConnection connection = Open();
        Assert.True(DatabaseCompatibility.Verify(connection, allowEmpty: true));
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => DatabaseCompatibility.Verify(connection));
        Assert.Equal("Incompatible database.", BagErrors.GetMessage(exception));
    }

    private void AssertFailure(string zip, string expected, string? database = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = ImportCommand.Run(["import", zip, "--db", database ?? Database], output, error);
        Assert.Equal(2, exitCode);
        Assert.Equal("", output.ToString());
        Assert.Equal(expected + Environment.NewLine, error.ToString());
        Assert.DoesNotContain(directory, error.ToString());
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_CONTENT", error.ToString());
    }

    private void Import(string zip)
    {
        new ImportService(new ClaudeExportReader()).Import(zip, Database);
    }

    private string Zip(string json, params string[] entries)
    {
        return ZipBytes(Encoding.UTF8.GetBytes(json), entries);
    }

    private string ZipBytes(byte[] bytes, params string[] entries)
    {
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (string name in entries.Length == 0 ? ["conversations.json"] : entries)
        {
            using Stream stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private SqliteConnection Open(SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Database,
            Mode = mode,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private void Execute(string sql)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
