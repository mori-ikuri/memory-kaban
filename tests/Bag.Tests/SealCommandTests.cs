using System.Text.Json;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class SealCommandTests
{
    private readonly SyntheticArchive archive = new();

    [Fact]
    public void RoomCommandsChangeOnlyTheChosenRoomAndNeverTheDatabase()
    {
        ImportTwoRooms();
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);

        AssertChanged("seal", 1, "--room", "room-a");
        Assert.Equal("room-b", Assert.Single(Find().Messages).Key.RoomId);
        Assert.True(File.Exists(SealsPath));
        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));

        AssertChanged("unseal", 1, "--room", "room-a");
        Assert.Equal(2, Find().Messages.Count);
        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public void AUniqueMessageIdCanBeSealedWithoutARoomArgument()
    {
        archive.Import(
            Room("room-a", "SYNTHETIC_ROOM_A", Message("unique-a", "SYNTHETIC_BODY_A")),
            Room("room-b", "SYNTHETIC_ROOM_B", Message("unique-b", "SYNTHETIC_BODY_B")));

        AssertChanged("seal", 1, "--message", "unique-a");

        Assert.Equal("unique-b", Assert.Single(Find().Messages).Key.MessageId);
    }

    [Fact]
    public void DuplicateMessageIdsRequireARoomAndDoNotWriteAnySeal()
    {
        ImportTwoRooms();
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);

        AssertFailure("seal", "Seal target is ambiguous. Specify a room ID.", "--message", "shared-message");

        Assert.Equal(2, Find().Messages.Count);
        Assert.False(File.Exists(SealsPath));
        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public void RoomAndMessageTogetherDisambiguateWithoutSealingTheOtherRoom()
    {
        ImportTwoRooms();

        AssertChanged("seal", 1, "--room", "room-a", "--message", "shared-message");

        FindMessage visible = Assert.Single(Find().Messages);
        Assert.Equal("room-b", visible.Key.RoomId);
        Assert.Equal("shared-message", visible.Key.MessageId);
    }

    [Fact]
    public void RepeatingSealAndUnsealReportsOnlyTheActualChangeCount()
    {
        ImportTwoRooms();

        AssertChanged("seal", 1, "--room", "room-a");
        AssertChanged("seal", 0, "--room", "room-a");
        AssertChanged("unseal", 1, "--room", "room-a");
        AssertChanged("unseal", 0, "--room", "room-a");
    }

    [Fact]
    public void UnsealRoomRetainsAnIndividualMessageSeal()
    {
        archive.Import(Room("room", "SYNTHETIC_ROOM",
            Message("individual", "SYNTHETIC_BODY_A"),
            Message("other", "SYNTHETIC_BODY_B", "individual", "2026-01-01T01:00:00Z")));
        AssertChanged("seal", 1, "--room", "room", "--message", "individual");
        AssertChanged("seal", 1, "--room", "room");
        Assert.Empty(Find().Messages);

        AssertChanged("unseal", 1, "--room", "room");

        Assert.Equal("other", Assert.Single(Find().Messages).Key.MessageId);
    }

    [Theory]
    [InlineData("--room")]
    [InlineData("--message")]
    public void MissingSealTargetsFailWithAFixedMessage(string option)
    {
        ImportTwoRooms();

        AssertFailure("seal", "Seal target not found.", option, "SYNTHETIC_MISSING_TARGET");

        Assert.False(File.Exists(SealsPath));
        Assert.Equal(2, Find().Messages.Count);
    }

    [Fact]
    public void UnsealCanRemoveAStoredRoomTargetThatIsAbsentFromTheDatabase()
    {
        ImportTwoRooms();
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "absent-room", null), true);
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);

        AssertChanged("unseal", 1, "--room", "absent-room");
        AssertChanged("unseal", 0, "--room", "absent-room");

        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
        Assert.Equal(2, Find().Messages.Count);
    }

    [Fact]
    public void MessageOnlyUnsealUsesTheUniqueSealedTargetBeforeTheAmbiguousDatabase()
    {
        ImportTwoRooms();
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "room-a", "shared-message"), true);

        AssertChanged("unseal", 1, "--message", "shared-message");

        Assert.Equal(2, Find().Messages.Count);
    }

    [Fact]
    public void MessageOnlyUnsealRejectsSeveralSealedTargetsWithoutChangingThem()
    {
        ImportTwoRooms();
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "room-a", "shared-message"), true);
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "room-b", "shared-message"), true);
        byte[] sealsBefore = File.ReadAllBytes(SealsPath);

        AssertFailure("unseal", "Seal target is ambiguous. Specify a room ID.", "--message", "shared-message");

        Assert.Equal(sealsBefore, File.ReadAllBytes(SealsPath));
        Assert.Empty(Find().Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindCliEmitsNoBodyWhenSealsAreCorrupt(bool includeSealed)
    {
        ImportTwoRooms();
        File.WriteAllText(SealsPath, "{broken-synthetic-seals");
        string[] arguments = includeSealed ? ["--include-sealed", "--json"] : ["--json"];

        CommandResult result = Run("find", arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.NotEmpty(result.Error);
        AssertNoPrivateValues(result.Error);
    }

    private string SealsPath => Path.Combine(archive.DirectoryPath, "seals.json");

    private void ImportTwoRooms()
    {
        archive.Import(
            Room("room-a", "SYNTHETIC_ROOM_A", Message("shared-message", "SYNTHETIC_BODY_A")),
            Room("room-b", "SYNTHETIC_ROOM_B", Message("shared-message", "SYNTHETIC_BODY_B")));
    }

    private FindResult Find()
    {
        return new FindService().Find(archive.DatabasePath,
            new FindRequest(null, null, null, null, null, false, 0, 20));
    }

    private void AssertChanged(string command, int expected, params string[] arguments)
    {
        CommandResult result = Run(command, arguments);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal(expected, document.RootElement.GetProperty("changed").GetInt32());
        AssertNoPrivateValues(result.Output);
    }

    private void AssertFailure(string command, string expected, params string[] arguments)
    {
        CommandResult result = Run(command, arguments);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal(expected + Environment.NewLine, result.Error);
        AssertNoPrivateValues(result.Error);
    }

    private CommandResult Run(string command, params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = BagCommand.Run([command, "--db", archive.DatabasePath, .. arguments], output, error);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private void AssertNoPrivateValues(string text)
    {
        Assert.DoesNotContain("SYNTHETIC", text);
        Assert.DoesNotContain("shared-message", text);
        Assert.DoesNotContain("room-a", text);
        Assert.DoesNotContain("room-b", text);
        Assert.DoesNotContain(archive.DirectoryPath, text);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
