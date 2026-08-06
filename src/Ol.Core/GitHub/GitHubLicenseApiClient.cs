using Ol.Core.SourceRepository;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ol.Core.GitHub;

/// <summary>Retrieves GitHub's detected repository license without parsing license bodies.</summary>
public sealed class GitHubLicenseApiClient
{
    private static readonly Uri ApiBaseUri = new("https://api.github.com/");
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromMinutes(5);
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
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            UpdateRateLimit(response, isRateLimitProbe);
        }
        catch
        {
            if (isRateLimitProbe)
            {
                ClearRateLimit();
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
                var retryDelay = GetRetryDelay(response);
                throw new SourceRepositoryFetchException(response.StatusCode, retryDelay, IsRateLimited(response, retryDelay));
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

                delay = rateLimit.NotBefore - DateTimeOffset.UtcNow;
                if (delay > MaximumRetryAfter)
                {
                    delay = MaximumRetryAfter;
                }

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

    private void UpdateRateLimit(HttpResponseMessage response, bool isRateLimitProbe)
    {
        if (response.StatusCode is not HttpStatusCode.TooManyRequests and not HttpStatusCode.Forbidden)
        {
            if (isRateLimitProbe)
            {
                ClearRateLimit();
            }

            return;
        }

        var retryDelay = GetRetryDelay(response);
        if (IsRateLimited(response, retryDelay))
        {
            retryDelay ??= TimeSpan.FromSeconds(1);
            lock (rateLimitGate)
            {
                var notBefore = DateTimeOffset.UtcNow + retryDelay.Value;
                if (rateLimit is null)
                {
                    rateLimit = new RateLimitState(notBefore);
                }
                else
                {
                    if (notBefore > rateLimit.NotBefore)
                    {
                        rateLimit.NotBefore = notBefore;
                    }

                    rateLimit.ProbeInProgress = false;
                    rateLimit.SignalChanged();
                }
            }

            return;
        }

        if (isRateLimitProbe)
        {
            ClearRateLimit();
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

    private static bool IsRateLimited(HttpResponseMessage response, TimeSpan? retryDelay)
        => response.StatusCode == HttpStatusCode.TooManyRequests
        || response.StatusCode == HttpStatusCode.Forbidden
            && (retryDelay is not null || HasNoRemainingRequests(response));

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

        return delay > MaximumRetryAfter ? MaximumRetryAfter : delay;
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
            return long.TryParse(value, out reset);
        }

        return false;
    }

    private SourceRepositoryRecord CreateRecord(SourceRepositoryTarget target, HttpStatusCode status, GitHubLicenseResult? license, string[] warnings, string[] errors)
        => new(target.CacheKey, "github-license-api", authentication.Mode, target.Repository, target.Ref, status, license, warnings, errors, DateTimeOffset.UtcNow);

    private static string ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static string? ReadNullableString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class RateLimitState(DateTimeOffset notBefore)
    {
        public DateTimeOffset NotBefore = notBefore;
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
        : this(statusCode, null, false, innerException) { }

    internal SourceRepositoryFetchException(HttpStatusCode? statusCode, TimeSpan? retryAfter, bool isRateLimited, Exception? innerException = null)
        : base("GitHub License API request failed.", innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        IsRateLimited = isRateLimited;
    }

    /// <summary>Gets the response status when available.</summary>
    public HttpStatusCode? StatusCode { get; }
    /// <summary>Gets the server-directed retry delay when supplied.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>Gets whether the response represents a GitHub rate limit.</summary>
    public bool IsRateLimited { get; }
    /// <summary>Gets whether this failure may be retried.</summary>
    public bool IsTransient => IsRateLimited || StatusCode is { } value && (int)value >= 500;
}
