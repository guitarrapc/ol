namespace Ol.Internals;

internal sealed class MaximumLengthWriteStream(Stream destination, long maximumLength, bool leaveOpen) : Stream
{
    private long written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => destination.CanWrite;
    public override long Length => written;
    public override long Position
    {
        get => written;
        set => throw new NotSupportedException();
    }

    public override void Flush() => destination.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => destination.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateWrite(count);
        destination.Write(buffer, offset, count);
        written += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ValidateWrite(buffer.Length);
        destination.Write(buffer);
        written += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ValidateWrite(buffer.Length);
        await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        written += buffer.Length;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen) destination.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen) await destination.DisposeAsync().ConfigureAwait(false);
        base.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void ValidateWrite(int count)
    {
        if (maximumLength <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (count < 0 || written > maximumLength - count)
        {
            throw new InvalidDataException($"Archive exceeds {maximumLength} bytes.");
        }
    }
}
