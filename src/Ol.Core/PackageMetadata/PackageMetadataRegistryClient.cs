using System.Net;
using System.Text.Json;
using System.IO.Compression;

namespace Ol.Core.PackageMetadata;

/// <summary>
/// Retrieves normalized package metadata from supported public registries.
/// </summary>
public sealed class PackageMetadataRegistryClient
{
    private const string UserAgent = "ol";
    private readonly HttpClient httpClient;
    private readonly PackageMetadataProviders providers;
    private readonly Dictionary<PackageMetadataProvider, Task<Uri>> serviceEndpointTasks = [];
    private readonly object serviceEndpointGate = new();

    /// <summary>
    /// Initializes a registry client using the supplied HTTP client.
    /// </summary>
    /// <param name="httpClient">HTTP client used for registry requests.</param>
    public PackageMetadataRegistryClient(HttpClient httpClient, PackageMetadataProviders providers)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    /// <summary>
    /// Initializes a registry client using a test or custom HTTP message handler.
    /// </summary>
    /// <param name="handler">HTTP handler used for registry requests.</param>
    public PackageMetadataRegistryClient(HttpMessageHandler handler, PackageMetadataProviders providers)
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

        var serviceEndpoint = await ResolveServiceEndpointAsync(provider, cancellationToken).ConfigureAwait(false);
        var endpoint = serviceEndpoint is null
            ? provider.CreateEndpoint(request)
            : provider.CreateEndpoint(request, serviceEndpoint);
        using var response = await GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateFetchException(response);
        }

        try
        {
            using var document = await ReadJsonDocumentAsync(response, cancellationToken).ConfigureAwait(false);
            var followUpEndpoint = provider.CreateFollowUpEndpoint(document.RootElement);
            PackageMetadataResponse metadata;
            if (followUpEndpoint is not null)
            {
                using var followUpResponse = await GetAsync(followUpEndpoint, cancellationToken).ConfigureAwait(false);
                if (!followUpResponse.IsSuccessStatusCode)
                {
                    throw CreateFetchException(followUpResponse);
                }

                using var followUpDocument = await ReadJsonDocumentAsync(followUpResponse, cancellationToken).ConfigureAwait(false);
                metadata = provider.ParseResponse(followUpDocument.RootElement, request);
            }
            else
            {
                metadata = provider.ParseResponse(document.RootElement, request);
            }

            return new PackageMetadataRecord(request.CacheKey, metadata.Source, metadata.RawLicense, SanitizeRepositoryUrl(metadata.RepositoryUrl), [], [], DateTimeOffset.UtcNow, metadata.RepositoryRef);
        }
        catch (JsonException exception)
        {
            throw new PackageMetadataFetchException(null, exception);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private Task<Uri?> ResolveServiceEndpointAsync(PackageMetadataProvider provider, CancellationToken cancellationToken)
    {
        if (provider.ServiceIndexEndpoint is not { } serviceIndexEndpoint)
        {
            return Task.FromResult<Uri?>(null);
        }

        lock (serviceEndpointGate)
        {
            if (!serviceEndpointTasks.TryGetValue(provider, out var task))
            {
                task = DiscoverServiceEndpointAsync(provider, serviceIndexEndpoint, cancellationToken);
                serviceEndpointTasks.Add(provider, task);
            }

            return AwaitDiscoveredServiceEndpointAsync(provider, task);
        }
    }

    private async Task<Uri> DiscoverServiceEndpointAsync(PackageMetadataProvider provider, Uri serviceIndexEndpoint, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(serviceIndexEndpoint, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateFetchException(response);
        }

        try
        {
            using var document = await ReadJsonDocumentAsync(response, cancellationToken).ConfigureAwait(false);
            if (!provider.TryResolveServiceEndpoint(document.RootElement, out var endpoint))
            {
                throw new PackageMetadataFetchException(null);
            }

            return endpoint;
        }
        catch (JsonException exception)
        {
            throw new PackageMetadataFetchException(null, exception);
        }
    }

    private async Task<Uri?> AwaitDiscoveredServiceEndpointAsync(PackageMetadataProvider provider, Task<Uri> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            lock (serviceEndpointGate)
            {
                if (serviceEndpointTasks.TryGetValue(provider, out var current) && ReferenceEquals(current, task))
                {
                    serviceEndpointTasks.Remove(provider);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Determines whether an HTTP response status represents a retryable registry failure.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns><see langword="true"/> for HTTP 429 and 5xx responses.</returns>
    public static bool IsTransient(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static PackageMetadataFetchException CreateFetchException(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return new PackageMetadataFetchException(response.StatusCode, retryAfter: delay);
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count > 1)
        {
            throw new PackageMetadataFetchException(null);
        }

        foreach (var encoding in encodings)
        {
            return await ReadEncodedJsonDocumentAsync(content, encoding, cancellationToken).ConfigureAwait(false);
        }

        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadEncodedJsonDocumentAsync(Stream content, string encoding, CancellationToken cancellationToken)
    {
        if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            await using var gzip = new GZipStream(content, CompressionMode.Decompress, leaveOpen: true);
            return await JsonDocument.ParseAsync(gzip, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
        {
            await using var deflate = new DeflateStream(content, CompressionMode.Decompress, leaveOpen: true);
            return await JsonDocument.ParseAsync(deflate, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (encoding.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            await using var brotli = new BrotliStream(content, CompressionMode.Decompress, leaveOpen: true);
            return await JsonDocument.ParseAsync(brotli, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        throw new PackageMetadataFetchException(null);
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
    /// <param name="retryAfter">Optional server-requested delay before retrying.</param>
    public PackageMetadataFetchException(HttpStatusCode? statusCode, Exception? innerException = null, TimeSpan? retryAfter = null)
        : base("Package metadata registry request failed.", innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the optional HTTP status code returned by the registry.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Gets the delay requested by the registry before retrying.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Gets whether the failure should be retried.
    /// </summary>
    public bool IsTransient => StatusCode is { } statusCode && PackageMetadataRegistryClient.IsTransient(statusCode);
}
