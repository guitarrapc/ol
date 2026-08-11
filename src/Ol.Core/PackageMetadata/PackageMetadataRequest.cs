using System.Buffers;
using System.Text;

namespace Ol.Core.PackageMetadata;

/// <summary>
/// Identifies a package metadata lookup derived from a package URL.
/// </summary>
/// <param name="Ecosystem">The supported package ecosystem.</param>
/// <param name="Namespace">The optional package namespace.</param>
/// <param name="Name">The package name.</param>
/// <param name="Version">The package version.</param>
/// <param name="CacheKey">The normalized, stable package metadata cache key.</param>
/// <param name="Platform">The package platform qualifier, when the provider supports one.</param>
public readonly record struct PackageMetadataRequest(
    string Ecosystem,
    string Namespace,
    string Name,
    string Version,
    string CacheKey,
    string Platform = "")
{
    /// <summary>The stack budget for encoding a purl supplied as a string. 512 bytes.</summary>
    private const int MaxStackPurlBytes = 512;

    /// <summary>
    /// Parses a supported package URL using a provider registry.
    /// </summary>
    /// <param name="purl">The UTF-8 package URL.</param>
    /// <param name="providers">The ecosystem providers available for this operation.</param>
    /// <param name="request">The parsed request when the purl is supported and versioned.</param>
    /// <returns><see langword="true"/> when a supported request was created.</returns>
    public static bool TryCreate(ReadOnlySpan<byte> purl, PackageMetadataProviders providers, out PackageMetadataRequest request)
        => TryCreate(purl, providers, out request, out _);

    /// <summary>
    /// Creates a registry request, reporting whether a failure was the ecosystem or the purl itself.
    /// </summary>
    /// <param name="ecosystemSupported">
    /// <see langword="true"/> when a provider owns this purl's ecosystem. A supported ecosystem that still produced
    /// no request means the purl does not identify one package version, which asks the reader to fix the input
    /// rather than to wait for Ol to gain a provider.
    /// </param>
    public static bool TryCreate(ReadOnlySpan<byte> purl, PackageMetadataProviders providers, out PackageMetadataRequest request, out bool ecosystemSupported)
    {
        ArgumentNullException.ThrowIfNull(providers);
        request = default;
        ecosystemSupported = false;
        if (!TryGetEcosystem(purl, out var ecosystem) || !providers.TryGet(ecosystem, out var provider))
        {
            return false;
        }

        ecosystemSupported = true;
        return provider.TryCreate(purl, out request);
    }

    /// <summary>Parses a package URL supplied as text, for callers that hold one rather than an inventory slice.</summary>
    /// <remarks>
    /// Encodes into a bounded buffer instead of widening the parse to UTF-16, so the one representation the
    /// parser understands stays UTF-8 whichever way a caller arrives at it.
    /// </remarks>
    public static bool TryCreate(string purl, PackageMetadataProviders providers, out PackageMetadataRequest request)
        => TryCreate(purl, providers, out request, out _);

    /// <inheritdoc cref="TryCreate(string, PackageMetadataProviders, out PackageMetadataRequest)"/>
    public static bool TryCreate(string purl, PackageMetadataProviders providers, out PackageMetadataRequest request, out bool ecosystemSupported)
    {
        ArgumentNullException.ThrowIfNull(purl);
        var maximumByteCount = Encoding.UTF8.GetMaxByteCount(purl.Length);
        byte[]? rented = null;
        Span<byte> utf8 = maximumByteCount <= MaxStackPurlBytes
            ? stackalloc byte[MaxStackPurlBytes]
            : (rented = ArrayPool<byte>.Shared.Rent(maximumByteCount));
        try
        {
            return TryCreate(utf8[..Encoding.UTF8.GetBytes(purl, utf8)], providers, out request, out ecosystemSupported);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal static bool TryParse(ReadOnlySpan<byte> purl, string expectedEcosystem, out PackageMetadataRequest request)
    {
        request = default;
        if (!TryGetEcosystem(purl, out var ecosystem) || !PackageMetadataProviders.AsciiEqualsIgnoreCase(ecosystem, expectedEcosystem))
        {
            return false;
        }

        var qualifierIndex = purl.IndexOfAny((byte)'?', (byte)'#');

        // Decoded once, because the identity is itself the cache key the request retains and the name and
        // version are slices of it. Decoding each part separately would cost three UTF-8 decodes for the
        // same three strings, which measured slower than one decode and two copies.
        var identity = Encoding.UTF8.GetString(qualifierIndex < 0 ? purl : purl[..qualifierIndex]);
        var value = identity.AsSpan();
        var typeEnd = value.IndexOf('/');
        var versionSeparator = value.LastIndexOf('@');
        if (versionSeparator <= typeEnd + 1 || versionSeparator == value.Length - 1)
        {
            return false;
        }

        var packagePath = value[(typeEnd + 1)..versionSeparator];
        var nameSeparator = packagePath.LastIndexOf('/');
        var namespaceValue = nameSeparator < 0 ? string.Empty : Unescape(packagePath[..nameSeparator]);
        var name = Unescape(nameSeparator < 0 ? packagePath : packagePath[(nameSeparator + 1)..]);
        var version = Unescape(value[(versionSeparator + 1)..]);
        if (name.Length == 0 || version.Length == 0)
        {
            return false;
        }

        request = new PackageMetadataRequest(expectedEcosystem, namespaceValue, name, version, identity);
        return true;
    }

    /// <summary>
    /// Materializes one purl component, unescaping it only when it carries an escape.
    /// </summary>
    /// <remarks>
    /// This allocation is one the request retains, so it is the one that has to happen. Checking for a
    /// percent first keeps the ordinary purl — which escapes nothing — at exactly that one allocation,
    /// because unescaping an unescaped value would copy it a second time to return the same text.
    /// </remarks>
    private static string Unescape(ReadOnlySpan<char> value)
        => value.IndexOf('%') < 0 ? new string(value) : Uri.UnescapeDataString(new string(value));

    /// <summary>
    /// Reads the purl type without copying it out.
    /// </summary>
    /// <remarks>
    /// The type is only ever a lookup key and a comparison operand, never a retained value, and this runs
    /// once per distinct package an inventory names.
    /// </remarks>
    private static bool TryGetEcosystem(ReadOnlySpan<byte> purl, out ReadOnlySpan<byte> ecosystem)
    {
        const int SchemeLength = 4;
        ecosystem = default;
        if (purl.Length <= SchemeLength || !PackageMetadataProviders.AsciiEqualsIgnoreCase(purl[..SchemeLength], "pkg:"))
        {
            return false;
        }

        var typeEnd = purl.IndexOf((byte)'/');
        if (typeEnd <= SchemeLength)
        {
            return false;
        }

        ecosystem = purl[SchemeLength..typeEnd];
        return true;
    }
}
