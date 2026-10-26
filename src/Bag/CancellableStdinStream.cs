namespace Bag;

/// <summary>Allows SDK shutdown to stop waiting for console input.</summary>
internal sealed class CancellableStdinStream(Stream input) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        // Console streams can ignore cancellation while waiting in the OS.
        return input.ReadAsync(buffer, offset, count, cancellationToken)
            .WaitAsync(cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return await input.ReadAsync(buffer, cancellationToken).AsTask()
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            input.Dispose();
        }
        base.Dispose(disposing);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
