using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class FindTests
{
    private readonly SyntheticArchive archive = new();

    [Theory]
    [InlineData("鞄")]
    [InlineData("鞄を")]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("'")]
    [InlineData("\0")]
    [InlineData("e\u0301")]
    public void LiteralSubstringFindsShortJapaneseAndPreservesEveryCharacter(string text)
    {
        const string body = " \t鞄を探す % _ ' \0\r\n次の行\ne\u0301🙂  ";
        archive.Import(Room("room", "Synthetic room",
            Message("match", body), Message("other", "plain decoy", "match")));

        FindMessage message = Assert.Single(Find(Request(text: text)).Messages);

        Assert.True(message.IsMatch);
        Assert.Equal(body, message.Body);
        Assert.Equal("match", message.Key.MessageId);
    }

    [Fact]
    public void DoesNotNormalizeUnicodeOrTreatSqlWildcardsAsPatterns()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("combining", "e\u0301 鞄お"), Message("literal", "鞄%", "combining")));

        Assert.Empty(Find(Request(text: "é")).Messages);
        Assert.Equal("literal", Assert.Single(Find(Request(text: "鞄%")).Messages).Key.MessageId);
        Assert.Empty(Find(Request(text: "鞄_")).Messages);
    }

    [Fact]
    public void CombinesEveryConditionWithAnd()
    {
        archive.Import(
            Room("selected-room", "架空の研究部屋",
                Message("early", "鞄", createdAt: "2026-01-01T00:00:00Z"),
                Message("hit", "鞄", "early", "2026-01-02T00:00:00Z"),
                Message("wrong-speaker", "鞄", "hit", "2026-01-02T01:00:00Z", "assistant"),
                Message("wrong-text", "箱", "wrong-speaker", "2026-01-02T02:00:00Z"),
                Message("late", "鞄", "wrong-text", "2026-01-03T00:00:00Z")),
            Room("other-room", "架空の別室", Message("wrong-room", "鞄", createdAt: "2026-01-02T00:00:00Z")));

        FindResult result = Find(Request(from: "2026-01-02T09:00:00+09:00", to: "2026-01-02T23:00:00+09:00",
            room: "研究", speaker: "human", text: "鞄"));

        Assert.Equal(1, result.MatchCount);
        Assert.Equal("hit", Assert.Single(result.Messages).Key.MessageId);
    }

    [Fact]
    public void IncludesBothTimeEndpointsByInstant()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("start", "start", createdAt: "2026-01-01T00:00:00.1234567Z"),
            Message("end", "end", "start", "2026-01-01T09:00:01.1234567+09:00")));

        FindResult result = Find(Request(from: "2026-01-01T09:00:00.1234567+09:00", to: "2026-01-01T00:00:01.1234567Z"));

        Assert.Equal(new[] { "start", "end" }, Ids(result));
    }

    [Fact]
    public void SortsByUtcInstantIncludingFractionalSeconds()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("late", "late", "middle", "2026-01-01T01:00:00.0000002Z"),
            Message("middle", "middle", "early", "2026-01-01T10:00:00.0000001+09:00"),
            Message("early", "early", createdAt: "2026-01-01T02:00:00+01:00")));

        Assert.Equal(new[] { "early", "middle", "late" }, Ids(Find(Request())));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-time")]
    [InlineData("2026-01-01T00:00:00")]
    public void UnknownDatesFollowKnownDatesAndPreserveTheOriginalValue(string? createdAt)
    {
        archive.Import(Room("room", "Synthetic room",
            Message("known", "known", createdAt: "2026-01-01T00:00:00Z"),
            Message("unknown", "unknown", "known", createdAt)));

        FindResult result = Find(Request());

        Assert.Equal(2, result.MatchCount);
        Assert.Equal(new[] { "known", "unknown" }, Ids(result));
        FindMessage unknown = result.Messages[1];
        Assert.Null(unknown.Timestamp);
        Assert.Equal(createdAt, unknown.CreatedAt);
    }

    [Fact]
    public void MissingDateIsNotInferredFromRoomOrImportTime()
    {
        Dictionary<string, object?> message = Message("undated", "body");
        message.Remove("created_at");
        archive.Import(Room("room", "Synthetic room", message));

        FindMessage found = Assert.Single(Find(Request()).Messages);

        Assert.Null(found.Timestamp);
        Assert.Null(found.CreatedAt);
    }

    [Fact]
    public void UnknownDatesUseStableOrdinalOrderAfterKnownDates()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("a", "unqualified", "A", "2026-01-01T00:00:00"),
            Message("known", "known", createdAt: "2026-01-01T00:00:00Z"),
            Message("z", "missing", "known", null),
            Message("A", "invalid", "z", "not-a-time")));

        FindResult first = Find(Request());
        FindResult repeated = Find(Request());

        Assert.Equal(new[] { "known", "A", "a", "z" }, Ids(first));
        Assert.Equal(Ids(first), Ids(repeated));
    }

    [Theory]
    [InlineData("2025-12-31T00:00:00Z", null)]
    [InlineData(null, "2026-01-02T00:00:00Z")]
    [InlineData("2025-12-31T00:00:00Z", "2026-01-02T00:00:00Z")]
    public void AnyPeriodBoundaryExcludesUnknownDates(string? from, string? to)
    {
        archive.Import(Room("room", "Synthetic room",
            Message("known", "body", createdAt: "2026-01-01T00:00:00Z"),
            Message("missing", "body", "known", null),
            Message("invalid", "body", "missing", "not-a-time"),
            Message("unqualified", "body", "invalid", "2026-01-01T00:00:00"),
            Message("empty", "body", "unqualified", "")));

        FindResult result = Find(Request(from: from, to: to));

        Assert.Equal(1, result.MatchCount);
        Assert.Equal("known", Assert.Single(result.Messages).Key.MessageId);
    }

    [Fact]
    public void SpeakerAndTextConditionsStillApplyToUnknownDates()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("hit", "鞄", createdAt: null),
            Message("other-speaker", "鞄", "hit", "not-a-time", "assistant"),
            Message("other-text", "箱", "other-speaker", "2026-01-01T00:00:00")));

        FindMessage message = Assert.Single(Find(Request(speaker: "human", text: "鞄")).Messages);

        Assert.Equal("hit", message.Key.MessageId);
        Assert.Null(message.Timestamp);
    }

    [Fact]
    public void LimitCountsUnknownMatchesOnlyAfterKnownMatches()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("A", "needle", createdAt: null),
            Message("later", "needle", "A", "2026-01-02T00:00:00Z"),
            Message("z", "needle", "later", "not-a-time"),
            Message("earlier", "needle", "z", "2026-01-01T00:00:00Z")));

        FindResult knownOnly = Find(Request(text: "needle", limit: 2));
        Assert.Equal(2, knownOnly.MatchCount);
        Assert.Equal(new[] { "earlier", "later" }, Ids(knownOnly));

        FindResult withUnknown = Find(Request(text: "needle", limit: 3));
        Assert.Equal(3, withUnknown.MatchCount);
        Assert.Equal(new[] { "earlier", "later", "A" }, Ids(withUnknown));
    }

    [Fact]
    public void UnknownDateQueriesDoNotRewriteDatesOrDatabaseFiles()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("missing", "body", createdAt: null),
            Message("invalid", "body", "missing", "not-a-time"),
            Message("unqualified", "body", "invalid", "2026-01-01T00:00:00")));
        byte[] before = File.ReadAllBytes(archive.DatabasePath);
        string[] filesBefore = Directory.GetFiles(archive.DirectoryPath).Order().ToArray();
        DateTime timestampBefore = File.GetLastWriteTimeUtc(archive.DatabasePath);

        Assert.Equal(3, Find(Request()).MatchCount);
        Assert.Empty(Find(Request(from: "2025-12-31T00:00:00Z")).Messages);

        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
        Assert.Equal(filesBefore, Directory.GetFiles(archive.DirectoryPath).Order().ToArray());
        Assert.Equal(timestampBefore, File.GetLastWriteTimeUtc(archive.DatabasePath));
    }

    [Fact]
    public void RoomMatchesExactIdOrPartOfItsName()
    {
        archive.Import(
            Room("room-exact", "架空の青い部屋", Message("blue", "body")),
            Room("other", "架空の赤い部屋", Message("red", "body")));

        Assert.Equal("blue", Assert.Single(Find(Request(room: "room-exact")).Messages).Key.MessageId);
        Assert.Equal("red", Assert.Single(Find(Request(room: "赤い")).Messages).Key.MessageId);
        Assert.Empty(Find(Request(room: "room-exac")).Messages);
    }

    [Fact]
    public void DefaultContainsOnlyMainlineAndAllBranchesIncludesSideBranch()
    {
        Dictionary<string, object?> room = Room("room", "Synthetic room",
            Message("root", "root"),
            Message("side", "side", "root", "2026-01-01T01:00:00Z"),
            Message("main", "main", "root", "2026-01-01T02:00:00Z"));
        room["current_leaf_message_uuid"] = "main";
        archive.Import(room);

        Assert.Equal(new[] { "root", "main" }, Ids(Find(Request())));
        FindResult all = Find(Request(allBranches: true));
        Assert.Equal(new[] { "root", "side", "main" }, Ids(all));
        Assert.False(Assert.Single(all.Messages.Single(message => message.Key.MessageId == "side").Sources).IsMainline);
    }

    [Fact]
    public void AuxiliaryDataAndDisplayTextNeverBecomeSearchableBodies()
    {
        Dictionary<string, object?> message = Message("body", "ordinary text");
        message["content"] = new object[]
        {
            new { type = "text", text = "ordinary text" },
            new { type = "thinking", thinking = "AUXILIARY_NEEDLE" },
            new { type = "injected_prompt_block", text = "AUXILIARY_NEEDLE" },
            new { type = "future_type", text = "AUXILIARY_NEEDLE" }
        };
        message["flag"] = "AUXILIARY_NEEDLE";
        Dictionary<string, object?> room = Room("room", "Synthetic room", message);
        room["metadata"] = "AUXILIARY_NEEDLE";
        archive.Import(room);

        Assert.Empty(Find(Request(text: "AUXILIARY_NEEDLE", allBranches: true)).Messages);
        Assert.Empty(Find(Request(text: "SYNTHETIC_DISPLAY_ONLY", allBranches: true)).Messages);
        Assert.Equal("ordinary text", Assert.Single(Find(Request()).Messages).Body);
    }

    [Fact]
    public void DeduplicatesIdenticalVersionsAndRetainsEverySource()
    {
        string first = archive.Import(Room("room", "First synthetic name", Message("message", "鞄")));
        string second = archive.Import(Room("room", "Second synthetic name", Message("message", "鞄")));

        FindResult result = Find(Request(text: "鞄"));

        Assert.Equal(1, result.MatchCount);
        FindMessage message = Assert.Single(result.Messages);
        Assert.Equal(2, message.Sources.Count);
        Assert.Equal(new[] { first, second }.Order(), message.Sources.Select(source => source.SourceSha256).Order());
        Assert.Equal(new[] { "First synthetic name", "Second synthetic name" }.Order(),
            message.Sources.Select(source => source.RoomName).Order());
    }

    [Fact]
    public void RetainsChangedVersionsOfTheSameMessage()
    {
        archive.Import(Room("room", "Synthetic room", Message("message", "鞄 old")));
        archive.Import(Room("room", "Synthetic room", Message("message", "鞄 new")));

        FindResult result = Find(Request(text: "鞄"));

        Assert.Equal(2, result.MatchCount);
        Assert.Equal(new[] { "鞄 new", "鞄 old" }, result.Messages.Select(message => message.Body).Order());
        Assert.Equal(2, result.Messages.Select(message => message.Key.VersionHash).Distinct().Count());
    }

    [Fact]
    public void AppliesRoomAndMainlineConditionsToTheSameSource()
    {
        Dictionary<string, object?> firstRoom = Room("room", "Wanted name",
            Message("root", "root"), Message("selected", "needle", "root"), Message("other", "other", "root"));
        firstRoom["current_leaf_message_uuid"] = "other";
        string firstSource = archive.Import(firstRoom);
        Dictionary<string, object?> secondRoom = Room("room", "Different name",
            Message("root", "root"), Message("selected", "needle", "root"), Message("other", "other", "root"));
        secondRoom["current_leaf_message_uuid"] = "selected";
        archive.Import(secondRoom);

        Assert.Empty(Find(Request(room: "Wanted", text: "needle")).Messages);
        FindMessage all = Assert.Single(Find(Request(room: "Wanted", text: "needle", allBranches: true)).Messages);
        FindSource source = Assert.Single(all.Sources);
        Assert.Equal(firstSource, source.SourceSha256);
        Assert.Equal("Wanted name", source.RoomName);
        Assert.False(source.IsMainline);
    }

    [Fact]
    public void ContextUsesOnlyTheSameRoomAndSourceAndCanFallOutsideFilters()
    {
        string first = archive.Import(Room("room", "Wanted room",
            Message("before", "before old", createdAt: "2026-01-01T00:00:00Z", speaker: "assistant"),
            Message("hit", "needle", "before", "2026-01-01T01:00:00Z"),
            Message("after", "after old", "hit", "2026-01-01T02:00:00Z", "assistant")));
        archive.Import(
            Room("room", "Other snapshot",
                Message("before", "before new", createdAt: "2026-01-01T00:00:00Z", speaker: "assistant"),
                Message("hit", "changed", "before", "2026-01-01T01:00:00Z"),
                Message("after", "after new", "hit", "2026-01-01T02:00:00Z", "assistant")),
            Room("different-room", "Wanted room", Message("foreign", "different room body")));

        FindResult result = Find(Request(from: "2026-01-01T01:00:00Z", to: "2026-01-01T01:00:00Z",
            room: "Wanted", speaker: "human", text: "needle", context: 1));

        Assert.Equal(1, result.MatchCount);
        Assert.Equal(new[] { "before old", "needle", "after old" }, result.Messages.Select(message => message.Body));
        FindMessage match = Assert.Single(result.Messages, message => message.IsMatch);
        foreach (FindMessage context in result.Messages.Where(message => !message.IsMatch))
        {
            FindSource source = Assert.Single(context.Sources);
            Assert.Equal(first, source.SourceSha256);
            Assert.Contains(match.Key, source.ContextFor);
        }
    }

    [Fact]
    public void OverlappingContextIsDeduplicatedAndPreservesLinksToBothMatches()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("one", "needle", createdAt: "2026-01-01T00:00:00Z"),
            Message("between", "context", "one", "2026-01-01T01:00:00Z"),
            Message("two", "needle", "between", "2026-01-01T02:00:00Z")));

        FindResult result = Find(Request(text: "needle", context: 1));

        Assert.Equal(2, result.MatchCount);
        Assert.Equal(3, result.Messages.Count);
        FindMessage between = result.Messages.Single(message => message.Key.MessageId == "between");
        Assert.False(between.IsMatch);
        Assert.Equal(2, Assert.Single(between.Sources).ContextFor.Count);
    }

    [Fact]
    public void SharedMatchRetainsIndependentContextFromEachEligibleSource()
    {
        string first = archive.Import(Room("room", "First synthetic name",
            Message("before", "before old", createdAt: "2026-01-01T00:00:00Z"),
            Message("hit", "needle", "before", "2026-01-01T01:00:00Z"),
            Message("after", "after old", "hit", "2026-01-01T02:00:00Z")));
        string second = archive.Import(Room("room", "Second synthetic name",
            Message("before", "before new", createdAt: "2026-01-01T00:00:00Z"),
            Message("hit", "needle", "before", "2026-01-01T01:00:00Z"),
            Message("after", "after new", "hit", "2026-01-01T02:00:00Z")));

        FindResult result = Find(Request(text: "needle", context: 1));

        Assert.Equal(1, result.MatchCount);
        Assert.Equal(5, result.Messages.Count);
        FindMessage hit = Assert.Single(result.Messages, message => message.IsMatch);
        Assert.Equal(2, hit.Sources.Count);
        foreach (FindMessage context in result.Messages.Where(message => !message.IsMatch))
        {
            FindSource source = Assert.Single(context.Sources);
            Assert.Equal(context.Body.EndsWith("old", StringComparison.Ordinal) ? first : second, source.SourceSha256);
            Assert.Equal(hit.Key, Assert.Single(source.ContextFor));
        }
    }

    [Fact]
    public void AdjacentSelectedMatchesRetainMutualContextLinks()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("first", "needle", createdAt: "2026-01-01T00:00:00Z"),
            Message("second", "needle", "first", "2026-01-01T01:00:00Z")));

        FindResult result = Find(Request(text: "needle", context: 1));

        Assert.Equal(2, result.MatchCount);
        Assert.Equal(2, result.Messages.Count);
        FindMessage first = result.Messages[0];
        FindMessage second = result.Messages[1];
        Assert.True(first.IsMatch);
        Assert.True(second.IsMatch);
        Assert.Equal(second.Key, Assert.Single(Assert.Single(first.Sources).ContextFor));
        Assert.Equal(first.Key, Assert.Single(Assert.Single(second.Sources).ContextFor));
    }

    [Fact]
    public void ContextUsesParentLinksWhileResultsRemainChronological()
    {
        archive.Import(Room("room", "Synthetic room",
            Message("parent", "parent", createdAt: "2026-01-01T00:00:00Z"),
            Message("hit", "needle", "parent", "2026-01-01T02:00:00Z"),
            Message("child-earlier", "child", "hit", "2026-01-01T01:00:00Z"),
            Message("grandchild-later", "grandchild", "child-earlier", "2026-01-01T03:00:00Z")));

        FindResult result = Find(Request(text: "needle", context: 1));

        Assert.Equal(new[] { "parent", "child-earlier", "hit" }, Ids(result));
    }

    [Fact]
    public void LimitAppliesToDistinctMatchesBeforeContext()
    {
        Dictionary<string, object?>[] messages = Enumerable.Range(0, 25)
            .Select(index => Message("m" + index, "needle", index == 0 ? null : "m" + (index - 1),
                $"2026-01-01T00:00:{index:00}Z"))
            .ToArray();
        archive.Import(Room("room", "First name", messages));
        archive.Import(Room("room", "Second name", messages));

        FindResult defaultResult = Find(Request(text: "needle"));
        Assert.Equal(20, defaultResult.MatchCount);
        Assert.Equal(20, defaultResult.Messages.Count);
        Assert.Equal("m19", defaultResult.Messages[^1].Key.MessageId);

        FindResult withContext = Find(Request(text: "needle", limit: 1, context: 2));
        Assert.Equal(1, withContext.MatchCount);
        Assert.Equal(new[] { "m0", "m1", "m2" }, Ids(withContext));
        Assert.Single(withContext.Messages, message => message.IsMatch);
    }

    [Fact]
    public void FindDoesNotChangeDatabaseOrCreateSidecarFiles()
    {
        archive.Import(Room("room", "Synthetic room", Message("message", "鞄")));
        string[] filesBefore = Directory.GetFiles(archive.DirectoryPath).Order().ToArray();
        byte[] hashBefore = SHA256.HashData(File.ReadAllBytes(archive.DatabasePath));
        DateTime timestampBefore = File.GetLastWriteTimeUtc(archive.DatabasePath);

        Assert.Single(Find(Request(text: "鞄")).Messages);

        Assert.Equal(filesBefore, Directory.GetFiles(archive.DirectoryPath).Order().ToArray());
        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(archive.DatabasePath)));
        Assert.Equal(timestampBefore, File.GetLastWriteTimeUtc(archive.DatabasePath));
    }

    [Fact]
    public void MissingDatabaseIsNotCreated()
    {
        Exception? failure = Record.Exception(() => Find(Request()));

        Assert.NotNull(failure);
        Assert.True(BagErrors.IsHandled(failure));
        Assert.False(File.Exists(archive.DatabasePath));
        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
    }

    [Fact]
    public void RejectsTheEarlierSchemaWithoutChangingIt()
    {
        archive.Import(Room("room", "Synthetic room", Message("message", "body")));
        archive.Execute("UPDATE schema_info SET version=1; UPDATE sources SET importer_version='0.1.1'");
        byte[] before = File.ReadAllBytes(archive.DatabasePath);

        Exception? failure = Record.Exception(() => Find(Request()));

        Assert.NotNull(failure);
        Assert.Equal("Incompatible database.", BagErrors.GetMessage(failure));
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public void RejectsIncompatibleDatabaseWithoutChangingIt()
    {
        archive.Import(Room("room", "Synthetic room", Message("message", "body")));
        archive.Execute("PRAGMA application_id=42");
        byte[] before = File.ReadAllBytes(archive.DatabasePath);

        Exception? failure = Record.Exception(() => Find(Request()));

        Assert.NotNull(failure);
        Assert.Equal("Incompatible database.", BagErrors.GetMessage(failure));
        Assert.Equal(before, File.ReadAllBytes(archive.DatabasePath));
    }

    [Fact]
    public void BusyDatabaseIsReportedAndFindReleasesItsConnection()
    {
        archive.Import(Room("room", "Synthetic room", Message("message", "body")));
        using (SqliteConnection connection = archive.Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "BEGIN EXCLUSIVE";
            command.ExecuteNonQuery();
            Exception? failure = Record.Exception(() => Find(Request()));
            Assert.NotNull(failure);
            Assert.Equal("Database is in use.", BagErrors.GetMessage(failure));
            command.CommandText = "ROLLBACK";
            command.ExecuteNonQuery();
        }
        using var exclusive = new FileStream(archive.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    private FindResult Find(FindRequest request)
    {
        return new FindService().Find(archive.DatabasePath, request);
    }

    private static string[] Ids(FindResult result)
    {
        return result.Messages.Select(message => message.Key.MessageId).ToArray();
    }

    private static FindRequest Request(string? from = null, string? to = null, string? room = null,
        string? speaker = null, string? text = null, bool allBranches = false, int context = 0, int limit = 20)
    {
        return new FindRequest(ParseTime(from), ParseTime(to), room, speaker, text, allBranches, context, limit);
    }

    private static DateTimeOffset? ParseTime(string? value)
    {
        return value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }
}
