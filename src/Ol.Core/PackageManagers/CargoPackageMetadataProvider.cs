using Ol.Core.Licensing;
using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides Cargo registry metadata.</summary>
public sealed class CargoPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://crates.io/api/v1/crates/");
    public override string Ecosystem => "cargo";
    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(BaseUri, string.Concat(Uri.EscapeDataString(request.Name), "/", Uri.EscapeDataString(request.Version)));
    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
    {
        var version = PackageMetadataJson.ReadElement(root, "version");
        var licenseFile = PackageMetadataJson.ReadString(version, "license_file");
        return new(
            "cargo-registry",
            PackageMetadataJson.ReadString(version, "license"),
            PackageMetadataJson.ReadString(version, "repository"),
            string.Empty,
            LicenseCandidateWarnings.None,
            licenseFile.Length == 0 ? DeclaredLicenseReferenceKind.None : DeclaredLicenseReferenceKind.ArtifactPath,
            licenseFile);
    }
}
