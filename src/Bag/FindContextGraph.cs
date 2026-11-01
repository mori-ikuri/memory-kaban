using Microsoft.Data.Sqlite;

namespace Bag;

/// <summary>Follows message relationships within one source and room.</summary>
internal sealed class FindContextGraph
{
    private readonly Dictionary<string, FindContextNode> nodes;
    private readonly Dictionary<string, List<FindContextNode>> children = new(StringComparer.Ordinal);
    private readonly bool nullRootOnly;

    private FindContextGraph(Dictionary<string, FindContextNode> nodes, bool nullRootOnly)
    {
        this.nodes = nodes;
        this.nullRootOnly = nullRootOnly;
        foreach (FindContextNode node in nodes.Values)
        {
            if (IsRoot(node.ParentId))
            {
                continue;
            }
            if (!children.TryGetValue(node.ParentId!, out List<FindContextNode>? siblings))
            {
                siblings = [];
                children.Add(node.ParentId!, siblings);
            }
            siblings.Add(node);
        }
    }

    public static FindContextGraph Read(
        SqliteConnection connection, SqliteTransaction transaction, string sourceSha, FindMessageKey key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sm.version_id, sm.message_id, v.parent_id, v.created_at, sm.is_mainline
            FROM source_messages AS sm
            JOIN message_versions AS v
              ON v.version_id = sm.version_id
             AND v.provider = sm.provider AND v.room_id = sm.room_id AND v.message_id = sm.message_id
            WHERE sm.source_sha = $source AND sm.provider = $provider AND sm.room_id = $room
            """;
        command.Parameters.AddWithValue("$source", sourceSha);
        command.Parameters.AddWithValue("$provider", key.Provider);
        command.Parameters.AddWithValue("$room", key.RoomId);
        using SqliteDataReader reader = command.ExecuteReader();
        var nodes = new Dictionary<string, FindContextNode>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var node = new FindContextNode(reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4) == 1);
            nodes.Add(node.Id, node);
        }
        return new FindContextGraph(nodes, key.Provider == "chatgpt");
    }

    public IReadOnlyList<FindContextNode> Around(string messageId, int count, Func<string, bool>? isVisible = null)
    {
        if (!nodes.TryGetValue(messageId, out FindContextNode? hit))
        {
            throw BagErrors.InvalidData(FailureKind.DatabaseIncompatible);
        }
        isVisible ??= _ => true;
        if (!isVisible(hit.Id))
        {
            return [];
        }
        List<FindContextNode> context = Ancestors(hit, count, isVisible);
        HashSet<string>? continuation = hit.IsMainline ? null : SelectBranch(hit);
        if (hit.IsMainline || continuation is not null)
        {
            context.AddRange(Descendants(hit, count, continuation, isVisible));
        }
        return context.DistinctBy(node => node.Id).ToList();
    }

    private List<FindContextNode> Ancestors(FindContextNode hit, int count, Func<string, bool> isVisible)
    {
        var context = new List<FindContextNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { hit.Id };
        FindContextNode current = hit;
        while (context.Count < count && !IsRoot(current.ParentId))
        {
            if (!nodes.TryGetValue(current.ParentId!, out FindContextNode? parent)
                || !visited.Add(parent.Id) || !isVisible(parent.Id))
            {
                break;
            }
            context.Add(parent);
            current = parent;
        }
        return context;
    }

    private HashSet<string>? SelectBranch(FindContextNode hit)
    {
        var subtree = new List<GraphNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<FindContextNode>();
        pending.Push(hit);
        while (pending.Count > 0)
        {
            FindContextNode node = pending.Pop();
            if (!visited.Add(node.Id))
            {
                // A cycle cannot provide a uniquely directed continuation.
                return null;
            }
            subtree.Add(new GraphNode(node.Id, node.Id == hit.Id ? null : node.ParentId, node.CreatedAt));
            if (children.TryGetValue(node.Id, out List<FindContextNode>? descendants))
            {
                foreach (FindContextNode child in descendants)
                {
                    pending.Push(child);
                }
            }
        }

        // The room's selected leaf may be on another branch. Apply the same B rule
        // to this subtree, with its own root and no unrelated explicit selection.
        MainlineResult result = MainlineSelector.Select(subtree, selectedLeaf: null, nullRootOnly);
        return result.LeafId is null ? null : result.Members;
    }

    private List<FindContextNode> Descendants(
        FindContextNode hit, int count, HashSet<string>? continuation, Func<string, bool> isVisible)
    {
        var context = new List<FindContextNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { hit.Id };
        FindContextNode current = hit;
        while (context.Count < count && children.TryGetValue(current.Id, out List<FindContextNode>? descendants))
        {
            List<FindContextNode> next = descendants
                .Where(node => continuation is null ? node.IsMainline : continuation.Contains(node.Id))
                .ToList();
            if (next.Count != 1 || !visited.Add(next[0].Id) || !isVisible(next[0].Id))
            {
                break;
            }
            current = next[0];
            context.Add(current);
        }
        return context;
    }

    private bool IsRoot(string? parent) => nullRootOnly ? parent is null : MainlineSelector.IsRoot(parent);
}

internal sealed record FindContextNode(
    long VersionId, string Id, string? ParentId, string? CreatedAt, bool IsMainline);
