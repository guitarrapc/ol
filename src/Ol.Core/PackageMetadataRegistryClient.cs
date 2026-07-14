using System.Net;
using System.Text.Json;

namespace Ol.Core;

/// <summary>
/// Retrieves normalized package metadata from supported public registries.
/// </summary>
public sealed class PackageMetadataRegistryClient
{
    private static readonly Uri NpmBaseUri = new("https://registry.npmjs.org/");
    private static readonly Uri NuGetBaseUri = new("https://api.nuget.org/v3/registration5-semver1/");
    private static readonly Uri CargoBaseUri = new("https://crates.io/api/v1/crates/");
    private static readonly Uri GoBaseUri = new("https://proxy.golang.org/");
    private readonly HttpClient httpClient;

    /// <summary>
    /// Initializes a registry client using the supplied HTTP client.
    /// </summary>
    /// <param name="httpClient">HTTP client used for registry requests.</param>
    public PackageMetadataRegistryClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    /// <summary>
    /// Initializes a registry client using a test or custom HTTP message handler.
    /// </summary>
    /// <param name="handler">HTTP handler used for registry requests.</param>
    public PackageMetadataRegistryClient(HttpMessageHandler handler)
        : this(new HttpClient(handler, disposeHandler: true))
    {
    }

    /// <summary>
    /// Fetches and normalizes metadata for one supported package request.
    /// </summary>
    /// <param name="request">The supported package request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A cacheable normalized metadata record.</returns>
    /// <exception cref="PackageMetadataFetchException">The registry did not return usable metadata.</exception>
    public async Task<PackageMetadataRecord> FetchAsync(PackageMetadataRequest request, CancellationToken cancellationToken = default)
    {
        var endpoint = CreateEndpoint(request);
        using var response = await httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PackageMetadataFetchException(response.StatusCode);
        }

        try
        {
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            var (source, rawLicense, repositoryUrl) = request.Ecosystem switch
            {
                "npm" => ("npm-registry", ReadString(document.RootElement, "license"), ReadRepository(document.RootElement)),
                "nuget" => ReadNuGet(document.RootElement),
                "cargo" => ReadCargo(document.RootElement),
                "golang" => ("go-module-proxy", string.Empty, ReadGoRepository(document.RootElement)),
                _ => throw new PackageMetadataFetchException(null),
            };
            return new PackageMetadataRecord(request.CacheKey, source, rawLicense, SanitizeRepositoryUrl(repositoryUrl), [], [], DateTimeOffset.UtcNow);
        }
        catch (JsonException exception)
        {
            throw new PackageMetadataFetchException(null, exception);
        }
    }

    /// <summary>
    /// Determines whether an HTTP response status represents a retryable registry failure.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns><see langword="true"/> for HTTP 429 and 5xx responses.</returns>
    public static bool IsTransient(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static Uri CreateEndpoint(PackageMetadataRequest request)
    {
        return request.Ecosystem switch
        {
            "npm" => new Uri(NpmBaseUri, string.Concat(Uri.EscapeDataString(request.Namespace.Length == 0 ? request.Name : string.Concat(request.Namespace, "/", request.Name)), "/", Uri.EscapeDataString(request.Version))),
            "nuget" => new Uri(NuGetBaseUri, string.Concat(Uri.EscapeDataString(request.Name.ToLowerInvariant()), "/", Uri.EscapeDataString(request.Version.ToLowerInvariant()), ".json")),
            "cargo" => new Uri(CargoBaseUri, string.Concat(Uri.EscapeDataString(request.Name), "/", Uri.EscapeDataString(request.Version))),
            "golang" => new Uri(GoBaseUri, string.Concat(EscapePath(string.Concat(request.Namespace.Length == 0 ? string.Empty : string.Concat(request.Namespace, "/"), request.Name)), "/@v/", Uri.EscapeDataString(request.Version), ".info")),
            _ => throw new PackageMetadataFetchException(null),
        };
    }

    private static string EscapePath(string value) => Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    private static (string Source, string RawLicense, string RepositoryUrl) ReadNuGet(JsonElement root)
    {
        var catalog = root.TryGetProperty("catalogEntry", out var value) ? value : default;
        return ("nuget-registry", ReadString(catalog, "licenseExpression"), ReadString(catalog, "projectUrl"));
    }

    private static (string Source, string RawLicense, string RepositoryUrl) ReadCargo(JsonElement root)
    {
        var version = root.TryGetProperty("version", out var value) ? value : default;
        return ("cargo-registry", ReadString(version, "license"), ReadString(version, "repository"));
    }

    private static string ReadGoRepository(JsonElement root)
    {
        if (!root.TryGetProperty("Origin", out var origin))
        {
            return string.Empty;
        }

        return ReadString(origin, "URL");
    }

    private static string ReadRepository(JsonElement root)
    {
        if (!root.TryGetProperty("repository", out var repository))
        {
            return string.Empty;
        }

        return repository.ValueKind == JsonValueKind.String ? repository.GetString() ?? string.Empty : ReadString(repository, "url");
    }

    private static string SanitizeRepositoryUrl(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var at = value.IndexOf('@');
        if (Path.IsPathRooted(value)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || (at > 0 && value.AsSpan(at + 1).Contains(':')))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        return uri.IsFile || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ? string.Empty : value;
    }

    private static string ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}

/// <summary>
/// Represents a registry request failure and whether it is retryable.
/// </summary>
public sealed class PackageMetadataFetchException : Exception
{
    /// <summary>
    /// Initializes a registry request failure.
    /// </summary>
    /// <param name="statusCode">Optional HTTP status code returned by the registry.</param>
    /// <param name="innerException">Optional underlying request or parsing exception.</param>
    public PackageMetadataFetchException(HttpStatusCode? statusCode, Exception? innerException = null)
        : base("Package metadata registry request failed.", innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the optional HTTP status code returned by the registry.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Gets whether the failure should be retried.
    /// </summary>
    public bool IsTransient => StatusCode is { } statusCode && PackageMetadataRegistryClient.IsTransient(statusCode);
}
