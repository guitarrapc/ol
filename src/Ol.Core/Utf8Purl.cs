namespace Ol.Core;

/// <summary>
/// Writes package URL components as percent-encoded UTF-8.
/// </summary>
/// <remarks>
/// Shared because the encoding is the package URL specification's, not any one input format's. Every
/// resolved-input parser carried its own copy of these three members, and the copies had already drifted
/// apart in whether the length arithmetic was checked. A format still owns which parts it composes into a
/// purl and in what order; only the byte-level rule lives here.
/// </remarks>
internal static class Utf8Purl
{
    private static ReadOnlySpan<byte> Hex => "0123456789ABCDEF"u8;

    /// <summary>Reports whether a byte may appear in a purl component unescaped.</summary>
    /// <param name="value">The UTF-8 byte.</param>
    /// <param name="allowSlash">
    /// Whether <c>/</c> is part of the component rather than a separator to escape. True for the ecosystems
    /// whose package name carries its namespace, as golang and scoped npm names do.
    /// </param>
    public static bool IsUnreserved(byte value, bool allowSlash = false)
        => value is >= (byte)'a' and <= (byte)'z'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~'
        || (allowSlash && value == (byte)'/');

    /// <summary>Calculates the encoded byte length, so a caller can size its destination exactly.</summary>
    /// <remarks>
    /// Checked, because the length decides the size of the buffer the encoded value is written into: an
    /// unchecked overflow would report a destination smaller than what <see cref="WriteEncoded"/> then
    /// writes. The copies this replaces disagreed here — three were checked and seven were not.
    /// </remarks>
    public static int GetEncodedLength(ReadOnlySpan<byte> value, bool allowSlash = false)
    {
        var length = 0;
        for (var index = 0; index < value.Length; index++)
        {
            length = checked(length + (IsUnreserved(value[index], allowSlash) ? 1 : 3));
        }

        return length;
    }

    /// <summary>Writes the percent-encoded value, advancing the caller's write position.</summary>
    /// <param name="value">The raw UTF-8 component.</param>
    /// <param name="destination">The destination, at least <see cref="GetEncodedLength"/> bytes long from <paramref name="index"/>.</param>
    /// <param name="index">The write position, advanced by the number of bytes written.</param>
    /// <param name="allowSlash">Whether <c>/</c> is part of the component.</param>
    public static void WriteEncoded(ReadOnlySpan<byte> value, Span<byte> destination, ref int index, bool allowSlash = false)
    {
        for (var valueIndex = 0; valueIndex < value.Length; valueIndex++)
        {
            var item = value[valueIndex];
            if (IsUnreserved(item, allowSlash))
            {
                destination[index++] = item;
                continue;
            }

            destination[index++] = (byte)'%';
            destination[index++] = Hex[item >> 4];
            destination[index++] = Hex[item & 0x0F];
        }
    }
}
