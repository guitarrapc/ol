using Ol.Core.SourceRepository;

namespace Ol.Core.GitHub;

/// <summary>Applies the shared retry policy to GitHub License API requests.</summary>
public static class GitHubLicenseFetchScheduler
{
    /// <summary>Fetches source evidence, retrying only transient failures.</summary>
    public static Task<SourceRepositoryRecord> FetchAsync(GitHubLicenseApiClient client, SourceRepositoryTarget target, int retryCount, CancellationToken cancellationToken = default)
        => FetchAsync(client, target, retryCount, static (delay, token) => Task.Delay(delay, token), cancellationToken);

    internal static async Task<SourceRepositoryRecord> FetchAsync(
        GitHubLicenseApiClient client,
        SourceRepositoryTarget target,
        int retryCount,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await client.FetchAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceRepositoryFetchException exception) when (attempt < retryCount && exception.IsTransient)
            {
                // A retryable rate limit carries the delay GitHub asked for. Retrying sooner would ignore
                // the instruction that came with the failure.
                if (exception.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
                {
                    await delayAsync(retryAfter, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException) when (attempt < retryCount) { }
            catch (TaskCanceledException) when (attempt < retryCount && !cancellationToken.IsCancellationRequested) { }
        }
    }
}
