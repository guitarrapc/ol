using NuGet.Versioning;
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
        => new(serviceEndpoint, string.Concat(Uri.EscapeDataString(request.Name.ToLowerInvariant()), "/index.json"));
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
            if (TryCreateTrustedNuGetEndpoint(id, out var endpoint) && endpoint.AbsolutePath.EndsWith('/'))
            {
                serviceEndpoint = endpoint;
                return true;
            }
        }

        return false;
    }

    public override Uri? CreateFollowUpEndpoint(JsonElement root, PackageMetadataRequest request)
    {
        if (TryFindCatalogEntry(root, request.Version, out _)
            || !NuGetVersion.TryParse(request.Version, out var requestedVersion))
        {
            return null;
        }

        var pages = PackageMetadataJson.ReadElement(root, "items");
        if (pages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var page in pages.EnumerateArray())
        {
            if (PackageMetadataJson.ReadElement(page, "items").ValueKind == JsonValueKind.Array
                || !NuGetVersion.TryParse(PackageMetadataJson.ReadString(page, "lower"), out var lower)
                || !NuGetVersion.TryParse(PackageMetadataJson.ReadString(page, "upper"), out var upper)
                || VersionComparer.VersionRelease.Compare(requestedVersion, lower) < 0
                || VersionComparer.VersionRelease.Compare(requestedVersion, upper) > 0)
            {
                continue;
            }

            var id = PackageMetadataJson.ReadString(page, "@id");
            return TryCreateTrustedNuGetEndpoint(id, out var endpoint) ? endpoint : null;
        }

        return null;
    }

    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
    {
        if (!TryFindCatalogEntry(root, request.Version, out var catalog))
        {
            return new("nuget-registry", "", "", "");
        }

        var repository = PackageMetadataJson.ReadElement(catalog, "repository");
        var repositoryUrl = PackageMetadataJson.ReadString(repository, "url");
        if (repositoryUrl.Length == 0) repositoryUrl = PackageMetadataJson.ReadString(catalog, "projectUrl");
        return new("nuget-registry", PackageMetadataJson.ReadString(catalog, "licenseExpression"), repositoryUrl, PackageMetadataJson.ReadString(repository, "commit"));
    }

    private static bool TryFindCatalogEntry(JsonElement root, string requestedVersion, out JsonElement catalog)
    {
        catalog = default;
        if (!NuGetVersion.TryParse(requestedVersion, out var version))
        {
            return false;
        }

        var items = PackageMetadataJson.ReadElement(root, "items");
        if (items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (TryMatchCatalogEntry(item, version, out catalog))
            {
                return true;
            }

            var leaves = PackageMetadataJson.ReadElement(item, "items");
            if (leaves.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var leaf in leaves.EnumerateArray())
            {
                if (TryMatchCatalogEntry(leaf, version, out catalog))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryMatchCatalogEntry(JsonElement leaf, NuGetVersion requestedVersion, out JsonElement catalog)
    {
        catalog = PackageMetadataJson.ReadElement(leaf, "catalogEntry");
        return catalog.ValueKind == JsonValueKind.Object
            && NuGetVersion.TryParse(PackageMetadataJson.ReadString(catalog, "version"), out var catalogVersion)
            && VersionComparer.VersionRelease.Equals(requestedVersion, catalogVersion);
    }

    private static bool TryCreateTrustedNuGetEndpoint(string value, out Uri endpoint)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out endpoint!)
            && endpoint.Scheme == Uri.UriSchemeHttps
            && endpoint.IsDefaultPort
            && endpoint.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase)
            && endpoint.UserInfo.Length == 0
            && endpoint.Query.Length == 0
            && endpoint.Fragment.Length == 0)
        {
            return true;
        }

        endpoint = null!;
        return false;
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
