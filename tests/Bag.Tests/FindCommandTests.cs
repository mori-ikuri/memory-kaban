using System.Text.Json;
using Bag;
using static Bag.Tests.SyntheticArchive;

namespace Bag.Tests;

public sealed class FindCommandTests
{
    private readonly SyntheticArchive archive = new();

    [Fact]
    public void JsonIsMachineReadableAndPreservesBodyAndProvenance()
    {
        const string body = "  鞄\r\n\t次の行\n\0e\u0301🙂  ";
        string source = archive.Import(Room("room", "架空の部屋",
            Message("one", body, createdAt: "2026-10-26T04:00:00.1234567Z", speaker: "assistant")));
        var output = new StringWriter();
        var error = new StringWriter();

        int code = BagCommand.Run(["find", "--db", archive.DatabasePath, "--speaker", "assistant",
            "--text", "鞄", "--from", "2026-10-26 13:00", "--to", "2026-10-26T04:01:00Z", "--json"], output, error);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        JsonElement root = json.RootElement;
        Assert.Equal("bag-find/1", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("match_count").GetInt32());
        JsonElement message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal(body, message.GetProperty("body").GetString());
        Assert.Equal("2026-10-26T04:00:00.1234567+00:00", message.GetProperty("timestamp_utc").GetString());
        Assert.Equal("2026-10-26T13:00:00.1234567+09:00", message.GetProperty("timestamp_jst").GetString());
        Assert.Equal(source, message.GetProperty("sources")[0].GetProperty("source_sha256").GetString());
        Assert.DoesNotContain(archive.DatabasePath, output.ToString());
    }

    [Fact]
    public void HumanOutputUsesJapanTimeAndWritesTheExactBodyBetweenFraming()
    {
        const string body = " \r\n鞄\n\tend  ";
        archive.Import(Room("room", "架空の部屋",
            Message("one", body, createdAt: "2026-10-26T04:00:00Z")));
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, BagCommand.Run(["find", "--db", archive.DatabasePath], output, error));

        Assert.Contains("2026-10-26 13:00:00 +09:00", output.ToString());
        Assert.Contains("架空の部屋", output.ToString());
        Assert.Contains("user", output.ToString());
        Assert.Contains("Body:" + Environment.NewLine + body + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoMatchesIsSuccess(bool json)
    {
        archive.Import(Room("room", "Synthetic", Message("one", "plain")));
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = json
            ? ["find", "--db", archive.DatabasePath, "--text", "absent", "--json"]
            : ["find", "--db", archive.DatabasePath, "--text", "absent"];

        Assert.Equal(0, BagCommand.Run(args, output, error));
        Assert.Equal("", error.ToString());
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            Assert.Equal(0, document.RootElement.GetProperty("match_count").GetInt32());
            Assert.Empty(document.RootElement.GetProperty("messages").EnumerateArray());
        }
        else
        {
            Assert.Equal("No matches." + Environment.NewLine, output.ToString());
        }
    }

    [Fact]
    public void MissingDatabaseReportsAPathFreeFailureWithoutCreatingIt()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, BagCommand.Run(["find", "--db", archive.DatabasePath], output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal("File access failed." + Environment.NewLine, error.ToString());
        Assert.False(File.Exists(archive.DatabasePath));
    }

    [Fact]
    public void ResponseFilesAreNotExpanded()
    {
        string response = Path.Combine(archive.DirectoryPath, "synthetic-response.txt");
        File.WriteAllText(response, "--version");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, BagCommand.Run(["@" + response], output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal("Invalid arguments. Use --help." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void UnknownTimeHasAnExplicitLabelAndNullJsonTimestamps()
    {
        archive.Import(Room("room", "Synthetic", Message("one", "鞄", createdAt: null)));
        var human = new StringWriter();
        Assert.Equal(0, BagCommand.Run(["find", "--db", archive.DatabasePath], human, TextWriter.Null));
        Assert.Contains("[日時不明]", human.ToString());

        var output = new StringWriter();
        Assert.Equal(0, BagCommand.Run(["find", "--db", archive.DatabasePath, "--json"], output, TextWriter.Null));
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement message = document.RootElement.GetProperty("messages")[0];
        Assert.Equal(JsonValueKind.Null, message.GetProperty("created_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, message.GetProperty("timestamp_utc").ValueKind);
        Assert.Equal(JsonValueKind.Null, message.GetProperty("timestamp_jst").ValueKind);
    }

    [Fact]
    public async Task ExecutableFindAcceptsQuotedJapanTimeAndEmitsUtf8Json()
    {
        archive.Import(Room("room", "架空の部屋", Message("one", "鞄\r\n本文", createdAt: "2026-10-26T04:00:00Z")));
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bag.dll"));
        foreach (string argument in new[]
        {
            "find", "--db", archive.DatabasePath, "--from", "2026-10-26 13:00",
            "--to", "2026-10-26 13:00", "--speaker", "user", "--text", "鞄", "--json"
        })
        {
            start.ArgumentList.Add(argument);
        }
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await stderr);
        string json = await stdout;
        Assert.Contains("鞄", json);
        Assert.Contains("本文", json);
        Assert.Contains("架空の部屋", json);
        Assert.DoesNotContain("\\u9784", json, StringComparison.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("鞄\r\n本文", document.RootElement.GetProperty("messages")[0].GetProperty("body").GetString());
    }

    [Fact]
    public void JapanTimeOverflowDoesNotFailOrPartiallyWriteAResult()
    {
        const string date = "9999-12-31T23:00:00Z";
        archive.Import(Room("room", "Synthetic", Message("one", "鞄", createdAt: date)));
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, BagCommand.Run(["find", "--db", archive.DatabasePath, "--json"], output, error));
        Assert.Equal("", error.ToString());
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement message = document.RootElement.GetProperty("messages")[0];
        Assert.Equal(date, message.GetProperty("created_at").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("timestamp_jst").ValueKind);
        Assert.Equal("鞄", message.GetProperty("body").GetString());
    }
}
