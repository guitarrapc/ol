# Enrichment small-count fixed-cost allocation reduction

Date: 2026-07-28  
Status: Paused

## Objective

Reduce the avoidable fixed-cost allocations in package metadata and source repository enrichment for zero- and small-count inputs.

The target is:

- Avoid `Parallel.ForEachAsync` / `Parallel.ForAsync`, dictionaries, and async state machines when they are unnecessary.
- Return a synchronously completed `ValueTask` for zero-work and synchronous cache-hit paths.
- Keep required result-owned allocations distinct from scheduler, lookup, and state-machine overhead.
- Preserve the existing design and ownership boundary. Pooled arrays must not escape through the public API.

## Current result

The empty paths now reach `0 B`.

| Case | Before | Intermediate | Current |
| --- | ---: | ---: | ---: |
| Package empty | 1,120 B | 32 B | 0 B |
| Source empty | 296 B | 40 B | 0 B |
| Source one unavailable | 568 B | 176 B | 136 B |
| Package one cached | 10,601 B | 9,542 B | Not remeasured after the latest wrapper change |
| Source one cached | Not measured | Not measured | Benchmark added, execution interrupted |

Current timings from the last completed fixed-cost benchmark:

| Case | Mean | Allocated |
| --- | ---: | ---: |
| Package empty | 1.836 ns | 0 B |
| Source empty | 13.523 ns | 0 B |
| Source one unavailable | 232.843 ns | 136 B |

The remaining `136 B` in `Source one unavailable` is result-owned candidate data, not dictionary, parallel scheduling, or async state-machine overhead. Eliminating it would require changing result ownership or representation and is outside the current optimization.

## Implemented changes

### Package metadata enrichment

- `EnrichAsync` now has a non-async public wrapper returning `ValueTask`.
- Zero components complete synchronously without allocation.
- Inputs up to eight components use linear duplicate detection instead of:
  - `Dictionary<string, int>`
  - `Dictionary<Utf8Slice, int>`
- Zero lookup targets skip lookup result allocation and parallel execution.
- One lookup target is awaited directly.
- Multiple lookup targets continue to use `Parallel.ForEachAsync`.
- Cancellation is checked before a synchronous return.

### Source repository enrichment

- `EnrichAsync` now has a non-async public wrapper returning `ValueTask`.
- Zero components complete synchronously without allocation.
- A single component without metadata or an SBOM repository is resolved synchronously.
- Inputs up to eight components use linear target matching instead of a dictionary.
- One target is awaited directly.
- Multiple targets continue to use `Parallel.ForAsync`.
- Cancellation is checked before a synchronous return.

### Benchmark coverage

`EnrichmentFixedCostBenchmark` currently covers:

- `PackageEmpty`
- `PackageOneCached`
- `SourceEmpty`
- `SourceOneCached`
- `SourceOneUnavailable`

`SourceOneCached` was added immediately before the pause. Its benchmark execution was interrupted and produced no usable result.

## Important implementation notes

- The linear-planning threshold is currently eight components. This is an assumption and still needs benchmark justification around the boundary, especially at 8 versus 9 components and for duplicate-heavy inputs.
- A failed dictionary `TryGetValue` resets its `out int` value to zero. The large-input paths must explicitly restore the sentinel value to `-1`.
- Package planning requires a separate `purlPlanned` flag because `-1` is also a valid planned result for an unsupported PURL.
- Synchronous file I/O should be restricted to the one-target cache-hit fast path. Multi-target work and network fallback must retain bounded asynchronous concurrency.
- A synchronous cache miss or invalid cache entry must not cause the cache file to be read again in the asynchronous fallback.
- Sync and async cache reads must preserve identical hit, missing, invalid, and validation behavior.

## Unfinished work

The principal remaining allocation source is the cache-hit path:

- asynchronous cache file reads
- async state machines
- the remaining one-target orchestration

Implement synchronous cache-reading entry points for the one-target fast path:

1. Add synchronous equivalents for:
   - `PackageMetadataCache.TryReadAsync`
   - `SourceRepositoryCache.ReadAsync`
2. First add parity tests covering hit, missing, and invalid entries.
3. Let each service wrapper detect one target and return a completed `ValueTask` on a synchronous cache hit.
4. Enter the asynchronous core only for cache misses, invalid entries requiring fallback, network work, or multiple targets.
5. Pass the already-classified cache result into the asynchronous fallback, or split out a fetch-after-cache-miss helper, so the cache is not read twice.

## Verification completed

- `dotnet build Ol.slnx -c Release --no-restore`
  - Passed twice after the small-count changes.
  - 0 warnings, 0 errors.
- Fixed-cost benchmarks produced the current results above.

The full test suite and focused enrichment tests have not been rerun after the latest small-count changes.

Earlier prerequisite work, before this optimization, had:

- 254 tests passing.
- Removed the package metadata cache reread from source enrichment.
- Localized pooled-array ownership in scan execution.
- Kept SBOM Text E2E allocation at approximately `14.03 KB`.
- Reduced focused source enrichment from approximately `95.74 KB / 966.4 us` to `67.92 KB / 474.9 us`.

Those earlier results do not replace verification of the current changes.

## Resume procedure

Measure the current source cache-hit baseline:

```powershell
dotnet run -c Release --project src/Ol.Benchmark/Ol.Benchmark.csproj --no-restore -- --filter "*SourceOneCached*" --warmupCount 3 --iterationCount 5
```

After implementing the synchronous cache-hit path, run the complete fixed-cost benchmark:

```powershell
dotnet run -c Release --project src/Ol.Benchmark/Ol.Benchmark.csproj --no-restore -- --filter "*EnrichmentFixedCostBenchmark*" --warmupCount 3 --iterationCount 5
```

Run focused tests:

```powershell
dotnet test --project tests/Ol.Tests/Ol.Tests.csproj -c Release --no-restore --treenode-filter "/*/*/PackageMetadataTests/*|/*/*/SourceRepositoryTests/*"
```

Run the full suite:

```powershell
dotnet test Ol.slnx -c Release --no-restore
```

Check the E2E allocation:

```powershell
dotnet run -c Release --project src/Ol.Benchmark/Ol.Benchmark.csproj --no-restore -- --filter "*E2EBenchmark.ScanTextWithCachedMetadata*" --warmupCount 3 --iterationCount 5
```

Finally:

```powershell
git diff --check
git diff -- src/Ol/Internals/PackageMetadataService.cs src/Ol/Internals/SourceRepositoryService.cs src/Ol.Benchmark/EnrichmentFixedCostBenchmark.cs
```

## Completion criteria

- Package and source empty paths remain `0 B`.
- `SourceOneUnavailable` remains free of avoidable fixed overhead; its expected allocation is the required result data only.
- One cached package and one cached source avoid dictionaries, parallel scheduling, and async state-machine allocation on a cache hit.
- Sync and async cache behavior is covered by parity tests.
- Focused tests, full tests, Release build, and E2E benchmark pass.
- No unexplained regression greater than 10% remains in a relevant benchmark.
