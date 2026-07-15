using System.Text.Json;

namespace Ol.Core;

/// <summary>Provides Go module proxy metadata.</summary>
public sealed class GoPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://proxy.golang.org/");
    public override string Ecosystem => "golang";
    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(BaseUri, string.Concat(PackageMetadataJson.EscapePath(request.Namespace.Length == 0 ? request.Name : string.Concat(request.Namespace, "/", request.Name)), "/@v/", Uri.EscapeDataString(request.Version), ".info"));
    public override PackageMetadataResponse ParseResponse(JsonElement root)
    {
        var origin = PackageMetadataJson.ReadElement(root, "Origin");
        return new("go-module-proxy", string.Empty, PackageMetadataJson.ReadString(origin, "URL"), PackageMetadataJson.ReadString(origin, "Ref"));
    }
}
