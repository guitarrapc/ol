using System.Buffers;
using Ol.Core;

internal readonly record struct SourceRepositorySummary(int TargetCount, int GitHubRequestCount, int CacheHitCount, int CacheMissCount, int FetchErrorCount, int UnknownCount, string AuthMode, int Concurrency, int RetryCount);

internal static class SourceRepositoryPaths
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("OL_SOURCE_REPOSITORY_CACHE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "cache", "source-repository");
}

internal sealed class SourceRepositoryService(SpdxLicenseIndex spdxLicenseIndex, PackageMetadataCache metadataCache, SourceRepositoryCache sourceCache, bool refresh, int retryCount, HttpClient? client = null)
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient httpClient = client ?? SharedHttpClient;
    private readonly GitHubAuthentication authentication = GitHubAuthentication.FromEnvironment();

    public async Task<(ScanComponent[] Components, SourceRepositorySummary Summary)> EnrichAsync(ScanComponent[] components, int concurrency, CancellationToken cancellationToken = default)
    {
        var targetIndexes = new Dictionary<string, int>(components.Length, StringComparer.Ordinal);
        var metadataRecords = new Dictionary<string, PackageMetadataRecord?>(components.Length, StringComparer.Ordinal);
        var targets = ArrayPool<SourceRepositoryTarget>.Shared.Rent(Math.Max(components.Length, 1));
        var results = ArrayPool<SourceRepositoryLookupResult>.Shared.Rent(Math.Max(components.Length, 1));
        var componentTargetIndexes = ArrayPool<int>.Shared.Rent(Math.Max(components.Length, 1));
        componentTargetIndexes.AsSpan(0, components.Length).Fill(-1);
        var targetCount = 0;
        try
        {
            var unplannedUnknownCount = 0;
            for (var i = 0; i < components.Length; i++)
            {
                PackageMetadataRecord? metadata = null;
                if (PackageMetadataRequest.TryCreate(components[i].Purl.ToString(), out var request))
                {
                    if (!metadataRecords.TryGetValue(request.CacheKey, out metadata))
                    {
                        metadata = await metadataCache.TryReadAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
                        metadataRecords.Add(request.CacheKey, metadata);
                    }
                }

                var repositoryUrl = metadata is { } record && record.RepositoryUrl.Length != 0 ? record.RepositoryUrl : GetSbomRepositoryUrl(components[i]);
                if (repositoryUrl.Length == 0)
                {
                    components[i] = LicenseReconciler.AddCandidate(components[i], new LicenseCandidate("source-repository", "unavailable", default, default, LicenseStatus.Unknown, false, ["source_repository_unavailable"]));
                    unplannedUnknownCount++;
                    continue;
                }

                var repositoryRef = metadata?.RepositoryRef ?? string.Empty;
                if (!SourceRepositoryTarget.TryCreate(repositoryUrl, repositoryRef, out var target))
                {
                    components[i] = LicenseReconciler.AddCandidate(components[i], new LicenseCandidate("source-repository", "unsupported", Utf8Slice.FromString(repositoryUrl), default, LicenseStatus.Unknown, false, ["unsupported_source_repository"]));
                    unplannedUnknownCount++;
                    continue;
                }

                var cacheKey = target.CacheKey;
                if (!targetIndexes.TryGetValue(cacheKey, out var targetIndex))
                {
                    targetIndex = targetCount;
                    targetIndexes.Add(cacheKey, targetIndex);
                    targets[targetCount] = target;
                    targetCount++;
                }

                componentTargetIndexes[i] = targetIndex;
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken };
            await Parallel.ForAsync(0, targetCount, options, async (index, token) =>
            {
                results[index] = await EnrichTargetAsync(targets[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);

            for (var i = 0; i < components.Length; i++)
            {
                var targetIndex = componentTargetIndexes[i];
                if (targetIndex < 0) continue;
                components[i] = LicenseReconciler.AddCandidate(components[i], results[targetIndex].Candidate);
            }

            var requests = 0;
            var hits = 0;
            var misses = 0;
            var errors = 0;
            var unknown = unplannedUnknownCount;
            for (var i = 0; i < targetCount; i++)
            {
                var result = results[i];
                requests += result.Requested ? 1 : 0;
                hits += result.CacheHit ? 1 : 0;
                misses += result.CacheMiss ? 1 : 0;
                errors += result.FetchError ? 1 : 0;
                unknown += result.Unknown ? 1 : 0;
            }

            return (components, new SourceRepositorySummary(targetCount, requests, hits, misses, errors, unknown, authentication.Mode, concurrency, retryCount));
        }
        finally
        {
            targets.AsSpan(0, targetCount).Clear();
            results.AsSpan(0, targetCount).Clear();
            ArrayPool<SourceRepositoryTarget>.Shared.Return(targets);
            ArrayPool<SourceRepositoryLookupResult>.Shared.Return(results);
            ArrayPool<int>.Shared.Return(componentTargetIndexes);
        }
    }

    private async Task<SourceRepositoryLookupResult> EnrichTargetAsync(SourceRepositoryTarget target, CancellationToken cancellationToken)
    {
        var cacheWasInvalid = false;
        if (!refresh)
        {
            var cached = await sourceCache.ReadAsync(target.CacheKey, cancellationToken).ConfigureAwait(false);
            if (cached.Record is { } record) return CreateResult(record, cacheHit: true, cacheMiss: false, requested: false);
            cacheWasInvalid = cached.Status == SourceRepositoryCacheReadStatus.Invalid;
        }

        try
        {
            var githubClient = new GitHubLicenseApiClient(httpClient, authentication);
            var record = await SourceRepositoryFetchScheduler.FetchAsync(githubClient, target, retryCount, cancellationToken).ConfigureAwait(false);
            if (cacheWasInvalid)
            {
                record = record with { Warnings = [.. record.Warnings, "source_repository_cache_invalid"] };
            }

            record = await WriteCacheBestEffortAsync(record, cancellationToken).ConfigureAwait(false);
            return CreateResult(record, cacheHit: false, cacheMiss: true, requested: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return await CreateErrorAsync(target, null, cacheWasInvalid, cancellationToken).ConfigureAwait(false); }
        catch (SourceRepositoryFetchException exception) { return await CreateErrorAsync(target, exception.StatusCode, cacheWasInvalid, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { return await CreateErrorAsync(target, null, cacheWasInvalid, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { return await CreateErrorAsync(target, null, cacheWasInvalid, cancellationToken).ConfigureAwait(false); }
    }

    private SourceRepositoryLookupResult CreateResult(SourceRepositoryRecord record, bool cacheHit, bool cacheMiss, bool requested)
    {
        var raw = record.License?.SpdxId ?? "NOASSERTION";
        var candidate = LicenseCandidateFactory.Create("github-license-api", "license", Utf8Slice.FromString(raw), spdxLicenseIndex);
        var unknown = record.Errors.Length == 0 && candidate.Status == LicenseStatus.Unknown;
        if (record.Errors.Length != 0)
        {
            candidate = LicenseCandidateFactory.CreateError("github-license-api", "fetch", record.Errors[0]);
        }

        if (record.Warnings.Length != 0)
        {
            var warnings = new string[candidate.Warnings.Length + record.Warnings.Length];
            candidate.Warnings.CopyTo(warnings, 0);
            record.Warnings.CopyTo(warnings, candidate.Warnings.Length);
            candidate = candidate with { Warnings = warnings };
        }

        var license = record.License;
        candidate = candidate with
        {
            SourceRepository = new SourceRepositoryEvidence(
                record.Repository,
                record.Ref,
                record.HttpStatus is { } status ? (int)status : null,
                record.CacheKeySha256,
                license?.Path ?? string.Empty,
                license?.Sha ?? string.Empty,
                license?.Key ?? string.Empty,
                license?.Name ?? string.Empty,
                license?.HtmlUrl ?? string.Empty),
        };

        return new SourceRepositoryLookupResult(candidate, cacheHit, cacheMiss, requested, record.Errors.Length != 0, unknown);
    }

    private async Task<SourceRepositoryLookupResult> CreateErrorAsync(SourceRepositoryTarget target, System.Net.HttpStatusCode? statusCode, bool cacheWasInvalid, CancellationToken cancellationToken)
    {
        var warnings = cacheWasInvalid ? new[] { "source_repository_cache_invalid" } : [];
        var record = new SourceRepositoryRecord(target.CacheKey, "github-license-api", authentication.Mode, target.Repository, target.Ref, statusCode, null, warnings, ["source_repository_fetch_failed"], DateTimeOffset.UtcNow);
        record = await WriteCacheBestEffortAsync(record, cancellationToken).ConfigureAwait(false);
        return CreateResult(record, cacheHit: false, cacheMiss: true, requested: true);
    }

    private async Task<SourceRepositoryRecord> WriteCacheBestEffortAsync(SourceRepositoryRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await sourceCache.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            return record;
        }
        catch (IOException)
        {
            return record with { Warnings = [.. record.Warnings, "source_repository_cache_write_failed"] };
        }
        catch (UnauthorizedAccessException)
        {
            return record with { Warnings = [.. record.Warnings, "source_repository_cache_write_failed"] };
        }
    }

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
