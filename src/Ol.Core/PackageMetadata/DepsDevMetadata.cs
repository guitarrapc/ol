using System.Text.Json;

namespace Ol.Core.PackageMetadata;

/// <summary>
/// Projects the parts of a deps.dev version response that Ol treats as evidence.
/// </summary>
/// <remarks>
/// deps.dev answers for several ecosystems with one response shape, so the projection is shared rather than repeated
/// per provider. It derives its values from package contents, which is why it can state a license for a package whose
/// repository Ol cannot collect from.
/// </remarks>
internal static class DepsDevMetadata
{
    /// <summary>The evidence-source name recorded for values taken from deps.dev.</summary>
    public const string Source = "deps.dev";

    /// <summary>
    /// Reads the license values, joining several with <c>;</c> rather than an SPDX operator.
    /// </summary>
    /// <remarks>
    /// deps.dev lists what it found without stating how the values relate. Joining them with <c>OR</c> would assert
    /// that any one of them may be chosen, and with <c>AND</c> that all apply; the response says neither. The joined
    /// value therefore does not normalize, and the component stays ambiguous, which is the honest outcome.
    /// </remarks>
    public static string ReadLicenses(JsonElement root)
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

    /// <summary>Reads the source repository deps.dev associated with the version, when it named one.</summary>
    public static string ReadSourceRepository(JsonElement root)
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

    /// <summary>Creates the version endpoint for one deps.dev system.</summary>
    public static Uri CreateVersionEndpoint(string system, string package, string version)
        => new($"https://api.deps.dev/v3/systems/{system}/packages/{Uri.EscapeDataString(package)}/versions/{Uri.EscapeDataString(version)}");
}
