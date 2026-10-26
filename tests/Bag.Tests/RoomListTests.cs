using System.Text.Json;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class RoomListTests
{
    private readonly SyntheticArchive archive = new();

    [Fact]
    public void CountsDistinctLogicalMessagesAcrossVersionsAndBranches()
    {
        Dictionary<string, object?> first = Room("room", "First name",
            Message("root", "root"), Message("main", "old main", "root"), Message("side", "side", "root"));
        first["current_leaf_message_uuid"] = "main";
        archive.Import(first);
        Dictionary<string, object?> second = Room("room", "Second name",
            Message("root", "root"), Message("main", "new main", "root"), Message("side", "side", "root"));
        second["current_leaf_message_uuid"] = "side";
        archive.Import(second);

        RoomListItem room = Assert.Single(List().Rooms);

        Assert.Equal("claude", room.Provider);
        Assert.Equal("room", room.RoomId);
        Assert.Equal(3, room.MessageCount);
        Assert.Equal(2, room.Sources.Count);
    }

    [Fact]
    public void SealedRoomAndItsNameNeverReachTheOutput()
    {
        archive.Import(
            Room("sealed", "SYNTHETIC_SEALED_ROOM_NAME", Message("secret", "SYNTHETIC_SEALED_BODY")),
            Room("visible", "Visible synthetic room", Message("public", "visible body")));
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "sealed"), seal: true);

        RoomListResult result = List();
        JsonElement output = RoomListOutput.ToJsonElement(result);

        Assert.Equal("visible", Assert.Single(result.Rooms).RoomId);
        Assert.DoesNotContain("SYNTHETIC_SEALED", output.GetRawText());
        Assert.Equal("visible", Assert.Single(output.GetProperty("rooms").EnumerateArray()).GetProperty("room_id").GetString());
    }

    [Fact]
    public void SealedMessagesDoNotCountAndFullyHiddenSnapshotsAreOmitted()
    {
        string hiddenSource = archive.Import(Room("room", "SYNTHETIC_HIDDEN_SOURCE_NAME", Message("hidden", "hidden body")));
        string visibleSource = archive.Import(Room("room", "Visible source name", Message("visible", "visible body")));
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "room", "hidden"), seal: true);

        RoomListResult result = List();
        RoomListItem room = Assert.Single(result.Rooms);
        RoomListSnapshot source = Assert.Single(room.Sources);

        Assert.Equal(1, room.MessageCount);
        Assert.Equal(visibleSource, source.SourceSha256);
        Assert.NotEqual(hiddenSource, source.SourceSha256);
        Assert.Equal("Visible source name", source.Name);
        Assert.DoesNotContain("SYNTHETIC_HIDDEN", RoomListOutput.ToJsonElement(result).GetRawText());
    }

    [Fact]
    public void EmptyRoomsAndRoomsWithAllMessagesSealedAreOmitted()
    {
        archive.Import(
            Room("empty", "Empty synthetic room"),
            Room("hidden", "Hidden synthetic room", Message("message", "body")),
            Room("visible", "Visible synthetic room", Message("message", "body")));
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "hidden", "message"), seal: true);

        Assert.Equal("visible", Assert.Single(List().Rooms).RoomId);
    }

    [Fact]
    public void IdenticalMessageIdsInDifferentRoomsStayIndependent()
    {
        archive.Import(
            Room("first", "First synthetic room", Message("same-id", "first")),
            Room("second", "Second synthetic room", Message("same-id", "second")));
        Assert.Equal(2, List().Rooms.Count);
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "first", "same-id"), seal: true);

        RoomListItem room = Assert.Single(List().Rooms);

        Assert.Equal("second", room.RoomId);
        Assert.Equal(1, room.MessageCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-time")]
    [InlineData("2026-01-01T00:00:00")]
    public void OriginalUnknownRoomDatesAndNamesArePreserved(string? createdAt)
    {
        const string name = " \t架空の部屋\r\n\"引用\"🙂 ";
        Dictionary<string, object?> input = Room("room", name, Message("message", "body"));
        input["created_at"] = createdAt;
        string sourceHash = archive.Import(input);

        RoomListResult result = List();
        RoomListSnapshot source = Assert.Single(Assert.Single(result.Rooms).Sources);
        JsonElement sourceJson = Assert.Single(Assert.Single(RoomListOutput.ToJsonElement(result)
            .GetProperty("rooms").EnumerateArray()).GetProperty("sources").EnumerateArray());

        Assert.Equal(sourceHash, source.SourceSha256);
        Assert.Equal(name, source.Name);
        Assert.Equal(createdAt, source.CreatedAt);
        Assert.Equal(name, sourceJson.GetProperty("name").GetString());
        Assert.Equal(createdAt, sourceJson.GetProperty("created_at").GetString());
        Assert.Equal(JsonValueKind.Null, sourceJson.GetProperty("timestamp_utc").ValueKind);
        Assert.Equal(JsonValueKind.Null, sourceJson.GetProperty("timestamp_jst").ValueKind);
    }

    [Fact]
    public void JsonProvidesOriginalRoomDateAndExplicitUtcAndJapanTimes()
    {
        Dictionary<string, object?> input = Room("room", "Synthetic room", Message("message", "body"));
        input["created_at"] = "2026-01-01T01:02:03.1234567+01:00";
        archive.Import(input);

        JsonElement output = RoomListOutput.ToJsonElement(List());
        JsonElement source = Assert.Single(Assert.Single(output.GetProperty("rooms").EnumerateArray())
            .GetProperty("sources").EnumerateArray());

        Assert.Equal("bag-rooms/1", output.GetProperty("schema").GetString());
        Assert.Equal("2026-01-01T01:02:03.1234567+01:00", source.GetProperty("created_at").GetString());
        Assert.Equal("2026-01-01T00:02:03.1234567+00:00", source.GetProperty("timestamp_utc").GetString());
        Assert.Equal("2026-01-01T09:02:03.1234567+09:00", source.GetProperty("timestamp_jst").GetString());
    }

    [Fact]
    public void ReusedServiceReloadsSealsForEveryCall()
    {
        archive.Import(Room("room", "Synthetic room", Message("message", "body")));
        var service = new RoomListService();
        var seals = new SealStore(archive.DatabasePath);
        Assert.Single(service.List(archive.DatabasePath).Rooms);

        seals.Set(new SealTarget("claude", "room"), seal: true);
        Assert.Empty(service.List(archive.DatabasePath).Rooms);

        seals.Set(new SealTarget("claude", "room"), seal: false);
        Assert.Single(service.List(archive.DatabasePath).Rooms);
    }

    [Fact]
    public void ListDoesNotWriteDatabaseOrCreateSidecar()
    {
        archive.Import(Room("room", "Synthetic room", Message("message", "body")));
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        string[] filesBefore = Directory.GetFiles(archive.DirectoryPath).Order().ToArray();
        DateTime timestampBefore = File.GetLastWriteTimeUtc(archive.DatabasePath);

        Assert.Single(List().Rooms);

        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
        Assert.Equal(filesBefore, Directory.GetFiles(archive.DirectoryPath).Order().ToArray());
        Assert.Equal(timestampBefore, File.GetLastWriteTimeUtc(archive.DatabasePath));
    }

    [Fact]
    public void CorruptSealsFailClosedWithoutDatabaseWrites()
    {
        archive.Import(Room("room", "SYNTHETIC_PRIVATE_NAME", Message("message", "body")));
        File.WriteAllText(Path.Combine(archive.DirectoryPath, "seals.json"), "{");
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        string[] filesBefore = Directory.GetFiles(archive.DirectoryPath).Order().ToArray();

        Exception? failure = Record.Exception(() => List());

        Assert.NotNull(failure);
        Assert.Equal(FailureKind.SealInvalid, BagErrors.Classify(failure));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_NAME", BagErrors.GetMessage(failure));
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
        Assert.Equal(filesBefore, Directory.GetFiles(archive.DirectoryPath).Order().ToArray());
    }

    [Fact]
    public void MissingDatabaseIsNotCreated()
    {
        Exception? failure = Record.Exception(() => List());

        Assert.NotNull(failure);
        Assert.True(BagErrors.IsHandled(failure));
        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
    }

    private RoomListResult List()
    {
        return new RoomListService().List(archive.DatabasePath);
    }
}
