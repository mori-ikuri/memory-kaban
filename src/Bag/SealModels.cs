namespace Bag;

internal sealed record SealTarget(string Provider, string RoomId, string? MessageId = null);

/// <summary>An immutable view of room and message seals, independent of saved versions.</summary>
internal sealed class SealSnapshot
{
    private readonly HashSet<SealTarget> targets;

    public IReadOnlyList<SealTarget> Targets { get; }

    public SealSnapshot(IEnumerable<SealTarget> values)
    {
        SealTarget[] ordered = values
            .OrderBy(target => target.Provider, StringComparer.Ordinal)
            .ThenBy(target => target.RoomId, StringComparer.Ordinal)
            .ThenBy(target => target.MessageId, StringComparer.Ordinal)
            .ToArray();
        Targets = Array.AsReadOnly(ordered);
        targets = ordered.ToHashSet();
    }

    public bool RoomSealed(string provider, string roomId)
    {
        return targets.Contains(new SealTarget(provider, roomId));
    }

    public bool IsSealed(string provider, string roomId, string messageId)
    {
        return RoomSealed(provider, roomId) || targets.Contains(new SealTarget(provider, roomId, messageId));
    }
}

/// <summary>Holds a synchronous, thread-affine seal lock for an entire read operation.</summary>
internal sealed class SealReadLease : IDisposable
{
    private IDisposable? gate;

    public SealSnapshot Snapshot { get; }

    internal SealReadLease(SealSnapshot snapshot, IDisposable gate)
    {
        Snapshot = snapshot;
        this.gate = gate;
    }

    public void Dispose()
    {
        IDisposable? held = gate;
        gate = null;
        held?.Dispose();
    }
}
