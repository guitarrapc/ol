using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides NuGet registration metadata.</summary>
public sealed class NuGetPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri ServiceIndexUri = new("https://api.nuget.org/v3/index.json");
    private static readonly Uri FallbackBaseUri = new("https://api.nuget.org/v3/registration5-gz-semver2/");
    private const string RegistrationType = "RegistrationsBaseUrl/3.6.0";

    public override string Ecosystem => "nuget";
    public override Uri? ServiceIndexEndpoint => ServiceIndexUri;
    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => CreateEndpoint(request, FallbackBaseUri);
    public override Uri CreateEndpoint(PackageMetadataRequest request, Uri serviceEndpoint)
        => new(serviceEndpoint, string.Concat(Uri.EscapeDataString(request.Name.ToLowerInvariant()), "/", Uri.EscapeDataString(request.Version.ToLowerInvariant()), ".json"));
    public override bool TryResolveServiceEndpoint(JsonElement root, out Uri serviceEndpoint)
    {
        serviceEndpoint = null!;
        var resources = PackageMetadataJson.ReadElement(root, "resources");
        if (resources.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var resource in resources.EnumerateArray())
        {
            if (!HasRegistrationType(PackageMetadataJson.ReadElement(resource, "@type")))
            {
                continue;
            }

            var id = PackageMetadataJson.ReadString(resource, "@id");
            if (Uri.TryCreate(id, UriKind.Absolute, out var endpoint)
                && endpoint.Scheme == Uri.UriSchemeHttps
                && endpoint.IsDefaultPort
                && endpoint.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase)
                && endpoint.UserInfo.Length == 0
                && endpoint.Query.Length == 0
                && endpoint.Fragment.Length == 0
                && endpoint.AbsolutePath.EndsWith('/'))
            {
                serviceEndpoint = endpoint;
                return true;
            }
        }

        return false;
    }
    public override Uri? CreateFollowUpEndpoint(JsonElement root)
    {
        var catalog = PackageMetadataJson.ReadElement(root, "catalogEntry");
        if (catalog.ValueKind != JsonValueKind.String || !Uri.TryCreate(catalog.GetString(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort || !endpoint.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase) || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            return null;
        }

        return endpoint;
    }
    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
    {
        var catalog = PackageMetadataJson.ReadElement(root, "catalogEntry");
        if (catalog.ValueKind != JsonValueKind.Object) catalog = root;
        var repository = PackageMetadataJson.ReadElement(catalog, "repository");
        var repositoryUrl = PackageMetadataJson.ReadString(repository, "url");
        if (repositoryUrl.Length == 0) repositoryUrl = PackageMetadataJson.ReadString(catalog, "projectUrl");
        return new("nuget-registry", PackageMetadataJson.ReadString(catalog, "licenseExpression"), repositoryUrl, PackageMetadataJson.ReadString(repository, "commit"));
    }

    private static bool HasRegistrationType(JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            return type.ValueEquals(RegistrationType);
        }

        if (type.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var value in type.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.ValueEquals(RegistrationType))
            {
                return true;
            }
        }

        return false;
    }
}
