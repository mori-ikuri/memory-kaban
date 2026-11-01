using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Bag;

/// <summary>Owns the read-only source handle and atomic import lifetime.</summary>
internal sealed class ImportService(IExportReader reader)
{
    public static string Version => BagVersion.Current;

    public ImportCounts Import(string zipPath, string databasePath)
    {
        string sourcePath = Path.GetFullPath(zipPath);
        string targetPath = Path.GetFullPath(databasePath);
        if (string.Equals(sourcePath, targetPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw BagErrors.InvalidData(FailureKind.SourceConflict);
        }
        using FileStream source = OpenSource(sourcePath);
        string hash = Convert.ToHexString(SHA256.HashData(source));
        source.Position = 0;
        using ZipArchive archive = OpenArchive(source);
        IReadOnlyList<ZipArchiveEntry> conversations = FindConversations(archive, reader.SupportsNumberedConversations);
        using var store = new SqliteStore(targetPath, hash, source.Length, Path.GetFileName(sourcePath), reader.Provider);
        try
        {
            // All parts share the original ZIP's identity and one atomic transaction.
            // Each reader pass opens the entry directly; no extraction or concatenation.
            foreach (ZipArchiveEntry part in conversations)
            {
                reader.Read(() => part.Open(), store);
            }
        }
        catch (InvalidOperationException exception)
        {
            // JSON string decoding can report malformed UTF-8 with this exception.
            BagErrors.Tag(exception, FailureKind.ExportInvalid);
            throw;
        }
        source.Position = 0;
        if (Convert.ToHexString(SHA256.HashData(source)) != hash)
        {
            throw BagErrors.InvalidData(FailureKind.SourceChanged);
        }
        return store.Commit();
    }

    private static FileStream OpenSource(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            BagErrors.Tag(exception, FailureKind.ZipNotFound);
            throw;
        }
    }

    private static ZipArchive OpenArchive(Stream source)
    {
        try
        {
            var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                // Reading Entries also validates the central directory.
                _ = archive.Entries.Count;
                return archive;
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        catch (InvalidDataException exception)
        {
            BagErrors.Tag(exception, FailureKind.ZipInvalid);
            throw;
        }
    }

    private static IReadOnlyList<ZipArchiveEntry> FindConversations(ZipArchive archive, bool numbered)
    {
        if (numbered)
        {
            return FindChatGptConversations(archive);
        }
        List<ZipArchiveEntry> candidates = archive.Entries
            .Where(entry => Basename(entry) == "conversations.json")
            .ToList();
        if (candidates.Count == 0)
        {
            throw BagErrors.InvalidData(FailureKind.ConversationsMissing);
        }
        if (candidates.Count > 1)
        {
            throw BagErrors.InvalidData(FailureKind.ConversationsMultiple);
        }
        return candidates;
    }

    private static IReadOnlyList<ZipArchiveEntry> FindChatGptConversations(ZipArchive archive)
    {
        var single = new List<ZipArchiveEntry>();
        var parts = new SortedDictionary<int, ZipArchiveEntry>();
        string? partDirectory = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = Basename(entry);
            if (name == "conversations.json")
            {
                single.Add(entry);
                continue;
            }
            if (!name.StartsWith("conversations", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // A malformed conversation filename must not silently disappear from
            // an otherwise valid numbered set (or from a legacy single-file ZIP).
            const string prefix = "conversations-";
            const string suffix = ".json";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)
                || name.Length <= prefix.Length + suffix.Length)
            {
                throw BagErrors.InvalidData(FailureKind.ExportInvalid);
            }
            ReadOnlySpan<char> digits = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
            foreach (char digit in digits)
            {
                if (digit is < '0' or > '9')
                {
                    throw BagErrors.InvalidData(FailureKind.ExportInvalid);
                }
            }
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || !parts.TryAdd(index, entry))
            {
                throw BagErrors.InvalidData(FailureKind.ExportInvalid);
            }
            string path = entry.FullName.Replace('\\', '/');
            string directory = path[..(path.Length - name.Length)];
            partDirectory ??= directory;
            if (directory != partDirectory)
            {
                throw BagErrors.InvalidData(FailureKind.ExportInvalid);
            }
        }
        if (single.Count != 0 && parts.Count != 0)
        {
            throw BagErrors.InvalidData(FailureKind.ExportInvalid);
        }
        if (single.Count > 1)
        {
            throw BagErrors.InvalidData(FailureKind.ConversationsMultiple);
        }
        if (single.Count == 1)
        {
            return single;
        }
        if (parts.Count == 0)
        {
            throw BagErrors.InvalidData(FailureKind.ConversationsMissing);
        }
        int expected = 0;
        foreach (int index in parts.Keys)
        {
            if (index != expected++)
            {
                throw BagErrors.InvalidData(FailureKind.ExportInvalid);
            }
        }
        return parts.Values.ToList();
    }

    private static string Basename(ZipArchiveEntry entry) => Path.GetFileName(entry.FullName.Replace('\\', '/'));
}
