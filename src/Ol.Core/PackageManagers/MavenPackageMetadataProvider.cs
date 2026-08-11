using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides Maven package metadata from deps.dev.</summary>
public sealed class MavenPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://api.deps.dev/v3/systems/maven/packages/");

    public override string Ecosystem => "maven";

    public override bool TryCreate(ReadOnlySpan<byte> purl, out PackageMetadataRequest request)
    {
        if (!base.TryCreate(purl, out request)
            || request.Namespace.Length == 0
            || request.Namespace.IndexOfAny('/', ':') >= 0
            || request.Name.IndexOfAny('/', ':') >= 0)
        {
            request = default;
            return false;
        }

        return true;
    }

    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(
            BaseUri,
            string.Concat(
                Uri.EscapeDataString(string.Concat(request.Namespace, ":", request.Name)),
                "/versions/",
                Uri.EscapeDataString(request.Version)));

    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
        => new(DepsDevMetadata.Source, DepsDevMetadata.ReadLicenses(root), DepsDevMetadata.ReadSourceRepository(root));
}
