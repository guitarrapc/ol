using Ol.Core.PackageManagers;
using Ol.Core.PackageMetadata;

namespace Ol.Core;

/// <summary>Provides the standard composition of Ol.Core services.</summary>
public static class OlDefaults
{
    /// <summary>Gets the package metadata providers built into Ol.</summary>
    public static PackageMetadataProviders PackageMetadataProviders { get; } = new([
        new NpmPackageMetadataProvider(),
        new NuGetPackageMetadataProvider(),
        new CargoPackageMetadataProvider(),
        new GoPackageMetadataProvider(),
        new PyPiPackageMetadataProvider(),
    ]);

    /// <summary>Parses a package URL using Ol's built-in package metadata providers.</summary>
    public static bool TryCreatePackageMetadataRequest(string purl, out PackageMetadataRequest request)
        => PackageMetadataRequest.TryCreate(purl, PackageMetadataProviders, out request);

    /// <summary>Creates a package metadata registry client using Ol's built-in package metadata providers.</summary>
    public static PackageMetadataRegistryClient CreatePackageMetadataRegistryClient(HttpClient httpClient)
        => new(httpClient, PackageMetadataProviders);

    /// <summary>Creates a package metadata registry client using Ol's built-in package metadata providers.</summary>
    public static PackageMetadataRegistryClient CreatePackageMetadataRegistryClient(HttpMessageHandler handler)
        => new(handler, PackageMetadataProviders);
}
