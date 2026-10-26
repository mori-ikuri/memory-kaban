using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class SpeakerNamesTests
{
    private readonly SyntheticArchive archive = new();
    private string ConfigurationPath => Path.Combine(archive.DirectoryPath, "bag.config.json");

    [Fact]
    public void MissingConfigurationUsesGenericDefaultsWithoutCreatingFiles()
    {
        SpeakerNames names = SpeakerNames.Read(archive.DatabasePath);

        Assert.Equal("user", names.GetDisplayName("human"));
        Assert.Equal("assistant", names.GetDisplayName("assistant"));
        Assert.Equal(new[] { "assistant", "user" }, names.Labels.Order());
        Assert.True(names.TryResolve("user", out string? human));
        Assert.Equal("human", human);
        Assert.True(names.TryResolve("assistant", out string? assistant));
        Assert.Equal("assistant", assistant);
        Assert.False(names.TryResolve("human", out _));
        Assert.Equal("moderator", names.GetDisplayName("moderator"));
        Assert.False(names.TryResolve("moderator", out _));
        Assert.Equal("user", SpeakerNames.Default.GetDisplayName("human"));
        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
    }

    [Fact]
    public void PartialOverridesMergeWithDefaultsAndCanAddRawRoles()
    {
        WriteConfiguration("""
            {"speaker_names":{"human":"Visitor","moderator":"Guide"}}
            """);

        SpeakerNames names = SpeakerNames.Read(archive.DatabasePath);

        Assert.Equal("Visitor", names.GetDisplayName("human"));
        Assert.Equal("assistant", names.GetDisplayName("assistant"));
        Assert.Equal("Guide", names.GetDisplayName("moderator"));
        Assert.Equal(new[] { "assistant", "Guide", "Visitor" }.Order(), names.Labels.Order());
        Assert.True(names.TryResolve("Guide", out string? role));
        Assert.Equal("moderator", role);
        Assert.False(names.TryResolve("user", out _));
    }

    [Fact]
    public void EmptyOverrideObjectKeepsDefaults()
    {
        WriteConfiguration("""{"speaker_names":{}}""");

        SpeakerNames names = SpeakerNames.Read(archive.DatabasePath);

        Assert.Equal("user", names.GetDisplayName("human"));
        Assert.Equal("assistant", names.GetDisplayName("assistant"));
    }

    [Fact]
    public void LabelsAreNotTrimmedAndUseOrdinalMatching()
    {
        WriteConfiguration("""{"speaker_names":{"human":" Visitor ","assistant":"visitor"}}""");

        SpeakerNames names = SpeakerNames.Read(archive.DatabasePath);

        Assert.Equal(" Visitor ", names.GetDisplayName("human"));
        Assert.True(names.TryResolve(" Visitor ", out string? role));
        Assert.Equal("human", role);
        Assert.False(names.TryResolve("Visitor", out _));
        Assert.False(names.TryResolve("VISITOR", out _));
        Assert.True(names.TryResolve("visitor", out role));
        Assert.Equal("assistant", role);
    }

    [Fact]
    public void Utf8BomAndUnicodeLabelsAreAccepted()
    {
        const string configuration = """{"speaker_names":{"human":"来訪者","assistant":"案内役"}}""";
        File.WriteAllText(ConfigurationPath, configuration, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        SpeakerNames names = SpeakerNames.Read(archive.DatabasePath);

        Assert.Equal("来訪者", names.GetDisplayName("human"));
        Assert.Equal("案内役", names.GetDisplayName("assistant"));
        Assert.True(names.TryResolve("案内役", out string? role));
        Assert.Equal("assistant", role);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"speaker_names\":null}")]
    [InlineData("{\"speaker_names\":[]}")]
    [InlineData("{\"speaker_names\":{},\"unknown\":\"SYNTHETIC_PRIVATE_VALUE\"}")]
    [InlineData("{\"speaker_names\":{},\"speaker_names\":{}}")]
    [InlineData("{\"speaker_names\":{\"human\":\"Visitor\",\"human\":\"Guide\"}}")]
    [InlineData("{\"speaker_names\":{\"human\":null}}")]
    [InlineData("{\"speaker_names\":{\"human\":42}}")]
    [InlineData("{\"speaker_names\":{\"human\":\"\"}}")]
    [InlineData("{\"speaker_names\":{\"human\":\"   \"}}")]
    [InlineData("{\"speaker_names\":{\"human\":\"Visitor\\nGuide\"}}")]
    [InlineData("{\"speaker_names\":{\"human\":\"Visitor\\tGuide\"}}")]
    [InlineData("{\"speaker_names\":{\"human\":\"Visitor\\u0000Guide\"}}")]
    [InlineData("{\"speaker_names\":{\"human\":\"Guide\",\"assistant\":\"Guide\"}}")]
    [InlineData("{\"speaker_names\":{\"assistant\":\"user\"}}")]
    [InlineData("{\"speaker_names\":{\"moderator\":\"assistant\"}}")]
    [InlineData("{\"speaker_names\":{}} trailing")]
    public void InvalidConfigurationFailsWithAFixedMessageAndIsNotChanged(string json)
    {
        WriteConfiguration(json);
        byte[] before = File.ReadAllBytes(ConfigurationPath);

        Exception? failure = Record.Exception(() => SpeakerNames.Read(archive.DatabasePath));

        Assert.NotNull(failure);
        Assert.True(BagErrors.IsHandled(failure));
        Assert.Equal("Invalid configuration.", BagErrors.GetMessage(failure));
        Assert.Equal(before, File.ReadAllBytes(ConfigurationPath));
        Assert.False(File.Exists(archive.DatabasePath));
    }

    [Fact]
    public void InvalidUtf8ConfigurationFailsClosed()
    {
        byte[] prefix = Encoding.UTF8.GetBytes("{\"speaker_names\":{\"human\":\"");
        byte[] suffix = Encoding.UTF8.GetBytes("\"}}");
        byte[] bytes = [.. prefix, 0xFF, .. suffix];
        File.WriteAllBytes(ConfigurationPath, bytes);

        Exception? failure = Record.Exception(() => SpeakerNames.Read(archive.DatabasePath));

        Assert.NotNull(failure);
        Assert.Equal("Invalid configuration.", BagErrors.GetMessage(failure));
        Assert.Equal(bytes, File.ReadAllBytes(ConfigurationPath));
    }

    [Fact]
    public void ImportAndFindKeepRawRolesWhileOutputUsesDisplayNames()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("human-message", "human body", createdAt: "2026-01-01T00:00:00Z"),
            Message("assistant-message", "assistant body", "human-message", "2026-01-01T01:00:00Z", "assistant"),
            Message("other-message", "other body", "assistant-message", "2026-01-01T02:00:00Z", "moderator")));
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);
        WriteConfiguration("""{"speaker_names":{"human":"Visitor","assistant":"Guide"}}""");
        SpeakerNames names = SpeakerNames.Read(archive.DatabasePath);
        var request = new FindRequest(null, null, null, null, null, false, 0, 20);

        FindResult result = new FindService().Find(archive.DatabasePath, request);

        Assert.Equal(new[] { "human", "assistant", "moderator" }, result.Messages.Select(message => message.Speaker));
        using (SqliteConnection connection = archive.Open(SqliteOpenMode.ReadOnly))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT speaker FROM message_versions ORDER BY created_at";
            using SqliteDataReader reader = command.ExecuteReader();
            var roles = new List<string>();
            while (reader.Read())
            {
                roles.Add(reader.GetString(0));
            }
            Assert.Equal(new[] { "human", "assistant", "moderator" }, roles);
        }
        JsonElement defaultOutput = FindOutput.ToJsonElement(result);
        Assert.Equal(new[] { "user", "assistant", "moderator" },
            defaultOutput.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("speaker").GetString()));
        JsonElement configuredOutput = FindOutput.ToJsonElement(result, names);
        Assert.Equal(new[] { "Visitor", "Guide", "moderator" },
            configuredOutput.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("speaker").GetString()));
        using var humanOutput = new StringWriter();
        FindOutput.Write(result, false, humanOutput, names);
        Assert.Contains("Visitor (match)", humanOutput.ToString());
        Assert.Contains("Guide (match)", humanOutput.ToString());
        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public void CliReloadsDisplayNamesEachTimeWithoutChangingTheDatabase()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("human-message", "human body"),
            Message("assistant-message", "assistant body", "human-message", "2026-01-01T01:00:00Z", "assistant")));
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);
        WriteConfiguration("""{"speaker_names":{"human":"Visitor","assistant":"Guide"}}""");

        AssertSpeakerResult(RunFind("--speaker", "Visitor", "--json"), "human-message", "Visitor");
        AssertInvalidSpeaker(RunFind("--speaker", "user", "--json"));

        WriteConfiguration("""{"speaker_names":{"human":"Guest","assistant":"Helper"}}""");

        AssertSpeakerResult(RunFind("--speaker", "Guest", "--json"), "human-message", "Guest");
        AssertSpeakerResult(RunFind("--speaker", "Helper", "--json"), "assistant-message", "Helper");
        AssertInvalidSpeaker(RunFind("--speaker", "Visitor", "--json"));
        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public void ConfiguredExtraRawRoleCanBeFilteredByItsDisplayName()
    {
        archive.Import(Room("room", "Synthetic room", Message("other", "body", speaker: "moderator")));
        WriteConfiguration("""{"speaker_names":{"moderator":"Guide"}}""");

        AssertSpeakerResult(RunFind("--speaker", "Guide", "--json"), "other", "Guide");
        AssertInvalidSpeaker(RunFind("--speaker", "moderator", "--json"));
    }

    [Fact]
    public void InvalidConfigurationStopsCliBeforeAnyConversationOutput()
    {
        archive.Import(Room("room", "SYNTHETIC_PRIVATE_ROOM", Message("message", "SYNTHETIC_PRIVATE_BODY")));
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);
        WriteConfiguration("{SYNTHETIC_PRIVATE_INVALID_CONFIGURATION");

        CommandResult result = RunFind("--json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal("Invalid configuration." + Environment.NewLine, result.Error);
        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public void HelpDoesNotReadConfigurationOrOpenDatabase()
    {
        WriteConfiguration("{SYNTHETIC_PRIVATE_INVALID_CONFIGURATION");

        CommandResult result = RunFind("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--speaker", result.Output);
        Assert.Equal("", result.Error);
        Assert.False(File.Exists(archive.DatabasePath));
    }

    [Fact]
    public void RebuildingAnOldSchemaAtANewPathRetainsTheSharedSeals()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("hidden", "hidden body"),
            Message("visible", "visible body", "hidden", "2026-01-01T01:00:00Z", "assistant")));
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "room", "hidden"), true);
        archive.Execute("UPDATE schema_info SET version=1");
        byte[] oldDatabase = File.ReadAllBytes(archive.DatabasePath);
        string sealsPath = Path.Combine(archive.DirectoryPath, "seals.json");
        byte[] seals = File.ReadAllBytes(sealsPath);
        string zip = Assert.Single(Directory.GetFiles(archive.DirectoryPath, "*.zip"));

        Exception? oldImport = Record.Exception(() => new ImportService(new ClaudeExportReader()).Import(zip, archive.DatabasePath));
        Assert.NotNull(oldImport);
        Assert.Equal("Incompatible database.", BagErrors.GetMessage(oldImport));
        string rebuiltPath = Path.Combine(archive.DirectoryPath, "rebuilt.db");
        new ImportService(new ClaudeExportReader()).Import(zip, rebuiltPath);
        var request = new FindRequest(null, null, null, null, null, false, 0, 20);
        FindResult rebuilt = new FindService().Find(rebuiltPath, request);

        Assert.Equal("visible", Assert.Single(rebuilt.Messages).Key.MessageId);
        Assert.Equal("assistant", rebuilt.Messages[0].Speaker);
        Assert.Equal(2, new FindService().Find(rebuiltPath, request, includeSealed: true).Messages.Count);
        Assert.True(new SealStore(rebuiltPath).Read().IsSealed("claude", "room", "hidden"));
        Assert.Equal(oldDatabase, File.ReadAllBytes(archive.DatabasePath));
        Assert.Equal(seals, File.ReadAllBytes(sealsPath));
    }

    private void WriteConfiguration(string json)
    {
        File.WriteAllText(ConfigurationPath, json, new UTF8Encoding(false));
    }

    private CommandResult RunFind(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = BagCommand.Run(["find", "--db", archive.DatabasePath, .. arguments], output, error);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private static void AssertSpeakerResult(CommandResult result, string messageId, string displayName)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement message = Assert.Single(document.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal(messageId, message.GetProperty("message_id").GetString());
        Assert.Equal(displayName, message.GetProperty("speaker").GetString());
    }

    private static void AssertInvalidSpeaker(CommandResult result)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal("Invalid arguments. Use --help." + Environment.NewLine, result.Error);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
