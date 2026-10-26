using System.Security.Cryptography;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class SealFindTests
{
    private readonly SyntheticArchive archive = new();

    [Fact]
    public void SealedRoomsAreExcludedBeforeTheMatchLimit()
    {
        archive.Import(
            Room("hidden-room", "Synthetic hidden room",
                Message("hidden-first", "needle", createdAt: "2026-01-01T00:00:00Z"),
                Message("hidden-second", "needle", "hidden-first", "2026-01-01T01:00:00Z")),
            Room("visible-room", "Synthetic visible room",
                Message("visible", "needle", createdAt: "2026-01-01T02:00:00Z")));
        Seal("hidden-room");

        FindResult result = Find(limit: 1);

        Assert.Equal(1, result.MatchCount);
        Assert.Equal("visible", Assert.Single(result.Messages).Key.MessageId);
        Assert.Equal("hidden-first", Assert.Single(Find(limit: 1, includeSealed: true).Messages).Key.MessageId);
    }

    [Fact]
    public void SealedMessagesAreExcludedBeforeTheMatchLimit()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("hidden-first", "needle", createdAt: "2026-01-01T00:00:00Z"),
            Message("hidden-second", "needle", "hidden-first", "2026-01-01T01:00:00Z"),
            Message("visible", "needle", "hidden-second", "2026-01-01T02:00:00Z")));
        Seal("room", "hidden-first");
        Seal("room", "hidden-second");

        FindResult result = Find(limit: 1);

        Assert.Equal(1, result.MatchCount);
        Assert.Equal("visible", Assert.Single(result.Messages).Key.MessageId);
    }

    [Fact]
    public void MessageSealExcludesEverySavedVersionAndSourceButNotOtherRooms()
    {
        archive.Import(Room("room", "First synthetic name", Message("shared", "needle old")));
        archive.Import(Room("room", "Second synthetic name", Message("shared", "needle new")));
        archive.Import(
            Room("room", "Third synthetic name", Message("shared", "needle new")),
            Room("other-room", "Other synthetic room", Message("shared", "needle other room")));
        Seal("room", "shared");

        FindMessage visible = Assert.Single(Find().Messages);
        Assert.Equal("other-room", visible.Key.RoomId);
        Assert.Equal("needle other room", visible.Body);

        FindResult included = Find(includeSealed: true);
        Assert.Equal(3, included.MatchCount);
        Assert.Equal(2, included.Messages.Single(message => message.Body == "needle new").Sources.Count);
        Assert.Equal(new[] { "needle new", "needle old", "needle other room" },
            included.Messages.Select(message => message.Body).Order());
    }

    [Fact]
    public void AncestorContextStopsAtTheSealWithoutJumpingToEarlierParents()
    {
        ImportChain();
        Seal("room", "parent");

        FindResult result = Find(context: 10);

        Assert.Equal(new[] { "hit", "child", "leaf" }, Ids(result));
        Assert.Equal(new[] { "grandparent", "parent", "hit", "child", "leaf" },
            Ids(Find(context: 10, includeSealed: true)));
        AssertContextLinks(result);
    }

    [Fact]
    public void DescendantContextStopsAtTheSealWithoutJumpingToLaterChildren()
    {
        ImportChain();
        Seal("room", "child");

        FindResult result = Find(context: 10);

        Assert.Equal(new[] { "grandparent", "parent", "hit" }, Ids(result));
        Assert.Equal(new[] { "grandparent", "parent", "hit", "child", "leaf" },
            Ids(Find(context: 10, includeSealed: true)));
        AssertContextLinks(result);
    }

    [Fact]
    public void ContextKeepsVisibleNeighborsBeforeReachingASealedAncestor()
    {
        ImportChain();
        Seal("room", "grandparent");

        Assert.Equal(new[] { "parent", "hit", "child", "leaf" }, Ids(Find(context: 10)));
    }

    [Theory]
    [InlineData("chosen-child")]
    [InlineData("newest-leaf")]
    public void SealedBranchContinuationStopsOnTheChosenPathWithoutRerouting(string sealedId)
    {
        Dictionary<string, object?> room = Room("room", "Synthetic branch room",
            Message("root", "root", createdAt: "2026-01-01T00:00:00Z"),
            Message("main", "main", "root", "2026-01-01T11:00:00Z"),
            Message("hit", "needle", "root", "2026-01-01T01:00:00Z"),
            Message("chosen-child", "chosen child", "hit", "2026-01-01T02:00:00Z"),
            Message("newest-leaf", "chosen leaf", "chosen-child", "2026-01-01T10:00:00Z"),
            Message("other-child", "other child", "hit", "2026-01-01T03:00:00Z"),
            Message("older-leaf", "other leaf", "other-child", "2026-01-01T09:00:00Z"));
        room["current_leaf_message_uuid"] = "main";
        archive.Import(room);
        Seal("room", sealedId);

        FindResult result = Find(context: 10, allBranches: true);

        string[] expected = sealedId == "chosen-child" ? ["root", "hit"] : ["root", "hit", "chosen-child"];
        Assert.Equal(expected, Ids(result));
        Assert.Equal(new[] { "root", "hit", "chosen-child", "newest-leaf" },
            Ids(Find(context: 10, allBranches: true, includeSealed: true)));
        AssertContextLinks(result);
    }

    [Fact]
    public void ASealedHitCannotExposeItsContext()
    {
        ImportChain();
        Seal("room", "hit");

        FindResult result = Find(context: 10, allBranches: true);

        Assert.Equal(0, result.MatchCount);
        Assert.Empty(result.Messages);
        Assert.Equal(5, Find(context: 10, allBranches: true, includeSealed: true).Messages.Count);
    }

    [Fact]
    public void IncludeSealedRestoresRoomMatchesAndTheirContext()
    {
        ImportChain();
        Seal("room");

        Assert.Empty(Find(context: 10).Messages);
        FindResult included = Find(context: 10, includeSealed: true);
        Assert.Equal(new[] { "grandparent", "parent", "hit", "child", "leaf" }, Ids(included));
        AssertContextLinks(included);
    }

    [Fact]
    public void FindReadsTheCurrentSealsOnEveryCall()
    {
        ImportChain();
        var service = new FindService();
        FindRequest request = Request();
        Assert.Single(service.Find(archive.DatabasePath, request).Messages);

        Seal("room", "hit");
        Assert.Empty(service.Find(archive.DatabasePath, request).Messages);

        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "room", "hit"), false);
        Assert.Single(service.Find(archive.DatabasePath, request).Messages);
    }

    [Fact]
    public void UnsealingARoomDoesNotClearItsIndividualMessageSeal()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("individual", "needle", createdAt: "2026-01-01T00:00:00Z"),
            Message("other", "needle", "individual", "2026-01-01T01:00:00Z")));
        Seal("room", "individual");
        Seal("room");
        Assert.Empty(Find().Messages);

        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "room", null), false);

        Assert.Equal("other", Assert.Single(Find().Messages).Key.MessageId);
        Assert.Equal(2, Find(includeSealed: true).Messages.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptSealsFailClosedEvenWhenIncludingSealedMessages(bool includeSealed)
    {
        ImportChain();
        File.WriteAllText(Path.Combine(archive.DirectoryPath, "seals.json"), "{broken-synthetic-seals");

        Exception? failure = Record.Exception(() => Find(includeSealed: includeSealed));

        Assert.NotNull(failure);
        Assert.True(BagErrors.IsHandled(failure));
        Assert.DoesNotContain("broken-synthetic-seals", BagErrors.GetMessage(failure));
        Assert.DoesNotContain(archive.DirectoryPath, BagErrors.GetMessage(failure));
    }

    [Fact]
    public void ReadingWithoutSealsCreatesNoSidecarAndLeavesTheDatabaseUnchanged()
    {
        ImportChain();
        byte[] before = SHA256.HashData(File.ReadAllBytes(archive.DatabasePath));
        string[] filesBefore = Directory.GetFiles(archive.DirectoryPath).Order().ToArray();

        Assert.Single(Find().Messages);
        Assert.Single(Find(includeSealed: true).Messages);

        Assert.Equal(filesBefore, Directory.GetFiles(archive.DirectoryPath).Order().ToArray());
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(archive.DatabasePath)));
        Assert.False(File.Exists(Path.Combine(archive.DirectoryPath, "seals.json")));
    }

    [Fact]
    public void ReadingWithSealsChangesNeitherTheSidecarNorTheDatabase()
    {
        ImportChain();
        Seal("room", "parent");
        string sealsPath = Path.Combine(archive.DirectoryPath, "seals.json");
        byte[] databaseBefore = File.ReadAllBytes(archive.DatabasePath);
        byte[] sealsBefore = File.ReadAllBytes(sealsPath);
        string[] filesBefore = Directory.GetFiles(archive.DirectoryPath).Order().ToArray();

        Find(context: 10);
        Find(context: 10, includeSealed: true);

        Assert.Equal(databaseBefore, File.ReadAllBytes(archive.DatabasePath));
        Assert.Equal(sealsBefore, File.ReadAllBytes(sealsPath));
        Assert.Equal(filesBefore, Directory.GetFiles(archive.DirectoryPath).Order().ToArray());
    }

    private void ImportChain()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("grandparent", "grandparent", createdAt: "2026-01-01T00:00:00Z"),
            Message("parent", "parent", "grandparent", "2026-01-01T01:00:00Z"),
            Message("hit", "needle", "parent", "2026-01-01T02:00:00Z"),
            Message("child", "child", "hit", "2026-01-01T03:00:00Z"),
            Message("leaf", "leaf", "child", "2026-01-01T04:00:00Z")));
    }

    private void Seal(string roomId, string? messageId = null)
    {
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", roomId, messageId), true);
    }

    private FindResult Find(int context = 0, int limit = 20, bool allBranches = false, bool includeSealed = false)
    {
        return new FindService().Find(archive.DatabasePath, Request(context, limit, allBranches), includeSealed);
    }

    private static FindRequest Request(int context = 0, int limit = 20, bool allBranches = false)
    {
        return new FindRequest(null, null, null, null, "needle", allBranches, context, limit);
    }

    private static string[] Ids(FindResult result)
    {
        return result.Messages.Select(message => message.Key.MessageId).ToArray();
    }

    private static void AssertContextLinks(FindResult result)
    {
        FindMessage hit = Assert.Single(result.Messages, message => message.IsMatch);
        foreach (FindMessage context in result.Messages.Where(message => !message.IsMatch))
        {
            Assert.Equal(hit.Key, Assert.Single(Assert.Single(context.Sources).ContextFor));
        }
    }
}
