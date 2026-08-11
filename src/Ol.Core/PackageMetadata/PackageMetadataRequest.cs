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
    /// <summary>
    /// Parses a supported package URL using a provider registry.
    /// </summary>
    /// <param name="purl">The package URL.</param>
    /// <param name="providers">The ecosystem providers available for this operation.</param>
    /// <param name="request">The parsed request when the purl is supported and versioned.</param>
    /// <returns><see langword="true"/> when a supported request was created.</returns>
    public static bool TryCreate(string purl, PackageMetadataProviders providers, out PackageMetadataRequest request)
        => TryCreate(purl, providers, out request, out _);

    /// <summary>
    /// Creates a registry request, reporting whether a failure was the ecosystem or the purl itself.
    /// </summary>
    /// <param name="ecosystemSupported">
    /// <see langword="true"/> when a provider owns this purl's ecosystem. A supported ecosystem that still produced
    /// no request means the purl does not identify one package version, which asks the reader to fix the input
    /// rather than to wait for Ol to gain a provider.
    /// </param>
    public static bool TryCreate(string purl, PackageMetadataProviders providers, out PackageMetadataRequest request, out bool ecosystemSupported)
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

    internal static bool TryParse(string purl, string expectedEcosystem, out PackageMetadataRequest request)
    {
        request = default;
        if (!TryGetEcosystem(purl, out var ecosystem) || !ecosystem.Equals(expectedEcosystem, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var qualifierIndex = purl.AsSpan().IndexOfAny('?', '#');
        var identity = qualifierIndex < 0 ? purl : purl[..qualifierIndex];
        var typeEnd = identity.IndexOf('/');
        var versionSeparator = identity.LastIndexOf('@');
        if (versionSeparator <= typeEnd + 1 || versionSeparator == identity.Length - 1)
        {
            return false;
        }

        var packagePath = identity[(typeEnd + 1)..versionSeparator];
        var nameSeparator = packagePath.LastIndexOf('/');
        var namespaceValue = nameSeparator < 0 ? string.Empty : Uri.UnescapeDataString(packagePath[..nameSeparator]);
        var name = Uri.UnescapeDataString(nameSeparator < 0 ? packagePath : packagePath[(nameSeparator + 1)..]);
        var version = Uri.UnescapeDataString(identity[(versionSeparator + 1)..]);
        if (name.Length == 0 || version.Length == 0)
        {
            return false;
        }

        request = new PackageMetadataRequest(expectedEcosystem, namespaceValue, name, version, identity);
        return true;
    }

    /// <summary>
    /// Reads the purl type without copying it out.
    /// </summary>
    /// <remarks>
    /// The type is only ever a lookup key and a comparison operand, never a retained value, and this runs
    /// once per distinct package an inventory names. Returning a substring allocated a string per purl on
    /// both of the paths that ask for it, and every one of them was discarded immediately.
    /// </remarks>
    private static bool TryGetEcosystem(string purl, out ReadOnlySpan<char> ecosystem)
    {
        ecosystem = default;
        var value = purl.AsSpan();
        if (!value.StartsWith("pkg:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var typeEnd = value.IndexOf('/');
        if (typeEnd <= "pkg:".Length)
        {
            return false;
        }

        ecosystem = value["pkg:".Length..typeEnd];
        return true;
    }
}
