using Microsoft.Data.Sqlite;

namespace Bag;

/// <summary>Searches immutable saved versions without modifying the archive.</summary>
internal sealed class FindService
{
    public FindResult Find(string databasePath, FindRequest request, bool includeSealed = false, int? totalLimit = null)
    {
        if (totalLimit is int cap)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
            request = request with { Limit = Math.Min(request.Limit, cap), Context = Math.Min(request.Context, cap) };
        }
        using SealReadLease lease = new SealStore(databasePath).AcquireRead();
        SealSnapshot seals = lease.Snapshot;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        connection.CreateFunction<string?, long?>("bag_utc_ticks",
            value => FindTime.ParseStored(value)?.UtcTicks, isDeterministic: true);
        connection.CreateCollation("bag_ordinal", (left, right) => string.Compare(left, right, StringComparison.Ordinal));
        connection.CreateFunction<string, string, string, bool>("bag_visible",
            (provider, room, message) => includeSealed || !seals.IsSealed(provider, room, message), isDeterministic: true);

        // A deferred transaction provides one snapshot across selection and provenance.
        // The connection's read-only mode also prevents accidental database creation.
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        DatabaseCompatibility.Verify(connection, transaction);
        List<long> matches = SelectMatches(connection, transaction, request);
        var messages = new Dictionary<long, MessageBuilder>();
        bool contextTruncated = false;
        var selected = new List<(MessageBuilder Message, IReadOnlyList<FindSource> Sources)>();
        foreach (long versionId in matches)
        {
            MessageBuilder message = ReadMessage(connection, transaction, versionId);
            message.IsMatch = true;
            List<FindSource> sources = ReadMatchingSources(connection, transaction, message.Key, versionId, request);
            foreach (FindSource source in sources)
            {
                message.AddSource(source);
            }
            messages.Add(versionId, message);
            selected.Add((message, sources));
        }

        if (request.Context > 0)
        {
            var graphs = new Dictionary<(string Source, string Provider, string Room), FindContextGraph>();
            foreach ((MessageBuilder hit, IReadOnlyList<FindSource> sources) in selected)
            {
                foreach (FindSource source in sources)
                {
                    var graphKey = (source.SourceSha256, hit.Key.Provider, hit.Key.RoomId);
                    if (!graphs.TryGetValue(graphKey, out FindContextGraph? graph))
                    {
                        graph = FindContextGraph.Read(connection, transaction, source.SourceSha256, hit.Key);
                        graphs.Add(graphKey, graph);
                    }
                    contextTruncated |= AddContext(connection, transaction, messages, hit, source,
                        graph.Around(hit.Key.MessageId, request.Context,
                            messageId => includeSealed || !seals.IsSealed(hit.Key.Provider, hit.Key.RoomId, messageId)), totalLimit);
                }
            }
        }

        List<FindMessage> results = messages.Values
            .Select(message => message.Build())
            .OrderBy(message => message.Timestamp is null)
            .ThenBy(message => message.Timestamp)
            .ThenBy(message => message.Key.Provider, StringComparer.Ordinal)
            .ThenBy(message => message.Key.RoomId, StringComparer.Ordinal)
            .ThenBy(message => message.Key.MessageId, StringComparer.Ordinal)
            .ThenBy(message => message.Key.VersionHash, StringComparer.Ordinal)
            .ToList();
        return new FindResult(matches.Count, results, contextTruncated);
    }

    private static List<long> SelectMatches(
        SqliteConnection connection, SqliteTransaction transaction, FindRequest request)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT v.version_id
            FROM message_versions AS v
            WHERE ($from IS NULL OR bag_utc_ticks(v.created_at) >= $from)
              AND bag_visible(v.provider, v.room_id, v.message_id)
              AND ($to IS NULL OR bag_utc_ticks(v.created_at) <= $to)
              AND ($speaker IS NULL OR v.speaker = $speaker)
              AND ($text IS NULL OR instr(v.body, $text) > 0)
              AND EXISTS (
                  SELECT 1
                  FROM source_messages AS sm
                  JOIN room_snapshots AS rs
                    ON rs.source_sha = sm.source_sha
                   AND rs.provider = sm.provider AND rs.room_id = sm.room_id
                  WHERE sm.version_id = v.version_id
                    AND sm.provider = v.provider AND sm.room_id = v.room_id
                    AND sm.message_id = v.message_id
                    AND ($all_branches = 1 OR sm.is_mainline = 1)
                    AND ($room IS NULL OR sm.room_id = $room OR instr(rs.name, $room) > 0)
              )
            ORDER BY bag_utc_ticks(v.created_at) IS NULL, bag_utc_ticks(v.created_at),
                     v.provider COLLATE bag_ordinal, v.room_id COLLATE bag_ordinal,
                     v.message_id COLLATE bag_ordinal, v.version_hash COLLATE bag_ordinal
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$from", (object?)request.From?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)request.To?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$speaker", (object?)request.Speaker ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", (object?)request.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("$room", (object?)request.Room ?? DBNull.Value);
        command.Parameters.AddWithValue("$all_branches", request.AllBranches ? 1 : 0);
        command.Parameters.AddWithValue("$limit", request.Limit);
        using SqliteDataReader reader = command.ExecuteReader();
        var matches = new List<long>();
        while (reader.Read())
        {
            matches.Add(reader.GetInt64(0));
        }
        return matches;
    }

    private static MessageBuilder ReadMessage(
        SqliteConnection connection, SqliteTransaction transaction, long versionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT provider, room_id, message_id, version_hash, speaker, body, created_at
            FROM message_versions WHERE version_id = $version
            """;
        command.Parameters.AddWithValue("$version", versionId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw BagErrors.InvalidData(FailureKind.DatabaseIncompatible);
        }
        var key = new FindMessageKey(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        string? createdAt = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new MessageBuilder(key, reader.GetString(4), reader.GetString(5), createdAt);
    }

    private static List<FindSource> ReadMatchingSources(
        SqliteConnection connection, SqliteTransaction transaction,
        FindMessageKey key, long versionId, FindRequest request)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sm.source_sha, rs.name, sm.is_mainline
            FROM source_messages AS sm
            JOIN room_snapshots AS rs
              ON rs.source_sha = sm.source_sha
             AND rs.provider = sm.provider AND rs.room_id = sm.room_id
            WHERE sm.version_id = $version
              AND sm.provider = $provider AND sm.room_id = $room_id AND sm.message_id = $message_id
              AND ($all_branches = 1 OR sm.is_mainline = 1)
              AND ($room IS NULL OR sm.room_id = $room OR instr(rs.name, $room) > 0)
            ORDER BY sm.source_sha COLLATE BINARY
            """;
        command.Parameters.AddWithValue("$version", versionId);
        command.Parameters.AddWithValue("$provider", key.Provider);
        command.Parameters.AddWithValue("$room_id", key.RoomId);
        command.Parameters.AddWithValue("$message_id", key.MessageId);
        command.Parameters.AddWithValue("$all_branches", request.AllBranches ? 1 : 0);
        command.Parameters.AddWithValue("$room", (object?)request.Room ?? DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        var sources = new List<FindSource>();
        while (reader.Read())
        {
            sources.Add(new FindSource(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1, []));
        }
        return sources;
    }

    private static bool AddContext(
        SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<long, MessageBuilder> messages, MessageBuilder hit, FindSource source,
        IReadOnlyList<FindContextNode> context, int? totalLimit)
    {
        bool truncated = false;
        foreach (FindContextNode node in context)
        {
            if (!messages.TryGetValue(node.VersionId, out MessageBuilder? message))
            {
                if (totalLimit is int cap && messages.Count >= cap)
                {
                    truncated = true;
                    continue;
                }
                message = ReadMessage(connection, transaction, node.VersionId);
                messages.Add(node.VersionId, message);
            }
            message.AddSource(new FindSource(source.SourceSha256, source.RoomName, node.IsMainline, [hit.Key]));
        }
        return truncated;
    }

    private sealed class MessageBuilder(
        FindMessageKey key, string speaker, string body, string? createdAt)
    {
        private readonly Dictionary<string, FindSource> sources = new(StringComparer.Ordinal);

        public FindMessageKey Key { get; } = key;
        public DateTimeOffset? Timestamp { get; } = FindTime.ParseStored(createdAt);
        public bool IsMatch { get; set; }

        public void AddSource(FindSource source)
        {
            if (sources.TryGetValue(source.SourceSha256, out FindSource? previous))
            {
                List<FindMessageKey> links = previous.ContextFor.Concat(source.ContextFor)
                    .Distinct()
                    .OrderBy(key => key.Provider, StringComparer.Ordinal)
                    .ThenBy(key => key.RoomId, StringComparer.Ordinal)
                    .ThenBy(key => key.MessageId, StringComparer.Ordinal)
                    .ThenBy(key => key.VersionHash, StringComparer.Ordinal)
                    .ToList();
                sources[source.SourceSha256] = previous with { ContextFor = links };
            }
            else
            {
                sources.Add(source.SourceSha256, source);
            }
        }

        public FindMessage Build()
        {
            return new FindMessage(Key, speaker, body, createdAt, Timestamp, IsMatch,
                sources.Values.OrderBy(source => source.SourceSha256, StringComparer.Ordinal).ToList());
        }
    }
}
