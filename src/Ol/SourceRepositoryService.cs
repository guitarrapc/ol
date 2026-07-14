using System.Collections.Concurrent;
using Ol.Core;

internal readonly record struct SourceRepositorySummary(int TargetCount, int GitHubRequestCount, int CacheHitCount, int CacheMissCount, int FetchErrorCount, int UnknownCount, string AuthMode, int Concurrency, int RetryCount);

internal static class SourceRepositoryPaths
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("OL_SOURCE_REPOSITORY_CACHE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "cache", "source-repository");
}

internal sealed class SourceRepositoryService(SpdxLicenseIndex spdxLicenseIndex, PackageMetadataCache metadataCache, SourceRepositoryCache sourceCache, bool refresh, int retryCount)
{
    private static readonly HttpClient HttpClient = new();
    private readonly GitHubAuthentication authentication = GitHubAuthentication.FromEnvironment();

    public async Task<(ScanComponent[] Components, SourceRepositorySummary Summary)> EnrichAsync(ScanComponent[] components, int concurrency, CancellationToken cancellationToken = default)
    {
        var targets = new Dictionary<string, SourceRepositoryTarget>(Math.Clamp(components.Length, 1, 16), StringComparer.Ordinal);
        var componentTargets = new string?[components.Length];
        for (var i = 0; i < components.Length; i++)
        {
            PackageMetadataRecord? metadata = null;
            if (PackageMetadataRequest.TryCreate(components[i].Purl.ToString(), out var request))
            {
                metadata = await metadataCache.TryReadAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
            }

            var repositoryUrl = metadata is { } record && record.RepositoryUrl.Length != 0 ? record.RepositoryUrl : GetSbomRepositoryUrl(components[i]);
            if (!SourceRepositoryTarget.TryCreate(repositoryUrl, out var target)) continue;
            targets.TryAdd(target.CacheKey, target);
            componentTargets[i] = target.CacheKey;
        }

        var results = new ConcurrentDictionary<string, SourceRepositoryLookupResult>(StringComparer.Ordinal);
        var options = new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(targets, options, async (entry, token) =>
        {
            results[entry.Key] = await EnrichTargetAsync(entry.Value, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        for (var i = 0; i < components.Length; i++)
        {
            var key = componentTargets[i];
            if (key is null) continue;
            var result = results[key];
            components[i] = LicenseReconciler.AddCandidate(components[i], result.Candidate);
        }

        var requests = 0;
        var hits = 0;
        var misses = 0;
        var errors = 0;
        var unknown = 0;
        foreach (var result in results.Values)
        {
            requests += result.Requested ? 1 : 0;
            hits += result.CacheHit ? 1 : 0;
            misses += result.CacheMiss ? 1 : 0;
            errors += result.FetchError ? 1 : 0;
            unknown += result.Unknown ? 1 : 0;
        }

        return (components, new SourceRepositorySummary(targets.Count, requests, hits, misses, errors, unknown, authentication.Mode, concurrency, retryCount));
    }

    private async Task<SourceRepositoryLookupResult> EnrichTargetAsync(SourceRepositoryTarget target, CancellationToken cancellationToken)
    {
        if (!refresh)
        {
            var cached = await sourceCache.TryReadAsync(target.CacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is { } record) return CreateResult(record, cacheHit: true, cacheMiss: false, requested: false);
        }

        try
        {
            var client = new GitHubLicenseApiClient(HttpClient, authentication);
            SourceRepositoryRecord record = default;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    record = await client.FetchAsync(target, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (SourceRepositoryFetchException exception) when (attempt < retryCount && exception.IsTransient) { }
                catch (HttpRequestException) when (attempt < retryCount) { }
                catch (TaskCanceledException) when (attempt < retryCount && !cancellationToken.IsCancellationRequested) { }
            }

            await sourceCache.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            return CreateResult(record, cacheHit: false, cacheMiss: true, requested: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SourceRepositoryFetchException) { return CreateError(); }
        catch (HttpRequestException) { return CreateError(); }
        catch (IOException) { return CreateError(); }
    }

    private SourceRepositoryLookupResult CreateResult(SourceRepositoryRecord record, bool cacheHit, bool cacheMiss, bool requested)
    {
        var raw = record.License?.SpdxId ?? "NOASSERTION";
        var candidate = LicenseCandidateFactory.Create("github-license-api", "license", Utf8Slice.FromString(raw), spdxLicenseIndex);
        var unknown = candidate.Status == LicenseStatus.Unknown;
        if (record.Warnings.Length != 0)
        {
            var warnings = new string[candidate.Warnings.Length + record.Warnings.Length];
            candidate.Warnings.CopyTo(warnings, 0);
            record.Warnings.CopyTo(warnings, candidate.Warnings.Length);
            candidate = candidate with { Warnings = warnings };
        }

        return new SourceRepositoryLookupResult(candidate, cacheHit, cacheMiss, requested, false, unknown);
    }

    private static SourceRepositoryLookupResult CreateError()
        => new(LicenseCandidateFactory.CreateError("github-license-api", "fetch", "source_repository_fetch_failed"), false, true, true, true, false);

    private static string GetSbomRepositoryUrl(ScanComponent component)
    {
        for (var i = 0; i < component.CandidateCount; i++)
        {
            var candidate = component.GetCandidate(i);
            if (candidate.Source == "sbom" && candidate.Kind == "repository-url")
            {
                return candidate.Raw.ToString();
            }
        }

        return string.Empty;
    }

    private readonly record struct SourceRepositoryLookupResult(LicenseCandidate Candidate, bool CacheHit, bool CacheMiss, bool Requested, bool FetchError, bool Unknown);
}
