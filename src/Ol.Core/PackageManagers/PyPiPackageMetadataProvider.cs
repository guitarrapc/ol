using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides PyPI release metadata.</summary>
public sealed class PyPiPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://pypi.org/pypi/");

    public override string Ecosystem => "pypi";

    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(BaseUri, string.Concat(Uri.EscapeDataString(request.Name), "/", Uri.EscapeDataString(request.Version), "/json"));

    public override PackageMetadataResponse ParseResponse(JsonElement root)
    {
        var info = PackageMetadataJson.ReadElement(root, "info");
        var license = PackageMetadataJson.ReadString(info, "license_expression");
        if (license.Length == 0) license = PackageMetadataJson.ReadString(info, "license");
        var projectUrls = PackageMetadataJson.ReadElement(info, "project_urls");
        var repository = PackageMetadataJson.ReadString(projectUrls, "Source");
        if (repository.Length == 0) repository = PackageMetadataJson.ReadString(projectUrls, "Source Code");
        if (repository.Length == 0) repository = PackageMetadataJson.ReadString(projectUrls, "Repository");
        if (repository.Length == 0) repository = PackageMetadataJson.ReadString(projectUrls, "Code");
        if (repository.Length == 0) repository = PackageMetadataJson.ReadString(projectUrls, "Homepage");
        return new("pypi-registry", license, repository);
    }
}
