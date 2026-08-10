using Ol.Core.SourceRepository;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Buffers;
using System.Globalization;

namespace Ol.Core.GitHub;

/// <summary>Retrieves GitHub's detected repository license without parsing license bodies.</summary>
public sealed class GitHubLicenseApiClient
{
    private static readonly Uri ApiBaseUri = new("https://api.github.com/");

    /// <summary>The longest server-directed delay a command-line run absorbs before giving up on GitHub.</summary>
    /// <remarks>
    /// GitHub's limits are always longer than this in practice — a primary limit resets up to an hour
    /// later, and a secondary limit asks for at least a minute — so collection effectively always stops
    /// rather than waits. The budget is expressed anyway so this client and the registry client follow
    /// one rule instead of two policies that happen to differ.
    /// </remarks>
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(10);
    private static readonly long MinimumUnixTimeSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaximumUnixTimeSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
    private const int MaximumErrorBodyBytes = 16 * 1024;

    /// <summary>The number of GitHub redirects one License API request follows.</summary>
    /// <remarks>
    /// A renamed or transferred repository answers with one redirect to its current location. More than
    /// that is a loop or a chain Ol has no reason to walk, so the redirect is reported as the answer.
    /// </remarks>
    private const int MaximumRedirects = 3;
    private readonly Uri apiBaseUri;
    private readonly GitHubAuthentication authentication;
    private readonly HttpClient httpClient;
    private GitHubRateLimitStatus? rateLimit;

    /// <summary>Initializes a client using an HTTP handler.</summary>
    public GitHubLicenseApiClient(HttpMessageHandler handler, GitHubAuthentication authentication, Uri? apiBaseUri = null)
        : this(new HttpClient(handler, disposeHandler: true), authentication, apiBaseUri) { }

    /// <summary>Initializes a client using an HTTP client.</summary>
    public GitHubLicenseApiClient(HttpClient httpClient, GitHubAuthentication authentication, Uri? apiBaseUri = null)
    {
        this.httpClient = httpClient;
        this.authentication = authentication;
        this.apiBaseUri = apiBaseUri ?? ApiBaseUri;
    }

    /// <summary>Gets the rate limit that stopped collection, or <see langword="null"/> while none was reached.</summary>
    /// <remarks>
    /// A GitHub rate limit resets on GitHub's schedule, not on one a command-line run can wait out: a
    /// primary limit resets up to an hour later, and a secondary limit asks for at least a minute. Ol
    /// therefore stops instead of waiting, and reports this so the run can be repeated with a token or
    /// a lower concurrency rather than silently losing source evidence.
    /// </remarks>
    public GitHubRateLimitStatus? RateLimit => Volatile.Read(ref rateLimit);

    /// <summary>Fetches one GitHub License API response.</summary>
    /// <remarks>
    /// A pinned ref that answers <c>404</c> falls back to the repository's default ref. The ref reaches
    /// this client from package metadata — a repository commit, a tag, or the branch named in a license
    /// URL — and a branch moves or disappears while the repository keeps its license. GitHub answers a
    /// ref that no longer exists with the same <c>404</c> it uses for a repository holding no license
    /// file, so a single request cannot tell "the license is gone" from "the name is gone", and Ol
    /// reported both as no license at all. The second request separates them, and it is not a guess about
    /// the pinned version: the answer is reported as the default ref's, with
    /// <c>source_repository_ref_not_found</c> retained so the substitution stays visible.
    /// </remarks>
    public async Task<SourceRepositoryRecord> FetchAsync(SourceRepositoryTarget target, CancellationToken cancellationToken = default)
    {
        var pinned = target.Ref != "default";
        var result = await SendAsync(target, pinned ? target.Ref : null, cancellationToken).ConfigureAwait(false);
        if (pinned && result.StatusCode == HttpStatusCode.NotFound)
        {
            var fallback = await SendAsync(target, null, cancellationToken).ConfigureAwait(false);
            if (fallback.StatusCode != HttpStatusCode.NotFound)
            {
                return new(target.CacheKey, "github-license-api", authentication.Mode, target.Repository, "default", fallback.StatusCode, fallback.License, ["source_repository_ref_not_found"], [], DateTimeOffset.UtcNow);
            }
        }

        return result.StatusCode == HttpStatusCode.NotFound
            ? CreateRecord(target, HttpStatusCode.NotFound, null, ["license_not_detected"], [])
            : CreateRecord(target, result.StatusCode, result.License, [], []);
    }

    /// <summary>Sends one License API request, at a pinned ref or at the repository default.</summary>
    private async Task<LicenseResponse> SendAsync(SourceRepositoryTarget target, string? pinnedRef, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref rateLimit) is { } reached)
        {
            throw CreateRateLimitException(reached);
        }

        var endpoint = pinnedRef is null
            ? new Uri(apiBaseUri, string.Concat("repos/", Uri.EscapeDataString(target.Owner), "/", Uri.EscapeDataString(target.Name), "/license"))
            : new Uri(apiBaseUri, string.Concat("repos/", Uri.EscapeDataString(target.Owner), "/", Uri.EscapeDataString(target.Name), "/license?ref=", Uri.EscapeDataString(pinnedRef)));

        HttpResponseMessage? response = null;
        RateLimitDecision rateLimitDecision;
        try
        {
            response = await SendFollowingRedirectsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            rateLimitDecision = await ClassifyRateLimitAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response?.Dispose();
            throw;
        }

        using (response)
        {
            // A secondary limit says the pace was too high, so a delay within the wait budget is left to
            // the retry policy. A primary limit says the allowance is spent, which no delay this run can
            // absorb will change, so it ends collection whatever reset it names.
            if (rateLimitDecision.Kind != GitHubRateLimitKind.None)
            {
                throw rateLimitDecision.Kind == GitHubRateLimitKind.Secondary
                    && rateLimitDecision.RetryAfter is { } retryAfter
                    && retryAfter <= MaximumWait
                    ? new SourceRepositoryFetchException(response.StatusCode, retryAfter)
                    : CreateRateLimitException(Reach(response.StatusCode, rateLimitDecision));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new LicenseResponse(response.StatusCode, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SourceRepositoryFetchException(response.StatusCode);
            }

            try
            {
                var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var license = root.TryGetProperty("license", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
                var result = new GitHubLicenseResult(ReadNullableString(license, "spdx_id"), ReadString(license, "key"), ReadString(license, "name"), ReadString(root, "path"), ReadString(root, "sha"), ReadString(root, "html_url"));
                return new LicenseResponse(response.StatusCode, result);
            }
            catch (JsonException exception)
            {
                throw new SourceRepositoryFetchException(null, exception);
            }
        }
    }

    /// <summary>Sends one request, following GitHub's redirects with the token still attached.</summary>
    /// <remarks>
    /// A renamed or transferred repository answers <c>301</c> with its current location, and GitHub keeps
    /// serving the license there. HttpClient's own redirect handling drops the Authorization header when
    /// it retries, so the follow-up reaches GitHub unauthenticated, is counted against the sixty-request
    /// anonymous allowance, and comes back <c>403</c> once that allowance is spent — a limit the token
    /// never touched, which then stops collection for every remaining component. Following the redirect
    /// here keeps the header, under the same rule the first request uses: it is sent to
    /// <c>api.github.com</c> over HTTPS and nowhere else.
    /// </remarks>
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var uri = endpoint;
        for (var redirect = 0; ; redirect++)
        {
            var response = await SendOnceAsync(uri, cancellationToken).ConfigureAwait(false);
            if (redirect == MaximumRedirects
                || response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                || response.Headers.Location is not { } location)
            {
                return response;
            }

            uri = new Uri(uri, location);
            response.Dispose();
        }
    }

    /// <summary>Sends one License API request to the given URI.</summary>
    private async Task<HttpResponseMessage> SendOnceAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ol", "1.0"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (authentication.Token.Length != 0 && IsGitHubApi(uri))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token);
        }

        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsGitHubApi(Uri uri)
        => string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

    /// <summary>Records the first rate limit reached, which every later request reports without sending.</summary>
    private GitHubRateLimitStatus Reach(HttpStatusCode statusCode, in RateLimitDecision decision)
    {
        var reached = new GitHubRateLimitStatus(decision.Kind, statusCode, decision.RetryAfter, decision.ResetsAt, authentication.Mode, decision.Limit);
        return Interlocked.CompareExchange(ref rateLimit, reached, null) ?? reached;
    }

    private static SourceRepositoryFetchException CreateRateLimitException(GitHubRateLimitStatus reached)
        => new(reached.StatusCode, reached);

    private static ValueTask<RateLimitDecision> ClassifyRateLimitAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var isRateLimitStatus = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden;
        if (!isRateLimitStatus)
        {
            return ValueTask.FromResult(default(RateLimitDecision));
        }

        if (HasNoRemainingRequests(response))
        {
            var resetsAt = TryGetRateLimitReset(response, out var reset) ? DateTimeOffset.FromUnixTimeSeconds(reset) : (DateTimeOffset?)null;
            var limit = TryGetRateLimit(response, out var value) ? value : (int?)null;
            return ValueTask.FromResult(new RateLimitDecision(GitHubRateLimitKind.Primary, GetRetryDelay(response), resetsAt, limit));
        }

        var retryAfter = GetRetryDelay(response);
        if (retryAfter is not null)
        {
            return ValueTask.FromResult(new RateLimitDecision(GitHubRateLimitKind.Secondary, retryAfter, null));
        }

        // A bare 429 is a limit whichever kind it is; a bare 403 needs the body to tell a limit from
        // an ordinary permission failure.
        return response.StatusCode == HttpStatusCode.TooManyRequests
            ? ValueTask.FromResult(new RateLimitDecision(GitHubRateLimitKind.Secondary, null, null))
            : ClassifySecondaryRateLimitAsync(response.Content, cancellationToken);
    }

    private static async ValueTask<RateLimitDecision> ClassifySecondaryRateLimitAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumErrorBodyBytes + 1);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var length = 0;
            while (length <= MaximumErrorBodyBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, MaximumErrorBodyBytes + 1 - length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            return length <= MaximumErrorBodyBytes && IsSecondaryRateLimitBody(buffer.AsSpan(0, length))
                ? new RateLimitDecision(GitHubRateLimitKind.Secondary, null, null)
                : default;
        }
        catch (JsonException)
        {
            return default;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            // The body decides only whether this 403 is a secondary rate limit. Losing it leaves the
            // status as the answer instead of replacing a classified 403 with a transport failure.
            return default;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsSecondaryRateLimitBody(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName
                || (!reader.ValueTextEquals("message"u8) && !reader.ValueTextEquals("documentation_url"u8))
                || !reader.Read()
                || reader.TokenType != JsonTokenType.String)
            {
                continue;
            }

            var value = reader.GetString()!;
            if (value.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
                || value.Contains("abuse detection", StringComparison.OrdinalIgnoreCase)
                || value.Contains("rate-limits-for-the-rest-api", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNoRemainingRequests(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value == "0")
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        if (delay is null
            && HasNoRemainingRequests(response)
            && TryGetRateLimitReset(response, out var reset))
        {
            delay = DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow;
        }

        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay;
    }

    /// <summary>Reads the allowance GitHub applied to the request, which names which credential it saw.</summary>
    private static bool TryGetRateLimit(HttpResponseMessage response, out int limit)
    {
        limit = 0;
        if (!response.Headers.TryGetValues("X-RateLimit-Limit", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit);
        }

        return false;
    }

    private static bool TryGetRateLimitReset(HttpResponseMessage response, out long reset)
    {
        reset = 0;
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out reset)
                && reset >= MinimumUnixTimeSeconds
                && reset <= MaximumUnixTimeSeconds;
        }

        return false;
    }

    private SourceRepositoryRecord CreateRecord(SourceRepositoryTarget target, HttpStatusCode status, GitHubLicenseResult? license, string[] warnings, string[] errors)
        => new(target.CacheKey, "github-license-api", authentication.Mode, target.Repository, target.Ref, status, license, warnings, errors, DateTimeOffset.UtcNow);

    private static string ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static string? ReadNullableString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private readonly record struct RateLimitDecision(GitHubRateLimitKind Kind, TimeSpan? RetryAfter, DateTimeOffset? ResetsAt, int? Limit = null);
    private readonly record struct LicenseResponse(HttpStatusCode StatusCode, GitHubLicenseResult? License);
}

/// <summary>Identifies which GitHub rate limit a response reported.</summary>
public enum GitHubRateLimitKind : byte
{
    /// <summary>No rate limit was reported.</summary>
    None,
    /// <summary>The request allowance for the current window is exhausted.</summary>
    Primary,
    /// <summary>Requests were issued faster than GitHub accepts, independently of the allowance.</summary>
    Secondary,
}

/// <summary>Describes the rate limit that stopped GitHub License API collection.</summary>
/// <param name="Kind">The reported limit kind.</param>
/// <param name="StatusCode">The status that carried the limit.</param>
/// <param name="RetryAfter">The delay GitHub asked for, when supplied.</param>
/// <param name="ResetsAt">The allowance reset instant, when a primary limit supplied one.</param>
/// <param name="AuthMode">The authentication mode in use, which decides the allowance.</param>
/// <param name="Limit">The allowance GitHub reported applying, when supplied.</param>
public sealed record GitHubRateLimitStatus(
    GitHubRateLimitKind Kind,
    HttpStatusCode StatusCode,
    TimeSpan? RetryAfter,
    DateTimeOffset? ResetsAt,
    string AuthMode,
    int? Limit = null)
{
    /// <summary>The hourly allowance GitHub grants a request carrying no credential.</summary>
    private const int AnonymousRequestAllowance = 60;

    /// <summary>Gets whether the run was unauthenticated, which carries the smallest allowance.</summary>
    public bool IsUnauthenticated => AuthMode == "none";

    /// <summary>Gets whether GitHub applied the anonymous allowance although a token was configured.</summary>
    /// <remarks>
    /// The allowance GitHub names in the response is the one it actually applied, so a token that raises
    /// it to thousands cannot produce a limit of sixty. That combination says the credential did not
    /// reach GitHub, which is a different failure from a spent allowance and one no reset clears.
    /// </remarks>
    public bool IsTokenNotApplied => !IsUnauthenticated && Limit is > 0 and <= AnonymousRequestAllowance;
}

/// <summary>Represents a GitHub License API failure.</summary>
public sealed class SourceRepositoryFetchException : Exception
{
    /// <summary>Initializes a GitHub License API failure.</summary>
    public SourceRepositoryFetchException(HttpStatusCode? statusCode, Exception? innerException = null)
        : this(statusCode, rateLimit: null, innerException) { }

    /// <summary>Initializes a failure caused by a reached GitHub rate limit.</summary>
    public SourceRepositoryFetchException(HttpStatusCode? statusCode, GitHubRateLimitStatus? rateLimit, Exception? innerException = null)
        : base("GitHub License API request failed.", innerException)
    {
        StatusCode = statusCode;
        RateLimit = rateLimit;
        RetryAfter = rateLimit?.RetryAfter;
    }

    /// <summary>Initializes a rate-limit failure GitHub clears within the run's wait budget.</summary>
    internal SourceRepositoryFetchException(HttpStatusCode? statusCode, TimeSpan retryAfter)
        : base("GitHub License API request failed.")
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        IsRetryableRateLimit = true;
    }

    /// <summary>Gets the response status when available.</summary>
    public HttpStatusCode? StatusCode { get; }
    /// <summary>Gets the reached rate limit, or <see langword="null"/> when collection was not stopped.</summary>
    public GitHubRateLimitStatus? RateLimit { get; }
    /// <summary>Gets the server-directed retry delay when supplied.</summary>
    public TimeSpan? RetryAfter { get; }
    private bool IsRetryableRateLimit { get; }
    /// <summary>Gets whether the response represents a GitHub rate limit.</summary>
    public bool IsRateLimited => RateLimit is not null || IsRetryableRateLimit;
    /// <summary>Gets whether this failure may be retried.</summary>
    /// <remarks>
    /// A limit whose delay exceeds the run's wait budget is never retried. GitHub decides when it lifts,
    /// on a schedule a command-line run cannot absorb, so retrying only spends the remaining allowance
    /// and can extend a secondary limit.
    /// </remarks>
    public bool IsTransient => IsRetryableRateLimit || (RateLimit is null && StatusCode is { } value && (int)value >= 500);
}
