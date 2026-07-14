using System.Buffers;
using System.Collections.Frozen;
using System.Text;

namespace Ol.Core;

/// <summary>
/// Provides SPDX license and exception identifier lookup for scan classification.
/// </summary>
public sealed class SpdxLicenseIndex
{
    private readonly FrozenDictionary<string, string> licenses;
    private readonly FrozenDictionary<string, Utf8Slice> licenseUtf8;
    private readonly FrozenDictionary<string, string> exceptions;
    private readonly FrozenSet<string> deprecatedLicenses;
    private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> licenseSpanLookup;
    private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> exceptionSpanLookup;

    /// <summary>
    /// Initializes a new SPDX lookup index.
    /// </summary>
    /// <param name="licenses">Known SPDX license identifiers.</param>
    /// <param name="exceptions">Known SPDX exception identifiers.</param>
    public SpdxLicenseIndex(string[] licenses, string[] exceptions, string[]? deprecatedLicenses = null)
    {
        this.licenses = CreateLookup(licenses);
        licenseUtf8 = CreateUtf8Lookup(licenses);
        this.exceptions = CreateLookup(exceptions);
        this.deprecatedLicenses = (deprecatedLicenses ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        licenseSpanLookup = this.licenses.GetAlternateLookup<ReadOnlySpan<char>>();
        exceptionSpanLookup = this.exceptions.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// Attempts to normalize an UTF-8 SPDX license identifier without materializing an input string.
    /// </summary>
    /// <param name="licenseIdUtf8">The UTF-8 license identifier.</param>
    /// <param name="normalized">The normalized identifier when the lookup succeeds.</param>
    /// <returns><see langword="true" /> when the license identifier is known.</returns>
    public bool TryNormalizeLicenseIdUtf8(ReadOnlySpan<byte> licenseIdUtf8, out string normalized)
    {
        if (licenseIdUtf8.Length <= 128)
        {
            Span<char> characters = stackalloc char[128];
            var characterCount = Encoding.UTF8.GetChars(licenseIdUtf8, characters);
            return licenseSpanLookup.TryGetValue(characters[..characterCount], out normalized!);
        }

        var rented = ArrayPool<char>.Shared.Rent(licenseIdUtf8.Length);
        try
        {
            var characterCount = Encoding.UTF8.GetChars(licenseIdUtf8, rented);
            return licenseSpanLookup.TryGetValue(rented.AsSpan(0, characterCount), out normalized!);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Attempts to normalize a UTF-8 license identifier to a shared canonical UTF-8 slice.</summary>
    public bool TryNormalizeLicenseIdUtf8Slice(ReadOnlySpan<byte> licenseIdUtf8, out Utf8Slice normalized)
        => TryNormalizeLicenseIdUtf8Slice(licenseIdUtf8, out normalized, out _);

    /// <summary>Attempts to normalize a UTF-8 license identifier and retrieve its deprecation state.</summary>
    public bool TryNormalizeLicenseIdUtf8Slice(ReadOnlySpan<byte> licenseIdUtf8, out Utf8Slice normalized, out bool deprecated)
    {
        if (TryNormalizeLicenseIdUtf8(licenseIdUtf8, out var identifier))
        {
            normalized = licenseUtf8[identifier];
            deprecated = deprecatedLicenses.Contains(identifier);
            return true;
        }

        normalized = default;
        deprecated = false;
        return false;
    }

    /// <summary>
    /// Attempts to normalize an UTF-8 SPDX exception identifier without materializing an input string.
    /// </summary>
    /// <param name="exceptionIdUtf8">The UTF-8 exception identifier.</param>
    /// <param name="normalized">The normalized identifier when the lookup succeeds.</param>
    /// <returns><see langword="true" /> when the exception identifier is known.</returns>
    public bool TryNormalizeExceptionIdUtf8(ReadOnlySpan<byte> exceptionIdUtf8, out string normalized)
    {
        if (exceptionIdUtf8.Length <= 128)
        {
            Span<char> characters = stackalloc char[128];
            var characterCount = Encoding.UTF8.GetChars(exceptionIdUtf8, characters);
            return exceptionSpanLookup.TryGetValue(characters[..characterCount], out normalized!);
        }

        var rented = ArrayPool<char>.Shared.Rent(exceptionIdUtf8.Length);
        try
        {
            var characterCount = Encoding.UTF8.GetChars(exceptionIdUtf8, rented);
            return exceptionSpanLookup.TryGetValue(rented.AsSpan(0, characterCount), out normalized!);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Determines whether a known SPDX license identifier is deprecated.
    /// </summary>
    /// <param name="licenseId">The SPDX license identifier.</param>
    /// <returns><see langword="true"/> when the identifier is deprecated.</returns>
    public bool IsDeprecatedLicenseId(string licenseId) => deprecatedLicenses.Contains(licenseId);

    private static FrozenDictionary<string, string> CreateLookup(string[] identifiers)
    {
        var dictionary = new Dictionary<string, string>(identifiers.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < identifiers.Length; i++)
        {
            var identifier = identifiers[i];
            dictionary[identifier] = identifier;
        }

        return dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<string, Utf8Slice> CreateUtf8Lookup(string[] identifiers)
    {
        var dictionary = new Dictionary<string, Utf8Slice>(identifiers.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < identifiers.Length; i++)
        {
            var identifier = identifiers[i];
            dictionary[identifier] = Utf8Slice.FromString(identifier);
        }

        return dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
