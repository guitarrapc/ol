using System.Buffers;
using System.Buffers.Text;
using System.Text;

/// <summary>Writes deterministic, allocation-free ASCII table framing around UTF-8 values.</summary>
internal static class TextTable
{
    private static ReadOnlySpan<byte> ColumnSeparator => "  "u8;

    public static int Width(ReadOnlySpan<byte> value)
        => value.IsEmpty ? 1 : value.Length;

    public static int Width(string value)
        => value.Length == 0 ? 1 : Encoding.UTF8.GetByteCount(value);

    public static int Width(int value)
    {
        Span<byte> destination = stackalloc byte[11];
        Utf8Formatter.TryFormat(value, destination, out var written);
        return written;
    }

    public static void Include(ref int width, ReadOnlySpan<byte> value)
        => width = Math.Max(width, Width(value));

    public static void Include(ref int width, string value)
        => width = Math.Max(width, Width(value));

    public static void Include(ref int width, int value)
        => width = Math.Max(width, Width(value));

    public static void WriteCell(IBufferWriter<byte> writer, ReadOnlySpan<byte> value, int width, bool last = false)
    {
        if (value.IsEmpty) value = "-"u8;
        var trailing = last ? 0 : width - value.Length + ColumnSeparator.Length;
        var destination = writer.GetSpan(value.Length + trailing);
        value.CopyTo(destination);
        Sanitize(destination[..value.Length]);
        destination.Slice(value.Length, trailing).Fill((byte)' ');
        writer.Advance(value.Length + trailing);
    }

    public static void WriteCell(IBufferWriter<byte> writer, string value, int width, bool last = false)
    {
        var byteCount = Width(value);
        var trailing = last ? 0 : width - byteCount + ColumnSeparator.Length;
        var destination = writer.GetSpan(byteCount + trailing);
        if (value.Length == 0)
        {
            destination[0] = (byte)'-';
        }
        else
        {
            Encoding.UTF8.GetBytes(value, destination);
            Sanitize(destination[..byteCount]);
        }

        destination.Slice(byteCount, trailing).Fill((byte)' ');
        writer.Advance(byteCount + trailing);
    }

    public static void WriteCell(IBufferWriter<byte> writer, int value, int width, bool last = false)
    {
        var output = writer.GetSpan(last ? 11 : width + ColumnSeparator.Length);
        if (!Utf8Formatter.TryFormat(value, output, out var written))
        {
            throw new InvalidOperationException("Unable to format table value.");
        }

        var trailing = last ? 0 : width - written + ColumnSeparator.Length;
        output.Slice(written, trailing).Fill((byte)' ');
        writer.Advance(written + trailing);
    }

    public static void WriteSeparator(IBufferWriter<byte> writer, ReadOnlySpan<int> widths)
    {
        var newline = Environment.NewLine;
        var length = newline.Length + ColumnSeparator.Length * (widths.Length - 1);
        for (var i = 0; i < widths.Length; i++) length += widths[i];

        var destination = writer.GetSpan(length);
        var offset = 0;
        for (var i = 0; i < widths.Length; i++)
        {
            destination.Slice(offset, widths[i]).Fill((byte)'-');
            offset += widths[i];
            if (i + 1 >= widths.Length) continue;
            destination.Slice(offset, ColumnSeparator.Length).Fill((byte)' ');
            offset += ColumnSeparator.Length;
        }

        for (var i = 0; i < newline.Length; i++) destination[offset++] = (byte)newline[i];
        writer.Advance(length);
    }

    public static void WriteLine(IBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        Write(writer, value);
        WriteNewLine(writer);
    }

    public static void WriteLine(IBufferWriter<byte> writer, string value)
    {
        WriteSanitized(writer, value);
        WriteNewLine(writer);
    }

    public static void WriteNewLine(IBufferWriter<byte> writer)
        => Write(writer, Environment.NewLine);

    private static void Sanitize(Span<byte> value)
    {
        var offset = 0;
        while (offset < value.Length)
        {
            var index = value[offset..].IndexOfAny((byte)'\r', (byte)'\n', (byte)'\t');
            if (index < 0) return;
            offset += index;
            value[offset++] = (byte)' ';
        }
    }

    private static void WriteSanitized(IBufferWriter<byte> writer, string value)
    {
        var remaining = value.AsSpan();
        while (true)
        {
            var index = remaining.IndexOfAny('\r', '\n', '\t');
            if (index < 0)
            {
                Write(writer, remaining);
                return;
            }

            Write(writer, remaining[..index]);
            Write(writer, " "u8);
            remaining = remaining[(index + 1)..];
        }
    }

    private static void Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

    private static void Write(IBufferWriter<byte> writer, ReadOnlySpan<char> value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var destination = writer.GetSpan(byteCount);
        writer.Advance(Encoding.UTF8.GetBytes(value, destination));
    }

    private static void Write(IBufferWriter<byte> writer, string value)
        => Write(writer, value.AsSpan());
}
