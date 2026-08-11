using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides Packagist repository metadata for Composer packages.</summary>
public sealed class PackagistPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://packagist.org/packages/");

    public override string Ecosystem => "composer";
    public override bool PackageNameIncludesNamespace => true;

    public override bool TryCreate(Utf8Slice purl, out PackageMetadataRequest request)
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

    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
    {
        var package = PackageMetadataJson.ReadElement(root, "package");
        var versions = PackageMetadataJson.ReadElement(package, "versions");
        var version = versions.ValueKind == JsonValueKind.Object && versions.TryGetProperty(request.Version, out var requestedVersion)
            ? requestedVersion
            : default;
        return new("packagist-registry", ReadLicenses(version), PackageMetadataJson.ReadString(package, "repository"));
    }

    private static string ReadLicenses(JsonElement version)
    {
        var licenses = PackageMetadataJson.ReadElement(version, "license");
        if (licenses.ValueKind != JsonValueKind.Array) return string.Empty;
        var values = new string[licenses.GetArrayLength()];
        var count = 0;
        foreach (var item in licenses.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value)) values[count++] = value;
        }

        return count == 0 ? string.Empty : string.Join(" OR ", values.AsSpan(0, count));
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
