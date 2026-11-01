namespace Bag;

internal sealed record RoomInfo(string Id, string Name, string? CreatedAt, string? SelectedLeaf,
    MainlineResult? Mainline = null, string? RuleVersion = null);
internal sealed record AuxiliaryBlock(string Kind, string Json);
internal sealed record ImportedMessage(
    string Id, string? ParentId, string Speaker, string? CreatedAt,
    string? UpdatedAt, string Body, IReadOnlyList<AuxiliaryBlock> Blocks);
internal sealed record ImportCounts(long Rooms, long Messages, long NewVersions, long UnresolvedRooms);
internal sealed record GraphNode(string Id, string? ParentId, string? CreatedAt);
internal sealed record MainlineResult(string Reason, string? LeafId, HashSet<string> Members);

/// <summary>Reads a provider-specific export without owning the ZIP or database.</summary>
internal interface IExportReader
{
    string Provider { get; }
    bool SupportsNumberedConversations => false;
    void Read(Func<Stream> openJson, IImportSink sink);
}

/// <summary>Receives one room and one message at a time.</summary>
internal interface IImportSink
{
    void BeginRoom(RoomInfo room);
    void AddMessage(ImportedMessage message);
    void AddRoomBlock(AuxiliaryBlock block);
    void EndRoom();
}
