using BenchmarkDotNet.Attributes;

// TEMPORARY: added to measure the P0 cache-read allocation work
// (see .github/docs/plans/plan_allocation_reduction.md).
// It isolates one cache entry read, which EnrichmentFixedCostBenchmark and E2EBenchmark
// only measure together with planning, candidate creation, and reconciliation.
// Once the P0 targets are met and held by those two benchmarks, delete this file.

/// <summary>Measures a single package/source cache entry read in isolation.</summary>
[MemoryDiagnoser]
public class CacheReadBenchmark : IDisposable
{
    private const string PackageCacheKey = "pkg:npm/example@1.0.0";
    private readonly PackageMetadataCache packageCache;
    private readonly string root;
    private readonly SourceRepositoryCache sourceCache;
    private readonly string sourceCacheKey;

    public CacheReadBenchmark()
    {
        root = Path.Combine(Path.GetTempPath(), $"ol-cache-read-{Guid.NewGuid():N}");
        packageCache = new PackageMetadataCache(Path.Combine(root, "package"));
        packageCache
            .WriteAsync(new PackageMetadataRecord(PackageCacheKey, "npm-registry", "MIT", "https://github.com/owner/repository", [], []))
            .GetAwaiter()
            .GetResult();

        sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        sourceCacheKey = target.CacheKey;
        sourceCache
            .WriteAsync(new SourceRepositoryRecord(sourceCacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.OK, new GitHubLicenseResult("MIT", "mit", "MIT License", "LICENSE", "sha", string.Empty), [], []))
            .GetAwaiter()
            .GetResult();
    }

    [Benchmark]
    public int PackageCacheHit()
    {
        using var entry = packageCache.TryRead(PackageCacheKey);
        return entry.RawLicense.Length;
    }

    [Benchmark]
    public int PackageCacheMiss()
    {
        using var entry = packageCache.TryRead("pkg:npm/absent@1.0.0");
        return entry.IsHit ? 1 : 0;
    }

    [Benchmark]
    public int SourceCacheHit()
    {
        var read = sourceCache.Read(sourceCacheKey);
        return read.Record!.Value.Repository.Length;
    }

    [Benchmark]
    public int SourceCacheMiss() => (int)sourceCache.Read("github:owner/absent@default").Status;

    [Benchmark]
    public int PackageCachePath() => packageCache.GetPath(PackageCacheKey).Length;

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
