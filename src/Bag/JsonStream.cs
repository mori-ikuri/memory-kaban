using System.Text.Json;

namespace Bag;

/// <summary>Buffers one JSON value, never an entire conversation or export array.</summary>
internal sealed class JsonStream(Stream stream)
{
    private byte[] buffer = new byte[65536];
    private int start;
    private int end;
    private bool final;
    private JsonReaderState state;

    public JsonTokenType Token { get; private set; }
    public string? Text { get; private set; }

    public bool Read()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), final, state);
            bool found = reader.Read();
            if (found)
            {
                Token = reader.TokenType;
                Text = Token is JsonTokenType.PropertyName or JsonTokenType.String ? reader.GetString() : null;
            }
            Save(reader);
            if (found || final)
            {
                return found;
            }
            Fill();
        }
    }

    // Called immediately after a property name or array delimiter, before its value.
    public JsonDocument Value()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), final, state);
            if (reader.Read() && JsonDocument.TryParseValue(ref reader, out JsonDocument? document))
            {
                Save(reader);
                return document;
            }
            if (final)
            {
                throw new InvalidDataException("Incomplete JSON value.");
            }
            Fill();
        }
    }

    public void Expect(JsonTokenType token)
    {
        if (!Read() || Token != token)
        {
            throw new InvalidDataException("Unexpected JSON structure.");
        }
    }

    // Peek without consuming the next token, so Value can start before an object.
    public bool NextIs(JsonTokenType token)
    {
        while (true)
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), final, state);
            if (reader.Read())
            {
                return reader.TokenType == token;
            }
            if (final)
            {
                throw new InvalidDataException("Unexpected end of JSON.");
            }
            Fill();
        }
    }

    private void Save(Utf8JsonReader reader)
    {
        start += checked((int)reader.BytesConsumed);
        state = reader.CurrentState;
    }

    private void Fill()
    {
        int remaining = end - start;
        buffer.AsSpan(start, remaining).CopyTo(buffer);
        if (remaining == buffer.Length)
        {
            Array.Resize(ref buffer, checked(buffer.Length * 2));
        }
        start = 0;
        end = remaining;
        int read = stream.Read(buffer.AsSpan(end));
        end += read;
        final = read == 0;
    }
}
