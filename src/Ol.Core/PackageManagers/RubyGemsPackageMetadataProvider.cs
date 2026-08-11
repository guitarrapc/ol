using Ol.Core.PackageMetadata;
using System.Text;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides version-specific RubyGems.org metadata.</summary>
public sealed class RubyGemsPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://rubygems.org/api/v2/rubygems/");

    public override string Ecosystem => "gem";

    public override bool TryCreate(Utf8Slice purl, out PackageMetadataRequest request)
    {
        if (!base.TryCreate(purl, out request) || request.Namespace.Length != 0 || !TryReadPlatform(purl.Span, out var platform))
        {
            request = default;
            return false;
        }

        // The platform qualifier distinguishes two published gems, so unlike every other ecosystem the key
        // keeps the query. Only the subpath is dropped, and what remains is still a slice of the purl.
        var fragment = purl.Span.IndexOf((byte)'#');
        request = request with { CacheKey = fragment < 0 ? purl : purl.Slice(0, fragment), Platform = platform };
        return true;
    }

    public override Uri CreateEndpoint(PackageMetadataRequest request)
    {
        var path = string.Concat(
            Uri.EscapeDataString(request.Name),
            "/versions/",
            Uri.EscapeDataString(request.Version),
            ".json");
        if (request.Platform.Length != 0)
        {
            path = string.Concat(path, "?platform=", Uri.EscapeDataString(request.Platform));
        }

        return new(BaseUri, path);
    }

    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
    {
        var license = ReadLicenses(root);
        var repository = PackageMetadataJson.ReadString(root, "source_code_uri");
        if (repository.Length == 0)
        {
            var metadata = PackageMetadataJson.ReadElement(root, "metadata");
            repository = PackageMetadataJson.ReadString(metadata, "source_code_uri");
        }

        if (repository.Length == 0) repository = PackageMetadataJson.ReadString(root, "homepage_uri");
        return new("rubygems-registry", license, repository);
    }

    private static bool TryReadPlatform(ReadOnlySpan<byte> purl, out string platform)
    {
        platform = string.Empty;
        var query = purl.IndexOf((byte)'?');
        var fragment = purl.IndexOf((byte)'#');
        if (query < 0) return true;
        if (fragment >= 0 && query > fragment) return false;
        var end = fragment < 0 ? purl.Length : fragment;
        var value = purl[(query + 1)..end];
        const int PrefixLength = 9; // "platform="
        if (!value.StartsWith("platform="u8) || value[PrefixLength..].IndexOf((byte)'&') >= 0)
        {
            return false;
        }

        platform = Uri.UnescapeDataString(Encoding.UTF8.GetString(value[PrefixLength..]));
        return platform.Length != 0;
    }

    private static string ReadLicenses(JsonElement root)
    {
        var licenses = PackageMetadataJson.ReadElement(root, "licenses");
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
}
