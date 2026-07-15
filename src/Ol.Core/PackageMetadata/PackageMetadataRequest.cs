namespace Ol.Core.PackageMetadata;

/// <summary>
/// Identifies a package metadata lookup derived from a package URL.
/// </summary>
/// <param name="Ecosystem">The supported package ecosystem.</param>
/// <param name="Namespace">The optional package namespace.</param>
/// <param name="Name">The package name.</param>
/// <param name="Version">The package version.</param>
/// <param name="CacheKey">The normalized, stable package metadata cache key.</param>
public readonly record struct PackageMetadataRequest(
    string Ecosystem,
    string Namespace,
    string Name,
    string Version,
    string CacheKey)
{
    /// <summary>
    /// Parses a supported package URL into a package metadata lookup request.
    /// </summary>
    /// <param name="purl">The package URL.</param>
    /// <param name="request">The parsed request when the purl is supported and versioned.</param>
    /// <returns><see langword="true"/> when a supported request was created.</returns>
    public static bool TryCreate(string purl, out PackageMetadataRequest request)
    {
        return TryCreate(purl, OlDefaults.PackageMetadataProviders, out request);
    }

    /// <summary>
    /// Parses a supported package URL using a provider registry.
    /// </summary>
    /// <param name="purl">The package URL.</param>
    /// <param name="providers">The ecosystem providers available for this operation.</param>
    /// <param name="request">The parsed request when the purl is supported and versioned.</param>
    /// <returns><see langword="true"/> when a supported request was created.</returns>
    public static bool TryCreate(string purl, PackageMetadataProviders providers, out PackageMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(providers);
        request = default;
        if (!TryGetEcosystem(purl, out var ecosystem) || !providers.TryGet(ecosystem, out var provider))
        {
            return false;
        }

        return provider.TryCreate(purl, out request);
    }

    internal static bool TryParse(string purl, string expectedEcosystem, out PackageMetadataRequest request)
    {
        request = default;
        if (!TryGetEcosystem(purl, out var ecosystem) || !string.Equals(ecosystem, expectedEcosystem, StringComparison.OrdinalIgnoreCase))
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

    private static bool TryGetEcosystem(string purl, out string ecosystem)
    {
        ecosystem = string.Empty;
        if (!purl.StartsWith("pkg:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var typeEnd = purl.IndexOf('/');
        if (typeEnd <= "pkg:".Length)
        {
            return false;
        }

        ecosystem = purl["pkg:".Length..typeEnd];
        return true;
    }
}
