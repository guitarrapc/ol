namespace Ol.Core;

internal static class LicenseText
{
    private static readonly byte[] UnknownBytes = "-"u8.ToArray();
    private static readonly byte[] SuffixBytes = " (?)"u8.ToArray();
    private static readonly byte[] SeparatorBytes = ", "u8.ToArray();

    public static Utf8Slice Unknown => new(UnknownBytes, 0, UnknownBytes.Length);

    public static Utf8Slice WithUncertainty(Utf8Slice value)
    {
        var bytes = new byte[value.Length + SuffixBytes.Length];
        value.Span.CopyTo(bytes);
        SuffixBytes.CopyTo(bytes.AsSpan(value.Length));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    public static Utf8Slice Conflict(Utf8Slice first, Utf8Slice second)
    {
        var bytes = new byte[first.Length + SeparatorBytes.Length + second.Length + SuffixBytes.Length];
        var output = bytes.AsSpan();
        first.Span.CopyTo(output);
        var offset = first.Length;
        SeparatorBytes.CopyTo(output[offset..]);
        offset += SeparatorBytes.Length;
        second.Span.CopyTo(output[offset..]);
        offset += second.Length;
        SuffixBytes.CopyTo(output[offset..]);
        return Utf8Slice.FromOwnedBytes(bytes);
    }
}
