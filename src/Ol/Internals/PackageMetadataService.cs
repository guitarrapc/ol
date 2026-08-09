using System.Buffers;
using System.Net;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.PackageMetadata;
using Ol.Core.SourceRepository;
using Ol.Core.Spdx;

namespace Ol.Internals;

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
        PackageMetadataWorkspace workspace,
        int concurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Length < components.Length)
        {
            throw new ArgumentException("Package metadata workspace must correspond to every component.", nameof(workspace));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (components.Length == 0)
        {
            return ValueTask.FromResult((
                Components: components,
                Summary: new PackageMetadataSummary(0, 0, 0, 0, 0, 0, concurrency, retryCount)));
        }

        return components.Length == 1
            ? EnrichSingleComponent(components, workspace, concurrency, cancellationToken)
            : EnrichCoreAsync(components, workspace, concurrency, cancellationToken);
    }

    private ValueTask<(ScanComponent[] Components, PackageMetadataSummary Summary)> EnrichSingleComponent(
        ScanComponent[] components,
        PackageMetadataWorkspace workspace,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var purl = components[0].Purl;
        if (purl.IsEmpty)
        {
            return ValueTask.FromResult(ApplySingleLookup(components, workspace, default, concurrency, lookupCount: 0));
        }

        if (uncollectedPackages is not null && uncollectedPackages.Contains(purl))
        {
            components[0] = LicenseReconciler.AddCandidate(components[0], NotCollectedCandidate);
            return ValueTask.FromResult(ApplySingleLookup(components, workspace, default, concurrency, lookupCount: 0));
        }

        if (!OlDefaults.TryCreatePackageMetadataRequest(purl.ToString(), out var request, out var ecosystemSupported))
        {
            return ValueTask.FromResult(ApplySingleLookup(components, workspace, CreateUnqueryablePurlResult(purl, ecosystemSupported), concurrency, lookupCount: 0));
        }

        if (!refresh)
        {
            using (var entry = cache.TryRead(request.CacheKey))
            {
                if (entry.IsHit && !IsStaleUnresolvedEntry(entry))
                {
                    return ValueTask.FromResult(ApplySingleLookup(components, workspace, CreateCacheHit(request, entry), concurrency, lookupCount: 1));
                }
            }
        }

        return FetchSingleLookupAsync(components, workspace, request, concurrency, cancellationToken);
    }

    private async ValueTask<(ScanComponent[] Components, PackageMetadataSummary Summary)> FetchSingleLookupAsync(
        ScanComponent[] components,
        PackageMetadataWorkspace workspace,
        PackageMetadataRequest request,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var result = await FetchLookupAsync(request, cancellationToken).ConfigureAwait(false);
        return ApplySingleLookup(components, workspace, result, concurrency, lookupCount: 1);
    }

    private (ScanComponent[] Components, PackageMetadataSummary Summary) ApplySingleLookup(
        ScanComponent[] components,
        PackageMetadataWorkspace workspace,
        in PackageMetadataLookupResult result,
        int concurrency,
        int lookupCount)
    {
        workspace.Records[0] = result.Resolution;
        components[0] = result.HasCandidate ? LicenseReconciler.AddCandidate(components[0], result.Candidate) : components[0];
        return (
            components,
            new PackageMetadataSummary(
                result.Supported ? 1 : 0,
                result.CacheHit ? 1 : 0,
                result.CacheMiss ? 1 : 0,
                result.Refreshed ? 1 : 0,
                result.FetchError ? 1 : 0,
                result.Unsupported ? 1 : 0,
                concurrency,
                retryCount,
                lookupCount));
    }

    private async ValueTask<(ScanComponent[] Components, PackageMetadataSummary Summary)> EnrichCoreAsync(
        ScanComponent[] components,
        PackageMetadataWorkspace workspace,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var initialLookupCapacity = Math.Clamp(components.Length, 1, 16);
        var useLinearPlanning = components.Length <= LinearPlanningComponentLimit;
        var lookupByCacheKey = useLinearPlanning ? null : new Dictionary<string, int>(initialLookupCapacity, StringComparer.Ordinal);
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
                    componentLookupIndexes[i] = -1;
                    continue;
                }

                if (uncollectedPackages is not null && uncollectedPackages.Contains(purl))
                {
                    components[i] = LicenseReconciler.AddCandidate(component, NotCollectedCandidate);
                    componentLookupIndexes[i] = -1;
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

                if (!OlDefaults.TryCreatePackageMetadataRequest(purl.ToString(), out var request, out var ecosystemSupported))
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
                        if (string.Equals(lookups[existingLookupIndex].Request.CacheKey, request.CacheKey, StringComparison.Ordinal))
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

            return (components, ProjectLookups(components, workspace, componentLookupIndexes, lookupResults, concurrency, lookupCount));
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

    /// <summary>Writes every lookup result back in component order. Synchronous so the workspace records cannot span an await.</summary>
    private PackageMetadataSummary ProjectLookups(
        ScanComponent[] components,
        PackageMetadataWorkspace workspace,
        ReadOnlySpan<int> componentLookupIndexes,
        ReadOnlySpan<PackageMetadataLookupResult> lookupResults,
        int concurrency,
        int lookupCount)
    {
        var records = workspace.Records;
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
                : components[i].Purl.IsEmpty ? default : CreateUnqueryablePurlResult(components[i].Purl, lookupIndex == UnversionedPurlIndex);
            records[i] = result.Resolution;
            components[i] = result.HasCandidate ? LicenseReconciler.AddCandidate(components[i], result.Candidate) : components[i];
            supported += result.Supported ? 1 : 0;
            hits += result.CacheHit ? 1 : 0;
            misses += result.CacheMiss ? 1 : 0;
            refreshed += result.Refreshed ? 1 : 0;
            errors += result.FetchError ? 1 : 0;
            unsupported += result.Unsupported ? 1 : 0;
        }

        return new PackageMetadataSummary(supported, hits, misses, refreshed, errors, unsupported, concurrency, retryCount, lookupCount);
    }

    private async Task<PackageMetadataLookupResult> EnrichLookupAsync(PackageMetadataRequest request, CancellationToken cancellationToken)
    {
        if (!refresh)
        {
            using (var entry = await cache.TryReadAsync(request.CacheKey, cancellationToken).ConfigureAwait(false))
            {
                if (entry.IsHit && !IsStaleUnresolvedEntry(entry))
                {
                    return CreateCacheHit(request, entry);
                }
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
        && entry.Source.Span.SequenceEqual("npm-registry"u8);

    /// <summary>Reads the subdirectory fact back out of the warnings a registry answer persisted.</summary>
    /// <remarks>
    /// The warning is the persisted form of the fact, so a cache hit and a fresh fetch reach the source
    /// stage carrying the same answer without a second cache field to validate and version.
    /// </remarks>
    private static bool HasSubdirectoryWarning(LicenseCandidateWarnings warnings)
        => (warnings & LicenseCandidateWarnings.SourceRepositorySubdirectory) != 0;

    /// <summary>Projects a cache entry before its pooled buffer is returned.</summary>
    private PackageMetadataLookupResult CreateCacheHit(PackageMetadataRequest request, in PackageMetadataCacheEntry entry)
        => new(
            new PackageMetadataResolution(request.CacheKey, entry.RepositoryUrl, entry.RepositoryRef, HasSubdirectoryWarning(LicenseCandidateIdentifiers.ParseWarnings(entry.Warnings.Span))),
            CreateMetadataCandidate(entry),
            true,
            true,
            false,
            false,
            false,
            false);

    private async Task<PackageMetadataLookupResult> FetchLookupAsync(PackageMetadataRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var record = await PackageMetadataFetchScheduler.FetchAsync(registryClient, request, retryCount, cancellationToken).ConfigureAwait(false);
            await cache.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            var resolution = new PackageMetadataResolution(record.CacheKey, record.RepositoryUrl, record.RepositoryRef, HasSubdirectoryWarning(LicenseCandidateIdentifiers.ParseWarnings(record.Warnings)));
            return new PackageMetadataLookupResult(resolution, CreateMetadataCandidate(record), true, false, true, refresh, false, false);
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
            PackageRegistry: new PackageRegistryEvidence(PackageMetadataCache.GetCacheKeySha256(request.CacheKey)));
        var candidate = new LicenseCandidate(
            LicenseCandidateSource.PackageRegistry,
            LicenseCandidateKind.Fetch,
            default,
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.PackageMetadataNotFound,
            evidence);
        return new PackageMetadataLookupResult(null, candidate, true, false, true, false, false, false);
    }

    private static PackageMetadataLookupResult CreateFetchError(PackageMetadataRequest request)
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageRegistry,
            PackageRegistry: new PackageRegistryEvidence(PackageMetadataCache.GetCacheKeySha256(request.CacheKey)));
        var error = LicenseCandidateFactory.CreateError(LicenseCandidateSource.PackageRegistry, LicenseCandidateKind.Fetch, LicenseCandidateWarnings.PackageMetadataFetchFailed, evidence);
        return new PackageMetadataLookupResult(null, error, true, false, true, false, true, false);
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
        return new PackageMetadataLookupResult(null, candidate, true, false, false, false, false, true);
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

    /// <summary>Creates the candidate for a cached entry without decoding its UTF-8 values.</summary>
    private LicenseCandidate CreateMetadataCandidate(in PackageMetadataCacheEntry entry)
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageRegistry,
            PackageRegistry: new PackageRegistryEvidence(entry.CacheKeySha256, entry.FetchedAt));
        evidence = evidence with { DeclaredReference = CreateDeclaredReference(entry.DeclaredLicenseReferenceKind, entry.DeclaredLicenseReference.Span) };
        var candidate = CreateRegistryCandidate(GetCandidateSource(entry.Source.Span), Utf8Slice.FromOwnedBytes(entry.RawLicense.Span.ToArray()), evidence);
        return candidate with { Warnings = candidate.Warnings | LicenseCandidateIdentifiers.ParseWarnings(entry.Warnings.Span) };
    }

    /// <summary>Copies a declared location out of storage that the caller is about to release.</summary>
    /// <remarks>
    /// A cache entry's UTF-8 values point into a pooled buffer returned when the entry is disposed, and
    /// the candidate outlives it. Only an entry that declares a location pays for the copy.
    /// </remarks>
    private static DeclaredLicenseReference? CreateDeclaredReference(DeclaredLicenseReferenceKind kind, ReadOnlySpan<byte> value)
        => kind == DeclaredLicenseReferenceKind.None ? null : new(kind, Utf8Slice.FromOwnedBytes(value.ToArray()));

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

    /// <summary>Classifies a registry license, reading Cargo's pre-SPDX spelling as the expression it denotes.</summary>
    /// <remarks>
    /// Applied here rather than at the crates.io boundary so the cache keeps the license crates.io
    /// published. A cached entry is therefore still classified with the active SPDX data, and the
    /// rewrite cannot become a conclusion frozen into the cache by an older SPDX snapshot.
    /// </remarks>
    private LicenseCandidate CreateRegistryCandidate(LicenseCandidateSource source, Utf8Slice raw, LicenseEvidence evidence)
        => source == LicenseCandidateSource.CargoRegistry && CargoLicenseExpression.TryRewriteLegacyChoice(raw.Span, out var choice)
            ? LicenseCandidateFactory.CreateRewritten(source, LicenseCandidateKind.License, raw, choice, spdxLicenseIndex, evidence)
            : LicenseCandidateFactory.Create(source, LicenseCandidateKind.License, raw, spdxLicenseIndex, evidence);

    private static LicenseCandidateSource GetCandidateSource(string source) => source switch
    {
        "npm-registry" => LicenseCandidateSource.NpmRegistry,
        "nuget-registry" => LicenseCandidateSource.NuGetRegistry,
        "cargo-registry" => LicenseCandidateSource.CargoRegistry,
        "go-module-proxy" => LicenseCandidateSource.GoModuleProxy,
        _ => LicenseCandidateSource.PackageRegistry,
    };

    private static LicenseCandidateSource GetCandidateSource(ReadOnlySpan<byte> source)
    {
        if (source.SequenceEqual("npm-registry"u8)) return LicenseCandidateSource.NpmRegistry;
        if (source.SequenceEqual("nuget-registry"u8)) return LicenseCandidateSource.NuGetRegistry;
        if (source.SequenceEqual("cargo-registry"u8)) return LicenseCandidateSource.CargoRegistry;
        if (source.SequenceEqual("go-module-proxy"u8)) return LicenseCandidateSource.GoModuleProxy;
        return LicenseCandidateSource.PackageRegistry;
    }

    private readonly record struct PackageMetadataLookup(int Index, PackageMetadataRequest Request);

    private readonly record struct PackageMetadataLookupResult(PackageMetadataResolution? Resolution, LicenseCandidate Candidate, bool HasCandidate, bool Supported, bool CacheHit, bool CacheMiss, bool Refreshed, bool FetchError, bool Unsupported)
    {
        public PackageMetadataLookupResult(PackageMetadataResolution? resolution, LicenseCandidate candidate, bool supported, bool cacheHit, bool cacheMiss, bool refreshed, bool fetchError, bool unsupported)
            : this(resolution, candidate, true, supported, cacheHit, cacheMiss, refreshed, fetchError, unsupported)
        {
        }
    }
}
