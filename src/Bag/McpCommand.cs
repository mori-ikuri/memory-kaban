using System.CommandLine;

namespace Bag;

internal static class McpCommand
{
    public static Command Create(TextWriter error)
    {
        Option<string?> database = BagCommand.DatabaseOption();
        var command = new Command("mcp", "Serve read-only MCP tools over standard input and output.")
        {
            database
        };
        command.SetAction(parsed =>
        {
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                string path = BagCommand.DatabasePath(parsed.GetValue(database), createDirectory: false);
                BagMcpServer.RunAsync(path, cancellation.Token).GetAwaiter().GetResult();
                return 0;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception exception)
            {
                error.WriteLine(BagErrors.IsHandled(exception)
                    ? BagErrors.GetMessage(exception)
                    : "Operation failed.");
                return 2;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        });
        return command;
    }
}
