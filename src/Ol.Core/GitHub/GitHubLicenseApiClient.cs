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
    private static readonly long MinimumUnixTimeSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaximumUnixTimeSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
    private const int MaximumErrorBodyBytes = 16 * 1024;
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
    public async Task<SourceRepositoryRecord> FetchAsync(SourceRepositoryTarget target, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref rateLimit) is { } reached)
        {
            throw CreateRateLimitException(reached);
        }

        var endpoint = target.Ref == "default"
            ? new Uri(apiBaseUri, string.Concat("repos/", Uri.EscapeDataString(target.Owner), "/", Uri.EscapeDataString(target.Name), "/license"))
            : new Uri(apiBaseUri, string.Concat("repos/", Uri.EscapeDataString(target.Owner), "/", Uri.EscapeDataString(target.Name), "/license?ref=", Uri.EscapeDataString(target.Ref)));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ol", "1.0"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (authentication.Token.Length != 0 && string.Equals(endpoint.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token);
        }

        HttpResponseMessage? response = null;
        RateLimitDecision rateLimitDecision;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            rateLimitDecision = await ClassifyRateLimitAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response?.Dispose();
            throw;
        }

        using (response)
        {
            if (rateLimitDecision.Kind != GitHubRateLimitKind.None)
            {
                throw CreateRateLimitException(Reach(response.StatusCode, rateLimitDecision));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return CreateRecord(target, response.StatusCode, null, ["license_not_detected"], []);
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
                return CreateRecord(target, response.StatusCode, result, [], []);
            }
            catch (JsonException exception)
            {
                throw new SourceRepositoryFetchException(null, exception);
            }
        }
    }

    /// <summary>Records the first rate limit reached, which every later request reports without sending.</summary>
    private GitHubRateLimitStatus Reach(HttpStatusCode statusCode, in RateLimitDecision decision)
    {
        var reached = new GitHubRateLimitStatus(decision.Kind, statusCode, decision.RetryAfter, decision.ResetsAt, authentication.Mode);
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
            return ValueTask.FromResult(new RateLimitDecision(GitHubRateLimitKind.Primary, GetRetryDelay(response), resetsAt));
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

    private readonly record struct RateLimitDecision(GitHubRateLimitKind Kind, TimeSpan? RetryAfter, DateTimeOffset? ResetsAt);
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
public sealed record GitHubRateLimitStatus(
    GitHubRateLimitKind Kind,
    HttpStatusCode StatusCode,
    TimeSpan? RetryAfter,
    DateTimeOffset? ResetsAt,
    string AuthMode)
{
    /// <summary>Gets whether the run was unauthenticated, which carries the smallest allowance.</summary>
    public bool IsUnauthenticated => AuthMode == "none";
}

/// <summary>Represents a GitHub License API failure.</summary>
public sealed class SourceRepositoryFetchException : Exception
{
    /// <summary>Initializes a GitHub License API failure.</summary>
    public SourceRepositoryFetchException(HttpStatusCode? statusCode, Exception? innerException = null)
        : this(statusCode, null, innerException) { }

    /// <summary>Initializes a failure caused by a reached GitHub rate limit.</summary>
    public SourceRepositoryFetchException(HttpStatusCode? statusCode, GitHubRateLimitStatus? rateLimit, Exception? innerException = null)
        : base("GitHub License API request failed.", innerException)
    {
        StatusCode = statusCode;
        RateLimit = rateLimit;
    }

    /// <summary>Gets the response status when available.</summary>
    public HttpStatusCode? StatusCode { get; }
    /// <summary>Gets the reached rate limit, or <see langword="null"/> when this failure is not one.</summary>
    public GitHubRateLimitStatus? RateLimit { get; }
    /// <summary>Gets the server-directed retry delay when supplied.</summary>
    public TimeSpan? RetryAfter => RateLimit?.RetryAfter;
    /// <summary>Gets whether the response represents a GitHub rate limit.</summary>
    public bool IsRateLimited => RateLimit is not null;
    /// <summary>Gets whether this failure may be retried.</summary>
    /// <remarks>
    /// A rate limit is never retried. GitHub decides when it lifts, on a schedule a command-line run
    /// cannot wait out, so retrying only spends the remaining allowance and can extend a secondary limit.
    /// </remarks>
    public bool IsTransient => !IsRateLimited && StatusCode is { } value && (int)value >= 500;
}
