using System.Collections.Frozen;
using System.Text.Json;
using Ol.Core.Licensing;
namespace Ol.Core.PackageMetadata;

/// <summary>
/// Describes the normalized metadata returned by one package registry.
/// </summary>
/// <param name="Source">The evidence-source name.</param>
/// <param name="RawLicense">The registry license value.</param>
/// <param name="RepositoryUrl">The repository URL supplied by the registry.</param>
/// <param name="RepositoryRef">The repository commit or ref mapped to the package version, when supplied.</param>
/// <param name="Warnings">Non-fatal metadata warnings.</param>
/// <param name="DeclaredLicenseReferenceKind">What sort of place the publisher declared, when it declared one.</param>
/// <param name="DeclaredLicenseReference">The declared location, empty for embedded text whose content is not retained.</param>
public readonly record struct PackageMetadataResponse(
    string Source,
    string RawLicense,
    string RepositoryUrl,
    string RepositoryRef = "",
    LicenseCandidateWarnings Warnings = LicenseCandidateWarnings.None,
    DeclaredLicenseReferenceKind DeclaredLicenseReferenceKind = DeclaredLicenseReferenceKind.None,
    string DeclaredLicenseReference = "");

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
    /// Gets the optional service index used to discover this provider's current API endpoint.
    /// </summary>
    public virtual Uri? ServiceIndexEndpoint => null;

    /// <summary>
    /// Gets whether this ecosystem's package name includes its purl namespace, joined by <c>/</c>.
    /// </summary>
    /// <remarks>
    /// True where the namespace is part of what the ecosystem calls the package: npm installs
    /// <c>@scope/pkg</c>, Go requires <c>github.com/owner/repo</c>, Composer requires <c>vendor/package</c>.
    /// False where the namespace is a separate coordinate, as Maven's group is to its artifact, so the
    /// package name stays the artifact name and the group remains visible in the purl and source id.
    /// An SBOM that splits the two into <c>group</c> and <c>name</c> is reassembled by this rule, which
    /// is why it belongs to the provider that already composes the same two parts into a registry
    /// endpoint rather than to a switch in a parser.
    /// </remarks>
    public virtual bool PackageNameIncludesNamespace => false;

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
    /// Creates the registry endpoint from a service endpoint discovered once for this client.
    /// </summary>
    /// <param name="request">The request to retrieve.</param>
    /// <param name="serviceEndpoint">The provider-owned endpoint selected from its service index.</param>
    /// <returns>The registry endpoint.</returns>
    public virtual Uri CreateEndpoint(PackageMetadataRequest request, Uri serviceEndpoint)
        => CreateEndpoint(request);

    /// <summary>
    /// Selects the provider endpoint from its service index.
    /// </summary>
    /// <param name="root">The service-index root.</param>
    /// <param name="serviceEndpoint">The selected endpoint.</param>
    /// <returns><see langword="true" /> when a supported endpoint was found.</returns>
    public virtual bool TryResolveServiceEndpoint(JsonElement root, out Uri serviceEndpoint)
    {
        serviceEndpoint = null!;
        return false;
    }

    /// <summary>
    /// Creates an optional provider-owned endpoint referenced by a registry response.
    /// </summary>
    /// <param name="root">The root JSON response element.</param>
    /// <returns>A trusted follow-up endpoint, or <see langword="null"/>.</returns>
    public virtual Uri? CreateFollowUpEndpoint(JsonElement root) => null;

    /// <summary>
    /// Creates an optional provider-owned endpoint referenced by a registry response for a package request.
    /// </summary>
    /// <param name="root">The root JSON response element.</param>
    /// <param name="request">The package request associated with the response.</param>
    /// <returns>A trusted follow-up endpoint, or <see langword="null"/>.</returns>
    public virtual Uri? CreateFollowUpEndpoint(JsonElement root, PackageMetadataRequest request)
        => CreateFollowUpEndpoint(root);

    /// <summary>
    /// Gets whether a follow-up this provider named must succeed for the lookup to succeed.
    /// </summary>
    /// <remarks>
    /// It must when the first response is only a pointer and carries no metadata of its own, as a NuGet registration
    /// does. It must not when the first response already stands on its own and the follow-up only adds to it: failing
    /// the whole component would then lose evidence Ol had already obtained.
    /// </remarks>
    public virtual bool FollowUpIsRequired => true;

    /// <summary>
    /// Projects a registry response into normalized metadata evidence.
    /// </summary>
    /// <param name="root">The root JSON response element.</param>
    /// <param name="request">The package request associated with the response.</param>
    /// <returns>Normalized provider metadata.</returns>
    public abstract PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request);

    /// <summary>
    /// Projects a registry response that a follow-up replaced, together with the response that named it.
    /// </summary>
    /// <param name="root">The last document reached, or the only one when no follow-up was made.</param>
    /// <param name="initial">The first document, which equals <paramref name="root"/> when no follow-up was made.</param>
    /// <param name="request">The package request associated with the response.</param>
    /// <remarks>
    /// A provider needs both only when each document states something the other does not. Go is the case: the module
    /// proxy states where a version came from and at which ref, and deps.dev states the license.
    /// </remarks>
    public virtual PackageMetadataResponse ParseResponse(JsonElement root, JsonElement initial, PackageMetadataRequest request)
        => ParseResponse(root, request);
}

/// <summary>
/// Immutable package-metadata provider registry.
/// </summary>
public sealed class PackageMetadataProviders
{
    private readonly FrozenDictionary<string, PackageMetadataProvider> byEcosystem;
    private readonly PackageMetadataProvider[] providers;

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
        => GetEcosystem(purl, out _);

    /// <summary>
    /// Resolves the display ecosystem for an unescaped purl without decoding it, and reports whether the
    /// ecosystem names a package with its namespace.
    /// </summary>
    /// <param name="purl">The source-backed purl.</param>
    /// <param name="packageNameIncludesNamespace">
    /// <see langword="true"/> when the resolved provider's ecosystem includes the namespace in the package
    /// name. Always <see langword="false"/> for an unregistered ecosystem, which has no known convention.
    /// </param>
    /// <returns>The registered ecosystem name, or <c>-</c>.</returns>
    public string GetEcosystem(Utf8Slice purl, out bool packageNameIncludesNamespace)
    {
        packageNameIncludesNamespace = false;
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
                packageNameIncludesNamespace = providers[i].PackageNameIncludesNamespace;
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
