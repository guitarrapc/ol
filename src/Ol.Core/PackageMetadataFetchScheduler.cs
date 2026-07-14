namespace Ol.Core;

/// <summary>
/// Applies the v2 retry policy to package metadata registry requests.
/// </summary>
public static class PackageMetadataFetchScheduler
{
    /// <summary>
    /// Fetches package metadata, retrying only transient registry and transport failures.
    /// </summary>
    /// <param name="registryClient">The registry client that performs one request.</param>
    /// <param name="request">The package metadata request.</param>
    /// <param name="retryCount">The number of retries after the first attempt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The normalized metadata record.</returns>
    public static async Task<PackageMetadataRecord> FetchAsync(PackageMetadataRegistryClient registryClient, PackageMetadataRequest request, int retryCount, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await registryClient.FetchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (PackageMetadataFetchException exception) when (attempt < retryCount && exception.IsTransient)
            {
            }
            catch (HttpRequestException) when (attempt < retryCount)
            {
            }
            catch (TaskCanceledException) when (attempt < retryCount && !cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
