using System.Globalization;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class FindLimitTests
{
    private readonly SyntheticArchive archive = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveTotalLimitIsRejectedWithoutCreatingFiles(int cap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Find(Request(), cap));
        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
    }

    [Fact]
    public void RequestedMatchLimitIsClampedToTheTotalCap()
    {
        ImportChain(100, _ => "needle");

        FindResult result = Find(Request(limit: 100), 50);

        Assert.Equal(50, result.MatchCount);
        Assert.Equal(50, result.Messages.Count);
        Assert.All(result.Messages, message => Assert.True(message.IsMatch));
        Assert.Equal("m049", result.Messages[^1].Key.MessageId);
        Assert.False(result.ContextTruncated);
    }

    [Fact]
    public void DefaultMatchCountAndLargeContextStayWithinFiftyTotalMessages()
    {
        ImportChain(100, _ => "needle");

        FindResult result = Find(Request(context: 50), 50);

        Assert.Equal(20, result.MatchCount);
        Assert.Equal(50, result.Messages.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(Id), result.Messages.Where(message => message.IsMatch).Select(message => message.Key.MessageId));
        Assert.True(result.ContextTruncated);
    }

    [Fact]
    public void AllSelectedHitsTakePriorityOverContext()
    {
        ImportChain(100, index => index % 20 == 0 ? "needle" : "context");

        FindResult result = Find(Request(context: 50, limit: 5), 50);

        Assert.Equal(5, result.MatchCount);
        Assert.Equal(50, result.Messages.Count);
        Assert.Equal(new[] { "m000", "m020", "m040", "m060", "m080" },
            result.Messages.Where(message => message.IsMatch).Select(message => message.Key.MessageId));
        Assert.True(result.ContextTruncated);
    }

    [Fact]
    public void ExistingSelectedHitsKeepContextLinksAfterTheCapIsFull()
    {
        ImportChain(3, index => index < 2 ? "needle" : "context");

        FindResult result = Find(Request(context: 1, limit: 2), 2);

        Assert.Equal(2, result.MatchCount);
        Assert.Equal(2, result.Messages.Count);
        FindMessage first = result.Messages[0];
        FindMessage second = result.Messages[1];
        Assert.True(first.IsMatch);
        Assert.True(second.IsMatch);
        Assert.Equal(second.Key, Assert.Single(Assert.Single(first.Sources).ContextFor));
        Assert.Equal(first.Key, Assert.Single(Assert.Single(second.Sources).ContextFor));
        Assert.True(result.ContextTruncated);
    }

    [Fact]
    public void WithoutTotalLimitContextRemainsUncapped()
    {
        ImportChain(100, _ => "needle");

        FindResult result = Find(Request(context: 50));

        Assert.Equal(20, result.MatchCount);
        Assert.Equal(70, result.Messages.Count);
        Assert.Equal("m069", result.Messages[^1].Key.MessageId);
        Assert.False(result.ContextTruncated);
    }

    [Fact]
    public void NoTruncationIsReportedWhenEveryAvailableNeighborFits()
    {
        ImportChain(3, index => index == 1 ? "needle" : "context");

        FindResult result = Find(Request(context: 50), 50);

        Assert.Equal(1, result.MatchCount);
        Assert.Equal(3, result.Messages.Count);
        Assert.False(result.ContextTruncated);
    }

    [Fact]
    public void IdenticalVersionsAcrossSourcesUseOneSlotAndKeepTheirProvenance()
    {
        Dictionary<string, object?>[] messages = Chain(20, _ => "needle");
        archive.Import(Room("room", "First synthetic name", messages));
        archive.Import(Room("room", "Second synthetic name", messages));

        FindResult result = Find(Request(limit: 100), 5);

        Assert.Equal(5, result.MatchCount);
        Assert.Equal(5, result.Messages.Count);
        Assert.All(result.Messages, message => Assert.Equal(2, message.Sources.Count));
        Assert.Equal(new[] { "m000", "m001", "m002", "m003", "m004" }, result.Messages.Select(message => message.Key.MessageId));
    }

    [Fact]
    public void SealedMessagesAndRoomsNeverTakeSlotsOrReachResults()
    {
        archive.Import(
            Room("room", "Visible synthetic name", Chain(60, index => index == 0 ? "SYNTHETIC_SEALED_BODY needle" : "needle")),
            Room("hidden", "SYNTHETIC_SEALED_NAME", Message("secret", "SYNTHETIC_SEALED_BODY needle")));
        var seals = new SealStore(archive.DatabasePath);
        seals.Set(new SealTarget("claude", "room", "m000"), seal: true);
        seals.Set(new SealTarget("claude", "hidden"), seal: true);

        FindResult result = Find(Request(context: 50, limit: 100), 50);

        Assert.Equal(50, result.MatchCount);
        Assert.Equal(50, result.Messages.Count);
        Assert.DoesNotContain(result.Messages, message => message.Key.RoomId == "hidden" || message.Key.MessageId == "m000");
        Assert.DoesNotContain(result.Messages, message => message.Body.Contains("SYNTHETIC_SEALED", StringComparison.Ordinal));
        Assert.All(result.Messages.SelectMany(message => message.Sources),
            source => Assert.DoesNotContain("SYNTHETIC_SEALED", source.RoomName));
    }

    [Fact]
    public void BodiesBeyondTheContextCapAreNotMaterialized()
    {
        const int bodySize = 8 * 1024 * 1024;
        archive.Import(Room("room", "Synthetic room",
            Message("hit", "needle", createdAt: "2026-01-01T00:00:00Z"),
            Message("large", new string('x', bodySize), "hit", "2026-01-01T01:00:00Z")));
        FindRequest request = Request(context: 1, limit: 1);
        Find(request, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();

        FindResult result = Find(request, 1);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("hit", Assert.Single(result.Messages).Key.MessageId);
        Assert.True(result.ContextTruncated);
        Assert.True(allocated < bodySize, $"Capped find allocated {allocated} bytes for a skipped synthetic body.");
    }

    [Fact]
    public void NoMatchesReturnAnEmptyUntruncatedResult()
    {
        ImportChain(5, _ => "other text");

        FindResult result = Find(Request(context: 50), 50);

        Assert.Equal(0, result.MatchCount);
        Assert.Empty(result.Messages);
        Assert.False(result.ContextTruncated);
    }

    private FindResult Find(FindRequest request, int? totalLimit = null)
    {
        return new FindService().Find(archive.DatabasePath, request, includeSealed: false, totalLimit: totalLimit);
    }

    private void ImportChain(int count, Func<int, string> body)
    {
        archive.Import(Room("room", "Synthetic room", Chain(count, body)));
    }

    private static Dictionary<string, object?>[] Chain(int count, Func<int, string> body)
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        return Enumerable.Range(0, count).Select(index => Message(Id(index), body(index),
            index == 0 ? null : Id(index - 1), start.AddMinutes(index).ToString("O", CultureInfo.InvariantCulture))).ToArray();
    }

    private static string Id(int index)
    {
        return "m" + index.ToString("000", CultureInfo.InvariantCulture);
    }

    private static FindRequest Request(int context = 0, int limit = 20)
    {
        return new FindRequest(null, null, null, null, "needle", false, context, limit);
    }
}
