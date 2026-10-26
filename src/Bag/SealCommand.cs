using System.CommandLine;
using System.Text.Json;

namespace Bag;

internal static class SealCommand
{
    public static Command Create(bool seal, TextWriter output)
    {
        Option<string?> database = BagCommand.DatabaseOption();
        Option<string?> room = IdentifierOption("--room", "Exact room ID. Also disambiguates a message ID.");
        Option<string?> message = IdentifierOption("--message", "Exact message ID; seals every saved version.");
        var command = new Command(seal ? "seal" : "unseal", seal
            ? "Hide a room or message using the separate seal file."
            : "Remove a room or message seal from the separate seal file.")
        {
            database, room, message
        };
        command.Validators.Add(result =>
        {
            if (result.GetValue(room) is null && result.GetValue(message) is null)
            {
                result.AddError("Provide a room or message ID.");
            }
        });
        command.SetAction(parsed =>
        {
            string path = BagCommand.DatabasePath(parsed.GetValue(database), createDirectory: false);
            bool changed = new SealService().Set(path, parsed.GetValue(room), parsed.GetValue(message), seal);
            output.WriteLine(JsonSerializer.Serialize(new { changed = changed ? 1 : 0 }));
            return 0;
        });
        return command;
    }

    private static Option<string?> IdentifierOption(string name, string description)
    {
        var option = new Option<string?>(name) { Description = description };
        option.Validators.Add(result =>
        {
            if (result.IdentifierTokenCount > 1 || string.IsNullOrWhiteSpace(result.GetValue(option)))
            {
                result.AddError("Provide one nonempty identifier.");
            }
        });
        return option;
    }
}
