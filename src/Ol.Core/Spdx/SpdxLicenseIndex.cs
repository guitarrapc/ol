using System.Buffers;
using System.Collections.Frozen;
using System.Text;

namespace Ol.Core.Spdx;

/// <summary>
/// Provides SPDX license and exception identifier lookup for scan classification.
/// </summary>
public sealed class SpdxLicenseIndex
{
    private readonly FrozenDictionary<string, string> licenses;
    private readonly FrozenDictionary<string, Utf8Slice> licenseUtf8;
    private readonly FrozenDictionary<string, string> exceptions;
    private readonly FrozenSet<string> deprecatedLicenses;
    private readonly FrozenDictionary<string, string> licenseNames;
    private readonly FrozenDictionary<string, string> licenseUrls;
    private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> licenseSpanLookup;
    private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> exceptionSpanLookup;
    private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> licenseNameSpanLookup;
    private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> licenseUrlSpanLookup;

    /// <summary>
    /// Initializes a new SPDX lookup index.
    /// </summary>
    /// <param name="licenses">Known SPDX license identifiers.</param>
    /// <param name="exceptions">Known SPDX exception identifiers.</param>
    /// <param name="deprecatedLicenses">Identifiers SPDX marks deprecated.</param>
    /// <param name="licenseNames">SPDX license names, one per entry of <paramref name="licenses"/> at the same index.</param>
    /// <param name="seeAlsoUrls">SPDX <c>seeAlso</c> URLs, as published.</param>
    /// <param name="seeAlsoLicenseIds">The license each entry of <paramref name="seeAlsoUrls"/> belongs to, at the same index.</param>
    public SpdxLicenseIndex(
        string[] licenses,
        string[] exceptions,
        string[]? deprecatedLicenses = null,
        string[]? licenseNames = null,
        string[]? seeAlsoUrls = null,
        string[]? seeAlsoLicenseIds = null)
        : this(licenses, exceptions, deprecatedLicenses, licenseNames, seeAlsoUrls, seeAlsoLicenseIds, default)
    {
    }

    internal SpdxLicenseIndex(
        string[] licenses,
        string[] exceptions,
        string[]? deprecatedLicenses,
        string[]? licenseNames,
        string[]? seeAlsoUrls,
        string[]? seeAlsoLicenseIds,
        ReadOnlySpan<byte> licenseIdsUtf8)
    {
        this.licenses = CreateLookup(licenses);
        licenseUtf8 = licenseIdsUtf8.IsEmpty ? CreateUtf8Lookup(licenses) : CreateUtf8Lookup(licenses, licenseIdsUtf8);
        this.exceptions = CreateLookup(exceptions);
        this.deprecatedLicenses = (deprecatedLicenses ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        this.licenseNames = CreateNameLookup(licenses, licenseNames, this.deprecatedLicenses);
        licenseUrls = CreateUrlLookup(seeAlsoUrls, seeAlsoLicenseIds);
        licenseSpanLookup = this.licenses.GetAlternateLookup<ReadOnlySpan<char>>();
        exceptionSpanLookup = this.exceptions.GetAlternateLookup<ReadOnlySpan<char>>();
        licenseNameSpanLookup = this.licenseNames.GetAlternateLookup<ReadOnlySpan<char>>();
        licenseUrlSpanLookup = licenseUrls.GetAlternateLookup<ReadOnlySpan<char>>();
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

    /// <summary>Attempts to normalize an SPDX license identifier without allocating an input string.</summary>
    public bool TryNormalizeLicenseId(ReadOnlySpan<char> licenseId, out string normalized)
        => licenseSpanLookup.TryGetValue(licenseId, out normalized!);

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
    /// Attempts to resolve an SPDX license name to its identifier.
    /// </summary>
    /// <param name="licenseNameUtf8">The UTF-8 value to resolve.</param>
    /// <param name="normalized">The identifier SPDX gives that name, when the lookup succeeds.</param>
    /// <param name="deprecated">Whether the resolved identifier is deprecated.</param>
    /// <returns><see langword="true" /> when the value is a name in the active SPDX data.</returns>
    /// <remarks>
    /// The comparison is exact apart from case, like the identifier lookup. A value that merely
    /// resembles a name is not a name: <c>Apache 2.0</c> and <c>Modified BSD License</c> resolve
    /// nothing, because resolving them would mean choosing the version or variant the publisher did
    /// not write.
    /// </remarks>
    public bool TryNormalizeLicenseNameUtf8Slice(ReadOnlySpan<byte> licenseNameUtf8, out Utf8Slice normalized, out bool deprecated)
    {
        if (TryLookup(licenseNameSpanLookup, licenseNameUtf8, out var identifier))
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
    /// Attempts to resolve a URL that SPDX publishes as one license's <c>seeAlso</c>.
    /// </summary>
    /// <param name="urlUtf8">The UTF-8 URL to resolve.</param>
    /// <param name="normalized">The identifier SPDX gives that URL, when the lookup succeeds.</param>
    /// <param name="deprecated">Whether the resolved identifier is deprecated.</param>
    /// <returns><see langword="true" /> when the URL names exactly one license in the active SPDX data.</returns>
    /// <remarks>
    /// Exact apart from the spellings that cannot change which document a URL names: scheme, case, a
    /// leading <c>www.</c>, and a trailing slash. Nothing else is rewritten, so a redirector, a site's own
    /// renamed path, or a repository blob resolves nothing and stays a declared location.
    /// </remarks>
    public bool TryResolveLicenseUrl(ReadOnlySpan<byte> urlUtf8, out Utf8Slice normalized, out bool deprecated)
    {
        if (licenseUrls.Count != 0 && TryLookupUrl(urlUtf8, out var identifier))
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
    /// Attempts to resolve a decoded URL that SPDX publishes as one license's <c>seeAlso</c>.
    /// </summary>
    /// <param name="url">The URL to resolve, already decoded.</param>
    /// <param name="licenseId">The identifier SPDX gives that URL, when the lookup succeeds.</param>
    /// <returns><see langword="true" /> when the URL names exactly one license in the active SPDX data.</returns>
    /// <remarks>
    /// The same lookup as the UTF-8 overload, for callers that already hold characters. Scanning a
    /// license document for declared URLs decodes the document once, so re-encoding each candidate
    /// only to decode it again would be the sole reason to allocate.
    /// </remarks>
    public bool TryResolveLicenseUrl(ReadOnlySpan<char> url, out string licenseId)
    {
        if (licenseUrls.Count != 0 && licenseUrlSpanLookup.TryGetValue(TrimUrl(url), out licenseId!)) return true;
        licenseId = string.Empty;
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

    /// <summary>Decodes a UTF-8 value once and looks it up without materializing a string.</summary>
    /// <remarks>
    /// The stack buffer covers every identifier and every SPDX name, whose longest entry is well under
    /// this length. Anything longer is not a known value, but it is still decoded rather than rejected
    /// so the lookup answers on content rather than on a buffer size.
    /// </remarks>
    private static bool TryLookup(FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> lookup, ReadOnlySpan<byte> utf8, out string value)
    {
        if (utf8.Length <= 128)
        {
            Span<char> characters = stackalloc char[128];
            var characterCount = Encoding.UTF8.GetChars(utf8, characters);
            return lookup.TryGetValue(characters[..characterCount], out value!);
        }

        var rented = ArrayPool<char>.Shared.Rent(utf8.Length);
        try
        {
            var characterCount = Encoding.UTF8.GetChars(utf8, rented);
            return lookup.TryGetValue(rented.AsSpan(0, characterCount), out value!);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Decodes a URL, trims the spellings that do not change what it names, and looks it up.</summary>
    private bool TryLookupUrl(ReadOnlySpan<byte> utf8, out string value)
    {
        const int MaximumUrlChars = 512;
        if (utf8.Length is 0 or > MaximumUrlChars)
        {
            value = null!;
            return false;
        }

        Span<char> characters = stackalloc char[MaximumUrlChars];
        var characterCount = Encoding.UTF8.GetChars(utf8, characters);
        return licenseUrlSpanLookup.TryGetValue(TrimUrl(characters[..characterCount]), out value!);
    }

    /// <summary>Removes the scheme, a leading <c>www.</c>, and trailing slashes. Case is left to the lookup.</summary>
    private static ReadOnlySpan<char> TrimUrl(ReadOnlySpan<char> value)
    {
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = value[8..];
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        return value.TrimEnd('/');
    }

    /// <summary>Builds the URL lookup, dropping every URL SPDX publishes for more than one license.</summary>
    /// <remarks>
    /// Not the deprecated-and-replacement pair the name lookup resolves: one OSI page serves both
    /// <c>LGPL-2.1</c> and <c>LGPL-2.1-or-later</c>, which are different licenses. A shared URL names no
    /// single license, so it is dropped rather than resolved to whichever entry was read first.
    /// </remarks>
    private static FrozenDictionary<string, string> CreateUrlLookup(string[]? urls, string[]? identifiers)
    {
        if (urls is null || identifiers is null || urls.Length == 0)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        var count = Math.Min(urls.Length, identifiers.Length);
        var dictionary = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var url = TrimUrl(urls[i]).ToString();
            if (url.Length == 0)
            {
                continue;
            }

            if (dictionary.TryGetValue(url, out var existing))
            {
                if (!string.Equals(existing, identifiers[i], StringComparison.OrdinalIgnoreCase)) ambiguous.Add(url);
                continue;
            }

            dictionary[url] = identifiers[i];
        }

        foreach (var url in ambiguous)
        {
            dictionary.Remove(url);
        }

        return dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Builds the name lookup, resolving the names SPDX gives to more than one identifier.</summary>
    /// <remarks>
    /// SPDX shares a name only between a deprecated identifier and the replacement that supersedes it —
    /// <c>GPL-2.0</c> and <c>GPL-2.0-only</c> carry the same name because they are the same license. The
    /// replacement is therefore the answer, and preferring it is not a choice between two licenses.
    /// A name Ol cannot attribute to exactly one current identifier this way is dropped rather than
    /// resolved arbitrarily, so an unexpected upstream duplicate leaves a value unresolved instead of
    /// resolving it to whichever entry was read first.
    /// </remarks>
    private static FrozenDictionary<string, string> CreateNameLookup(string[] identifiers, string[]? names, FrozenSet<string> deprecated)
    {
        if (names is null || names.Length == 0)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        var count = Math.Min(identifiers.Length, names.Length);
        var dictionary = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = names[i];
            if (name.Length == 0)
            {
                continue;
            }

            var identifier = identifiers[i];
            if (!dictionary.TryGetValue(name, out var existing))
            {
                dictionary[name] = identifier;
                continue;
            }

            if (deprecated.Contains(existing) != deprecated.Contains(identifier))
            {
                dictionary[name] = deprecated.Contains(existing) ? identifier : existing;
                continue;
            }

            ambiguous.Add(name);
        }

        foreach (var name in ambiguous)
        {
            dictionary.Remove(name);
        }

        return dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

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

    private static FrozenDictionary<string, Utf8Slice> CreateUtf8Lookup(string[] identifiers, ReadOnlySpan<byte> generatedUtf8)
    {
        var owned = generatedUtf8.ToArray();
        var dictionary = new Dictionary<string, Utf8Slice>(identifiers.Length, StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        for (var i = 0; i < identifiers.Length; i++)
        {
            var remaining = owned.AsSpan(offset);
            var separator = remaining.IndexOf((byte)'\n');
            var length = separator >= 0 ? separator : remaining.Length;
            if (length == 0 || (i < identifiers.Length - 1 && separator < 0))
            {
                throw new InvalidDataException("Generated SPDX UTF-8 identifiers do not align with the identifier array.");
            }

            var identifier = identifiers[i];
            if (identifier.Length != length)
            {
                throw new InvalidDataException("Generated SPDX UTF-8 identifiers do not align with the identifier array.");
            }
            for (var characterIndex = 0; characterIndex < identifier.Length; characterIndex++)
            {
                var character = identifier[characterIndex];
                if (character > 0x7f || owned[offset + characterIndex] != (byte)character)
                {
                    throw new InvalidDataException("Generated SPDX UTF-8 identifiers do not align with the identifier array.");
                }
            }

            dictionary[identifier] = new Utf8Slice(owned, offset, length);
            offset += length + (separator >= 0 ? 1 : 0);
        }

        if (offset != owned.Length)
        {
            throw new InvalidDataException("Generated SPDX UTF-8 identifiers do not align with the identifier array.");
        }

        return dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
