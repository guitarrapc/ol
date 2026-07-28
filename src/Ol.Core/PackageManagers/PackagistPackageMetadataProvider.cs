using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides Packagist repository metadata for Composer packages.</summary>
public sealed class PackagistPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://packagist.org/packages/");

    public override string Ecosystem => "composer";

    public override bool TryCreate(string purl, out PackageMetadataRequest request)
    {
        if (!base.TryCreate(purl, out request)
            || request.Namespace.Length == 0
            || request.Namespace.Contains('/')
            || !IsComposerNamePart(request.Namespace)
            || !IsComposerNamePart(request.Name))
        {
            request = default;
            return false;
        }

        return true;
    }

    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(BaseUri, string.Concat(Uri.EscapeDataString(request.Namespace), "/", Uri.EscapeDataString(request.Name), ".json"));

    public override PackageMetadataResponse ParseResponse(JsonElement root)
    {
        var package = PackageMetadataJson.ReadElement(root, "package");
        return new("packagist-registry", string.Empty, PackageMetadataJson.ReadString(package, "repository"));
    }

    private static bool IsComposerNamePart(string value)
    {
        if (value.Length == 0 || !IsAsciiAlphaNumeric(value[0]) || !IsAsciiAlphaNumeric(value[^1])) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.') continue;
            return false;
        }

        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value)
        => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
