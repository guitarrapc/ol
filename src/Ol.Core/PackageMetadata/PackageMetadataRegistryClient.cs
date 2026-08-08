using System.Net;
using System.Text.Json;
using System.IO.Compression;
using Ol.Core.Licensing;

namespace Ol.Core.PackageMetadata;

/// <summary>
/// Retrieves normalized package metadata from supported public registries.
/// </summary>
public sealed class PackageMetadataRegistryClient
{
    private const string UserAgent = "ol";

    /// <summary>Bounds the provider-owned document chain: NuGet needs index, page, then catalog entry.</summary>
    private const int MaximumFollowUpHops = 2;

    /// <summary>The longest server-directed delay a command-line run absorbs before giving up on an origin.</summary>
    /// <remarks>
    /// Registries answer a rate limit either with no delay at all, where a moment's pause clears it, or
    /// with one measured in minutes, which no interactive run can absorb. Shortening the second case into
    /// an earlier retry would spend the pause without honoring what the registry asked for, so a delay
    /// past this budget stops the origin instead.
    /// </remarks>
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(10);
    private readonly HttpClient httpClient;
    private readonly PackageMetadataProviders providers;
    private readonly Dictionary<PackageMetadataProvider, Task<Uri>> serviceEndpointTasks = [];
    private readonly object serviceEndpointGate = new();
    private readonly Dictionary<string, OriginRateLimitState> originRateLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly object originRateLimitGate = new();

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
            var metadata = await ResolveMetadataAsync(provider, request, document, cancellationToken).ConfigureAwait(false);
            return new PackageMetadataRecord(request.CacheKey, metadata.Source, metadata.RawLicense, SanitizeRepositoryUrl(metadata.RepositoryUrl), metadata.Warnings.ToStrings(), [], DateTimeOffset.UtcNow, metadata.RepositoryRef);
        }
        catch (JsonException exception)
        {
            throw new PackageMetadataFetchException(null, exception);
        }
    }

    /// <summary>Follows the provider-owned document chain and projects the last document it reaches.</summary>
    /// <remarks>
    /// One hop is not always enough. NuGet resolves a registration index to a page and a page entry to
    /// the catalog entry that carries the metadata the registration omits. The bound keeps a malformed
    /// or self-referential chain from requesting indefinitely.
    /// </remarks>
    private async Task<PackageMetadataResponse> ResolveMetadataAsync(
        PackageMetadataProvider provider,
        PackageMetadataRequest request,
        JsonDocument document,
        CancellationToken cancellationToken)
    {
        JsonDocument? current = null;
        try
        {
            var root = document.RootElement;
            for (var hop = 0; hop < MaximumFollowUpHops; hop++)
            {
                if (provider.CreateFollowUpEndpoint(root, request) is not { } followUpEndpoint)
                {
                    break;
                }

                using var followUpResponse = await GetAsync(followUpEndpoint, cancellationToken).ConfigureAwait(false);
                if (!followUpResponse.IsSuccessStatusCode)
                {
                    throw CreateFetchException(followUpResponse);
                }

                var followUpDocument = await ReadJsonDocumentAsync(followUpResponse, cancellationToken).ConfigureAwait(false);
                current?.Dispose();
                current = followUpDocument;
                root = followUpDocument.RootElement;
            }

            return provider.ParseResponse(root, request);
        }
        finally
        {
            current?.Dispose();
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var origin = endpoint.GetLeftPart(UriPartial.Authority);
        var isRateLimitProbe = await WaitForOriginRateLimitAsync(origin, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        try
        {
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            UpdateOriginRateLimit(origin, response, isRateLimitProbe);
            return response;
        }
        catch
        {
            if (isRateLimitProbe)
            {
                ClearOriginRateLimit(origin);
            }

            throw;
        }
    }

    private Task<Uri?> ResolveServiceEndpointAsync(PackageMetadataProvider provider, CancellationToken cancellationToken)
    {
        if (provider.ServiceIndexEndpoint is not { } serviceIndexEndpoint)
        {
            return Task.FromResult<Uri?>(null);
        }

        TaskCompletionSource<Uri>? pendingDiscovery = null;
        Task<Uri> task;
        lock (serviceEndpointGate)
        {
            if (!serviceEndpointTasks.TryGetValue(provider, out task!))
            {
                pendingDiscovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
                task = pendingDiscovery.Task;
                serviceEndpointTasks.Add(provider, task);
            }
        }

        if (pendingDiscovery is not null)
        {
            _ = CompleteServiceEndpointDiscoveryAsync(provider, serviceIndexEndpoint, pendingDiscovery);
        }

        return AwaitDiscoveredServiceEndpointAsync(task, cancellationToken);
    }

    private async Task CompleteServiceEndpointDiscoveryAsync(PackageMetadataProvider provider, Uri serviceIndexEndpoint, TaskCompletionSource<Uri> completion)
    {
        try
        {
            var endpoint = await DiscoverServiceEndpointAsync(provider, serviceIndexEndpoint, CancellationToken.None).ConfigureAwait(false);
            completion.TrySetResult(endpoint);
        }
        catch (Exception exception)
        {
            lock (serviceEndpointGate)
            {
                if (serviceEndpointTasks.TryGetValue(provider, out var current) && ReferenceEquals(current, completion.Task))
                {
                    serviceEndpointTasks.Remove(provider);
                }
            }

            completion.TrySetException(exception);
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

    private static async Task<Uri?> AwaitDiscoveredServiceEndpointAsync(Task<Uri> task, CancellationToken cancellationToken)
        => await task.WaitAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Determines whether an HTTP response status represents a retryable registry failure.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns><see langword="true"/> for HTTP 429 and 5xx responses.</returns>
    public static bool IsTransient(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static PackageMetadataFetchException CreateFetchException(HttpResponseMessage response)
        => new(response.StatusCode, retryAfter: GetRetryDelay(response));

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
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

        return delay;
    }

    private async Task<bool> WaitForOriginRateLimitAsync(string origin, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            Task? changed = null;
            lock (originRateLimitGate)
            {
                if (!originRateLimits.TryGetValue(origin, out var state))
                {
                    return false;
                }

                if (state.Stopped)
                {
                    throw new PackageMetadataFetchException(HttpStatusCode.TooManyRequests, retryAfter: state.RetryAfter);
                }

                delay = state.NotBefore - DateTimeOffset.UtcNow;
                if (delay <= TimeSpan.Zero)
                {
                    if (!state.ProbeInProgress)
                    {
                        state.ProbeInProgress = true;
                        return true;
                    }

                    changed = state.Changed.Task;
                }
            }

            if (changed is not null)
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void UpdateOriginRateLimit(string origin, HttpResponseMessage response, bool isRateLimitProbe)
    {
        var retryDelay = GetRetryDelay(response);
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (retryDelay is not null && IsTransient(response.StatusCode)))
        {
            retryDelay ??= TimeSpan.FromSeconds(1);
            var stopped = retryDelay > MaximumWait;
            lock (originRateLimitGate)
            {
                // A stopped origin is never waited on, and an unbounded delay would overflow the instant.
                var notBefore = stopped ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow + retryDelay.Value;
                if (!originRateLimits.TryGetValue(origin, out var state))
                {
                    originRateLimits.Add(origin, new OriginRateLimitState(notBefore) { Stopped = stopped, RetryAfter = retryDelay });
                }
                else
                {
                    if (notBefore > state.NotBefore)
                    {
                        state.NotBefore = notBefore;
                    }

                    // Tracked separately from NotBefore: a delay past the budget stops the origin and
                    // pins NotBefore to now, so the longest delay the registry asked for would otherwise
                    // be lost behind an existing cooldown and reported as a shorter, retryable one.
                    if (state.RetryAfter is not { } known || retryDelay > known)
                    {
                        state.RetryAfter = retryDelay;
                    }

                    state.Stopped |= stopped;

                    // Only the probe owns the slot. A request that was already in flight when the limit
                    // began also lands here, and releasing the slot for it admits a second probe.
                    if (isRateLimitProbe)
                    {
                        state.ProbeInProgress = false;
                    }

                    state.SignalChanged();
                }
            }

            return;
        }

        if (isRateLimitProbe)
        {
            ClearOriginRateLimit(origin);
        }
    }

    private void ClearOriginRateLimit(string origin)
    {
        lock (originRateLimitGate)
        {
            if (originRateLimits.Remove(origin, out var state))
            {
                state.SignalChanged();
            }
        }
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

    internal static string SanitizeRepositoryUrl(string value)
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

    private sealed class OriginRateLimitState(DateTimeOffset notBefore)
    {
        public DateTimeOffset NotBefore = notBefore;
        public bool ProbeInProgress;
        /// <summary>Set when the registry asked for longer than <see cref="MaximumWait"/>, which ends this origin for the run.</summary>
        public bool Stopped;
        public TimeSpan? RetryAfter;
        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SignalChanged()
        {
            var changed = Changed;
            Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
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
    /// <remarks>
    /// A delay past the run's wait budget is not retryable. Retrying it earlier than the registry asked
    /// would ignore the instruction that came with the failure.
    /// </remarks>
    public bool IsTransient
        => StatusCode is { } statusCode
        && PackageMetadataRegistryClient.IsTransient(statusCode)
        && (RetryAfter is not { } delay || delay <= PackageMetadataRegistryClient.MaximumWait);
}
