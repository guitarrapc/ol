using System.Net;
using System.Text.Json;

namespace Ol.Core.PackageMetadata;

/// <summary>
/// Retrieves normalized package metadata from supported public registries.
/// </summary>
public sealed class PackageMetadataRegistryClient
{
    private readonly HttpClient httpClient;
    private readonly PackageMetadataProviders providers;

    /// <summary>
    /// Initializes a registry client using the supplied HTTP client.
    /// </summary>
    /// <param name="httpClient">HTTP client used for registry requests.</param>
    public PackageMetadataRegistryClient(HttpClient httpClient, PackageMetadataProviders? providers = null)
    {
        this.httpClient = httpClient;
        this.providers = providers ?? OlDefaults.PackageMetadataProviders;
    }

    /// <summary>
    /// Initializes a registry client using a test or custom HTTP message handler.
    /// </summary>
    /// <param name="handler">HTTP handler used for registry requests.</param>
    public PackageMetadataRegistryClient(HttpMessageHandler handler, PackageMetadataProviders? providers = null)
        : this(new HttpClient(handler, disposeHandler: true), providers)
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
        if (!providers.TryGet(request.Ecosystem, out var provider))
        {
            throw new PackageMetadataFetchException(null);
        }

        var endpoint = provider.CreateEndpoint(request);
        using var response = await httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PackageMetadataFetchException(response.StatusCode);
        }

        try
        {
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            var followUpEndpoint = provider.CreateFollowUpEndpoint(document.RootElement);
            PackageMetadataResponse metadata;
            if (followUpEndpoint is not null)
            {
                using var followUpResponse = await httpClient.GetAsync(followUpEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!followUpResponse.IsSuccessStatusCode)
                {
                    throw new PackageMetadataFetchException(followUpResponse.StatusCode);
                }

                var followUpPayload = await followUpResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                using var followUpDocument = JsonDocument.Parse(followUpPayload);
                metadata = provider.ParseResponse(followUpDocument.RootElement);
            }
            else
            {
                metadata = provider.ParseResponse(document.RootElement);
            }

            return new PackageMetadataRecord(request.CacheKey, metadata.Source, metadata.RawLicense, SanitizeRepositoryUrl(metadata.RepositoryUrl), [], [], DateTimeOffset.UtcNow, metadata.RepositoryRef);
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
