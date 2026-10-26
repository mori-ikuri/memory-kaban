using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class SealStoreTests
{
    private readonly SyntheticArchive archive = new();
    private string Sidecar => Path.Combine(archive.DirectoryPath, "seals.json");

    [Fact]
    public void MissingSidecarReadsAndLeasesCreateNoFiles()
    {
        var store = new SealStore(archive.DatabasePath);

        Assert.Empty(store.Read().Targets);
        using (SealReadLease lease = store.AcquireRead())
        {
            Assert.False(lease.Snapshot.IsSealed("claude", "room", "message"));
            Assert.Empty(lease.Snapshot.Targets);
        }

        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
        Assert.False(File.Exists(archive.DatabasePath));
    }

    [Fact]
    public void MissingDirectoryIsNeverCreated()
    {
        string directory = Path.Combine(archive.DirectoryPath, "absent");
        var store = new SealStore(Path.Combine(directory, "bag.db"));

        Assert.Empty(store.Read().Targets);
        Assert.False(store.Set(new SealTarget("claude", "room"), seal: false));
        Assert.Throws<DirectoryNotFoundException>(() => store.Set(new SealTarget("claude", "room"), seal: true));

        Assert.False(Directory.Exists(directory));
        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
    }

    [Fact]
    public void UnsealingAnAbsentTargetDoesNotCreateSidecar()
    {
        var store = new SealStore(archive.DatabasePath);

        Assert.False(store.Set(new SealTarget("claude", "room", "message"), seal: false));

        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
    }

    [Fact]
    public void SidecarStoresOnlyStableIdentifiersAndNeverChangesDatabase()
    {
        archive.Import(Room("room", "SYNTHETIC_PRIVATE_ROOM", Message("message", "SYNTHETIC_PRIVATE_BODY")));
        byte[] before = SHA256.HashData(File.ReadAllBytes(archive.DatabasePath));
        string[] originalFiles = Directory.GetFiles(archive.DirectoryPath);
        var store = new SealStore(archive.DatabasePath);

        Assert.True(store.Set(new SealTarget("claude", "room"), seal: true));
        Assert.True(store.Set(new SealTarget("claude", "room", "message"), seal: true));

        string json = File.ReadAllText(Sidecar);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", json);
        Assert.DoesNotContain(archive.DirectoryPath, json);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("bag-seals/1", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(new[] { "schema", "seals" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        JsonElement[] targets = document.RootElement.GetProperty("seals").EnumerateArray().ToArray();
        Assert.Equal(2, targets.Length);
        Assert.Equal(new[] { "provider", "room_id" }, targets[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(new[] { "provider", "room_id", "message_id" }, targets[1].EnumerateObject().Select(property => property.Name));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(archive.DatabasePath)));
        Assert.Equal(originalFiles.Append(Sidecar).Order(), Directory.GetFiles(archive.DirectoryPath).Order());
    }

    [Fact]
    public void RoomAndMessageSealsAreProviderScopedAndIndependent()
    {
        var store = new SealStore(archive.DatabasePath);
        var room = new SealTarget("claude", "room");
        var message = new SealTarget("claude", "room", "message");
        store.Set(room, seal: true);
        store.Set(message, seal: true);
        SealSnapshot both = store.Read();

        Assert.True(both.RoomSealed("claude", "room"));
        Assert.True(both.IsSealed("claude", "room", "any-version-message"));
        Assert.False(both.IsSealed("different-provider", "room", "message"));
        Assert.False(both.IsSealed("claude", "different-room", "message"));

        store.Set(room, seal: false);
        SealSnapshot messageOnly = store.Read();
        Assert.False(messageOnly.RoomSealed("claude", "room"));
        Assert.True(messageOnly.IsSealed("claude", "room", "message"));
        Assert.False(messageOnly.IsSealed("claude", "room", "another-message"));
        Assert.True(both.RoomSealed("claude", "room"));
    }

    [Fact]
    public void RepeatedSetAndUnsealAreIdempotentWithoutRewriting()
    {
        var store = new SealStore(archive.DatabasePath);
        var target = new SealTarget("claude", "room", "message");
        Assert.True(store.Set(target, seal: true));
        byte[] sealedBytes = File.ReadAllBytes(Sidecar);
        DateTime sealedTime = File.GetLastWriteTimeUtc(Sidecar);

        Assert.False(store.Set(target, seal: true));
        Assert.Equal(sealedBytes, File.ReadAllBytes(Sidecar));
        Assert.Equal(sealedTime, File.GetLastWriteTimeUtc(Sidecar));

        Assert.True(store.Set(target, seal: false));
        Assert.Empty(store.Read().Targets);
        byte[] unsealedBytes = File.ReadAllBytes(Sidecar);
        DateTime unsealedTime = File.GetLastWriteTimeUtc(Sidecar);
        Assert.False(store.Set(target, seal: false));
        Assert.Equal(unsealedBytes, File.ReadAllBytes(Sidecar));
        Assert.Equal(unsealedTime, File.GetLastWriteTimeUtc(Sidecar));
    }

    [Fact]
    public void SnapshotTargetsAreImmutableAndDetachedFromLaterWrites()
    {
        var store = new SealStore(archive.DatabasePath);
        var first = new SealTarget("claude", "部屋", "発言🙂");
        store.Set(first, seal: true);
        SealSnapshot snapshot = store.Read();

        Assert.Equal(first, Assert.Single(snapshot.Targets));
        Assert.Throws<NotSupportedException>(() => ((IList<SealTarget>)snapshot.Targets).Add(new SealTarget("claude", "other")));
        store.Set(first, seal: false);

        Assert.Equal(first, Assert.Single(snapshot.Targets));
        Assert.Empty(store.Read().Targets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"bag-seals/2\",\"seals\":[]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"schema\":\"bag-seals/1\",\"seals\":[]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[],\"seals\":[]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[],\"body\":\"SYNTHETIC_PRIVATE_BODY\"}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":{}}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[null]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"provider\":\"claude\"}]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":null,\"room_id\":\"room\"}]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":\"claude\",\"room_id\":42}]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":\"claude\",\"room_id\":\" \"}]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"message_id\":null}]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"message_id\":\"\"}]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":\"claude\",\"room_id\":\"room\",\"body\":\"SYNTHETIC_PRIVATE_BODY\"}]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":\"claude\",\"room_id\":\"room\"},{\"provider\":\"claude\",\"room_id\":\"room\"}]}")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[]} trailing")]
    [InlineData("{\"schema\":\"bag-seals/1\",\"seals\":[],}")]
    public void InvalidSidecarFailsClosedAndCannotBeOverwritten(string json)
    {
        byte[] before = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(Sidecar, before);
        var store = new SealStore(archive.DatabasePath);

        AssertFailure(FailureKind.SealInvalid, () => store.Read());
        AssertFailure(FailureKind.SealInvalid, () => store.Set(new SealTarget("claude", "room"), seal: true));

        Assert.Equal(before, File.ReadAllBytes(Sidecar));
        Assert.Equal(new[] { Sidecar }, Directory.GetFiles(archive.DirectoryPath));
    }

    [Fact]
    public void InvalidUtf8InsideAJsonStringFailsClosed()
    {
        byte[] prefix = Encoding.UTF8.GetBytes("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":\"claude\",\"room_id\":\"");
        byte[] suffix = Encoding.UTF8.GetBytes("\"}]}");
        byte[] before = [.. prefix, 0xFF, .. suffix];
        File.WriteAllBytes(Sidecar, before);

        AssertFailure(FailureKind.SealInvalid, () => new SealStore(archive.DatabasePath).Read());

        Assert.Equal(before, File.ReadAllBytes(Sidecar));
    }

    [Fact]
    public void UnreadableSidecarFailsClosedWithoutCreatingTemporaryFiles()
    {
        var store = new SealStore(archive.DatabasePath);
        store.Set(new SealTarget("claude", "room"), seal: true);
        using (var held = new FileStream(Sidecar, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AssertFailure(FailureKind.FileAccess, () => store.Read());
            AssertFailure(FailureKind.FileAccess, () => store.Set(new SealTarget("claude", "other"), seal: true));
        }
        Assert.Equal(new[] { Sidecar }, Directory.GetFiles(archive.DirectoryPath));
        Assert.True(store.Read().RoomSealed("claude", "room"));
    }

    [Fact]
    public void DirectoryAtSidecarPathFailsClosed()
    {
        Directory.CreateDirectory(Sidecar);

        AssertFailure(FailureKind.SealInvalid, () => new SealStore(archive.DatabasePath).Read());

        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
    }

    [Theory]
    [InlineData("", "room", null)]
    [InlineData("claude", " ", null)]
    [InlineData("claude", "room", "")]
    public void InvalidTargetsCannotCreateSidecar(string provider, string room, string? message)
    {
        var store = new SealStore(archive.DatabasePath);

        AssertFailure(FailureKind.SealInvalid, () => store.Set(new SealTarget(provider, room, message), seal: true));

        Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
    }

    [Fact]
    public void DatabaseCannotBeTheSidecarPath()
    {
        byte[] before = "SYNTHETIC_DATABASE_PLACEHOLDER"u8.ToArray();
        File.WriteAllBytes(Sidecar, before);

        AssertFailure(FailureKind.SealInvalid, () => new SealStore(Sidecar));
        if (OperatingSystem.IsWindows())
        {
            AssertFailure(FailureKind.SealInvalid, () => new SealStore(Path.Combine(archive.DirectoryPath, "SEALS.JSON")));
            AssertFailure(FailureKind.SealInvalid, () => new SealStore(Sidecar + "."));
        }

        Assert.Equal(before, File.ReadAllBytes(Sidecar));
    }

    [Fact]
    public void EquivalentDirectoryPathsShareOneMutex()
    {
        var direct = new SealStore(archive.DatabasePath);
        var relativeParts = new SealStore(Path.Combine(archive.DirectoryPath, ".", "child", "..", "another.db"));

        Assert.Equal(direct.MutexName, relativeParts.MutexName);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(direct.MutexName, new SealStore(archive.DatabasePath.ToUpperInvariant()).MutexName);
        }
    }

    [Fact]
    public void FailureBeforeReplacementPreservesPreviousCompleteFile()
    {
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "original"), seal: true);
        byte[] before = File.ReadAllBytes(Sidecar);
        var failing = new SealStore(archive.DatabasePath, TimeSpan.FromSeconds(5),
            () => throw new IOException("SYNTHETIC_PRIVATE_FAILURE"));

        AssertFailure(FailureKind.FileAccess, () => failing.Set(new SealTarget("claude", "new"), seal: true));

        Assert.Equal(before, File.ReadAllBytes(Sidecar));
        Assert.Equal(new SealTarget("claude", "original"), Assert.Single(new SealStore(archive.DatabasePath).Read().Targets));
        Assert.Single(Directory.GetFiles(archive.DirectoryPath, "bag-seals-*.tmp"));
    }

    [Fact]
    public void UnexpectedExternalChangesArePreservedInsteadOfOverwritten()
    {
        new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "original"), seal: true);
        byte[] external = Encoding.UTF8.GetBytes("{\"schema\":\"bag-seals/1\",\"seals\":[{\"provider\":\"claude\",\"room_id\":\"external\"}]}");
        var store = new SealStore(archive.DatabasePath, TimeSpan.FromSeconds(5), () => File.WriteAllBytes(Sidecar, external));

        AssertFailure(FailureKind.SealChanged, () => store.Set(new SealTarget("claude", "new"), seal: true));

        Assert.Equal(external, File.ReadAllBytes(Sidecar));
        Assert.True(new SealStore(archive.DatabasePath).Read().RoomSealed("claude", "external"));
    }

    [Fact]
    public void UnexpectedCreationOfInitiallyMissingSidecarIsPreserved()
    {
        byte[] external = Encoding.UTF8.GetBytes("{\"schema\":\"bag-seals/1\",\"seals\":[]}");
        var store = new SealStore(archive.DatabasePath, TimeSpan.FromSeconds(5), () => File.WriteAllBytes(Sidecar, external));

        AssertFailure(FailureKind.SealChanged, () => store.Set(new SealTarget("claude", "new"), seal: true));

        Assert.Equal(external, File.ReadAllBytes(Sidecar));
    }

    [Fact]
    public void ConcurrentSealAndUnsealUpdatesDoNotLoseOtherTargets()
    {
        const int workers = 12;
        var initial = new SealStore(archive.DatabasePath);
        for (int index = 0; index < workers; index++)
        {
            initial.Set(new SealTarget("claude", "old-" + index), seal: true);
        }
        using var start = new ManualResetEventSlim();
        var failures = new ConcurrentQueue<Exception>();
        Thread[] threads = Enumerable.Range(0, workers).Select(index => new Thread(() =>
        {
            try
            {
                start.Wait();
                var store = new SealStore(archive.DatabasePath);
                store.Set(new SealTarget("claude", "new-" + index), seal: true);
                store.Set(new SealTarget("claude", "old-" + index), seal: false);
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        }) { IsBackground = true }).ToArray();
        foreach (Thread thread in threads)
        {
            thread.Start();
        }
        start.Set();
        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        }

        Assert.Empty(failures);
        Assert.Equal(Enumerable.Range(0, workers).Select(index => "new-" + index).Order(),
            initial.Read().Targets.Select(target => target.RoomId).Order());
        Assert.Equal(new[] { Sidecar }, Directory.GetFiles(archive.DirectoryPath));
    }

    [Fact]
    public void ReadLeaseKeepsWritersOutUntilTheReadOperationEnds()
    {
        var store = new SealStore(archive.DatabasePath);
        using var attempted = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        Thread writer;
        using (SealReadLease lease = store.AcquireRead())
        {
            writer = new Thread(() =>
            {
                try
                {
                    attempted.Set();
                    new SealStore(archive.DatabasePath).Set(new SealTarget("claude", "room"), seal: true);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    finished.Set();
                }
            }) { IsBackground = true };
            writer.Start();
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(finished.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.Empty(lease.Snapshot.Targets);
            Assert.False(File.Exists(Sidecar));
        }

        Assert.True(writer.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.True(store.Read().RoomSealed("claude", "room"));
    }

    [Fact]
    public async Task AnotherProcessHoldingTheMutexBlocksReadsAndWrites()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var store = new SealStore(archive.DatabasePath, TimeSpan.FromMilliseconds(150));
        var start = new ProcessStartInfo("powershell")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$gate = [Threading.Mutex]::new($false, $env:BAG_TEST_MUTEX); try { $null = $gate.WaitOne(); [Console]::WriteLine('LOCKED'); [Console]::Out.Flush(); $null = [Console]::ReadLine() } finally { $gate.ReleaseMutex(); $gate.Dispose() }");
        start.Environment["BAG_TEST_MUTEX"] = store.MutexName;
        using Process process = Process.Start(start)!;
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("LOCKED", ready);

            AssertFailure(FailureKind.SealBusy, () => store.Read());
            AssertFailure(FailureKind.SealBusy, () => store.Set(new SealTarget("claude", "room"), seal: true));
            Assert.Empty(Directory.GetFiles(archive.DirectoryPath));
        }
        finally
        {
            await process.StandardInput.WriteLineAsync("release");
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await process.StandardError.ReadToEndAsync());
        Assert.True(store.Set(new SealTarget("claude", "room"), seal: true));
    }

    private static void AssertFailure(FailureKind expected, Action action)
    {
        Exception? exception = Record.Exception(action);
        Assert.NotNull(exception);
        Assert.True(BagErrors.IsHandled(exception));
        Assert.Equal(expected, BagErrors.Classify(exception));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", BagErrors.GetMessage(exception));
    }
}
