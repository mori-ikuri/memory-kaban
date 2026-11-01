using System.CommandLine;
using System.Text.Json;

namespace Bag;

internal static class ImportCommand
{
    // Keep the original internal entry point for the importer regression suite.
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        return BagCommand.Run(args, output, error);
    }

    public static Command Create(TextWriter output)
    {
        var zip = new Argument<string>("zip")
        {
            Description = "Claude or ChatGPT export ZIP to read without extracting files."
        };
        zip.Validators.Add(result =>
        {
            if (!result.Errors.Any() && string.IsNullOrWhiteSpace(result.GetValue(zip)))
            {
                result.AddError("Provide a ZIP path.");
            }
        });
        Option<string?> database = BagCommand.DatabaseOption();
        var format = new Option<string>("--format")
        {
            Description = "Export provider.",
            DefaultValueFactory = _ => "claude"
        };
        format.AcceptOnlyFromAmong("claude", "chatgpt");
        format.Validators.Add(result =>
        {
            if (result.IdentifierTokenCount > 1)
            {
                result.AddError("Provide one export format.");
            }
        });
        var command = new Command("import", "Import an immutable Claude or ChatGPT export ZIP.")
        {
            zip, database, format
        };
        command.SetAction(parsed =>
        {
            string path = BagCommand.DatabasePath(parsed.GetValue(database), createDirectory: true);
            IExportReader reader = parsed.GetValue(format) == "chatgpt"
                ? new ChatGptExportReader() : new ClaudeExportReader();
            ImportCounts counts = new ImportService(reader).Import(parsed.GetValue(zip)!, path);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                rooms = counts.Rooms,
                messages = counts.Messages,
                new_versions = counts.NewVersions,
                unresolved_rooms = counts.UnresolvedRooms
            }));
            return 0;
        });
        return command;
    }
}
