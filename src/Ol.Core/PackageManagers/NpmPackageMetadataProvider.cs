using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides npm registry metadata.</summary>
public sealed class NpmPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://registry.npmjs.org/");
    public override string Ecosystem => "npm";
    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(BaseUri, string.Concat(Uri.EscapeDataString(request.Namespace.Length == 0 ? request.Name : string.Concat(request.Namespace, "/", request.Name)), "/", Uri.EscapeDataString(request.Version)));
    public override PackageMetadataResponse ParseResponse(JsonElement root)
        => new("npm-registry", PackageMetadataJson.ReadString(root, "license"), PackageMetadataJson.ReadRepository(root), PackageMetadataJson.ReadString(root, "gitHead"));
}
