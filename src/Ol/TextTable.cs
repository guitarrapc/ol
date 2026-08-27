using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text;

/// <summary>Writes deterministic, allocation-free ASCII table framing around UTF-8 values.</summary>
internal static class TextTable
{
    /// <summary>
    /// Widest column the framing pads to.
    /// </summary>
    /// <remarks>
    /// A column is as wide as its widest cell, so without a ceiling one oversized value — a name no
    /// registry can issue but a hand-written or broken-generator SBOM can still carry — pads every other
    /// row to its length and multiplies the whole table by the row count. The cap is above every
    /// identifier and filesystem path the tool legitimately prints, so a capped column only ever appears
    /// for a value that was already unreadable. Such a cell is written in full and overflows its column
    /// rather than being truncated: the row that names the offending component stays actionable, and
    /// only that row is ragged.
    /// </remarks>
    public const int MaxColumnWidth = 256;

    private static ReadOnlySpan<byte> ColumnSeparator => "  "u8;

    /// <summary>Bounds of the printable ASCII run whose byte count is already its column count.</summary>
    private const byte Lowest = 0x20;

    private const byte Highest = 0x7E;

    private static bool IsPlain(ReadOnlySpan<byte> value)
        => value.IndexOfAnyExceptInRange(Lowest, Highest) < 0;

    /// <summary>
    /// Counts the terminal columns a value occupies, which is what the padding has to match.
    /// </summary>
    /// <remarks>
    /// One vectorized scan settles the common case: a cell of printable ASCII occupies exactly its byte
    /// count and needs no sanitizing, so the same probe that rules out a control character also rules
    /// out a character whose byte count and column count differ.
    /// </remarks>
    public static int Width(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return 1;
        return IsPlain(value) ? value.Length : DisplayWidth(value);
    }

    public static int Width(string value)
    {
        if (value.Length == 0) return 1;
        var span = value.AsSpan();
        return span.IndexOfAnyExceptInRange((char)Lowest, (char)Highest) < 0 ? span.Length : DisplayWidth(span);
    }

    public static int Width(int value)
    {
        Span<byte> destination = stackalloc byte[11];
        Utf8Formatter.TryFormat(value, destination, out var written);
        return written;
    }

    public static void Include(ref int width, ReadOnlySpan<byte> value)
        => Widen(ref width, Width(value));

    public static void Include(ref int width, string value)
        => Widen(ref width, Width(value));

    public static void Include(ref int width, int value)
        => Widen(ref width, Width(value));

    public static void WriteCell(IBufferWriter<byte> writer, ReadOnlySpan<byte> value, int width, bool last = false)
    {
        if (value.IsEmpty) value = "-"u8;

        // One scan answers both questions the cell has: how many columns it occupies, and whether any
        // byte has to be replaced before it reaches the row.
        var irregular = value.IndexOfAnyExceptInRange(Lowest, Highest);
        var trailing = Trailing(width, irregular < 0 ? value.Length : DisplayWidth(value), last);
        var destination = writer.GetSpan(value.Length + trailing);
        value.CopyTo(destination);
        if (irregular >= 0) Sanitize(destination.Slice(irregular, value.Length - irregular));
        destination.Slice(value.Length, trailing).Fill((byte)' ');
        writer.Advance(value.Length + trailing);
    }

    public static void WriteCell(IBufferWriter<byte> writer, string value, int width, bool last = false)
    {
        if (value.Length == 0)
        {
            WriteCell(writer, "-"u8, width, last);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        var trailing = Trailing(width, Width(value), last);
        var destination = writer.GetSpan(byteCount + trailing);
        Encoding.UTF8.GetBytes(value, destination);
        Sanitize(destination[..byteCount]);
        destination.Slice(byteCount, trailing).Fill((byte)' ');
        writer.Advance(byteCount + trailing);
    }

    public static void WriteCell(IBufferWriter<byte> writer, int value, int width, bool last = false)
    {
        var output = writer.GetSpan(Math.Max(11, width) + ColumnSeparator.Length);
        if (!Utf8Formatter.TryFormat(value, output, out var written))
        {
            throw new InvalidOperationException("Unable to format table value.");
        }

        var trailing = Trailing(width, written, last);
        output.Slice(written, trailing).Fill((byte)' ');
        writer.Advance(written + trailing);
    }

    public static void WriteSeparator(IBufferWriter<byte> writer, ReadOnlySpan<int> widths)
    {
        var newline = Environment.NewLine;
        var length = newline.Length;
        if (widths.Length != 0) length += ColumnSeparator.Length * (widths.Length - 1);
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

    private static void Widen(ref int width, int cellWidth)
        => width = Math.Max(width, Math.Min(MaxColumnWidth, cellWidth));

    /// <summary>
    /// Pads a cell to its column, or to nothing when the value is wider than the column allows.
    /// </summary>
    private static int Trailing(int width, int cellWidth, bool last)
        => last ? 0 : Math.Max(0, width - cellWidth) + ColumnSeparator.Length;

    /// <summary>
    /// Counts the terminal columns a UTF-8 value occupies, which is what the padding has to match.
    /// </summary>
    /// <remarks>
    /// Byte length is the wrong metric twice over: a CJK character costs three bytes and occupies two
    /// columns, an accented Latin character costs two and occupies one. Control characters count as one
    /// because <see cref="Sanitize"/> replaces each with a single space.
    /// </remarks>
    private static int DisplayWidth(ReadOnlySpan<byte> value)
    {
        var width = 0;
        while (!value.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(value, out var rune, out var consumed) != OperationStatus.Done)
            {
                width++;
                value = value[consumed..];
                continue;
            }

            width += RuneWidth(rune);
            value = value[consumed..];
        }

        return width;
    }

    private static int DisplayWidth(ReadOnlySpan<char> value)
    {
        var width = 0;
        while (!value.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(value, out var rune, out var consumed) != OperationStatus.Done)
            {
                width++;
                value = value[consumed..];
                continue;
            }

            width += RuneWidth(rune);
            value = value[consumed..];
        }

        return width;
    }

    private static int RuneWidth(Rune rune)
    {
        // A mark renders into the cell of the character it follows, and a format character renders
        // nothing at all, so neither advances the cursor the padding is counting.
        if (rune.Value >= 0x0300 && Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        return IsWide(rune.Value) ? 2 : 1;
    }

    /// <summary>East Asian Wide and Fullwidth ranges, which a terminal draws in two columns.</summary>
    private static bool IsWide(int value)
        => value >= 0x1100
            && (value <= 0x115F
                || (value >= 0x2E80 && value <= 0x303E)
                || (value >= 0x3041 && value <= 0x33FF)
                || (value >= 0x3400 && value <= 0x4DBF)
                || (value >= 0x4E00 && value <= 0x9FFF)
                || (value >= 0xA000 && value <= 0xA4CF)
                || (value >= 0xA960 && value <= 0xA97F)
                || (value >= 0xAC00 && value <= 0xD7A3)
                || (value >= 0xF900 && value <= 0xFAFF)
                || (value >= 0xFE10 && value <= 0xFE19)
                || (value >= 0xFE30 && value <= 0xFE6F)
                || (value >= 0xFF00 && value <= 0xFF60)
                || (value >= 0xFFE0 && value <= 0xFFE6)
                || (value >= 0x17000 && value <= 0x18AFF)
                || (value >= 0x1F300 && value <= 0x1F64F)
                || (value >= 0x1F900 && value <= 0x1F9FF)
                || (value >= 0x1FA70 && value <= 0x1FAFF)
                || (value >= 0x20000 && value <= 0x3FFFD));

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
