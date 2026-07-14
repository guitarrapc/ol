using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ol.Core;

/// <summary>Retrieves GitHub's detected repository license without parsing license bodies.</summary>
public sealed class GitHubLicenseApiClient
{
    private static readonly Uri ApiBaseUri = new("https://api.github.com/");
    private readonly Uri apiBaseUri;
    private readonly GitHubAuthentication authentication;
    private readonly HttpClient httpClient;

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
        if (authentication.Token.Length != 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private SourceRepositoryRecord CreateRecord(SourceRepositoryTarget target, HttpStatusCode status, GitHubLicenseResult? license, string[] warnings, string[] errors)
        => new(target.CacheKey, "github-license-api", authentication.Mode, target.Repository, target.Ref, status, license, warnings, errors, DateTimeOffset.UtcNow);

    private static string ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static string? ReadNullableString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Represents a GitHub License API failure.</summary>
public sealed class SourceRepositoryFetchException(HttpStatusCode? statusCode, Exception? innerException = null) : Exception("GitHub License API request failed.", innerException)
{
    /// <summary>Gets the response status when available.</summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;
    /// <summary>Gets whether this failure may be retried.</summary>
    public bool IsTransient => StatusCode == HttpStatusCode.TooManyRequests || StatusCode is { } value && (int)value >= 500;
}
