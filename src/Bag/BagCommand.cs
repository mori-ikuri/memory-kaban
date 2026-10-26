using System.CommandLine;
using System.CommandLine.Invocation;

namespace Bag;

internal static class BagCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var root = new RootCommand("Preserve and find conversations in a local archive.");
        root.Subcommands.Add(ImportCommand.Create(output));
        root.Subcommands.Add(FindCommand.Create(output, error));
        root.Subcommands.Add(SealCommand.Create(seal: true, output));
        root.Subcommands.Add(SealCommand.Create(seal: false, output));
        root.Subcommands.Add(McpCommand.Create(error));
        root.Options.OfType<VersionOption>().Single().Action = new VersionAction(output);
        root.SetAction(_ =>
        {
            error.WriteLine("Invalid arguments. Use --help.");
            return 1;
        });

        // Response files would read arbitrary files before command validation.
        var parser = new ParserConfiguration { ResponseFileTokenReplacer = null };
        ParseResult parsed;
        try
        {
            parsed = root.Parse(args, parser);
        }
        catch (InvalidOperationException)
        {
            // Typed validators can encounter a failed conversion before parse errors
            // are finalized. The exception can contain the rejected private value.
            error.WriteLine("Invalid arguments. Use --help.");
            return 1;
        }
        if (parsed.Errors.Count != 0)
        {
            // Library diagnostics can echo input values, including paths and text.
            error.WriteLine("Invalid arguments. Use --help.");
            return 1;
        }
        try
        {
            return parsed.Invoke(new InvocationConfiguration
            {
                Output = output,
                Error = error,
                EnableDefaultExceptionHandler = false
            });
        }
        catch (Exception exception) when (BagErrors.IsHandled(exception))
        {
            error.WriteLine(BagErrors.GetMessage(exception));
            return 2;
        }
    }

    public static Option<string?> DatabaseOption()
    {
        var option = new Option<string?>("--db")
        {
            Description = "SQLite database path. Defaults to the local application data directory."
        };
        option.Validators.Add(result =>
        {
            if (result.IdentifierTokenCount > 1 || (!result.Errors.Any() && string.IsNullOrWhiteSpace(result.GetValue(option))))
            {
                result.AddError("Provide one database path.");
            }
        });
        return option;
    }

    public static string DatabasePath(string? path, bool createDirectory)
    {
        if (path is not null)
        {
            return path;
        }
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bag");
        if (createDirectory)
        {
            Directory.CreateDirectory(directory);
        }
        return Path.Combine(directory, "bag.db");
    }

    private sealed class VersionAction(TextWriter output) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            output.WriteLine(BagVersion.Current);
            return 0;
        }
    }
}
