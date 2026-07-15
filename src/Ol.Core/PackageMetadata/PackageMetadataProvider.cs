using System.Collections.Frozen;
using System.Text.Json;
using Ol.Core.PackageManagers;

namespace Ol.Core.PackageMetadata;

/// <summary>
/// Describes the normalized metadata returned by one package registry.
/// </summary>
/// <param name="Source">The evidence-source name.</param>
/// <param name="RawLicense">The registry license value.</param>
/// <param name="RepositoryUrl">The repository URL supplied by the registry.</param>
/// <param name="RepositoryRef">The repository commit or ref mapped to the package version, when supplied.</param>
public readonly record struct PackageMetadataResponse(string Source, string RawLicense, string RepositoryUrl, string RepositoryRef = "");

/// <summary>
/// Owns purl validation, endpoint construction, and response projection for one package ecosystem.
/// </summary>
public abstract class PackageMetadataProvider
{
    /// <summary>
    /// Gets the lowercase package-url type handled by this provider.
    /// </summary>
    public abstract string Ecosystem { get; }

    /// <summary>
    /// Parses a versioned purl handled by this provider.
    /// </summary>
    /// <param name="purl">The purl without qualifiers or subpaths.</param>
    /// <param name="request">The resulting metadata request.</param>
    /// <returns><see langword="true"/> when the purl is supported by this provider.</returns>
    public virtual bool TryCreate(string purl, out PackageMetadataRequest request)
        => PackageMetadataRequest.TryParse(purl, Ecosystem, out request);

    /// <summary>
    /// Creates the registry endpoint for a parsed request.
    /// </summary>
    /// <param name="request">The request to retrieve.</param>
    /// <returns>The registry endpoint.</returns>
    public abstract Uri CreateEndpoint(PackageMetadataRequest request);

    /// <summary>
    /// Creates an optional provider-owned endpoint referenced by a registry response.
    /// </summary>
    /// <param name="root">The root JSON response element.</param>
    /// <returns>A trusted follow-up endpoint, or <see langword="null"/>.</returns>
    public virtual Uri? CreateFollowUpEndpoint(JsonElement root) => null;

    /// <summary>
    /// Projects a registry response into normalized metadata evidence.
    /// </summary>
    /// <param name="root">The root JSON response element.</param>
    /// <returns>Normalized provider metadata.</returns>
    public abstract PackageMetadataResponse ParseResponse(JsonElement root);
}

/// <summary>
/// Immutable package-metadata provider registry.
/// </summary>
public sealed class PackageMetadataProviders
{
    private readonly FrozenDictionary<string, PackageMetadataProvider> byEcosystem;
    private readonly PackageMetadataProvider[] providers;

    /// <summary>
    /// Gets the providers built into Ol.
    /// </summary>
    public static PackageMetadataProviders Default { get; } = new([new NpmPackageMetadataProvider(), new NuGetPackageMetadataProvider(), new CargoPackageMetadataProvider(), new GoPackageMetadataProvider()]);

    /// <summary>Gets the number of registered package ecosystems.</summary>
    public int Count => providers.Length;

    /// <summary>
    /// Initializes a provider registry.
    /// </summary>
    /// <param name="providers">The distinct provider instances to register.</param>
    public PackageMetadataProviders(PackageMetadataProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = providers.Length == 0 ? [] : (PackageMetadataProvider[])providers.Clone();
        byEcosystem = this.providers.ToFrozenDictionary(static provider => provider.Ecosystem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds a provider by purl type.
    /// </summary>
    /// <param name="ecosystem">The purl type.</param>
    /// <param name="provider">The registered provider.</param>
    /// <returns><see langword="true"/> when a provider is registered.</returns>
    public bool TryGet(string ecosystem, out PackageMetadataProvider provider)
        => byEcosystem.TryGetValue(ecosystem, out provider!);

    /// <summary>
    /// Resolves the display ecosystem for an unescaped purl without decoding it.
    /// </summary>
    /// <param name="purl">The source-backed purl.</param>
    /// <returns>The registered ecosystem name, or <c>-</c>.</returns>
    public string GetEcosystem(Utf8Slice purl)
    {
        var value = purl.Span;
        if (!value.StartsWith("pkg:"u8))
        {
            return "-";
        }

        var type = value[4..];
        var slash = type.IndexOf((byte)'/');
        if (slash < 0)
        {
            return "-";
        }

        type = type[..slash];
        for (var i = 0; i < providers.Length; i++)
        {
            if (AsciiEqualsIgnoreCase(type, providers[i].Ecosystem))
            {
                return providers[i].Ecosystem;
            }
        }

        return "-";
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> value, string expected)
    {
        if (value.Length != expected.Length)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current is >= (byte)'A' and <= (byte)'Z')
            {
                current = (byte)(current | 0x20);
            }

            if (current != expected[i])
            {
                return false;
            }
        }

        return true;
    }
}

internal static class PackageMetadataJson
{
    public static JsonElement ReadElement(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

    public static string ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    public static string ReadRepository(JsonElement root)
    {
        var repository = ReadElement(root, "repository");
        if (repository.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return repository.ValueKind == JsonValueKind.String ? repository.GetString() ?? string.Empty : ReadString(repository, "url");
    }

    public static string EscapePath(string value) => Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
}
