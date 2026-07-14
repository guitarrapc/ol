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
    public async Task<(ScanComponent[] Components, PackageMetadataSummary Summary)> EnrichAsync(ScanComponent[] components, int concurrency, CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = new Task<PackageMetadataEnrichment>[components.Length];
        for (var i = 0; i < components.Length; i++)
        {
            tasks[i] = EnrichComponentAsync(components[i], gate, cancellationToken);
        }

        var enrichments = await Task.WhenAll(tasks).ConfigureAwait(false);
        var enrichedComponents = new ScanComponent[components.Length];
        var supported = 0;
        var hits = 0;
        var misses = 0;
        var refreshed = 0;
        var errors = 0;
        var unsupported = 0;
        for (var i = 0; i < enrichments.Length; i++)
        {
            var enrichment = enrichments[i];
            enrichedComponents[i] = enrichment.Component;
            supported += enrichment.Supported ? 1 : 0;
            hits += enrichment.CacheHit ? 1 : 0;
            misses += enrichment.CacheMiss ? 1 : 0;
            refreshed += enrichment.Refreshed ? 1 : 0;
            errors += enrichment.FetchError ? 1 : 0;
            unsupported += enrichment.Unsupported ? 1 : 0;
        }

        return (enrichedComponents, new PackageMetadataSummary(supported, hits, misses, refreshed, errors, unsupported, concurrency, retryCount));
    }

    private async Task<PackageMetadataEnrichment> EnrichComponentAsync(ScanComponent component, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        if (!PackageMetadataRequest.TryCreate(component.Purl, out var request))
        {
            if (component.Purl.Length == 0)
            {
                return new PackageMetadataEnrichment(component, false, false, false, false, false, false);
            }

            var unsupported = new LicenseCandidate("package-metadata", "unsupported", component.Purl, string.Empty, LicenseStatus.Unknown, false, ["unsupported_package_metadata"]);
            return new PackageMetadataEnrichment(LicenseReconciler.AddCandidate(component, unsupported), false, false, false, false, false, true);
        }

        if (!refresh)
        {
            var cached = await cache.TryReadAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is { } record)
            {
                return new PackageMetadataEnrichment(LicenseReconciler.AddCandidate(component, CreateMetadataCandidate(record)), true, true, false, false, false, false);
            }
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var error = LicenseCandidateFactory.CreateError($"{request.Ecosystem}-registry", "fetch", "package_metadata_fetch_failed");
            return new PackageMetadataEnrichment(LicenseReconciler.AddCandidate(component, error), true, false, true, false, true, false);
        }
        finally
        {
            gate.Release();
        }
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

    private readonly record struct PackageMetadataEnrichment(ScanComponent Component, bool Supported, bool CacheHit, bool CacheMiss, bool Refreshed, bool FetchError, bool Unsupported);
}
