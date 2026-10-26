using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bag;

/// <summary>Keeps SDK input handling while emitting readable UTF-8 JSON.</summary>
internal sealed class UnicodeStdioTransport(
    Stream inputStream,
    Stream outputStream,
    string serverName)
    : StreamServerTransport(inputStream, outputStream, serverName, loggerFactory: null)
{
    private static readonly JsonSerializerOptions SerializerOptions =
        JsonOutput.CreateOptions(McpJsonUtilities.DefaultOptions);
    private static readonly byte[] Newline = [(byte)'\n'];

    // Field initializers run before the base constructor starts the read loop.
    // A malformed first request can therefore safely use this send override.
    private readonly Stream output = outputStream;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public static UnicodeStdioTransport Open(string serverName)
    {
        return new UnicodeStdioTransport(
            new CancellableStdinStream(Console.OpenStandardInput()),
            new BufferedStream(Console.OpenStandardOutput()),
            serverName);
    }

    public override async Task SendMessageAsync(
        JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected)
            {
                return;
            }

            // SDK 2.3.0 fixes its own encoder inside the default send method.
            // Clone its public options to retain the protocol converters, and
            // change only JSON output encoding. The SDK still reads all input.
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
            await output.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }
}
