using System.Reflection;
using Bag;

namespace Bag.Tests;

public sealed class CommandTests
{
    [Theory]
    [InlineData()]
    [InlineData("import")]
    [InlineData("private-token")]
    [InlineData("import", "private-path", "--db")]
    [InlineData("import", "private-path", "--unknown", "private-value")]
    [InlineData("import", "private-path", "--format", "private-provider")]
    [InlineData("import", "private-path", "--db", "one", "--db", "two")]
    [InlineData("import", "private-path", "--format", "claude", "--format", "claude")]
    [InlineData("import", "private-path", "--db", "")]
    [InlineData("import", "")]
    [InlineData("--version", "private-token")]
    [InlineData("find", "--limit", "0")]
    [InlineData("find", "--limit", "-1")]
    [InlineData("find", "--limit", "2147483648")]
    [InlineData("find", "--context", "-1")]
    [InlineData("find", "--context", "private-token")]
    [InlineData("find", "--from", "private-token")]
    [InlineData("find", "--from", "2026-10-27", "--to", "2026-10-26")]
    [InlineData("find", "--speaker", "private-token")]
    [InlineData("find", "--text", "")]
    [InlineData("find", "--room", "")]
    [InlineData("find", "--from")]
    [InlineData("find", "--room", "one", "--room", "two")]
    [InlineData("find", "--limit", "1", "--limit", "2")]
    [InlineData("find", "--from", "2026-10-26", "--from", "2026-10-26")]
    public void InvalidInputUsesPrivateFixedDiagnostic(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        // Speaker validation loads configuration at invocation time; keep that
        // lookup inside a synthetic directory instead of the user's default path.
        string[] invocation = args.Contains("--speaker")
            ? [.. args, "--db", new SyntheticArchive().DatabasePath]
            : args;
        Assert.Equal(1, BagCommand.Run(invocation, output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal("Invalid arguments. Use --help." + Environment.NewLine, error.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("import", "--help")]
    [InlineData("import", "-h")]
    [InlineData("find", "--help")]
    [InlineData("find", "-h")]
    public void HelpIsGeneratedByCommandLineLibrary(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, BagCommand.Run(args, output, error));
        Assert.Contains(args[0] == "find" ? "find" : "import", output.ToString());
        Assert.Contains("--help", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void VersionUsesTheAssemblyGeneratedFromProjectSettings()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, BagCommand.Run(["--version"], output, error));
        string version = typeof(ImportService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(version + Environment.NewLine, output.ToString());
        Assert.Equal(BagVersion.Current, ImportService.Version);
        Assert.Equal("", error.ToString());
    }
}
