using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class GraphContextTests
{
    private readonly SyntheticArchive archive = new();

    [Fact]
    public void BranchContextKeepsItsOwnAncestorsAndDescendants()
    {
        ImportSelectedRoom("main",
            Message("root", "root", createdAt: "2026-01-01T00:00:00Z"),
            Message("main", "main", "root", "2026-01-01T01:00:00Z"),
            Message("branch-parent", "branch parent", "root", "2026-01-01T02:00:00Z"),
            Message("hit", "needle", "branch-parent", "2026-01-01T03:00:00Z"),
            Message("branch-child", "branch child", "hit", "2026-01-01T04:00:00Z"));

        FindResult result = Find(context: 1, allBranches: true);

        Assert.Equal(new[] { "branch-parent", "hit", "branch-child" }, Ids(result));
        Assert.All(result.Messages, message => Assert.False(Assert.Single(message.Sources).IsMainline));
        AssertContextLinks(result);
    }

    [Fact]
    public void BranchContinuationChoosesNewestLeafRatherThanNewestImmediateChild()
    {
        ImportSelectedRoom("main",
            Message("root", "root", createdAt: "2026-01-01T00:00:00Z"),
            Message("main", "main", "root", "2026-01-01T11:00:00Z"),
            Message("hit", "needle", "root", "2026-01-01T01:00:00Z"),
            Message("newer-child", "excluded child", "hit", "2026-01-01T08:00:00Z"),
            Message("older-leaf", "excluded leaf", "newer-child", "2026-01-01T09:00:00Z"),
            Message("chosen-child", "chosen child", "hit", "2026-01-01T02:00:00Z"),
            Message("newest-leaf", "chosen leaf", "chosen-child", "2026-01-01T10:00:00Z"));

        Assert.Equal(new[] { "root", "hit", "chosen-child" }, Ids(Find(context: 1, allBranches: true)));
        FindResult twoSteps = Find(context: 2, allBranches: true);
        Assert.Equal(new[] { "root", "hit", "chosen-child", "newest-leaf" }, Ids(twoSteps));
        AssertContextLinks(twoSteps);
    }

    [Fact]
    public void BranchLeafTimestampTiesUseOrdinalLeafIds()
    {
        ImportSelectedRoom("main",
            Message("root", "root", createdAt: "2026-01-01T00:00:00Z"),
            Message("main", "main", "root", "2026-01-01T11:00:00Z"),
            Message("hit", "needle", "root", "2026-01-01T01:00:00Z"),
            Message("chosen-child", "chosen child", "hit", "2026-01-01T02:00:00Z"),
            Message("A-leaf", "chosen leaf", "chosen-child", "2026-01-01T19:00:00+09:00"),
            Message("other-child", "excluded child", "hit", "2026-01-01T03:00:00Z"),
            Message("z-leaf", "excluded leaf", "other-child", "2026-01-01T10:00:00Z"));

        Assert.Equal(new[] { "root", "hit", "chosen-child", "A-leaf" }, Ids(Find(context: 2, allBranches: true)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-4000-8000-000000000000")]
    public void EveryRootMarkerEndsAncestorTraversal(string? parent)
    {
        archive.Import(Room("room", "Synthetic room",
            Message("root", "root", parent, "2026-01-01T00:00:00Z"),
            Message("hit", "needle", "root", "2026-01-01T01:00:00Z"),
            Message("child", "child", "hit", "2026-01-01T02:00:00Z")));

        FindResult result = Find(context: 10);

        Assert.Equal(new[] { "root", "hit", "child" }, Ids(result));
        AssertContextLinks(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-time")]
    [InlineData("2026-01-01T03:00:00")]
    public void AmbiguousBranchLeafTimeKeepsAncestorsWithoutGuessingContinuation(string? leafTime)
    {
        ImportSelectedRoom("main",
            Message("root", "root", createdAt: "2026-01-01T00:00:00Z"),
            Message("main", "main", "root", "2026-01-01T05:00:00Z"),
            Message("branch-parent", "branch parent", "root", "2026-01-01T01:00:00Z"),
            Message("hit", "needle", "branch-parent", "2026-01-01T02:00:00Z"),
            Message("unknown-leaf", "unknown leaf", "hit", leafTime),
            Message("known-leaf", "known leaf", "hit", "2026-01-01T04:00:00Z"));

        FindResult result = Find(context: 3, allBranches: true);

        Assert.Equal(new[] { "root", "branch-parent", "hit" }, Ids(result));
        AssertContextLinks(result);
    }

    [Fact]
    public void SingleBranchContinuationIncludesConnectedUnknownDates()
    {
        ImportSelectedRoom("main",
            Message("root", "root", createdAt: "2026-01-01T00:00:00Z"),
            Message("main", "main", "root", "2026-01-01T05:00:00Z"),
            Message("hit", "needle", "root", null),
            Message("child", "child", "hit", "not-a-time"),
            Message("leaf", "leaf", "child", "2026-01-01T03:00:00"));

        FindResult result = Find(context: 2, allBranches: true);

        Assert.Equal(new[] { "root", "child", "hit", "leaf" }, Ids(result));
        Assert.All(result.Messages.Where(message => message.Key.MessageId != "root"), message => Assert.Null(message.Timestamp));
        AssertContextLinks(result);
    }

    [Fact]
    public void MainlineContextIncludesUnknownParentsAndChildren()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("parent", "parent", createdAt: null),
            Message("hit", "needle", "parent", "not-a-time"),
            Message("child", "child", "hit", "2026-01-01T00:00:00")));

        FindResult result = Find(context: 1);

        Assert.Equal(new[] { "child", "hit", "parent" }, Ids(result));
        Assert.All(result.Messages, message => Assert.Null(message.Timestamp));
        AssertContextLinks(result);
    }

    [Fact]
    public void MainlineContinuationFollowsSelectedPathDespiteNewerSideLeaf()
    {
        ImportSelectedRoom("chosen-leaf",
            Message("hit", "needle", createdAt: "2026-01-01T02:00:00Z"),
            Message("chosen-child", "chosen child", "hit", "2026-01-01T01:00:00Z"),
            Message("chosen-leaf", "chosen leaf", "chosen-child", "2026-01-01T03:00:00Z"),
            Message("side", "excluded side", "hit", "2026-01-01T09:00:00Z"));

        FindResult result = Find(context: 2, allBranches: true);

        Assert.Equal(new[] { "chosen-child", "hit", "chosen-leaf" }, Ids(result));
        AssertContextLinks(result);
    }

    [Fact]
    public void CycleStopsWithoutRepeatingNodesOrInferringContinuation()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("hit", "needle", "parent", "2026-01-01T01:00:00Z"),
            Message("parent", "parent", "hit", "2026-01-01T00:00:00Z")));

        FindResult result = Find(context: 100, allBranches: true);

        Assert.Equal(new[] { "parent", "hit" }, Ids(result));
        AssertContextLinks(result);
    }

    [Fact]
    public void MissingParentStopsWithoutInventingNodes()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("hit", "needle", "missing-parent", "2026-01-01T01:00:00Z")));

        FindResult result = Find(context: 100, allBranches: true);

        Assert.Equal("hit", Assert.Single(result.Messages).Key.MessageId);
        Assert.Empty(Assert.Single(result.Messages[0].Sources).ContextFor);
    }

    [Fact]
    public void MissingParentAndUnrelatedCycleDoNotHideReachableDescendants()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("hit", "needle", "missing-parent", "2026-01-01T01:00:00Z"),
            Message("child", "child", "hit", "2026-01-01T02:00:00Z"),
            Message("leaf", "leaf", "child", "2026-01-01T03:00:00Z"),
            Message("cycle-one", "unrelated", "cycle-two", "2026-01-01T04:00:00Z"),
            Message("cycle-two", "unrelated", "cycle-one", "2026-01-01T05:00:00Z")));

        FindResult result = Find(context: 2, allBranches: true);

        Assert.Equal(new[] { "hit", "child", "leaf" }, Ids(result));
        AssertContextLinks(result);
    }

    private void ImportSelectedRoom(string selectedLeaf, params Dictionary<string, object?>[] messages)
    {
        Dictionary<string, object?> room = Room("room", "Synthetic room", messages);
        room["current_leaf_message_uuid"] = selectedLeaf;
        archive.Import(room);
    }

    private FindResult Find(int context, bool allBranches = false)
    {
        var request = new FindRequest(null, null, null, null, "needle", allBranches, context, 20);
        return new FindService().Find(archive.DatabasePath, request);
    }

    private static string[] Ids(FindResult result)
    {
        return result.Messages.Select(message => message.Key.MessageId).ToArray();
    }

    private static void AssertContextLinks(FindResult result)
    {
        Assert.Equal(1, result.MatchCount);
        Assert.Equal(result.Messages.Count, result.Messages.Select(message => message.Key).Distinct().Count());
        FindMessage hit = Assert.Single(result.Messages, message => message.IsMatch);
        foreach (FindMessage context in result.Messages.Where(message => !message.IsMatch))
        {
            Assert.Equal(hit.Key, Assert.Single(Assert.Single(context.Sources).ContextFor));
        }
    }
}
