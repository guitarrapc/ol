using System.Buffers;
using System.Text;

namespace Ol.Tests;

/// <summary>Covers the framing primitive every text table shares.</summary>
public sealed class TextTableTests
{
    [Test]
    [Arguments("abcdef", 6)]
    [Arguments("", 1)]
    [Arguments("a", 1)]
    public async Task Width_WithAsciiValue_CountsOneColumnPerByte(string value, int expected)
    {
        await Assert.That(TextTable.Width(value)).IsEqualTo(expected);
        await Assert.That(TextTable.Width(Encoding.UTF8.GetBytes(value))).IsEqualTo(expected);
    }

    /// <summary>A CJK cell costs 3 bytes but occupies 2 columns; padding must follow the columns.</summary>
    [Test]
    [Arguments("日本語", 6)]
    [Arguments("パッケージ", 10)]
    [Arguments("한국어", 6)]
    [Arguments("ｱｲｳ", 3)]
    [Arguments("ＡＢ", 4)]
    public async Task Width_WithEastAsianWideValue_CountsTwoColumnsPerWideCharacter(string value, int expected)
    {
        await Assert.That(TextTable.Width(value)).IsEqualTo(expected);
        await Assert.That(TextTable.Width(Encoding.UTF8.GetBytes(value))).IsEqualTo(expected);
    }

    [Test]
    [Arguments("café", 4)]
    [Arguments("ünïcode", 7)]
    [Arguments("Ελλάδα", 6)]
    [Arguments("Привет", 6)]
    public async Task Width_WithNarrowNonAsciiValue_CountsOneColumnPerCharacter(string value, int expected)
    {
        await Assert.That(TextTable.Width(value)).IsEqualTo(expected);
        await Assert.That(TextTable.Width(Encoding.UTF8.GetBytes(value))).IsEqualTo(expected);
    }

    /// <summary>A combining mark renders into the preceding cell rather than advancing the cursor.</summary>
    [Test]
    public async Task Width_WithCombiningMark_DoesNotCountTheMark()
    {
        await Assert.That(TextTable.Width("é")).IsEqualTo(1);
        await Assert.That(TextTable.Width("é"u8)).IsEqualTo(1);
    }

    /// <summary>Malformed input must terminate the decode loop rather than hang the command.</summary>
    [Test]
    [Arguments(new byte[] { 0x80 }, 1)]
    [Arguments(new byte[] { 0xFF }, 1)]
    [Arguments(new byte[] { 0xC0, 0x80 }, 2)]
    [Arguments(new byte[] { 0xE3, 0x81 }, 1)]
    [Arguments(new byte[] { 0xF0, 0x9F, 0x8E }, 1)]
    [Arguments(new byte[] { 0xED, 0xA0, 0x80 }, 3)]
    [Arguments(new byte[] { 0xE3, 0x81, 0x82, 0xFF }, 3)]
    [Arguments(new byte[] { 0x61, 0x80, 0x62 }, 3)]
    public async Task Width_WithMalformedUtf8_TerminatesAndCountsEachUndecodableByte(byte[] value, int expected)
        => await Assert.That(TextTable.Width(value)).IsEqualTo(expected);

    /// <summary>A lone surrogate is representable in a string and encodes to one replacement glyph.</summary>
    [Test]
    [Arguments("\uD800", 1)]
    [Arguments("\uDC00", 1)]
    [Arguments("a\uD800b", 3)]
    public async Task Width_WithLoneSurrogate_TerminatesAndCountsOneColumn(string value, int expected)
        => await Assert.That(TextTable.Width(value)).IsEqualTo(expected);

    /// <summary>The bytes a malformed value encodes to must still land inside its measured column.</summary>
    [Test]
    public async Task WriteCell_WithLoneSurrogate_PadsToTheReplacementGlyphWidth()
    {
        var writer = new ArrayBufferWriter<byte>();
        var width = 4;
        TextTable.Include(ref width, "\uD800");

        TextTable.WriteCell(writer, "\uD800", width);
        TextTable.WriteCell(writer, "end"u8, 3, last: true);

        await Assert.That(Encoding.UTF8.GetString(writer.WrittenSpan)).IsEqualTo("�     end");
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(9, 1)]
    [Arguments(10, 2)]
    [Arguments(-1, 2)]
    [Arguments(int.MaxValue, 10)]
    [Arguments(int.MinValue, 11)]
    public async Task Width_WithInt32Value_CountsFormattedDigits(int value, int expected)
        => await Assert.That(TextTable.Width(value)).IsEqualTo(expected);

    /// <summary>Without a cap one oversized value pads every other row to its length.</summary>
    [Test]
    public async Task Include_WithValueBeyondTheColumnCap_StopsWideningTheColumn()
    {
        var oversized = new string('x', TextTable.MaxColumnWidth * 4);
        var width = 4;

        TextTable.Include(ref width, oversized);
        await Assert.That(width).IsEqualTo(TextTable.MaxColumnWidth);

        width = 4;
        TextTable.Include(ref width, Encoding.UTF8.GetBytes(oversized));
        await Assert.That(width).IsEqualTo(TextTable.MaxColumnWidth);
    }

    /// <summary>A capped column overflows rather than truncating evidence or throwing on a negative pad.</summary>
    [Test]
    public async Task WriteCell_WithValueWiderThanTheColumn_WritesItInFullWithOnlyTheSeparator()
    {
        var writer = new ArrayBufferWriter<byte>();

        TextTable.WriteCell(writer, "overflowing"u8, 4);
        TextTable.WriteCell(writer, "end"u8, 3, last: true);

        await Assert.That(Encoding.UTF8.GetString(writer.WrittenSpan)).IsEqualTo("overflowing  end");
    }

    [Test]
    public async Task WriteCell_WithStringValueWiderThanTheColumn_WritesItInFullWithOnlyTheSeparator()
    {
        var writer = new ArrayBufferWriter<byte>();

        TextTable.WriteCell(writer, "overflowing", 4);
        TextTable.WriteCell(writer, "end", 3, last: true);

        await Assert.That(Encoding.UTF8.GetString(writer.WrittenSpan)).IsEqualTo("overflowing  end");
    }

    [Test]
    public async Task WriteCell_WithInt32ValueWiderThanTheColumn_WritesItInFullWithOnlyTheSeparator()
    {
        var writer = new ArrayBufferWriter<byte>();

        TextTable.WriteCell(writer, 123456, 2);
        TextTable.WriteCell(writer, "end"u8, 3, last: true);

        await Assert.That(Encoding.UTF8.GetString(writer.WrittenSpan)).IsEqualTo("123456  end");
    }

    /// <summary>Padding follows the same display metric the width does, or the two disagree per row.</summary>
    [Test]
    public async Task WriteCell_WithWideAndAsciiValues_PadsBothToTheSameDisplayColumn()
    {
        var width = "NAME"u8.Length;
        TextTable.Include(ref width, "日本語");
        TextTable.Include(ref width, "abcdef");

        var wide = new ArrayBufferWriter<byte>();
        TextTable.WriteCell(wide, Encoding.UTF8.GetBytes("日本語"), width);
        TextTable.WriteCell(wide, "1.0.0"u8, 5, last: true);

        var ascii = new ArrayBufferWriter<byte>();
        TextTable.WriteCell(ascii, "abcdef"u8, width);
        TextTable.WriteCell(ascii, "2.0.0"u8, 5, last: true);

        await Assert.That(Encoding.UTF8.GetString(wide.WrittenSpan)).IsEqualTo("日本語  1.0.0");
        await Assert.That(Encoding.UTF8.GetString(ascii.WrittenSpan)).IsEqualTo("abcdef  2.0.0");
    }

    [Test]
    public async Task WriteCell_WithEmptyValue_WritesThePlaceholder()
    {
        var writer = new ArrayBufferWriter<byte>();

        TextTable.WriteCell(writer, default(ReadOnlySpan<byte>), 1);
        TextTable.WriteCell(writer, string.Empty, 1, last: true);

        await Assert.That(Encoding.UTF8.GetString(writer.WrittenSpan)).IsEqualTo("-  -");
    }

    /// <summary>A newline inside a cell would end the row, so it becomes a space of the same width.</summary>
    [Test]
    public async Task WriteCell_WithControlCharacters_ReplacesThemWithSpaces()
    {
        var writer = new ArrayBufferWriter<byte>();

        TextTable.WriteCell(writer, "a\r\nb\tc", 6, last: true);

        await Assert.That(Encoding.UTF8.GetString(writer.WrittenSpan)).IsEqualTo("a  b c");
    }

    [Test]
    public async Task WriteSeparator_WithOneColumn_WritesOnlyThatRun()
    {
        var writer = new ArrayBufferWriter<byte>();
        Span<int> widths = stackalloc int[] { 3 };

        TextTable.WriteSeparator(writer, widths);

        await Assert.That(Encoding.UTF8.GetString(writer.WrittenSpan)).IsEqualTo($"---{Environment.NewLine}");
    }

    [Test]
    public async Task WriteSeparator_WithNoColumns_WritesOnlyTheLineBreak()
    {
        var writer = new ArrayBufferWriter<byte>();

        TextTable.WriteSeparator(writer, default);

        await Assert.That(Encoding.UTF8.GetString(writer.WrittenSpan)).IsEqualTo(Environment.NewLine);
    }
}
