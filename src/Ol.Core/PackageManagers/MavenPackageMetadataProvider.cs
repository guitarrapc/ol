using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides Maven package metadata from deps.dev.</summary>
public sealed class MavenPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://api.deps.dev/v3/systems/maven/packages/");

    public override string Ecosystem => "maven";

    public override bool TryCreate(string purl, out PackageMetadataRequest request)
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
        => new("deps.dev", ReadLicenses(root), ReadSourceRepository(root));

    private static string ReadLicenses(JsonElement root)
    {
        var licenses = PackageMetadataJson.ReadElement(root, "licenses");
        if (licenses.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var length = licenses.GetArrayLength();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length == 1)
        {
            var value = licenses[0].ValueKind == JsonValueKind.String ? licenses[0].GetString() : null;
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
        }

        var values = new string[length];
        var count = 0;
        foreach (var item in licenses.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[count++] = value;
            }
        }

        return count switch
        {
            0 => string.Empty,
            1 => values[0],
            _ => string.Join("; ", values.AsSpan(0, count)),
        };
    }

    private static string ReadSourceRepository(JsonElement root)
    {
        var links = PackageMetadataJson.ReadElement(root, "links");
        if (links.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.ValueKind == JsonValueKind.Object
                && link.TryGetProperty("label", out var label)
                && label.ValueKind == JsonValueKind.String
                && label.ValueEquals("SOURCE_REPO"))
            {
                return PackageMetadataJson.ReadString(link, "url");
            }
        }

        return string.Empty;
    }
}
