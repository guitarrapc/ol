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
        new PackagistPackageMetadataProvider(),
        new RubyGemsPackageMetadataProvider(),
        new MavenPackageMetadataProvider(),
        new CocoaPodsPackageMetadataProvider(),
    ]);

    /// <summary>Parses a UTF-8 package URL using Ol's built-in package metadata providers.</summary>
    public static bool TryCreatePackageMetadataRequest(ReadOnlySpan<byte> purl, out PackageMetadataRequest request)
        => PackageMetadataRequest.TryCreate(purl, PackageMetadataProviders, out request);

    /// <summary>Parses a UTF-8 package URL, also reporting whether its ecosystem has a registered provider.</summary>
    public static bool TryCreatePackageMetadataRequest(ReadOnlySpan<byte> purl, out PackageMetadataRequest request, out bool ecosystemSupported)
        => PackageMetadataRequest.TryCreate(purl, PackageMetadataProviders, out request, out ecosystemSupported);

    /// <summary>Parses a package URL supplied as text.</summary>
    public static bool TryCreatePackageMetadataRequest(string purl, out PackageMetadataRequest request)
        => PackageMetadataRequest.TryCreate(purl, PackageMetadataProviders, out request);

    /// <summary>Parses a package URL supplied as text, also reporting whether its ecosystem is registered.</summary>
    public static bool TryCreatePackageMetadataRequest(string purl, out PackageMetadataRequest request, out bool ecosystemSupported)
        => PackageMetadataRequest.TryCreate(purl, PackageMetadataProviders, out request, out ecosystemSupported);

    /// <summary>Creates a package metadata registry client using Ol's built-in package metadata providers.</summary>
    public static PackageMetadataRegistryClient CreatePackageMetadataRegistryClient(HttpClient httpClient)
        => new(httpClient, PackageMetadataProviders);

    /// <summary>Creates a package metadata registry client using Ol's built-in package metadata providers.</summary>
    public static PackageMetadataRegistryClient CreatePackageMetadataRegistryClient(HttpMessageHandler handler)
        => new(handler, PackageMetadataProviders);
}
