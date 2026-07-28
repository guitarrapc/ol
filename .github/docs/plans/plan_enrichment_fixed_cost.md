# Enrichment small-count fixed-cost allocation reduction

Date: 2026-07-28  
Status: Completed

## Objective

Reduce the avoidable fixed-cost allocations in package metadata and source repository enrichment for zero- and small-count inputs.

The target is:

- Avoid `Parallel.ForEachAsync` / `Parallel.ForAsync`, dictionaries, and async state machines when they are unnecessary.
- Return a synchronously completed `ValueTask` for zero-work and synchronous cache-hit paths.
- Keep required result-owned allocations distinct from scheduler, lookup, and state-machine overhead.
- Preserve the existing design and ownership boundary. Pooled arrays must not escape through the public API.

## Result

All measurements come from `EnrichmentFixedCostBenchmark` with `--warmupCount 3 --iterationCount 5`. The baseline column is the previous commit measured on the same machine in the same session.

| Case | Baseline mean | Final mean | Baseline allocated | Final allocated |
| --- | ---: | ---: | ---: | ---: |
| Package empty | — | 1.061 ns | 0 B | 0 B |
| Package one cached | 92,206 ns | 58,115 ns | 9,654 B | 7,792 B |
| Source empty | 7.198 ns | 1.463 ns | 0 B | 0 B |
| Source one cached | 66,331 ns | 30,953 ns | 7,076 B | 4,728 B |
| Source one unavailable | 71.308 ns | 76.258 ns | 136 B | 136 B |

Supporting benchmarks:

| Benchmark | Baseline | Final |
| --- | --- | --- |
| `E2EBenchmark.ScanTextWithCachedMetadata` | 197.7 us / 12.71 KB | 150.5 us / 10.91 KB |
| `SourceRepositoryEnrichmentBenchmark.EnrichDuplicateCachedTarget` | 86.35 us / 67.57 KB | 84.25 us / 67.56 KB |

The multi-target enrichment benchmark is unchanged, which is expected because the multi-component path was not modified.

`Source one unavailable` is `+6.9%`, within the accepted threshold. The remaining `136 B` there is result-owned candidate data, not dictionary, parallel scheduling, or async state-machine overhead. Eliminating it would require changing result ownership or representation and is outside this optimization.

## Implemented changes

### Package metadata enrichment

- `EnrichAsync` is a non-async wrapper returning `ValueTask`.
- Zero components complete synchronously without allocation.
- One component is handled by `EnrichSingleComponent`, which resolves synchronously for an empty PURL, an unsupported PURL, and a cache hit.
- A single-component cache miss enters `FetchSingleLookupAsync`, which fetches without reading the cache again.
- Inputs from two to eight components use linear duplicate detection instead of `Dictionary<string, int>` and `Dictionary<Utf8Slice, int>`.
- Multiple lookup targets continue to use `Parallel.ForEachAsync`.
- Cancellation is checked before a synchronous return.

### Source repository enrichment

- `EnrichAsync` is a non-async wrapper returning `ValueTask`.
- Zero components complete synchronously without allocation.
- One component is handled by `EnrichSingleComponent` and `EnrichSingleTarget`, which resolve synchronously for a missing repository URL, an unsupported repository URL, and a cache hit.
- A single-target cache miss or invalid entry enters `FetchSingleTargetAsync` with the already-classified `cacheWasInvalid` flag, so the cache is not read again.
- Inputs from two to eight components use linear target matching instead of a dictionary.
- Multiple targets continue to use `Parallel.ForAsync`.
- Cancellation is checked before a synchronous return.

### Synchronous cache reads

- `PackageMetadataCache.TryRead` mirrors `TryReadAsync`, including the missing-file, invalid-JSON, and version-1 validation behavior.
- `SourceRepositoryCache.Read` mirrors `ReadAsync`, including the `Missing` / `Hit` / `Invalid` classification and the exception ordering that keeps `FileNotFoundException` and `DirectoryNotFoundException` distinct from other `IOException` cases.

### Test coverage

- `PackageMetadataTests.Cache_TryRead_ValidEntry_MatchesAsyncHit`
- `PackageMetadataTests.Cache_TryRead_MissingCorruptAndInvalidEntries_MatchAsyncMiss`
- `PackageMetadataTests.Cache_TryRead_MissingCacheRoot_ReportsMissWithoutThrowing`
- `PackageMetadataTests.Enrichment_SingleComponentWithCachedMetadata_ReportsCacheHitWithoutFetching`
- `PackageMetadataTests.Enrichment_SingleComponentWithoutSupportedPurl_ReportsUnsupportedWithoutTarget`
- `SourceRepositoryTests.Cache_Read_ValidEntry_MatchesAsyncHit`
- `SourceRepositoryTests.Cache_Read_MissingCorruptAndIncompatibleEntries_MatchAsyncStatus`
- `SourceRepositoryTests.Cache_Read_MissingCacheRoot_ReportsMissingWithoutThrowing`

The existing single-component source enrichment tests already cover the cache hit, refresh, invalid-cache fallback, and cache-write-failure paths, so they now exercise the new fast path directly.

### Benchmark coverage

`EnrichmentFixedCostBenchmark` covers `PackageEmpty`, `PackageOneCached`, `SourceEmpty`, `SourceOneCached`, and `SourceOneUnavailable`.

## Important implementation notes

- Keep `EnrichAsync` itself small. When the single-component fast path was inlined into the wrapper, the empty-input cases regressed from single-digit nanoseconds to `12.6 ns` and `17.0 ns` because the wrapper stopped being inlined. Moving each early-return group into its own method restored `1.06 ns` and `1.46 ns`. The same effect appeared for `SourceOneUnavailable`, which is why the unavailable and unsupported returns live in separate methods.
- The linear-planning threshold is eight components. It remains an assumption, but a boundary benchmark is not worth adding: a cached lookup costs tens of microseconds of file I/O, so the difference between a linear scan and a dictionary probe over at most eight entries is several orders of magnitude below the surrounding cost.
- A failed dictionary `TryGetValue` resets its `out int` value to zero. The large-input paths must explicitly restore the sentinel value to `-1`.
- Package planning requires a separate `purlPlanned` flag because `-1` is also a valid planned result for an unsupported PURL.
- Synchronous file I/O is restricted to the one-target fast path. Multi-target work and network fallback retain bounded asynchronous concurrency.
- A synchronous cache miss or invalid entry must not cause the cache file to be read again in the asynchronous fallback. This is why `EnrichLookupAsync` and `EnrichTargetAsync` delegate to `FetchLookupAsync` and `FetchTargetAsync`, which the fast path calls directly.
- Sync and async cache reads preserve identical hit, missing, invalid, and validation behavior.

## Verification completed

- `dotnet build Ol.slnx -c Release --no-restore`: 0 warnings, 0 errors.
- `dotnet test Ol.slnx -c Release --no-restore`: 262 passed, 0 failed.
- `dotnet format Ol.slnx --verify-no-changes --no-restore`: clean. This also fixed the missing UTF-8 BOM in `EnrichmentFixedCostBenchmark.cs` and a stale using directive in `PackageMetadataTests.cs`, both introduced earlier in this work.
- `git diff --check`: clean.
- Fixed-cost, E2E, and source enrichment benchmarks produced the results above.

## Completion criteria

- Package and source empty paths remain `0 B`. Met.
- `SourceOneUnavailable` remains free of avoidable fixed overhead; its allocation is the required result data only. Met.
- One cached package and one cached source avoid dictionaries, parallel scheduling, and async state-machine allocation on a cache hit. Met.
- Sync and async cache behavior is covered by parity tests. Met.
- Focused tests, full tests, Release build, and E2E benchmark pass. Met.
- No unexplained regression greater than 10% remains in a relevant benchmark. Met.
