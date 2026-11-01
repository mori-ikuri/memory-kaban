namespace Bag;

internal sealed record ChatGptNode(string Id, string? ParentId, IReadOnlyList<string> Children, bool IsUtterance);

internal static class ChatGptGraph
{
    public static MainlineResult Select(IReadOnlyDictionary<string, ChatGptNode> nodes, string? current)
    {
        if (nodes.Count == 0)
        {
            return Unresolved("empty");
        }
        var childSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (ChatGptNode node in nodes.Values)
        {
            if (node.ParentId is not null && !nodes.ContainsKey(node.ParentId))
            {
                return Unresolved("missing-parent");
            }
            var children = new HashSet<string>(StringComparer.Ordinal);
            childSets.Add(node.Id, children);
            foreach (string child in node.Children)
            {
                if (!nodes.TryGetValue(child, out ChatGptNode? next))
                {
                    return Unresolved("missing-child");
                }
                if (!children.Add(child) || next.ParentId != node.Id)
                {
                    return Unresolved("inconsistent-children");
                }
            }
        }
        var checkedNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChatGptNode node in nodes.Values)
        {
            if (node.ParentId is not null && !childSets[node.ParentId].Contains(node.Id))
            {
                return Unresolved("inconsistent-children");
            }
            var path = new HashSet<string>(StringComparer.Ordinal);
            ChatGptNode? cursor = node;
            while (cursor is not null && !checkedNodes.Contains(cursor.Id))
            {
                if (!path.Add(cursor.Id))
                {
                    return Unresolved("cycle");
                }
                if (cursor.ParentId is not null && !nodes.ContainsKey(cursor.ParentId))
                {
                    return Unresolved("missing-parent");
                }
                cursor = cursor.ParentId is null ? null : nodes[cursor.ParentId];
            }
            checkedNodes.UnionWith(path);
        }
        if (current is null)
        {
            return Unresolved("missing-current-node");
        }
        if (!nodes.TryGetValue(current, out ChatGptNode? selected))
        {
            return Unresolved("invalid-current-node");
        }
        var members = new HashSet<string>(StringComparer.Ordinal);
        for (ChatGptNode? cursor = selected; cursor is not null; cursor = cursor.ParentId is null ? null : nodes[cursor.ParentId])
        {
            if (cursor.IsUtterance)
            {
                members.Add(cursor.Id);
            }
        }
        return new MainlineResult("explicit-current-node", current, members);
    }

    public static string? VisibleParent(IReadOnlyDictionary<string, ChatGptNode> nodes, string id)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { id };
        string? parent = nodes[id].ParentId;
        while (parent is not null)
        {
            if (!visited.Add(parent) || !nodes.TryGetValue(parent, out ChatGptNode? node) || node.IsUtterance)
            {
                // Broken/cyclic links remain stopping points, never guessed roots.
                return parent;
            }
            parent = node.ParentId;
        }
        return null;
    }

    private static MainlineResult Unresolved(string reason) => new(reason, null, new(StringComparer.Ordinal));
}
