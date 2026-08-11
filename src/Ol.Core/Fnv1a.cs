namespace Ol.Core;

/// <summary>
/// Calculates the 32-bit FNV-1a hash the resolved-input parsers key their node-index tables with.
/// </summary>
/// <remarks>
/// Shared because ten parsers had written the same two constants themselves. The hash is not
/// collision-resistant and is not persisted: it indexes an in-memory table whose entries are confirmed by
/// comparing the bytes, so it may change without affecting any output.
/// </remarks>
internal static class Fnv1a
{
    /// <summary>The FNV-1a 32-bit offset basis, and the seed an unchained hash starts from.</summary>
    public const uint OffsetBasis = 2166136261;

    private const uint Prime = 16777619;

    /// <summary>Hashes UTF-8 bytes, optionally continuing an earlier hash.</summary>
    /// <param name="value">The bytes to hash.</param>
    /// <param name="hash">The hash to continue, or <see cref="OffsetBasis"/> to start one.</param>
    public static uint Hash(ReadOnlySpan<byte> value, uint hash = OffsetBasis)
    {
        for (var index = 0; index < value.Length; index++)
        {
            hash = (hash ^ value[index]) * Prime;
        }

        return hash;
    }

    /// <summary>Mixes a part boundary into a composite key's hash.</summary>
    /// <remarks>
    /// Without it, a key of two parts hashes the same as the concatenation, so <c>("ab", "")</c> and
    /// <c>("a", "b")</c> would land in the same bucket. 0xFF cannot occur in valid UTF-8, so no part
    /// content can reproduce the boundary.
    /// </remarks>
    public static uint HashSeparator(uint hash) => (hash ^ 0xff) * Prime;
}
