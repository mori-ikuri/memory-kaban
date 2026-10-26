using System.Globalization;

namespace Bag;

internal static class MainlineSelector
{
    public const string RuleVersion = "latest-leaf-created-at/2";

    public static MainlineResult Select(IReadOnlyList<GraphNode> nodes, string? selectedLeaf)
    {
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        var checkedNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphNode node in nodes)
        {
            if (!IsRoot(node.ParentId))
            {
                if (!byId.ContainsKey(node.ParentId!))
                {
                    return Unresolved("missing-parent");
                }
                parents.Add(node.ParentId!);
            }
            var path = new HashSet<string>(StringComparer.Ordinal);
            GraphNode? cursor = node;
            while (cursor is not null && !checkedNodes.Contains(cursor.Id))
            {
                if (!path.Add(cursor.Id))
                {
                    return Unresolved("cycle");
                }
                cursor = !IsRoot(cursor.ParentId) && byId.TryGetValue(cursor.ParentId!, out GraphNode? next) ? next : null;
            }
            checkedNodes.UnionWith(path);
        }
        List<GraphNode> leaves = nodes.Where(n => !parents.Contains(n.Id)).ToList();
        if (leaves.Count == 0)
        {
            return Unresolved("empty");
        }

        GraphNode? leaf;
        string reason;
        if (selectedLeaf is not null)
        {
            leaf = leaves.Find(n => n.Id == selectedLeaf);
            if (leaf is null)
            {
                return Unresolved("invalid-selected-leaf");
            }
            reason = "explicit";
        }
        else if (leaves.Count == 1)
        {
            leaf = leaves[0];
            reason = "single-leaf";
        }
        else
        {
            var dated = new List<(GraphNode Node, DateTimeOffset Time)>();
            foreach (GraphNode candidate in leaves)
            {
                if (!TryTimestamp(candidate.CreatedAt, out DateTimeOffset timestamp))
                {
                    return Unresolved("uncomparable-leaf-timestamp");
                }
                dated.Add((candidate, timestamp));
            }
            leaf = dated.OrderByDescending(n => n.Time).ThenBy(n => n.Node.Id, StringComparer.Ordinal).First().Node;
            reason = "inferred-latest-created-at";
        }

        var members = new HashSet<string>(StringComparer.Ordinal);
        for (GraphNode? node = leaf; node is not null;)
        {
            members.Add(node.Id);
            node = !IsRoot(node.ParentId) ? byId[node.ParentId!] : null;
        }
        return new MainlineResult(reason, leaf.Id, members);
    }

    // All graph traversals use this rule; the stored parent value is never normalized.
    internal static bool IsRoot(string? parent) => parent is null or ""
        or "00000000-0000-0000-0000-000000000000"
        or "00000000-0000-4000-8000-000000000000";

    private static bool TryTimestamp(string? text, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (text is null || text.Length < 20 || text[10] != 'T')
        {
            return false;
        }
        // Require an explicit UTC marker or offset; never depend on this machine's timezone.
        bool hasZone = text.EndsWith('Z') || (text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-');
        return hasZone && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out timestamp);
    }

    private static MainlineResult Unresolved(string reason) => new(reason, null, new HashSet<string>(StringComparer.Ordinal));
}
