using System.Text.Json;

namespace Ol.Core;

/// <summary>Provides NuGet registration metadata.</summary>
public sealed class NuGetPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://api.nuget.org/v3/registration5-semver1/");
    public override string Ecosystem => "nuget";
    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(BaseUri, string.Concat(Uri.EscapeDataString(request.Name.ToLowerInvariant()), "/", Uri.EscapeDataString(request.Version.ToLowerInvariant()), ".json"));
    public override Uri? CreateFollowUpEndpoint(JsonElement root)
    {
        var catalog = PackageMetadataJson.ReadElement(root, "catalogEntry");
        if (catalog.ValueKind != JsonValueKind.String || !Uri.TryCreate(catalog.GetString(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort || !endpoint.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase) || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            return null;
        }

        return endpoint;
    }
    public override PackageMetadataResponse ParseResponse(JsonElement root)
    {
        var catalog = PackageMetadataJson.ReadElement(root, "catalogEntry");
        if (catalog.ValueKind != JsonValueKind.Object) catalog = root;
        var repository = PackageMetadataJson.ReadElement(catalog, "repository");
        var repositoryUrl = PackageMetadataJson.ReadString(repository, "url");
        if (repositoryUrl.Length == 0) repositoryUrl = PackageMetadataJson.ReadString(catalog, "projectUrl");
        return new("nuget-registry", PackageMetadataJson.ReadString(catalog, "licenseExpression"), repositoryUrl, PackageMetadataJson.ReadString(repository, "commit"));
    }
}
