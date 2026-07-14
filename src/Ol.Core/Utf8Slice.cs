using System.Text;

namespace Ol.Core;

/// <summary>
/// A UTF-8 value backed by the scanned input buffer or an explicitly created fallback buffer.
/// </summary>
public readonly struct Utf8Slice : IEquatable<Utf8Slice>
{
    private readonly byte[]? buffer;
    private readonly int offset;

    /// <summary>
    /// Initializes a slice into an owned UTF-8 buffer.
    /// </summary>
    public Utf8Slice(byte[] buffer, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)offset > (uint)buffer.Length || (uint)length > (uint)(buffer.Length - offset))
        {
            throw new ArgumentOutOfRangeException();
        }

        this.buffer = buffer;
        this.offset = offset;
        Length = length;
    }

    /// <summary>
    /// Gets the byte length.
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// Gets whether the value is empty.
    /// </summary>
    public bool IsEmpty => Length == 0;

    /// <summary>
    /// Gets the UTF-8 bytes without decoding them.
    /// </summary>
    public ReadOnlySpan<byte> Span => buffer is null ? [] : buffer.AsSpan(offset, Length);

    /// <summary>
    /// Creates a separately owned UTF-8 value. This is for external or exceptional text only.
    /// </summary>
    public static Utf8Slice FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return default;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        return new Utf8Slice(bytes, 0, bytes.Length);
    }

    /// <summary>Wraps an owned UTF-8 buffer without copying it.</summary>
    /// <param name="value">The exclusively owned UTF-8 buffer.</param>
    /// <returns>A slice over the complete buffer.</returns>
    public static Utf8Slice FromOwnedBytes(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == 0 ? default : new Utf8Slice(value, 0, value.Length);
    }

    /// <summary>
    /// Decodes this UTF-8 value for an output boundary.
    /// </summary>
    public override string ToString() => buffer is null ? string.Empty : Encoding.UTF8.GetString(Span);

    /// <inheritdoc/>
    public bool Equals(Utf8Slice other) => Span.SequenceEqual(other.Span);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Utf8Slice other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Span)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Compares two UTF-8 values in ordinal byte order.
    /// </summary>
    public static int CompareOrdinal(Utf8Slice left, Utf8Slice right) => left.Span.SequenceCompareTo(right.Span);

    /// <summary>
    /// Converts external text to an owned UTF-8 value.
    /// </summary>
    public static implicit operator Utf8Slice(string value) => FromString(value);
}
