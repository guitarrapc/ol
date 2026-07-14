using Ol.Core;

internal readonly record struct PackageMetadataSummary(
    int SupportedComponentCount,
    int CacheHitCount,
    int CacheMissCount,
    int RefreshedCount,
    int FetchErrorCount,
    int UnsupportedEcosystemCount,
    int Concurrency,
    int RetryCount);

internal static class PackageMetadataPaths
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("OL_PACKAGE_METADATA_CACHE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "cache", "package-metadata");
}

internal sealed class PackageMetadataService(SpdxLicenseIndex spdxLicenseIndex, PackageMetadataCache cache, bool refresh, int retryCount)
{
    private static readonly HttpClient HttpClient = new();
    private readonly PackageMetadataRegistryClient registryClient = new(HttpClient);

    public async Task<(ScanComponent[] Components, PackageMetadataSummary Summary)> EnrichAsync(ScanComponent[] components, int concurrency, CancellationToken cancellationToken = default)
    {
        var lookupByCacheKey = new Dictionary<string, int>(components.Length, StringComparer.Ordinal);
        var lookups = new PackageMetadataLookup[components.Length];
        var componentLookupIndexes = new int[components.Length];
        var immediateResults = new PackageMetadataLookupResult[components.Length];
        var lookupCount = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            var purl = component.Purl.ToString();
            if (!PackageMetadataRequest.TryCreate(purl, out var request))
            {
                componentLookupIndexes[i] = -1;
                if (!component.Purl.IsEmpty)
                {
                    var unsupportedCandidate = new LicenseCandidate("package-metadata", "unsupported", component.Purl, string.Empty, LicenseStatus.Unknown, false, ["unsupported_package_metadata"]);
                    immediateResults[i] = new PackageMetadataLookupResult(unsupportedCandidate, true, false, false, false, false, true);
                }

                continue;
            }

            if (!lookupByCacheKey.TryGetValue(request.CacheKey, out var lookupIndex))
            {
                lookupIndex = lookupCount;
                lookupByCacheKey.Add(request.CacheKey, lookupIndex);
                lookups[lookupCount] = new PackageMetadataLookup(lookupIndex, request);
                lookupCount++;
            }

            componentLookupIndexes[i] = lookupIndex;
        }

        var lookupResults = new PackageMetadataLookupResult[lookupCount];
        var options = new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync<PackageMetadataLookup>(new ArraySegment<PackageMetadataLookup>(lookups, 0, lookupCount), options, async (lookup, token) =>
        {
            lookupResults[lookup.Index] = await EnrichLookupAsync(lookup.Request, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var enrichedComponents = new ScanComponent[components.Length];
        var supported = 0;
        var hits = 0;
        var misses = 0;
        var refreshed = 0;
        var errors = 0;
        var unsupported = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var lookupIndex = componentLookupIndexes[i];
            var result = lookupIndex >= 0 ? lookupResults[lookupIndex] : immediateResults[i];
            enrichedComponents[i] = result.HasCandidate ? LicenseReconciler.AddCandidate(components[i], result.Candidate) : components[i];
            supported += result.Supported ? 1 : 0;
            hits += result.CacheHit ? 1 : 0;
            misses += result.CacheMiss ? 1 : 0;
            refreshed += result.Refreshed ? 1 : 0;
            errors += result.FetchError ? 1 : 0;
            unsupported += result.Unsupported ? 1 : 0;
        }

        return (enrichedComponents, new PackageMetadataSummary(supported, hits, misses, refreshed, errors, unsupported, concurrency, retryCount));
    }

    private async Task<PackageMetadataLookupResult> EnrichLookupAsync(PackageMetadataRequest request, CancellationToken cancellationToken)
    {
        if (!refresh)
        {
            var cached = await cache.TryReadAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is { } record)
            {
                return new PackageMetadataLookupResult(CreateMetadataCandidate(record), true, true, false, false, false, false);
            }
        }

        try
        {
            var record = await PackageMetadataFetchScheduler.FetchAsync(registryClient, request, retryCount, cancellationToken).ConfigureAwait(false);
            await cache.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            return new PackageMetadataLookupResult(CreateMetadataCandidate(record), true, false, true, refresh, false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PackageMetadataFetchException)
        {
            return CreateFetchError(request);
        }
        catch (HttpRequestException)
        {
            return CreateFetchError(request);
        }
        catch (IOException)
        {
            return CreateFetchError(request);
        }
    }

    private static PackageMetadataLookupResult CreateFetchError(PackageMetadataRequest request)
    {
        var error = LicenseCandidateFactory.CreateError($"{request.Ecosystem}-registry", "fetch", "package_metadata_fetch_failed");
        return new PackageMetadataLookupResult(error, true, false, true, false, true, false);
    }

    private LicenseCandidate CreateMetadataCandidate(PackageMetadataRecord record)
    {
        var candidate = LicenseCandidateFactory.Create(record.Source, "license", record.RawLicense, spdxLicenseIndex);
        if (record.Warnings.Length == 0)
        {
            return candidate;
        }

        var warnings = new string[candidate.Warnings.Length + record.Warnings.Length];
        candidate.Warnings.CopyTo(warnings, 0);
        record.Warnings.CopyTo(warnings, candidate.Warnings.Length);
        return candidate with { Warnings = warnings };
    }

    private readonly record struct PackageMetadataLookup(int Index, PackageMetadataRequest Request);

    private readonly record struct PackageMetadataLookupResult(LicenseCandidate Candidate, bool HasCandidate, bool Supported, bool CacheHit, bool CacheMiss, bool Refreshed, bool FetchError, bool Unsupported)
    {
        public PackageMetadataLookupResult(LicenseCandidate candidate, bool supported, bool cacheHit, bool cacheMiss, bool refreshed, bool fetchError, bool unsupported)
            : this(candidate, true, supported, cacheHit, cacheMiss, refreshed, fetchError, unsupported)
        {
        }
    }
}
