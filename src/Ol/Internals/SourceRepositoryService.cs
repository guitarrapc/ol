using System.Buffers;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.Licensing;
using Ol.Core.SourceRepository;
using Ol.Core.Spdx;

namespace Ol.Internals;

internal readonly record struct SourceRepositorySummary(int TargetCount, int GitHubRequestCount, int CacheHitCount, int CacheMissCount, int FetchErrorCount, int UnknownCount, string AuthMode, int Concurrency, int RetryCount);

internal static class SourceRepositoryPaths
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("OL_SOURCE_REPOSITORY_CACHE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "cache", "source-repository");
}

internal sealed class SourceRepositoryService
{
    private const int LinearPlanningComponentLimit = 8;
    // The License API client follows redirects itself to keep the token attached, which only works while
    // the handler leaves them alone.
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    private readonly SpdxLicenseIndex spdxLicenseIndex;
    private readonly SourceRepositoryCache sourceCache;
    private readonly bool refresh;
    private readonly int retryCount;
    private readonly PurlPrefixSet? uncollectedPackages;
    private readonly GitHubAuthentication authentication;
    private readonly GitHubLicenseApiClient githubClient;

    public SourceRepositoryService(SpdxLicenseIndex spdxLicenseIndex, SourceRepositoryCache sourceCache, bool refresh, int retryCount, HttpClient? client = null, PurlPrefixSet? uncollectedPackages = null)
    {
        this.spdxLicenseIndex = spdxLicenseIndex;
        this.sourceCache = sourceCache;
        this.refresh = refresh;
        this.retryCount = retryCount;
        this.uncollectedPackages = uncollectedPackages;
        authentication = GitHubAuthentication.FromEnvironment();
        githubClient = new GitHubLicenseApiClient(client ?? SharedHttpClient, authentication);
    }

    /// <summary>Gets the rate limit that stopped source collection, or <see langword="null"/> while none was reached.</summary>
    public GitHubRateLimitStatus? RateLimit => githubClient.RateLimit;

    public ValueTask<(ScanComponent[] Components, SourceRepositorySummary Summary)> EnrichAsync(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        int concurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        if (resolutions.Length < components.Length)
        {
            throw new ArgumentException("Package metadata records must correspond to every component.", nameof(resolutions));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (components.Length == 0)
        {
            return ValueTask.FromResult((
                Components: components,
                Summary: new SourceRepositorySummary(0, 0, 0, 0, 0, 0, authentication.Mode, concurrency, retryCount)));
        }

        return components.Length == 1
            ? EnrichSingleComponent(components, resolutions, concurrency, cancellationToken)
            : EnrichCoreAsync(components, resolutions, concurrency, cancellationToken);
    }

    private ValueTask<(ScanComponent[] Components, SourceRepositorySummary Summary)> EnrichSingleComponent(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        int concurrency,
        CancellationToken cancellationToken)
    {
        // A component whose external collection was disabled keeps only the package-side record of that decision.
        if (uncollectedPackages is not null && uncollectedPackages.Contains(components[0].Purl))
        {
            return ValueTask.FromResult((
                Components: components,
                Summary: new SourceRepositorySummary(0, 0, 0, 0, 0, 0, authentication.Mode, concurrency, retryCount)));
        }

        var metadata = resolutions[0];
        if (metadata is { RepositorySubdirectoryDeclared: true })
        {
            components[0] = AddSubdirectoryCandidate(components[0], metadata.Value.RepositoryUrl);
            return ValueTask.FromResult((
                Components: components,
                Summary: new SourceRepositorySummary(0, 0, 0, 0, 0, 1, authentication.Mode, concurrency, retryCount)));
        }

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
                return ValueTask.FromResult(ApplySingleTarget(components, CreateResult(cachedRecord, cached.CacheKeySha256, LookupOutcome.CacheHit), concurrency));
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
                result.Has(LookupOutcome.Requested) ? 1 : 0,
                result.Has(LookupOutcome.CacheHit) ? 1 : 0,
                result.Has(LookupOutcome.CacheMiss) ? 1 : 0,
                result.Has(LookupOutcome.FetchError) ? 1 : 0,
                result.Has(LookupOutcome.Unknown) ? 1 : 0,
                authentication.Mode,
                concurrency,
                retryCount));
    }

    private async ValueTask<(ScanComponent[] Components, SourceRepositorySummary Summary)> EnrichCoreAsync(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var useLinearPlanning = components.Length <= LinearPlanningComponentLimit;
        var originIndexes = useLinearPlanning ? null : new Dictionary<SourceRepositoryOrigin, int>(components.Length);
        var targetIndexes = useLinearPlanning ? null : new Dictionary<string, int>(components.Length, StringComparer.Ordinal);
        var targets = ArrayPool<SourceRepositoryTarget>.Shared.Rent(Math.Max(components.Length, 1));
        var origins = ArrayPool<SourceRepositoryOrigin>.Shared.Rent(Math.Max(components.Length, 1));
        var results = ArrayPool<SourceRepositoryLookupResult>.Shared.Rent(Math.Max(components.Length, 1));
        var componentTargetIndexes = ArrayPool<int>.Shared.Rent(Math.Max(components.Length, 1));
        componentTargetIndexes.AsSpan(0, components.Length).Fill(-1);
        var targetCount = 0;
        try
        {
            targetCount = PlanTargets(components, resolutions, targets, origins, componentTargetIndexes, originIndexes, targetIndexes, uncollectedPackages, out var unplannedUnknownCount);

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
                unknown += result.Has(LookupOutcome.Unknown) ? 1 : 0;
            }

            var requests = 0;
            var hits = 0;
            var misses = 0;
            var errors = 0;
            for (var i = 0; i < targetCount; i++)
            {
                var result = results[i];
                requests += result.Has(LookupOutcome.Requested) ? 1 : 0;
                hits += result.Has(LookupOutcome.CacheHit) ? 1 : 0;
                misses += result.Has(LookupOutcome.CacheMiss) ? 1 : 0;
                errors += result.Has(LookupOutcome.FetchError) ? 1 : 0;
            }

            return (components, new SourceRepositorySummary(targetCount, requests, hits, misses, errors, unknown, authentication.Mode, concurrency, retryCount));
        }
        finally
        {
            targets.AsSpan(0, components.Length).Clear();
            origins.AsSpan(0, components.Length).Clear();
            results.AsSpan(0, components.Length).Clear();
            ArrayPool<SourceRepositoryTarget>.Shared.Return(targets);
            ArrayPool<SourceRepositoryOrigin>.Shared.Return(origins);
            ArrayPool<SourceRepositoryLookupResult>.Shared.Return(results);
            ArrayPool<int>.Shared.Return(componentTargetIndexes);
        }
    }

    /// <summary>
    /// Deduplicates every component's repository target. Synchronous so the resolution reads cannot span an await.
    /// </summary>
    /// <remarks>
    /// Deduplication happens twice, and the order matters. The first pass keys on the supplied
    /// repository URL and ref, which costs two string comparisons and lets a repeated repository skip
    /// normalization entirely; normalizing per component parses a <see cref="Uri"/> and builds owner,
    /// name, and cache-key strings for a result an earlier component already produced. The second pass
    /// keys on the normalized cache key, because distinct spellings of one repository must still
    /// collapse to a single target.
    /// </remarks>
    private static int PlanTargets(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        Span<SourceRepositoryTarget> targets,
        Span<SourceRepositoryOrigin> origins,
        Span<int> componentTargetIndexes,
        Dictionary<SourceRepositoryOrigin, int>? originIndexes,
        Dictionary<string, int>? targetIndexes,
        PurlPrefixSet? uncollectedPackages,
        out int unplannedUnknownCount)
    {
        var records = resolutions;
        var useLinearPlanning = targetIndexes is null;
        var targetCount = 0;
        unplannedUnknownCount = 0;
        for (var i = 0; i < components.Length; i++)
        {
            // A component whose external collection was disabled keeps only the package-side record of that decision.
            if (uncollectedPackages is not null && uncollectedPackages.Contains(components[i].Purl)) continue;

            var metadata = records[i];
            if (metadata is { RepositorySubdirectoryDeclared: true })
            {
                components[i] = AddSubdirectoryCandidate(components[i], metadata.Value.RepositoryUrl);
                unplannedUnknownCount++;
                continue;
            }

            var repositoryUrl = metadata is { } record && record.RepositoryUrl.Length != 0 ? record.RepositoryUrl : GetSbomRepositoryUrl(components[i]);
            if (repositoryUrl.Length == 0)
            {
                components[i] = AddUnavailableCandidate(components[i]);
                unplannedUnknownCount++;
                continue;
            }

            var repositoryRef = metadata?.RepositoryRef ?? string.Empty;
            var origin = new SourceRepositoryOrigin(repositoryUrl, repositoryRef);
            if (TryGetPlannedOrigin(origins, originIndexes, origin, targetCount, out var plannedIndex))
            {
                if (plannedIndex < 0)
                {
                    components[i] = AddUnsupportedCandidate(components[i], repositoryUrl);
                    unplannedUnknownCount++;
                    continue;
                }

                componentTargetIndexes[i] = plannedIndex;
                continue;
            }

            if (!SourceRepositoryTarget.TryCreate(repositoryUrl, repositoryRef, out var target))
            {
                originIndexes?.Add(origin, -1);
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
                origins[targetCount] = origin;
                targetCount++;
            }

            originIndexes?.Add(origin, targetIndex);
            componentTargetIndexes[i] = targetIndex;
        }

        return targetCount;
    }

    /// <summary>Finds a target already planned for one supplied repository URL and ref.</summary>
    /// <remarks>
    /// The linear path only records the first origin that produced each target, so a second spelling of
    /// an already planned repository still normalizes once. That path is bounded by
    /// <see cref="LinearPlanningComponentLimit"/> components.
    /// </remarks>
    private static bool TryGetPlannedOrigin(
        ReadOnlySpan<SourceRepositoryOrigin> origins,
        Dictionary<SourceRepositoryOrigin, int>? originIndexes,
        in SourceRepositoryOrigin origin,
        int targetCount,
        out int targetIndex)
    {
        if (originIndexes is not null)
        {
            return originIndexes.TryGetValue(origin, out targetIndex);
        }

        for (var existingTargetIndex = 0; existingTargetIndex < targetCount; existingTargetIndex++)
        {
            if (origins[existingTargetIndex].Equals(origin))
            {
                targetIndex = existingTargetIndex;
                return true;
            }
        }

        targetIndex = -1;
        return false;
    }

    private async Task<SourceRepositoryLookupResult> EnrichTargetAsync(SourceRepositoryTarget target, CancellationToken cancellationToken)
    {
        var cacheWasInvalid = false;
        if (!refresh)
        {
            var cached = await sourceCache.ReadAsync(target.CacheKey, cancellationToken).ConfigureAwait(false);
            if (cached.Record is { } record) return CreateResult(record, cached.CacheKeySha256, LookupOutcome.CacheHit);
            cacheWasInvalid = cached.Status == SourceRepositoryCacheReadStatus.Invalid;
        }

        return await FetchTargetAsync(target, cacheWasInvalid, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceRepositoryLookupResult> FetchTargetAsync(SourceRepositoryTarget target, bool cacheWasInvalid, CancellationToken cancellationToken)
    {
        try
        {
            var record = await GitHubLicenseFetchScheduler.FetchAsync(githubClient, target, retryCount, cancellationToken).ConfigureAwait(false);
            if (cacheWasInvalid)
            {
                record = record with { Warnings = [.. record.Warnings, "source_repository_cache_invalid"] };
            }

            record = await WriteCacheBestEffortAsync(record, cancellationToken).ConfigureAwait(false);
            return CreateResult(record, SourceRepositoryCache.GetCacheKeySha256(target.CacheKey), LookupOutcome.CacheMiss | LookupOutcome.Requested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return await CreateErrorAsync(target, null, cacheWasInvalid, cancellationToken).ConfigureAwait(false); }
        catch (SourceRepositoryFetchException exception) { return await CreateErrorAsync(target, exception.StatusCode, cacheWasInvalid, !exception.IsRateLimited, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { return await CreateErrorAsync(target, null, cacheWasInvalid, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { return await CreateErrorAsync(target, null, cacheWasInvalid, cancellationToken).ConfigureAwait(false); }
    }

    /// <param name="cacheKeySha256">
    /// The digest of <see cref="SourceRepositoryRecord.CacheKey"/>. Passed in because a cache hit already
    /// derived it to locate the entry file, and <see cref="SourceRepositoryRecord.CacheKeySha256"/> is a
    /// calculated property that would hash the key again for every target.
    /// </param>
    /// <param name="outcome">How the record was obtained: <see cref="LookupOutcome.CacheHit"/>, or a miss that issued a request.</param>
    private SourceRepositoryLookupResult CreateResult(SourceRepositoryRecord record, string cacheKeySha256, LookupOutcome outcome)
    {
        var raw = record.License?.SpdxId ?? "NOASSERTION";
        var candidate = LicenseCandidateFactory.Create(LicenseCandidateSource.GitHubLicenseApi, LicenseCandidateKind.License, Utf8Slice.FromString(raw), spdxLicenseIndex);
        var unknown = record.Errors.Length == 0 && candidate.Status == LicenseStatus.Unknown;
        if (record.Errors.Length != 0)
        {
            candidate = LicenseCandidateFactory.CreateError(LicenseCandidateSource.GitHubLicenseApi, LicenseCandidateKind.Fetch, LicenseCandidateIdentifiers.ParseWarning(record.Errors[0]));
        }

        candidate = candidate with { Warnings = candidate.Warnings | LicenseCandidateIdentifiers.ParseWarnings(record.Warnings) | GetUnresolvedLicenseWarning(record) };

        var license = record.License;
        candidate = candidate with
        {
            Evidence = new LicenseEvidence(
                LicenseEvidenceKind.SourceRepository,
                SourceRepository: new SourceRepositoryEvidence(
                record.Repository,
                record.Ref,
                record.HttpStatus is { } status ? (int)status : null,
                cacheKeySha256,
                license?.Path ?? string.Empty,
                license?.Sha ?? string.Empty,
                license?.Key ?? string.Empty,
                license?.Name ?? string.Empty,
                license?.HtmlUrl ?? string.Empty)),
        };

        if (record.Errors.Length != 0) outcome |= LookupOutcome.FetchError;
        if (unknown) outcome |= LookupOutcome.Unknown;
        return new SourceRepositoryLookupResult(candidate, outcome);
    }

    /// <summary>Names why a completed source lookup produced no SPDX identifier.</summary>
    /// <remarks>
    /// Derived from the record's own fields rather than from a stored warning string, so an entry an
    /// earlier version already cached explains itself without being collected again. A repository that
    /// answered with a license file GitHub could not identify is a different fact from one that has no
    /// license file at all, and only the first gives a human a document to read.
    /// </remarks>
    private static LicenseCandidateWarnings GetUnresolvedLicenseWarning(in SourceRepositoryRecord record)
        => record.Errors.Length != 0 ? LicenseCandidateWarnings.None
        : record.License is not { } license ? LicenseCandidateWarnings.None
        : license.Path.Length != 0 && license.SpdxId is null or "NOASSERTION" ? LicenseCandidateWarnings.SourceLicenseNotRecognized
        : LicenseCandidateWarnings.None;

    private async Task<SourceRepositoryLookupResult> CreateErrorAsync(SourceRepositoryTarget target, System.Net.HttpStatusCode? statusCode, bool cacheWasInvalid, CancellationToken cancellationToken)
        => await CreateErrorAsync(target, statusCode, cacheWasInvalid, persist: true, cancellationToken).ConfigureAwait(false);

    private async Task<SourceRepositoryLookupResult> CreateErrorAsync(SourceRepositoryTarget target, System.Net.HttpStatusCode? statusCode, bool cacheWasInvalid, bool persist, CancellationToken cancellationToken)
    {
        var warnings = cacheWasInvalid ? new[] { "source_repository_cache_invalid" } : [];
        var record = new SourceRepositoryRecord(target.CacheKey, "github-license-api", authentication.Mode, target.Repository, target.Ref, statusCode, null, warnings, ["source_repository_fetch_failed"], DateTimeOffset.UtcNow);
        if (persist)
        {
            record = await WriteCacheBestEffortAsync(record, cancellationToken).ConfigureAwait(false);
        }

        return CreateResult(record, SourceRepositoryCache.GetCacheKeySha256(target.CacheKey), LookupOutcome.CacheMiss | LookupOutcome.Requested);
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

    /// <summary>Records that the repository is known but answers for more than this component.</summary>
    /// <remarks>
    /// The repository-level License API describes the repository root, so for a package the publisher
    /// placed in one directory of a shared repository the answer is about a different package. Reading it
    /// as this component's license turned a correctly declared license into a conflict with whatever the
    /// repository as a whole is licensed under, which is the case this candidate replaces. The repository
    /// is still reported, because a reviewer needs to see which one was set aside and why.
    /// </remarks>
    private static ScanComponent AddSubdirectoryCandidate(ScanComponent component, string repositoryUrl)
    {
        return LicenseReconciler.AddCandidate(component, new LicenseCandidate(
            LicenseCandidateSource.SourceRepository,
            LicenseCandidateKind.Unsupported,
            Utf8Slice.FromString(repositoryUrl),
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.SourceRepositorySubdirectory,
            new LicenseEvidence(LicenseEvidenceKind.SourceRepository)));
    }

    /// <summary>Records that no repository was learned for a component Ol could have looked one up for.</summary>
    /// <remarks>
    /// A component with no package identity is not that case. Nothing ever named a repository for it and
    /// nothing ever could, so "unavailable" would assert an outcome for a lookup that had no subject —
    /// naming a place never sought and implying a rerun that cannot help. It is left with no
    /// source-repository record at all, and the empty purl the report already carries is what the
    /// unresolved mechanism is derived from instead.
    /// </remarks>
    private static ScanComponent AddUnavailableCandidate(ScanComponent component)
    {
        if (component.Purl.IsEmpty)
        {
            return component;
        }

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

    /// <summary>What one planned source lookup did, as the projection and the summary counters read it.</summary>
    /// <remarks>
    /// One field rather than one bool per outcome, matching the package side. The outcomes are not
    /// independent: a lookup is a cache hit or a cache miss, and only a miss issues a request, so the two
    /// combinations that occur were being spelled out as five positional literals that named nothing at
    /// the call site.
    /// </remarks>
    [Flags]
    private enum LookupOutcome : byte
    {
        None = 0,
        CacheHit = 1 << 0,
        CacheMiss = 1 << 1,
        Requested = 1 << 2,
        FetchError = 1 << 3,
        Unknown = 1 << 4,
    }

    private readonly record struct SourceRepositoryLookupResult(LicenseCandidate Candidate, LookupOutcome Outcome)
    {
        public bool Has(LookupOutcome outcome) => (Outcome & outcome) != 0;
    }

    /// <summary>Identifies the supplied repository reference a component was planned from, before normalization.</summary>
    /// <param name="RepositoryUrl">The repository URL exactly as package metadata or the SBOM supplied it.</param>
    /// <param name="RepositoryRef">The repository ref exactly as package metadata supplied it.</param>
    private readonly record struct SourceRepositoryOrigin(string RepositoryUrl, string RepositoryRef);
}
