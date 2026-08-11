using System.Buffers;
using System.Net;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.PackageMetadata;
using Ol.Core.Spdx;

namespace Ol.Internals;

internal readonly record struct PackageMetadataSummary(
    int SupportedComponentCount,
    int CacheHitCount,
    int CacheMissCount,
    int RefreshedCount,
    int FetchErrorCount,
    int UnsupportedEcosystemCount,
    int UnversionedPurlCount,
    int Concurrency,
    int RetryCount,
    int TargetCount = 0);

internal static class PackageMetadataPaths
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("OL_PACKAGE_METADATA_CACHE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "cache", "package-metadata");
}

internal sealed class PackageMetadataService(
    SpdxLicenseIndex spdxLicenseIndex,
    PackageMetadataCache cache,
    bool refresh,
    int retryCount,
    PurlPrefixSet? uncollectedPackages = null,
    HttpClient? client = null)
{
    private const int LinearPlanningComponentLimit = 8;

    /// <summary>Plan index for a purl whose ecosystem has no registered provider.</summary>
    private const int UnsupportedEcosystemIndex = -1;

    /// <summary>Plan index for a purl whose ecosystem is supported but which names no single package version.</summary>
    private const int UnversionedPurlIndex = -2;

    /// <summary>
    /// Plan index for a component the user excluded from collection, and for a component with no purl.
    /// </summary>
    /// <remarks>
    /// Neither issues a request and neither says anything about an ecosystem, so the projection must add
    /// no candidate and count nothing. This is a distinct index because the plan's negative space carries
    /// meaning: reusing <see cref="UnsupportedEcosystemIndex"/> made an excluded component report an
    /// ecosystem Ol supports as unsupported, and count toward the unsupported total.
    /// </remarks>
    private const int NoLookupIndex = -3;

    private static readonly HttpClient HttpClient = new();

    /// <summary>
    /// Records that collection was not performed for a component by user configuration. It is a candidate rather than
    /// silence so a report keeps "not asked" distinguishable from "asked and the registry declared no license".
    /// </summary>
    private static readonly LicenseCandidate NotCollectedCandidate = new(
        LicenseCandidateSource.PackageRegistry,
        LicenseCandidateKind.Unavailable,
        default,
        default,
        LicenseStatus.Unknown,
        false,
        LicenseCandidateWarnings.ExternalEvidenceNotCollected,
        new LicenseEvidence(LicenseEvidenceKind.PackageRegistry));
    private readonly PackageMetadataRegistryClient registryClient = OlDefaults.CreatePackageMetadataRegistryClient(client ?? HttpClient);

    public ValueTask<(ScanComponent[] Components, PackageMetadataSummary Summary)> EnrichAsync(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        int concurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        if (resolutions.Length < components.Length)
        {
            throw new ArgumentException("Package metadata resolutions must correspond to every component.", nameof(resolutions));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (components.Length == 0)
        {
            return ValueTask.FromResult((
                Components: components,
                Summary: new PackageMetadataSummary(0, 0, 0, 0, 0, 0, 0, concurrency, retryCount)));
        }

        return components.Length == 1
            ? EnrichSingleComponent(components, resolutions, concurrency, cancellationToken)
            : EnrichCoreAsync(components, resolutions, concurrency, cancellationToken);
    }

    private ValueTask<(ScanComponent[] Components, PackageMetadataSummary Summary)> EnrichSingleComponent(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var purl = components[0].Purl;
        if (purl.IsEmpty)
        {
            return ValueTask.FromResult(ApplySingleLookup(components, resolutions, default, concurrency, lookupCount: 0));
        }

        if (uncollectedPackages is not null && uncollectedPackages.Contains(purl))
        {
            components[0] = LicenseReconciler.AddCandidate(components[0], NotCollectedCandidate);
            return ValueTask.FromResult(ApplySingleLookup(components, resolutions, default, concurrency, lookupCount: 0));
        }

        if (!OlDefaults.TryCreatePackageMetadataRequest(purl, out var request, out var ecosystemSupported))
        {
            return ValueTask.FromResult(ApplySingleLookup(components, resolutions, CreateUnqueryablePurlResult(purl, ecosystemSupported), concurrency, lookupCount: 0));
        }

        if (!refresh)
        {
            var entry = cache.TryRead(request.CacheKey);
            if (entry.IsHit && !IsStaleUnresolvedEntry(entry))
            {
                return ValueTask.FromResult(ApplySingleLookup(components, resolutions, CreateCacheHit(request, entry), concurrency, lookupCount: 1));
            }
        }

        return FetchSingleLookupAsync(components, resolutions, request, concurrency, cancellationToken);
    }

    private async ValueTask<(ScanComponent[] Components, PackageMetadataSummary Summary)> FetchSingleLookupAsync(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        PackageMetadataRequest request,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var result = await FetchLookupAsync(request, cancellationToken).ConfigureAwait(false);
        return ApplySingleLookup(components, resolutions, result, concurrency, lookupCount: 1);
    }

    private (ScanComponent[] Components, PackageMetadataSummary Summary) ApplySingleLookup(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        in PackageMetadataLookupResult result,
        int concurrency,
        int lookupCount)
    {
        resolutions[0] = result.Resolution;
        components[0] = result.Has(LookupOutcome.HasCandidate) ? LicenseReconciler.AddCandidate(components[0], result.Candidate) : components[0];
        return (
            components,
            new PackageMetadataSummary(
                result.Has(LookupOutcome.Supported) ? 1 : 0,
                result.Has(LookupOutcome.CacheHit) ? 1 : 0,
                result.Has(LookupOutcome.CacheMiss) ? 1 : 0,
                result.Has(LookupOutcome.Refreshed) ? 1 : 0,
                result.Has(LookupOutcome.FetchError) ? 1 : 0,
                result.Has(LookupOutcome.Unsupported) ? 1 : 0,
                result.Has(LookupOutcome.UnversionedPurl) ? 1 : 0,
                concurrency,
                retryCount,
                lookupCount));
    }

    private async ValueTask<(ScanComponent[] Components, PackageMetadataSummary Summary)> EnrichCoreAsync(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var initialLookupCapacity = Math.Clamp(components.Length, 1, 16);
        var useLinearPlanning = components.Length <= LinearPlanningComponentLimit;
        var lookupByCacheKey = useLinearPlanning ? null : new Dictionary<Utf8Slice, int>(initialLookupCapacity);
        var lookupByPurl = useLinearPlanning ? null : new Dictionary<Utf8Slice, int>(initialLookupCapacity);
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
                    componentLookupIndexes[i] = NoLookupIndex;
                    continue;
                }

                if (uncollectedPackages is not null && uncollectedPackages.Contains(purl))
                {
                    components[i] = LicenseReconciler.AddCandidate(component, NotCollectedCandidate);
                    componentLookupIndexes[i] = NoLookupIndex;
                    continue;
                }

                var lookupIndex = -1;
                var purlPlanned = false;
                if (useLinearPlanning)
                {
                    for (var previousIndex = 0; previousIndex < i; previousIndex++)
                    {
                        if (components[previousIndex].Purl.Equals(purl))
                        {
                            lookupIndex = componentLookupIndexes[previousIndex];
                            purlPlanned = true;
                            break;
                        }
                    }
                }
                else if (lookupByPurl!.TryGetValue(purl, out lookupIndex))
                {
                    purlPlanned = true;
                }

                if (purlPlanned)
                {
                    componentLookupIndexes[i] = lookupIndex;
                    continue;
                }

                if (!OlDefaults.TryCreatePackageMetadataRequest(purl, out var request, out var ecosystemSupported))
                {
                    // Two distinct "no request" outcomes share the plan's negative index space, so the projection can
                    // name the right one without parsing the purl a second time.
                    var unqueryable = ecosystemSupported ? UnversionedPurlIndex : UnsupportedEcosystemIndex;
                    lookupByPurl?.Add(purl, unqueryable);
                    componentLookupIndexes[i] = unqueryable;
                    continue;
                }

                lookupIndex = -1;
                if (useLinearPlanning)
                {
                    for (var existingLookupIndex = 0; existingLookupIndex < lookupCount; existingLookupIndex++)
                    {
                        if (lookups[existingLookupIndex].Request.CacheKey.Equals(request.CacheKey))
                        {
                            lookupIndex = existingLookupIndex;
                            break;
                        }
                    }
                }
                else
                {
                    if (!lookupByCacheKey!.TryGetValue(request.CacheKey, out lookupIndex))
                    {
                        lookupIndex = -1;
                    }
                }

                if (lookupIndex < 0)
                {
                    EnsureLookupCapacity(ref lookups, lookupCount);
                    lookupIndex = lookupCount;
                    lookupByCacheKey?.Add(request.CacheKey, lookupIndex);
                    lookups[lookupCount] = new PackageMetadataLookup(lookupIndex, request);
                    lookupCount++;
                }

                lookupByPurl?.Add(purl, lookupIndex);
                componentLookupIndexes[i] = lookupIndex;
            }

            if (lookupCount != 0)
            {
                lookupResults = ArrayPool<PackageMetadataLookupResult>.Shared.Rent(lookupCount);
                if (lookupCount == 1)
                {
                    lookupResults[0] = await EnrichLookupAsync(lookups[0].Request, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var options = new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken };
                    await Parallel.ForEachAsync<PackageMetadataLookup>(new ArraySegment<PackageMetadataLookup>(lookups, 0, lookupCount), options, async (lookup, token) =>
                    {
                        lookupResults[lookup.Index] = await EnrichLookupAsync(lookup.Request, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
            }

            return (components, ProjectLookups(components, resolutions, componentLookupIndexes, lookupResults, concurrency, lookupCount));
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

    /// <summary>Writes every lookup result back in component order. Synchronous so the resolution writes cannot span an await.</summary>
    private PackageMetadataSummary ProjectLookups(
        ScanComponent[] components,
        PackageMetadataResolution?[] resolutions,
        ReadOnlySpan<int> componentLookupIndexes,
        ReadOnlySpan<PackageMetadataLookupResult> lookupResults,
        int concurrency,
        int lookupCount)
    {
        var records = resolutions;
        var supported = 0;
        var hits = 0;
        var misses = 0;
        var refreshed = 0;
        var errors = 0;
        var unsupported = 0;
        var unversioned = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var lookupIndex = componentLookupIndexes[i];
            var result = lookupIndex >= 0
                ? lookupResults[lookupIndex]
                : lookupIndex == NoLookupIndex ? default : CreateUnqueryablePurlResult(components[i].Purl, lookupIndex == UnversionedPurlIndex);
            records[i] = result.Resolution;
            components[i] = result.Has(LookupOutcome.HasCandidate) ? LicenseReconciler.AddCandidate(components[i], result.Candidate) : components[i];
            supported += result.Has(LookupOutcome.Supported) ? 1 : 0;
            hits += result.Has(LookupOutcome.CacheHit) ? 1 : 0;
            misses += result.Has(LookupOutcome.CacheMiss) ? 1 : 0;
            refreshed += result.Has(LookupOutcome.Refreshed) ? 1 : 0;
            errors += result.Has(LookupOutcome.FetchError) ? 1 : 0;
            unsupported += result.Has(LookupOutcome.Unsupported) ? 1 : 0;
            unversioned += result.Has(LookupOutcome.UnversionedPurl) ? 1 : 0;
        }

        return new PackageMetadataSummary(supported, hits, misses, refreshed, errors, unsupported, unversioned, concurrency, retryCount, lookupCount);
    }

    private async Task<PackageMetadataLookupResult> EnrichLookupAsync(PackageMetadataRequest request, CancellationToken cancellationToken)
    {
        if (!refresh)
        {
            var entry = await cache.TryReadAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
            if (entry.IsHit && !IsStaleUnresolvedEntry(entry))
            {
                return CreateCacheHit(request, entry);
            }
        }

        return await FetchLookupAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether an entry was observed by a resolver that could not see what this one can.
    /// </summary>
    /// <remarks>
    /// An empty license is the only observation a newer resolver revisits, and it is stale in every
    /// ecosystem rather than only the one whose resolver changed: every provider can now state where
    /// a publisher said its license is, and a registry lookup can supply a repository the older
    /// resolver never read. A declared license is unaffected, so those entries stay cache hits.
    /// Recollection writes the current version, so an unresolved package is refetched once rather than
    /// on every scan.
    /// </remarks>
    private static bool IsStaleUnresolvedEntry(in PackageMetadataCacheEntry entry)
        => entry.ResolverVersion < PackageMetadataRecord.CurrentResolverVersion
        && (entry.RawLicense.IsEmpty || IsPreSubdirectoryNpmEntry(entry));

    /// <summary>
    /// Reports whether an npm entry predates reading npm's <c>repository.directory</c>.
    /// </summary>
    /// <remarks>
    /// The one observation a resolved entry can still be missing. Whether a package sits in one directory
    /// of a shared repository decides whether the repository's own license describes it, so an entry
    /// written before that field was read leaves a correctly licensed package reported as conflicting
    /// with its monorepo's root license until it is collected again. Narrow on purpose: it names the
    /// entries one capability change can improve and stops applying once entries carry that version.
    /// </remarks>
    private static bool IsPreSubdirectoryNpmEntry(in PackageMetadataCacheEntry entry)
        => entry.ResolverVersion < PackageMetadataRecord.NpmRepositoryDirectoryResolverVersion
        && entry.Source == LicenseCandidateSource.NpmRegistry;

    /// <summary>Reads the subdirectory fact back out of the warnings a registry answer persisted.</summary>
    /// <remarks>
    /// The warning is the persisted form of the fact, so a cache hit and a fresh fetch reach the source
    /// stage carrying the same answer without a second cache field to validate and version.
    /// </remarks>
    private static bool HasSubdirectoryWarning(LicenseCandidateWarnings warnings)
        => (warnings & LicenseCandidateWarnings.SourceRepositorySubdirectory) != 0;

    /// <summary>Projects one cache entry into the lookup result the plan carries.</summary>
    private PackageMetadataLookupResult CreateCacheHit(PackageMetadataRequest request, in PackageMetadataCacheEntry entry)
        => new(
            new PackageMetadataResolution(request.CacheKey, entry.RepositoryUrl, entry.RepositoryRef, HasSubdirectoryWarning(entry.Warnings)),
            CreateMetadataCandidate(entry),
            LookupOutcome.HasCandidate | LookupOutcome.Supported | LookupOutcome.CacheHit);

    private async Task<PackageMetadataLookupResult> FetchLookupAsync(PackageMetadataRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var record = await PackageMetadataFetchScheduler.FetchAsync(registryClient, request, retryCount, cancellationToken).ConfigureAwait(false);
            await cache.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            var resolution = new PackageMetadataResolution(record.CacheKey, record.RepositoryUrl, record.RepositoryRef, HasSubdirectoryWarning(LicenseCandidateIdentifiers.ParseWarnings(record.Warnings)));
            var outcome = LookupOutcome.HasCandidate | LookupOutcome.Supported | LookupOutcome.CacheMiss;
            return new PackageMetadataLookupResult(resolution, CreateMetadataCandidate(record), refresh ? outcome | LookupOutcome.Refreshed : outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PackageMetadataFetchException exception)
        {
            // A 404 is an answer, not a failed operation: the registry completed the request and reported that this
            // package is not published there. Recording it as unknown keeps "asked and told no" separate from
            // "could not ask", which is what makes a private package acknowledgeable instead of permanently errored.
            return exception.StatusCode == HttpStatusCode.NotFound
                ? CreateNotFoundResult(request)
                : CreateFetchError(request);
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

    private static PackageMetadataLookupResult CreateNotFoundResult(PackageMetadataRequest request)
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageRegistry,
            PackageRegistry: new PackageRegistryEvidence(PackageMetadataCache.GetCacheKeySha256(request.CacheKey.Span)));
        var candidate = new LicenseCandidate(
            LicenseCandidateSource.PackageRegistry,
            LicenseCandidateKind.Fetch,
            default,
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.PackageMetadataNotFound,
            evidence);
        return new PackageMetadataLookupResult(null, candidate, LookupOutcome.HasCandidate | LookupOutcome.Supported | LookupOutcome.CacheMiss);
    }

    private static PackageMetadataLookupResult CreateFetchError(PackageMetadataRequest request)
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageRegistry,
            PackageRegistry: new PackageRegistryEvidence(PackageMetadataCache.GetCacheKeySha256(request.CacheKey.Span)));
        var error = LicenseCandidateFactory.CreateError(LicenseCandidateSource.PackageRegistry, LicenseCandidateKind.Fetch, LicenseCandidateWarnings.PackageMetadataFetchFailed, evidence);
        return new PackageMetadataLookupResult(null, error, LookupOutcome.HasCandidate | LookupOutcome.Supported | LookupOutcome.CacheMiss | LookupOutcome.FetchError);
    }

    /// <summary>
    /// Records that no registry could be asked about this purl, naming which of the two reasons applies.
    /// </summary>
    /// <remarks>
    /// A purl that states no version is not the same outcome as an ecosystem Ol has no provider for, even though
    /// neither issues a request. The first is answered by fixing the input; the second only by Ol gaining a provider.
    /// Multi-module builds make the first common: a generator reading a child POM emits the module without the
    /// version its parent supplies.
    /// </remarks>
    private static PackageMetadataLookupResult CreateUnqueryablePurlResult(Utf8Slice purl, bool ecosystemSupported = false)
    {
        var candidate = new LicenseCandidate(
            LicenseCandidateSource.PackageRegistry,
            LicenseCandidateKind.Unsupported,
            purl,
            default,
            LicenseStatus.Unknown,
            false,
            ecosystemSupported
                ? LicenseCandidateWarnings.PackageMetadataUnversionedPurl
                : LicenseCandidateWarnings.UnsupportedPackageMetadata,
            new LicenseEvidence(LicenseEvidenceKind.PackageRegistry));

        // The summary keeps the distinction the warning makes. Counting an unversioned purl as an
        // unsupported ecosystem reports that Ol has no provider for an ecosystem it does support.
        return new PackageMetadataLookupResult(
            null,
            candidate,
            LookupOutcome.HasCandidate | LookupOutcome.Supported | (ecosystemSupported ? LookupOutcome.UnversionedPurl : LookupOutcome.Unsupported));
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

    /// <summary>Creates the candidate for a cached entry, whose values the cache already owns.</summary>
    private LicenseCandidate CreateMetadataCandidate(in PackageMetadataCacheEntry entry)
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageRegistry,
            PackageRegistry: new PackageRegistryEvidence(entry.CacheKeySha256, entry.FetchedAt));
        evidence = evidence with
        {
            DeclaredReference = entry.DeclaredLicenseReferenceKind == DeclaredLicenseReferenceKind.None
                ? null
                : new(entry.DeclaredLicenseReferenceKind, entry.DeclaredLicenseReference),
        };
        var candidate = CreateRegistryCandidate(entry.Source, entry.RawLicense, evidence);
        return candidate with { Warnings = candidate.Warnings | entry.Warnings };
    }

    /// <summary>Classifies a registry license and, failing that, the location the registry declared.</summary>
    private LicenseCandidate CreateRegistryCandidate(LicenseCandidateSource source, Utf8Slice raw, LicenseEvidence evidence)
        => LicenseCandidateFactory.ResolveDeclaredLocation(CreateRegistryLicenseCandidate(source, raw, evidence), spdxLicenseIndex);

    private LicenseCandidate CreateMetadataCandidate(PackageMetadataRecord record)
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageRegistry,
            PackageRegistry: new PackageRegistryEvidence(record.CacheKeySha256, record.FetchedAt));
        evidence = evidence with
        {
            DeclaredReference = record.DeclaredLicenseReferenceKind == DeclaredLicenseReferenceKind.None
                ? null
                : new(record.DeclaredLicenseReferenceKind, Utf8Slice.FromString(record.DeclaredLicenseReference)),
        };
        var candidate = CreateRegistryCandidate(GetCandidateSource(record.Source), Utf8Slice.FromString(record.RawLicense), evidence);
        return candidate with { Warnings = candidate.Warnings | LicenseCandidateIdentifiers.ParseWarnings(record.Warnings) };
    }

    /// <summary>Classifies a registry license, reading each registry's own spelling of one.</summary>
    /// <remarks>
    /// Applied here rather than at each registry boundary so the cache keeps what the registry published.
    /// A cached entry is then still classified with the active SPDX data, so neither Cargo's pre-SPDX
    /// choice spelling nor a deps.dev listing freezes a conclusion into the cache.
    /// </remarks>
    private LicenseCandidate CreateRegistryLicenseCandidate(LicenseCandidateSource source, Utf8Slice raw, LicenseEvidence evidence)
        => source switch
        {
            LicenseCandidateSource.CargoRegistry when CargoLicenseExpression.TryRewriteLegacyChoice(raw.Span, out var choice)
                => LicenseCandidateFactory.CreateRewritten(source, LicenseCandidateKind.License, raw, choice, spdxLicenseIndex, evidence),
            LicenseCandidateSource.DepsDev
                => LicenseCandidateFactory.CreateLicenseSet(source, raw, spdxLicenseIndex, evidence),
            _ => LicenseCandidateFactory.Create(source, LicenseCandidateKind.License, raw, spdxLicenseIndex, evidence),
        };

    private static LicenseCandidateSource GetCandidateSource(string source) => source switch
    {
        "npm-registry" => LicenseCandidateSource.NpmRegistry,
        "nuget-registry" => LicenseCandidateSource.NuGetRegistry,
        "cargo-registry" => LicenseCandidateSource.CargoRegistry,
        "go-module-proxy" => LicenseCandidateSource.GoModuleProxy,
        "deps.dev" => LicenseCandidateSource.DepsDev,
        _ => LicenseCandidateSource.PackageRegistry,
    };

    private readonly record struct PackageMetadataLookup(int Index, PackageMetadataRequest Request);

    /// <summary>What one planned lookup did, as the projection and the summary counters read it.</summary>
    /// <remarks>
    /// One field rather than one bool per outcome. The outcomes are not independent — a lookup is a cache
    /// hit or a cache miss, and an unqueryable purl is an unsupported ecosystem or an unversioned purl but
    /// never both — so a positional bool list could state a combination that cannot occur, and stated the
    /// real ones as a row of bare literals that named nothing at the call site.
    /// </remarks>
    [Flags]
    private enum LookupOutcome : byte
    {
        /// <summary>Nothing was looked up and no candidate was produced: no purl, or collection the user excluded.</summary>
        None = 0,
        HasCandidate = 1 << 0,
        Supported = 1 << 1,
        CacheHit = 1 << 2,
        CacheMiss = 1 << 3,
        Refreshed = 1 << 4,
        FetchError = 1 << 5,
        Unsupported = 1 << 6,
        UnversionedPurl = 1 << 7,
    }

    private readonly record struct PackageMetadataLookupResult(
        PackageMetadataResolution? Resolution,
        LicenseCandidate Candidate,
        LookupOutcome Outcome)
    {
        public bool Has(LookupOutcome outcome) => (Outcome & outcome) != 0;
    }
}
