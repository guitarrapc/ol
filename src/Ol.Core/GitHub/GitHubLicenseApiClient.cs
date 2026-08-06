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
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultSecondaryRetryAfter = TimeSpan.FromMinutes(1);
    private static readonly long MinimumUnixTimeSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaximumUnixTimeSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
    private const int MaximumErrorBodyBytes = 16 * 1024;
    private readonly Uri apiBaseUri;
    private readonly GitHubAuthentication authentication;
    private readonly HttpClient httpClient;
    private readonly object rateLimitGate = new();
    private RateLimitState? rateLimit;

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

    /// <summary>Fetches one GitHub License API response.</summary>
    public async Task<SourceRepositoryRecord> FetchAsync(SourceRepositoryTarget target, CancellationToken cancellationToken = default)
    {
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

        var isRateLimitProbe = await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);
        HttpResponseMessage? response = null;
        RateLimitDecision rateLimitDecision;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            rateLimitDecision = await ClassifyRateLimitAsync(response, cancellationToken).ConfigureAwait(false);
            UpdateRateLimit(response.StatusCode, rateLimitDecision, isRateLimitProbe);
        }
        catch
        {
            response?.Dispose();
            if (isRateLimitProbe)
            {
                ReleaseRateLimitProbe();
            }

            throw;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return CreateRecord(target, response.StatusCode, null, ["license_not_detected"], []);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SourceRepositoryFetchException(response.StatusCode, rateLimitDecision.RetryAfter, rateLimitDecision.IsRateLimited, rateLimitDecision.CanRetry);
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

    private ValueTask<bool> WaitForRateLimitAsync(CancellationToken cancellationToken)
        => Volatile.Read(ref rateLimit) is null
            ? ValueTask.FromResult(false)
            : WaitForRateLimitCoreAsync(cancellationToken);

    private async ValueTask<bool> WaitForRateLimitCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            Task? changed = null;
            lock (rateLimitGate)
            {
                if (rateLimit is null)
                {
                    return false;
                }

                if (!rateLimit.CanRetry)
                {
                    throw new SourceRepositoryFetchException(rateLimit.StatusCode, rateLimit.RetryAfter, isRateLimited: true, canRetry: false);
                }

                delay = rateLimit.NotBefore - DateTimeOffset.UtcNow;

                if (delay <= TimeSpan.Zero)
                {
                    if (!rateLimit.ProbeInProgress)
                    {
                        rateLimit.ProbeInProgress = true;
                        return true;
                    }

                    changed = rateLimit.Changed.Task;
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

    private void UpdateRateLimit(HttpStatusCode statusCode, in RateLimitDecision decision, bool isRateLimitProbe)
    {
        if (!decision.IsRateLimited)
        {
            if (isRateLimitProbe)
            {
                ClearRateLimit();
            }

            return;
        }

        var retryAfter = decision.RetryAfter ?? DefaultSecondaryRetryAfter;
        lock (rateLimitGate)
        {
            var notBefore = decision.CanRetry ? DateTimeOffset.UtcNow + retryAfter : DateTimeOffset.UtcNow;
            if (rateLimit is null)
            {
                rateLimit = new RateLimitState(notBefore, retryAfter, statusCode, decision.CanRetry);
            }
            else
            {
                if (notBefore > rateLimit.NotBefore)
                {
                    rateLimit.NotBefore = notBefore;
                }

                if (retryAfter > rateLimit.RetryAfter)
                {
                    rateLimit.RetryAfter = retryAfter;
                }

                rateLimit.StatusCode = statusCode;
                rateLimit.CanRetry &= decision.CanRetry;

                // Only the probe owns the slot. A request that was already in flight when the limit
                // began also lands here, and releasing the slot for it admits a second probe.
                if (isRateLimitProbe)
                {
                    rateLimit.ProbeInProgress = false;
                }

                rateLimit.SignalChanged();
            }
        }
    }

    private void ReleaseRateLimitProbe()
    {
        lock (rateLimitGate)
        {
            if (rateLimit is { } state)
            {
                state.ProbeInProgress = false;
                state.SignalChanged();
            }
        }
    }

    private void ClearRateLimit()
    {
        lock (rateLimitGate)
        {
            var state = rateLimit;
            rateLimit = null;
            state?.SignalChanged();
        }
    }

    private static ValueTask<RateLimitDecision> ClassifyRateLimitAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return ValueTask.FromResult(CreateRateLimitDecision(response));
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return ValueTask.FromResult(default(RateLimitDecision));
        }

        var retryAfter = GetRetryDelay(response);
        if (retryAfter is not null || HasNoRemainingRequests(response))
        {
            return ValueTask.FromResult(CreateRateLimitDecision(retryAfter));
        }

        return ClassifySecondaryRateLimitAsync(response.Content, cancellationToken);
    }

    private static RateLimitDecision CreateRateLimitDecision(HttpResponseMessage response)
        => CreateRateLimitDecision(GetRetryDelay(response));

    private static RateLimitDecision CreateRateLimitDecision(TimeSpan? retryAfter)
    {
        retryAfter ??= DefaultSecondaryRetryAfter;
        return new RateLimitDecision(true, retryAfter, retryAfter <= MaximumRetryAfter);
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
                ? CreateRateLimitDecision(retryAfter: null)
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

    private readonly record struct RateLimitDecision(bool IsRateLimited, TimeSpan? RetryAfter, bool CanRetry);

    private sealed class RateLimitState(DateTimeOffset notBefore, TimeSpan retryAfter, HttpStatusCode statusCode, bool canRetry)
    {
        public DateTimeOffset NotBefore = notBefore;
        public TimeSpan RetryAfter = retryAfter;
        public HttpStatusCode StatusCode = statusCode;
        public bool CanRetry = canRetry;
        public bool ProbeInProgress;
        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SignalChanged()
        {
            var changed = Changed;
            Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }
}

/// <summary>Represents a GitHub License API failure.</summary>
public sealed class SourceRepositoryFetchException : Exception
{
    /// <summary>Initializes a GitHub License API failure.</summary>
    public SourceRepositoryFetchException(HttpStatusCode? statusCode, Exception? innerException = null)
        : this(statusCode, null, false, statusCode == HttpStatusCode.TooManyRequests || statusCode is { } value && (int)value >= 500, innerException) { }

    internal SourceRepositoryFetchException(HttpStatusCode? statusCode, TimeSpan? retryAfter, bool isRateLimited, bool canRetry, Exception? innerException = null)
        : base("GitHub License API request failed.", innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        IsRateLimited = isRateLimited;
        IsTransient = canRetry || !isRateLimited && statusCode is { } value && (int)value >= 500;
    }

    /// <summary>Gets the response status when available.</summary>
    public HttpStatusCode? StatusCode { get; }
    /// <summary>Gets the server-directed retry delay when supplied.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>Gets whether the response represents a GitHub rate limit.</summary>
    public bool IsRateLimited { get; }
    /// <summary>Gets whether this failure may be retried.</summary>
    public bool IsTransient { get; }
}
