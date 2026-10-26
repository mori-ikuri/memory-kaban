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
        ZipArchiveEntry conversations = FindConversations(archive);
        using var store = new SqliteStore(targetPath, hash, source.Length, Path.GetFileName(sourcePath), reader.Provider);
        try
        {
            reader.Read(() => conversations.Open(), store);
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

    private static ZipArchiveEntry FindConversations(ZipArchive archive)
    {
        List<ZipArchiveEntry> candidates = archive.Entries
            .Where(entry => Path.GetFileName(entry.FullName.Replace('\\', '/')) == "conversations.json")
            .ToList();
        if (candidates.Count == 0)
        {
            throw BagErrors.InvalidData(FailureKind.ConversationsMissing);
        }
        if (candidates.Count > 1)
        {
            throw BagErrors.InvalidData(FailureKind.ConversationsMultiple);
        }
        return candidates[0];
    }
}
