using System.Buffers;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageMetadata;
using Ol.Core.Spdx;

internal readonly record struct PackageMetadataSummary(
    int SupportedComponentCount,
    int CacheHitCount,
    int CacheMissCount,
    int RefreshedCount,
    int FetchErrorCount,
    int UnsupportedEcosystemCount,
    int Concurrency,
    int RetryCount,
    int TargetCount = 0);

internal static class PackageMetadataPaths
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("OL_PACKAGE_METADATA_CACHE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "cache", "package-metadata");
}

internal sealed class PackageMetadataService(SpdxLicenseIndex spdxLicenseIndex, PackageMetadataCache cache, bool refresh, int retryCount)
{
    private static readonly HttpClient HttpClient = new();
    private readonly PackageMetadataRegistryClient registryClient = OlDefaults.CreatePackageMetadataRegistryClient(HttpClient);

    public async Task<(ScanComponent[] Components, PackageMetadataSummary Summary)> EnrichAsync(ScanComponent[] components, int concurrency, CancellationToken cancellationToken = default)
    {
        var initialLookupCapacity = Math.Clamp(components.Length, 1, 16);
        var lookupByCacheKey = new Dictionary<string, int>(initialLookupCapacity, StringComparer.Ordinal);
        var lookupByPurl = new Dictionary<Utf8Slice, int>(initialLookupCapacity);
        var lookups = ArrayPool<PackageMetadataLookup>.Shared.Rent(initialLookupCapacity);
        var componentLookupIndexes = ArrayPool<int>.Shared.Rent(components.Length);
        PackageMetadataLookupResult[]? lookupResults = null;
        var lookupCount = 0;
        try
        {
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                var purl = component.Purl;
                if (purl.IsEmpty)
                {
                    componentLookupIndexes[i] = -1;
                    continue;
                }

                if (lookupByPurl.TryGetValue(purl, out var lookupIndex))
                {
                    componentLookupIndexes[i] = lookupIndex;
                    continue;
                }

                if (!OlDefaults.TryCreatePackageMetadataRequest(purl.ToString(), out var request))
                {
                    lookupByPurl.Add(purl, -1);
                    componentLookupIndexes[i] = -1;
                    continue;
                }

                if (!lookupByCacheKey.TryGetValue(request.CacheKey, out lookupIndex))
                {
                    EnsureLookupCapacity(ref lookups, lookupCount);
                    lookupIndex = lookupCount;
                    lookupByCacheKey.Add(request.CacheKey, lookupIndex);
                    lookups[lookupCount] = new PackageMetadataLookup(lookupIndex, request);
                    lookupCount++;
                }

                lookupByPurl.Add(purl, lookupIndex);
                componentLookupIndexes[i] = lookupIndex;
            }

            lookupResults = ArrayPool<PackageMetadataLookupResult>.Shared.Rent(lookupCount);
            var options = new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync<PackageMetadataLookup>(new ArraySegment<PackageMetadataLookup>(lookups, 0, lookupCount), options, async (lookup, token) =>
            {
                lookupResults[lookup.Index] = await EnrichLookupAsync(lookup.Request, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

            var supported = 0;
            var hits = 0;
            var misses = 0;
            var refreshed = 0;
            var errors = 0;
            var unsupported = 0;
            for (var i = 0; i < components.Length; i++)
            {
                var lookupIndex = componentLookupIndexes[i];
                var result = lookupIndex >= 0
                    ? lookupResults[lookupIndex]
                    : components[i].Purl.IsEmpty ? default : CreateUnsupportedPurlResult(components[i].Purl);
                components[i] = result.HasCandidate ? LicenseReconciler.AddCandidate(components[i], result.Candidate) : components[i];
                supported += result.Supported ? 1 : 0;
                hits += result.CacheHit ? 1 : 0;
                misses += result.CacheMiss ? 1 : 0;
                refreshed += result.Refreshed ? 1 : 0;
                errors += result.FetchError ? 1 : 0;
                unsupported += result.Unsupported ? 1 : 0;
            }

            return (components, new PackageMetadataSummary(supported, hits, misses, refreshed, errors, unsupported, concurrency, retryCount, lookupCount));
        }
        finally
        {
            ArrayPool<PackageMetadataLookup>.Shared.Return(lookups, clearArray: true);
            ArrayPool<int>.Shared.Return(componentLookupIndexes);
            if (lookupResults is not null)
            {
                ArrayPool<PackageMetadataLookupResult>.Shared.Return(lookupResults, clearArray: true);
            }
        }
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
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageRegistry,
            PackageRegistry: new PackageRegistryEvidence(PackageMetadataCache.GetCacheKeySha256(request.CacheKey)));
        var error = LicenseCandidateFactory.CreateError($"{request.Ecosystem}-registry", "fetch", "package_metadata_fetch_failed", evidence);
        return new PackageMetadataLookupResult(error, true, false, true, false, true, false);
    }

    private static PackageMetadataLookupResult CreateUnsupportedPurlResult(Utf8Slice purl)
    {
        var candidate = new LicenseCandidate(
            "package-metadata",
            "unsupported",
            purl,
            default,
            LicenseStatus.Unknown,
            false,
            ["unsupported_package_metadata"],
            new LicenseEvidence(LicenseEvidenceKind.PackageRegistry));
        return new PackageMetadataLookupResult(candidate, true, false, false, false, false, true);
    }

    private static void EnsureLookupCapacity(ref PackageMetadataLookup[] lookups, int lookupCount)
    {
        if (lookupCount < lookups.Length)
        {
            return;
        }

        var expanded = ArrayPool<PackageMetadataLookup>.Shared.Rent(lookups.Length * 2);
        lookups.AsSpan(0, lookupCount).CopyTo(expanded);
        ArrayPool<PackageMetadataLookup>.Shared.Return(lookups, clearArray: true);
        lookups = expanded;
    }

    private LicenseCandidate CreateMetadataCandidate(PackageMetadataRecord record)
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageRegistry,
            PackageRegistry: new PackageRegistryEvidence(record.CacheKeySha256, record.FetchedAt));
        var candidate = LicenseCandidateFactory.Create(record.Source, "license", Utf8Slice.FromString(record.RawLicense), spdxLicenseIndex, evidence);
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
