using NuGet.Versioning;
using Ol.Core.PackageMetadata;
using System.Text.Json;
using Ol.Core.Licensing;
using Ol.Core.SourceRepository;

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
        if (!NuGetVersion.TryParse(request.Version, out var requestedVersion))
        {
            return null;
        }

        if (TryFindRegistrationCatalogEntry(root, requestedVersion, out var registrationEntry))
        {
            // The registration inlines only part of the catalog entry. It omits `licenseFile` and
            // `repository`, and rewrites `licenseUrl` to the gallery license page, so the entry itself
            // is the only place those are observable. It is worth one request only when the
            // registration declared no expression, because an expression already answers the question.
            if (PackageMetadataJson.ReadString(registrationEntry, "licenseExpression").Length != 0)
            {
                return null;
            }

            var catalogId = PackageMetadataJson.ReadString(registrationEntry, "@id");
            return TryCreateTrustedNuGetEndpoint(catalogId, out var catalogEndpoint) ? catalogEndpoint : null;
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
            return new("nuget-registry", "", "", "", LicenseCandidateWarnings.NuGetLicenseMetadataMissing);
        }

        var repository = PackageMetadataJson.ReadElement(catalog, "repository");
        var repositoryUrl = PackageMetadataRegistryClient.SanitizeRepositoryUrl(PackageMetadataJson.ReadString(repository, "url"));
        var repositoryRef = PackageMetadataJson.ReadString(repository, "commit");
        var licenseExpression = PackageMetadataJson.ReadString(catalog, "licenseExpression");
        var licenseFile = PackageMetadataJson.ReadString(catalog, "licenseFile");
        var licenseUrl = PackageMetadataJson.ReadString(catalog, "licenseUrl");
        var projectUrl = PackageMetadataJson.ReadString(catalog, "projectUrl");
        // A declared repository stays the package's repository even when Ol cannot collect from it.
        // The legacy URL only fills an absent one, and only to resolve a license Ol does not already have.
        if (repositoryUrl.Length == 0)
        {
            if (licenseExpression.Length == 0
                && TryCreateRepositoryFromLicenseUrl(licenseUrl, out var licenseRepository, out var licenseRef))
            {
                repositoryUrl = licenseRepository;
                repositoryRef = licenseRef;
            }
            else
            {
                repositoryUrl = PackageMetadataRegistryClient.SanitizeRepositoryUrl(projectUrl);
            }
        }

        var warnings = LicenseCandidateWarnings.None;
        if (licenseExpression.Length == 0 && !SourceRepositoryTarget.TryCreate(repositoryUrl, repositoryRef, out _))
        {
            warnings = licenseFile.Length != 0 ? LicenseCandidateWarnings.NuGetLicenseFileUnresolved
                : licenseUrl.Length != 0 ? LicenseCandidateWarnings.NuGetLicenseUrlUnsupported
                : LicenseCandidateWarnings.NuGetLicenseMetadataMissing;
        }

        return new("nuget-registry", licenseExpression, repositoryUrl, repositoryRef, warnings);
    }

    private static bool TryCreateRepositoryFromLicenseUrl(string value, out string repositoryUrl, out string repositoryRef)
    {
        repositoryUrl = string.Empty;
        repositoryRef = string.Empty;
        if (!TryCreateTrustedGitHubUri(value, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.AsSpan();
        if (path.IsEmpty || path[0] != '/')
        {
            return false;
        }

        path = path[1..];
        var offset = 0;
        if (!TryTakePathSegment(path, ref offset, out var ownerRange)
            || !TryTakePathSegment(path, ref offset, out var repositoryRange))
        {
            return false;
        }

        Range referenceRange;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryTakePathSegment(path, ref offset, out var markerRange)
                || !path[markerRange].Equals("blob", StringComparison.Ordinal)
                || !TryTakePathSegment(path, ref offset, out referenceRange))
            {
                return false;
            }
        }
        else if (!TryTakePathSegment(path, ref offset, out referenceRange))
        {
            return false;
        }

        if (offset >= path.Length)
        {
            return false;
        }

        // The repository-level License API answers with the license GitHub detects at the repository
        // root, never with the file this URL names. Only a root file whose name is one GitHub detects
        // can be assumed to be that answer, so a nested path or a qualified name is not projected.
        var file = path[offset..];
        if (file.Contains('/') || !IsDetectableLicenseFileName(file))
        {
            return false;
        }

        var owner = path[ownerRange];
        var repository = path[repositoryRange];
        var reference = path[referenceRange];
        if (reference.Length > 256)
        {
            return false;
        }

        repositoryUrl = string.Concat("https://github.com/", owner, "/", repository);
        repositoryRef = reference.ToString();
        return true;
    }

    /// <summary>Reports whether a root file name is one GitHub reports as the repository license.</summary>
    /// <remarks>
    /// A qualified name such as <c>LICENSE.MIT</c> or <c>COPYING.LESSER</c> is excluded: it names one
    /// license among several in the same repository, and the repository-level answer would be a
    /// different file.
    /// </remarks>
    private static bool IsDetectableLicenseFileName(ReadOnlySpan<char> value)
    {
        var dot = value.LastIndexOf('.');
        if (dot >= 0
            && !value[(dot + 1)..].Equals("txt", StringComparison.OrdinalIgnoreCase)
            && !value[(dot + 1)..].Equals("md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = dot < 0 ? value : value[..dot];
        return stem.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("LICENCE", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("COPYING", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("UNLICENSE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateTrustedGitHubUri(string value, out Uri uri)
    {
        if (value.Length == 0
            || ContainsUnsafeUrlCharacter(value)
            || ContainsDotPathSegment(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out uri!)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals("raw.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            uri = null!;
            return false;
        }

        return true;
    }

    private static bool ContainsDotPathSegment(string value)
    {
        var schemeEnd = value.AsSpan().IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return false;
        }

        var pathStart = value.AsSpan(schemeEnd + 3).IndexOf('/');
        if (pathStart < 0)
        {
            return false;
        }

        var path = value.AsSpan(schemeEnd + 3 + pathStart + 1);
        while (!path.IsEmpty)
        {
            var separator = path.IndexOf('/');
            var segment = separator < 0 ? path : path[..separator];
            if (segment.SequenceEqual(".".AsSpan()) || segment.SequenceEqual("..".AsSpan()))
            {
                return true;
            }

            if (separator < 0)
            {
                break;
            }

            path = path[(separator + 1)..];
        }

        return false;
    }

    private static bool TryTakePathSegment(ReadOnlySpan<char> path, ref int offset, out Range range)
    {
        range = default;
        if ((uint)offset >= (uint)path.Length)
        {
            return false;
        }

        var separator = path[offset..].IndexOf('/');
        var end = separator < 0 ? path.Length : offset + separator;
        var segment = path[offset..end];
        range = offset..end;
        offset = separator < 0 ? path.Length : end + 1;
        return !segment.IsEmpty && !segment.SequenceEqual(".".AsSpan()) && !segment.SequenceEqual("..".AsSpan());
    }

    private static bool ContainsUnsafeUrlCharacter(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is '%' or '\\' || char.IsControl(value[i]) || char.IsWhiteSpace(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindCatalogEntry(JsonElement root, string requestedVersion, out JsonElement catalog)
    {
        catalog = default;
        if (!NuGetVersion.TryParse(requestedVersion, out var version))
        {
            return false;
        }

        if (TryFindRegistrationCatalogEntry(root, version, out catalog))
        {
            return true;
        }

        // A catalog entry describes exactly one package version and carries no registration items, so
        // the document Ol followed to is itself the entry to project.
        if (root.ValueKind != JsonValueKind.Object
            || PackageMetadataJson.ReadElement(root, "items").ValueKind == JsonValueKind.Array
            || !NuGetVersion.TryParse(PackageMetadataJson.ReadString(root, "version"), out var catalogVersion)
            || !VersionComparer.VersionRelease.Equals(version, catalogVersion))
        {
            catalog = default;
            return false;
        }

        catalog = root;
        return true;
    }

    private static bool TryFindRegistrationCatalogEntry(JsonElement root, NuGetVersion version, out JsonElement catalog)
    {
        catalog = default;
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
