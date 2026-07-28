using System.Buffers;

internal sealed class PooledStreamBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int InitialBufferSize = 4 * 1024;
    private readonly Stream output;
    private byte[] buffer;
    private int count;

    public PooledStreamBufferWriter(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        this.output = output;
        buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
    }

    public void Advance(int count)
    {
        if ((uint)count > (uint)(buffer.Length - this.count))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        this.count += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsMemory(count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsSpan(count);
    }

    public void Flush()
    {
        if (count == 0)
        {
            return;
        }

        output.Write(buffer.AsSpan(0, count));
        count = 0;
    }

    public void Dispose()
    {
        try
        {
            Flush();
        }
        finally
        {
            var returned = buffer;
            buffer = [];
            if (returned.Length != 0)
            {
                ArrayPool<byte>.Shared.Return(returned);
            }
        }
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }

        sizeHint = Math.Max(sizeHint, 1);
        if (sizeHint <= buffer.Length - count)
        {
            return;
        }

        Flush();
        if (sizeHint <= buffer.Length)
        {
            return;
        }

        var expanded = ArrayPool<byte>.Shared.Rent(sizeHint);
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = expanded;
    }
}
