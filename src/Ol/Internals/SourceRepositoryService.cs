using System.Buffers;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.Licensing;
using Ol.Core.PackageMetadata;
using Ol.Core.SourceRepository;
using Ol.Core.Spdx;

namespace Ol.Internals;

internal readonly record struct SourceRepositorySummary(int TargetCount, int GitHubRequestCount, int CacheHitCount, int CacheMissCount, int FetchErrorCount, int UnknownCount, string AuthMode, int Concurrency, int RetryCount);

internal static class SourceRepositoryPaths
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("OL_SOURCE_REPOSITORY_CACHE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "cache", "source-repository");
}

internal sealed class SourceRepositoryService(SpdxLicenseIndex spdxLicenseIndex, SourceRepositoryCache sourceCache, bool refresh, int retryCount, HttpClient? client = null)
{
    private const int LinearPlanningComponentLimit = 8;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient httpClient = client ?? SharedHttpClient;
    private readonly GitHubAuthentication authentication = GitHubAuthentication.FromEnvironment();

    public ValueTask<(ScanComponent[] Components, SourceRepositorySummary Summary)> EnrichAsync(
        ScanComponent[] components,
        PackageMetadataWorkspace workspace,
        int concurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Length < components.Length)
        {
            throw new ArgumentException("Package metadata records must correspond to every component.", nameof(workspace));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (components.Length == 0)
        {
            return ValueTask.FromResult((
                Components: components,
                Summary: new SourceRepositorySummary(0, 0, 0, 0, 0, 0, authentication.Mode, concurrency, retryCount)));
        }

        return components.Length == 1
            ? EnrichSingleComponent(components, workspace, concurrency, cancellationToken)
            : EnrichCoreAsync(components, workspace, concurrency, cancellationToken);
    }

    private ValueTask<(ScanComponent[] Components, SourceRepositorySummary Summary)> EnrichSingleComponent(
        ScanComponent[] components,
        PackageMetadataWorkspace workspace,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var metadata = workspace.Records[0];
        var repositoryUrl = metadata is { } record && record.RepositoryUrl.Length != 0 ? record.RepositoryUrl : GetSbomRepositoryUrl(components[0]);
        if (repositoryUrl.Length == 0)
        {
            components[0] = AddUnavailableCandidate(components[0]);
            return ValueTask.FromResult((
                Components: components,
                Summary: new SourceRepositorySummary(0, 0, 0, 0, 0, 1, authentication.Mode, concurrency, retryCount)));
        }

        return EnrichSingleTarget(components, repositoryUrl, metadata?.RepositoryRef ?? string.Empty, concurrency, cancellationToken);
    }

    private ValueTask<(ScanComponent[] Components, SourceRepositorySummary Summary)> EnrichSingleTarget(
        ScanComponent[] components,
        string repositoryUrl,
        string repositoryRef,
        int concurrency,
        CancellationToken cancellationToken)
    {
        if (!SourceRepositoryTarget.TryCreate(repositoryUrl, repositoryRef, out var target))
        {
            components[0] = AddUnsupportedCandidate(components[0], repositoryUrl);
            return ValueTask.FromResult((
                Components: components,
                Summary: new SourceRepositorySummary(0, 0, 0, 0, 0, 1, authentication.Mode, concurrency, retryCount)));
        }

        var cacheWasInvalid = false;
        if (!refresh)
        {
            var cached = sourceCache.Read(target.CacheKey);
            if (cached.Record is { } cachedRecord)
            {
                return ValueTask.FromResult(ApplySingleTarget(components, CreateResult(cachedRecord, cacheHit: true, cacheMiss: false, requested: false), concurrency));
            }

            cacheWasInvalid = cached.Status == SourceRepositoryCacheReadStatus.Invalid;
        }

        return FetchSingleTargetAsync(components, target, cacheWasInvalid, concurrency, cancellationToken);
    }

    private async ValueTask<(ScanComponent[] Components, SourceRepositorySummary Summary)> FetchSingleTargetAsync(
        ScanComponent[] components,
        SourceRepositoryTarget target,
        bool cacheWasInvalid,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var result = await FetchTargetAsync(target, cacheWasInvalid, cancellationToken).ConfigureAwait(false);
        return ApplySingleTarget(components, result, concurrency);
    }

    private (ScanComponent[] Components, SourceRepositorySummary Summary) ApplySingleTarget(
        ScanComponent[] components,
        in SourceRepositoryLookupResult result,
        int concurrency)
    {
        components[0] = LicenseReconciler.AddCandidate(components[0], result.Candidate);
        return (
            components,
            new SourceRepositorySummary(
                1,
                result.Requested ? 1 : 0,
                result.CacheHit ? 1 : 0,
                result.CacheMiss ? 1 : 0,
                result.FetchError ? 1 : 0,
                result.Unknown ? 1 : 0,
                authentication.Mode,
                concurrency,
                retryCount));
    }

    private async ValueTask<(ScanComponent[] Components, SourceRepositorySummary Summary)> EnrichCoreAsync(
        ScanComponent[] components,
        PackageMetadataWorkspace workspace,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var useLinearPlanning = components.Length <= LinearPlanningComponentLimit;
        var targetIndexes = useLinearPlanning ? null : new Dictionary<string, int>(components.Length, StringComparer.Ordinal);
        var targets = ArrayPool<SourceRepositoryTarget>.Shared.Rent(Math.Max(components.Length, 1));
        var results = ArrayPool<SourceRepositoryLookupResult>.Shared.Rent(Math.Max(components.Length, 1));
        var componentTargetIndexes = ArrayPool<int>.Shared.Rent(Math.Max(components.Length, 1));
        componentTargetIndexes.AsSpan(0, components.Length).Fill(-1);
        var targetCount = 0;
        try
        {
            targetCount = PlanTargets(components, workspace, targets, componentTargetIndexes, targetIndexes, out var unplannedUnknownCount);

            if (targetCount == 1)
            {
                results[0] = await EnrichTargetAsync(targets[0], cancellationToken).ConfigureAwait(false);
            }
            else if (targetCount > 1)
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken };
                await Parallel.ForAsync(0, targetCount, options, async (index, token) =>
                {
                    results[index] = await EnrichTargetAsync(targets[index], token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            var unknown = unplannedUnknownCount;
            for (var i = 0; i < components.Length; i++)
            {
                var targetIndex = componentTargetIndexes[i];
                if (targetIndex < 0) continue;
                var result = results[targetIndex];
                components[i] = LicenseReconciler.AddCandidate(components[i], result.Candidate);
                unknown += result.Unknown ? 1 : 0;
            }

            var requests = 0;
            var hits = 0;
            var misses = 0;
            var errors = 0;
            for (var i = 0; i < targetCount; i++)
            {
                var result = results[i];
                requests += result.Requested ? 1 : 0;
                hits += result.CacheHit ? 1 : 0;
                misses += result.CacheMiss ? 1 : 0;
                errors += result.FetchError ? 1 : 0;
            }

            return (components, new SourceRepositorySummary(targetCount, requests, hits, misses, errors, unknown, authentication.Mode, concurrency, retryCount));
        }
        finally
        {
            targets.AsSpan(0, components.Length).Clear();
            results.AsSpan(0, components.Length).Clear();
            ArrayPool<SourceRepositoryTarget>.Shared.Return(targets);
            ArrayPool<SourceRepositoryLookupResult>.Shared.Return(results);
            ArrayPool<int>.Shared.Return(componentTargetIndexes);
        }
    }

    /// <summary>Deduplicates every component's repository target. Synchronous so the workspace records cannot span an await.</summary>
    private static int PlanTargets(
        ScanComponent[] components,
        PackageMetadataWorkspace workspace,
        Span<SourceRepositoryTarget> targets,
        Span<int> componentTargetIndexes,
        Dictionary<string, int>? targetIndexes,
        out int unplannedUnknownCount)
    {
        var records = workspace.Records;
        var useLinearPlanning = targetIndexes is null;
        var targetCount = 0;
        unplannedUnknownCount = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var metadata = records[i];
            var repositoryUrl = metadata is { } record && record.RepositoryUrl.Length != 0 ? record.RepositoryUrl : GetSbomRepositoryUrl(components[i]);
            if (repositoryUrl.Length == 0)
            {
                components[i] = AddUnavailableCandidate(components[i]);
                unplannedUnknownCount++;
                continue;
            }

            var repositoryRef = metadata?.RepositoryRef ?? string.Empty;
            if (!SourceRepositoryTarget.TryCreate(repositoryUrl, repositoryRef, out var target))
            {
                components[i] = AddUnsupportedCandidate(components[i], repositoryUrl);
                unplannedUnknownCount++;
                continue;
            }

            var targetIndex = -1;
            if (useLinearPlanning)
            {
                for (var existingTargetIndex = 0; existingTargetIndex < targetCount; existingTargetIndex++)
                {
                    if (targets[existingTargetIndex].Equals(target))
                    {
                        targetIndex = existingTargetIndex;
                        break;
                    }
                }
            }
            else
            {
                if (!targetIndexes!.TryGetValue(target.CacheKey, out targetIndex))
                {
                    targetIndex = -1;
                }
            }

            if (targetIndex < 0)
            {
                targetIndex = targetCount;
                targetIndexes?.Add(target.CacheKey, targetIndex);
                targets[targetCount] = target;
                targetCount++;
            }

            componentTargetIndexes[i] = targetIndex;
        }

        return targetCount;
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

        return await FetchTargetAsync(target, cacheWasInvalid, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceRepositoryLookupResult> FetchTargetAsync(SourceRepositoryTarget target, bool cacheWasInvalid, CancellationToken cancellationToken)
    {
        try
        {
            var githubClient = new GitHubLicenseApiClient(httpClient, authentication);
            var record = await GitHubLicenseFetchScheduler.FetchAsync(githubClient, target, retryCount, cancellationToken).ConfigureAwait(false);
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
        var candidate = LicenseCandidateFactory.Create(LicenseCandidateSource.GitHubLicenseApi, LicenseCandidateKind.License, Utf8Slice.FromString(raw), spdxLicenseIndex);
        var unknown = record.Errors.Length == 0 && candidate.Status == LicenseStatus.Unknown;
        if (record.Errors.Length != 0)
        {
            candidate = LicenseCandidateFactory.CreateError(LicenseCandidateSource.GitHubLicenseApi, LicenseCandidateKind.Fetch, LicenseCandidateIdentifiers.ParseWarning(record.Errors[0]));
        }

        candidate = candidate with { Warnings = candidate.Warnings | LicenseCandidateIdentifiers.ParseWarnings(record.Warnings) };

        var license = record.License;
        candidate = candidate with
        {
            Evidence = new LicenseEvidence(
                LicenseEvidenceKind.SourceRepository,
                SourceRepository: new SourceRepositoryEvidence(
                record.Repository,
                record.Ref,
                record.HttpStatus is { } status ? (int)status : null,
                record.CacheKeySha256,
                license?.Path ?? string.Empty,
                license?.Sha ?? string.Empty,
                license?.Key ?? string.Empty,
                license?.Name ?? string.Empty,
                license?.HtmlUrl ?? string.Empty)),
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
        return component.RepositoryUrl.ToString();
    }

    private static ScanComponent AddUnsupportedCandidate(ScanComponent component, string repositoryUrl)
    {
        return LicenseReconciler.AddCandidate(component, new LicenseCandidate(
            LicenseCandidateSource.SourceRepository,
            LicenseCandidateKind.Unsupported,
            Utf8Slice.FromString(repositoryUrl),
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.UnsupportedSourceRepository,
            new LicenseEvidence(LicenseEvidenceKind.SourceRepository)));
    }

    private static ScanComponent AddUnavailableCandidate(ScanComponent component)
    {
        return LicenseReconciler.AddCandidate(component, new LicenseCandidate(
            LicenseCandidateSource.SourceRepository,
            LicenseCandidateKind.Unavailable,
            default,
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.SourceRepositoryUnavailable,
            new LicenseEvidence(LicenseEvidenceKind.SourceRepository)));
    }

    private readonly record struct SourceRepositoryLookupResult(LicenseCandidate Candidate, bool CacheHit, bool CacheMiss, bool Requested, bool FetchError, bool Unknown);
}
