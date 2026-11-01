using System.CommandLine;

namespace Bag;

internal static class FindCommand
{
    public static Command Create(TextWriter output, TextWriter error)
    {
        Option<string?> database = BagCommand.DatabaseOption();
        Option<DateTimeOffset?> from = TimeOption("--from", "Earliest creation time, inclusive. Unzoned input is Japan time.");
        Option<DateTimeOffset?> to = TimeOption("--to", "Latest creation time, inclusive. Unzoned input is Japan time.");
        Option<string?> room = TextOption("--room", "Literal part of a room name, or an exact room ID.");
        Option<string?> speaker = TextOption("--speaker", "Configured speaker label. ChatGPT defaults: chatgpt-user or chatgpt-assistant.");
        Option<string?> text = TextOption("--text", "Literal, case-sensitive text contained in the message body.");
        Option<int> context = CountOption("--context", "Parent-linked ancestors and selected continuation messages around each match.", 0, 0);
        Option<int> limit = CountOption("--limit", "Maximum distinct matching versions before adding context.", 20, 1);
        var branches = new Option<bool>("--all-branches") { Description = "Include messages outside the mainline." };
        var json = new Option<bool>("--json") { Description = "Write structured JSON with exact bodies and provenance." };
        var sealedMessages = new Option<bool>("--include-sealed") { Description = "Explicitly include sealed rooms and messages (CLI only)." };
        var command = new Command("find", "Find stored message bodies using a read-only database connection.")
        {
            database, from, to, room, speaker, text, context, limit, branches, json, sealedMessages
        };
        command.Validators.Add(result =>
        {
            if (result.GetResult(from)?.Errors.Any() == true || result.GetResult(to)?.Errors.Any() == true)
            {
                return;
            }
            if (result.GetValue(from) is DateTimeOffset start && result.GetValue(to) is DateTimeOffset end && start > end)
            {
                result.AddError("The time range is reversed.");
            }
        });
        command.SetAction(parsed =>
        {
            string path = BagCommand.DatabasePath(parsed.GetValue(database), createDirectory: false);
            SpeakerNames names = SpeakerNames.Read(path);
            string? originalSpeaker = null;
            if (parsed.GetValue(speaker) is string displaySpeaker)
            {
                if (!names.TryResolve(displaySpeaker, out string resolved))
                {
                    error.WriteLine("Invalid arguments. Use --help.");
                    return 1;
                }
                originalSpeaker = resolved;
            }
            var request = new FindRequest(parsed.GetValue(from), parsed.GetValue(to), parsed.GetValue(room),
                originalSpeaker, parsed.GetValue(text), parsed.GetValue(branches),
                parsed.GetValue(context), parsed.GetValue(limit));
            FindResult result = new FindService().Find(path, request, parsed.GetValue(sealedMessages));
            FindOutput.Write(result, parsed.GetValue(json), output, names);
            return 0;
        });
        return command;
    }

    private static Option<DateTimeOffset?> TimeOption(string name, string description)
    {
        var option = new Option<DateTimeOffset?>(name)
        {
            Description = description,
            CustomParser = result =>
            {
                if (result.Tokens.Count == 1 && FindTime.TryParseInput(result.Tokens[0].Value, out DateTimeOffset timestamp))
                {
                    return timestamp;
                }
                result.AddError("Provide a supported timestamp.");
                return null;
            }
        };
        RejectRepeated(option);
        return option;
    }

    private static Option<string?> TextOption(string name, string description)
    {
        var option = new Option<string?>(name) { Description = description };
        option.Validators.Add(result =>
        {
            if (!result.Errors.Any() && string.IsNullOrEmpty(result.GetValue(option)))
            {
                result.AddError("Provide a nonempty value.");
            }
        });
        RejectRepeated(option);
        return option;
    }

    private static Option<int> CountOption(string name, string description, int defaultValue, int minimum)
    {
        var option = new Option<int>(name)
        {
            Description = description,
            DefaultValueFactory = _ => defaultValue
        };
        option.Validators.Add(result =>
        {
            if (!result.Errors.Any() && result.GetValue(option) < minimum)
            {
                result.AddError("The count is out of range.");
            }
        });
        RejectRepeated(option);
        return option;
    }

    private static void RejectRepeated(Option option)
    {
        option.Validators.Add(result =>
        {
            if (result.IdentifierTokenCount > 1)
            {
                result.AddError("Provide this option only once.");
            }
        });
    }
}
