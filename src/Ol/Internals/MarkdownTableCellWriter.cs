using System.Buffers;
using System.Text;
using Ol.Core;

namespace Ol.Internals;

/// <summary>Writes values into GitHub-flavored Markdown cells without allowing table or HTML structure.</summary>
internal static class MarkdownTableCellWriter
{
    private static readonly SearchValues<byte> Utf8Escapes = SearchValues.Create("|\r\n&<>"u8);
    private static readonly SearchValues<char> TextEscapes = SearchValues.Create("|\r\n&<>");

    /// <summary>Writes source-backed UTF-8 without decoding it.</summary>
    public static void Write(IBufferWriter<byte> writer, Utf8Slice value)
        => Write(writer, value.Span);

    /// <summary>Writes UTF-8 without allocating an escaped copy.</summary>
    public static void Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            WriteUtf8(writer, "-"u8);
            return;
        }

        var remaining = value;
        while (true)
        {
            var index = remaining.IndexOfAny(Utf8Escapes);
            if (index < 0)
            {
                WriteUtf8(writer, remaining);
                return;
            }

            var current = remaining[index];
            WriteUtf8(writer, remaining[..index]);
            WriteUtf8(writer, current switch
            {
                (byte)'|' => "\\|"u8,
                (byte)'&' => "&amp;"u8,
                (byte)'<' => "&lt;"u8,
                (byte)'>' => "&gt;"u8,
                _ => " "u8,
            });
            remaining = remaining[(index + 1)..];
        }
    }

    /// <summary>Writes owned text without allocating an escaped copy.</summary>
    public static void Write(IBufferWriter<byte> writer, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteUtf8(writer, "-"u8);
            return;
        }

        var remaining = value.AsSpan();
        while (true)
        {
            var index = remaining.IndexOfAny(TextEscapes);
            if (index < 0)
            {
                WriteUtf8(writer, remaining);
                return;
            }

            var current = remaining[index];
            WriteUtf8(writer, remaining[..index]);
            WriteUtf8(writer, current switch
            {
                '|' => "\\|"u8,
                '&' => "&amp;"u8,
                '<' => "&lt;"u8,
                '>' => "&gt;"u8,
                _ => " "u8,
            });
            remaining = remaining[(index + 1)..];
        }
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<char> value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var destination = writer.GetSpan(byteCount);
        writer.Advance(Encoding.UTF8.GetBytes(value, destination));
    }
}
