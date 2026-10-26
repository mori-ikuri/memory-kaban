using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bag;

/// <summary>Stores seals beside the database without opening or modifying the database.</summary>
internal sealed class SealStore
{
    private const string Schema = "bag-seals/1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string path;
    private readonly TimeSpan lockTimeout;
    private readonly Action? beforeReplace;

    internal string MutexName { get; }

    public SealStore(string databasePath) : this(databasePath, TimeSpan.FromSeconds(5), null)
    {
    }

    // The callback permits deterministic failure and external-change tests.
    internal SealStore(string databasePath, TimeSpan lockTimeout, Action? beforeReplace = null)
    {
        path = CanonicalSidecarPath(databasePath);
        this.lockTimeout = lockTimeout;
        this.beforeReplace = beforeReplace;
        string identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        MutexName = (OperatingSystem.IsWindows() ? @"Local\" : "") + "bag-seals-" + hash;
    }

    public SealSnapshot Read()
    {
        using SealReadLease lease = AcquireRead();
        return lease.Snapshot;
    }

    public SealReadLease AcquireRead()
    {
        IDisposable gate = AcquireLock();
        try
        {
            return new SealReadLease(ReadState().Snapshot, gate);
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    public bool Set(SealTarget target, bool seal)
    {
        ValidateTarget(target);
        using IDisposable gate = AcquireLock();
        SealFileState initial = ReadState();
        var updated = initial.Snapshot.Targets.ToHashSet();
        bool changed = seal ? updated.Add(target) : updated.Remove(target);
        if (!changed)
        {
            return false;
        }

        string directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The seal directory is unavailable.");
        }

        byte[] bytes = Serialize(new SealSnapshot(updated));
        string temporaryPath = Path.Combine(directory, "bag-seals-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            temporary.Write(bytes);
            temporary.Flush(flushToDisk: true);
        }

        beforeReplace?.Invoke();
        byte[]? current = ReadBytes();
        if (!SameBytes(initial.Bytes, current))
        {
            throw BagErrors.InvalidData(FailureKind.SealChanged);
        }

        // Same-directory rename exposes either complete file. Never overwrite a
        // file that appeared after an initially absent sidecar was read.
        File.Move(temporaryPath, path, overwrite: initial.Bytes is not null);
        return true;
    }

    private SealFileState ReadState()
    {
        byte[]? bytes = ReadBytes();
        return new SealFileState(bytes, bytes is null ? new SealSnapshot([]) : Parse(bytes));
    }

    private byte[]? ReadBytes()
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw BagErrors.InvalidData(FailureKind.SealInvalid);
            }
            return File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static SealSnapshot Parse(byte[] bytes)
    {
        try
        {
            string json = StrictUtf8.GetString(bytes);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            JsonRules.Validate(root);
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("schema", out JsonElement schema)
                || schema.ValueKind != JsonValueKind.String || schema.GetString() != Schema
                || !root.TryGetProperty("seals", out JsonElement seals) || seals.ValueKind != JsonValueKind.Array)
            {
                throw BagErrors.InvalidData(FailureKind.SealInvalid);
            }

            var targets = new HashSet<SealTarget>();
            foreach (JsonElement item in seals.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("provider", out JsonElement provider)
                    || !item.TryGetProperty("room_id", out JsonElement room)
                    || provider.ValueKind != JsonValueKind.String || room.ValueKind != JsonValueKind.String)
                {
                    throw BagErrors.InvalidData(FailureKind.SealInvalid);
                }
                bool hasMessage = item.TryGetProperty("message_id", out JsonElement message);
                if (item.EnumerateObject().Count() != (hasMessage ? 3 : 2)
                    || (hasMessage && message.ValueKind != JsonValueKind.String))
                {
                    throw BagErrors.InvalidData(FailureKind.SealInvalid);
                }
                var target = new SealTarget(provider.GetString()!, room.GetString()!, hasMessage ? message.GetString() : null);
                ValidateTarget(target);
                if (!targets.Add(target))
                {
                    throw BagErrors.InvalidData(FailureKind.SealInvalid);
                }
            }
            return new SealSnapshot(targets);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or DecoderFallbackException or InvalidOperationException)
        {
            BagErrors.Tag(exception, FailureKind.SealInvalid);
            throw;
        }
    }

    private static void ValidateTarget(SealTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Provider) || string.IsNullOrWhiteSpace(target.RoomId)
            || (target.MessageId is not null && string.IsNullOrWhiteSpace(target.MessageId)))
        {
            throw BagErrors.InvalidData(FailureKind.SealInvalid);
        }
    }

    private static byte[] Serialize(SealSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteStartArray("seals");
            foreach (SealTarget target in snapshot.Targets)
            {
                writer.WriteStartObject();
                writer.WriteString("provider", target.Provider);
                writer.WriteString("room_id", target.RoomId);
                if (target.MessageId is not null)
                {
                    writer.WriteString("message_id", target.MessageId);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private IDisposable AcquireLock()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(lockTimeout);
            }
            catch (AbandonedMutexException)
            {
                // A previous process may have stopped before its atomic rename.
                // The complete sidecar is validated while holding the recovered lock.
                acquired = true;
            }
            if (!acquired)
            {
                throw BagErrors.InvalidData(FailureKind.SealBusy);
            }
            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static bool SameBytes(byte[]? left, byte[]? right)
    {
        return left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    }

    private static string CanonicalSidecarPath(string databasePath)
    {
        string databaseFullPath = Path.GetFullPath(databasePath);
        string directory = Path.GetDirectoryName(databaseFullPath)!;
        string root = Path.GetPathRoot(directory)!;
        string current = root;
        foreach (string segment in Path.GetRelativePath(root, directory)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
        }
        string canonicalDirectory = Path.GetFullPath(current);
        string sidecarPath = Path.Combine(canonicalDirectory, "seals.json");
        string fileName = Path.GetFileName(databaseFullPath);
        if (OperatingSystem.IsWindows())
        {
            fileName = fileName.TrimEnd(' ', '.');
        }
        string canonicalDatabase = Path.Combine(canonicalDirectory, fileName);
        if (File.Exists(canonicalDatabase))
        {
            canonicalDatabase = new FileInfo(canonicalDatabase).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? canonicalDatabase;
        }
        if (string.Equals(canonicalDatabase, sidecarPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw BagErrors.InvalidData(FailureKind.SealInvalid);
        }
        return sidecarPath;
    }

    private sealed record SealFileState(byte[]? Bytes, SealSnapshot Snapshot);

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
