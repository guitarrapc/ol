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
    ]);
}
