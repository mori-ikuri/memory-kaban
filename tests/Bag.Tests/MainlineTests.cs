namespace Bag.Tests;

public sealed class MainlineTests
{
    [Fact]
    public void TiesUseOrdinalIdentifierOrder()
    {
        MainlineResult result = MainlineSelector.Select([
            new("root", null, null),
            new("z", "root", "2026-01-01T00:00:00Z"),
            new("A", "root", "2026-01-01T09:00:00+09:00")], null);
        Assert.Equal("A", result.LeafId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-01-01T00:00:00")]
    public void MissingOrInvalidLeafTimeLeavesGraphUnresolved(string? time)
    {
        MainlineResult result = MainlineSelector.Select([
            new("a", null, time), new("b", null, "2026-01-02T00:00:00Z")], null);
        Assert.Null(result.LeafId);
        Assert.Empty(result.Members);
        Assert.Equal("uncomparable-leaf-timestamp", result.Reason);
    }

    [Fact]
    public void DoesNotRepairMissingParentsOrCycles()
    {
        Assert.Equal("missing-parent", MainlineSelector.Select([new("a", "absent", null)], null).Reason);
        Assert.Equal("cycle", MainlineSelector.Select([new("a", "b", null), new("b", "a", null)], null).Reason);
        Assert.Equal("invalid-selected-leaf", MainlineSelector.Select([new("a", null, null)], "absent").Reason);
    }

    [Fact]
    public void HandlesEmptyRoom()
    {
        Assert.Equal("empty", MainlineSelector.Select([], null).Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-4000-8000-000000000000")]
    public void RootMarkersSupportSingleLeafWithoutTime(string? parent)
    {
        MainlineResult result = MainlineSelector.Select([new("a", parent, null)], null);
        Assert.Equal("a", result.LeafId);
        Assert.Equal("single-leaf", result.Reason);
        Assert.Equal(new[] { "a" }, result.Members);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-4000-8000-000000000000")]
    public void RootMarkersTerminateAncestorWalksAndSelectLatestBranch(string? parent)
    {
        // Start at a leaf so cycle detection must walk all the way to the root.
        MainlineResult result = MainlineSelector.Select([
            new("newer", "middle", "2026-01-02T00:00:00Z"),
            new("older", "root", "2026-01-01T00:00:00Z"),
            new("middle", "root", null),
            new("root", parent, null)], null);
        Assert.Equal("newer", result.LeafId);
        Assert.Equal("inferred-latest-created-at", result.Reason);
        Assert.Equal(new[] { "middle", "newer", "root" }, result.Members.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("00000000-0000-4000-8000-000000000001")]
    [InlineData("00000000-0000-4000-0000-000000000000")]
    public void SimilarUuidsAreNotRootMarkers(string parent)
    {
        MainlineResult result = MainlineSelector.Select([new("a", parent, null)], null);
        Assert.Equal("missing-parent", result.Reason);
        Assert.Null(result.LeafId);
        Assert.Empty(result.Members);
    }
}
