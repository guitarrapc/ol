using Ol.Core.Licensing;
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
    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
        => new(
            "npm-registry",
            ReadLicense(root),
            PackageMetadataJson.ReadRepository(root),
            PackageMetadataJson.ReadString(root, "gitHead"),
            HasRepositoryDirectory(root) ? LicenseCandidateWarnings.SourceRepositorySubdirectory : LicenseCandidateWarnings.None);

    /// <summary>Reports whether the publisher placed this package in one directory of a shared repository.</summary>
    /// <remarks>
    /// npm's <c>repository.directory</c> exists precisely because the repository holds more than this
    /// package. The repository stays the package's repository, but its root license answers for whatever
    /// the repository as a whole is licensed under, which in a monorepo is a different package.
    /// </remarks>
    private static bool HasRepositoryDirectory(JsonElement root)
        => PackageMetadataJson.ReadString(PackageMetadataJson.ReadElement(root, "repository"), "directory").Length != 0;

    /// <summary>Reads the license from whichever declaration shape the published metadata uses.</summary>
    /// <remarks>
    /// npm declared licenses as an object and as a collection of them before the current string field,
    /// and packages published under those shapes are still installed today. The current field wins when
    /// both are present, because a package that carries both was republished with the newer one.
    /// A collection of several entries states no relationship between them, so it resolves nothing
    /// rather than having one entry picked or an operator invented.
    /// </remarks>
    private static string ReadLicense(JsonElement root)
    {
        var license = PackageMetadataJson.ReadElement(root, "license");
        if (license.ValueKind == JsonValueKind.String)
        {
            return license.GetString() ?? string.Empty;
        }

        if (license.ValueKind == JsonValueKind.Object)
        {
            return PackageMetadataJson.ReadString(license, "type");
        }

        var licenses = PackageMetadataJson.ReadElement(root, "licenses");
        if (licenses.ValueKind == JsonValueKind.Object)
        {
            return PackageMetadataJson.ReadString(licenses, "type");
        }

        if (licenses.ValueKind != JsonValueKind.Array || licenses.GetArrayLength() != 1)
        {
            return string.Empty;
        }

        var single = licenses[0];
        return single.ValueKind == JsonValueKind.String ? single.GetString() ?? string.Empty : PackageMetadataJson.ReadString(single, "type");
    }
}
