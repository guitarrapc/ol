using System.Text.Json;

namespace Ol.Core;

/// <summary>Provides Cargo registry metadata.</summary>
public sealed class CargoPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://crates.io/api/v1/crates/");
    public override string Ecosystem => "cargo";
    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(BaseUri, string.Concat(Uri.EscapeDataString(request.Name), "/", Uri.EscapeDataString(request.Version)));
    public override PackageMetadataResponse ParseResponse(JsonElement root)
    {
        var version = PackageMetadataJson.ReadElement(root, "version");
        return new("cargo-registry", PackageMetadataJson.ReadString(version, "license"), PackageMetadataJson.ReadString(version, "repository"), string.Empty);
    }
}
