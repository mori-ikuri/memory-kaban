namespace Bag;

internal sealed record FindRequest(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Room,
    string? Speaker,
    string? Text,
    bool AllBranches,
    int Context,
    int Limit);

internal sealed record FindMessageKey(
    string Provider,
    string RoomId,
    string MessageId,
    string VersionHash);

internal sealed record FindSource(
    string SourceSha256,
    string RoomName,
    bool IsMainline,
    IReadOnlyList<FindMessageKey> ContextFor);

internal sealed record FindMessage(
    FindMessageKey Key,
    string Speaker,
    string Body,
    string? CreatedAt,
    DateTimeOffset? Timestamp,
    bool IsMatch,
    IReadOnlyList<FindSource> Sources);

internal sealed record FindResult(int MatchCount, IReadOnlyList<FindMessage> Messages, bool ContextTruncated = false);
