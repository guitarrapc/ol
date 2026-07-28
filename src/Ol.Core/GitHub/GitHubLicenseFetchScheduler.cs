using Ol.Core.SourceRepository;

namespace Ol.Core.GitHub;

/// <summary>Applies the shared retry policy to GitHub License API requests.</summary>
public static class GitHubLicenseFetchScheduler
{
    /// <summary>Fetches source evidence, retrying only transient failures.</summary>
    public static async Task<SourceRepositoryRecord> FetchAsync(GitHubLicenseApiClient client, SourceRepositoryTarget target, int retryCount, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await client.FetchAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceRepositoryFetchException exception) when (attempt < retryCount && exception.IsTransient) { }
            catch (HttpRequestException) when (attempt < retryCount) { }
            catch (TaskCanceledException) when (attempt < retryCount && !cancellationToken.IsCancellationRequested) { }
        }
    }
}
