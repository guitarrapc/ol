using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.SourceRepository;
using Ol.Core.Spdx;

/// <summary>Measures deterministic projection of one cached source result to duplicate package components.</summary>
public class SourceRepositoryEnrichmentBenchmark : IDisposable
{
    private const int ComponentCount = 64;
    private readonly ScanComponent[] components;
    private readonly string root;
    private readonly SourceRepositoryService service;

    public SourceRepositoryEnrichmentBenchmark()
    {
        root = Path.Combine(Path.GetTempPath(), $"ol-source-benchmark-{Guid.NewGuid():N}");
        var metadataCache = new PackageMetadataCache(Path.Combine(root, "package"));
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        metadataCache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], [])).GetAwaiter().GetResult();
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        sourceCache.WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.OK, new GitHubLicenseResult("MIT", "mit", "MIT License", "LICENSE", "sha", string.Empty), [], [])).GetAwaiter().GetResult();
        var index = new SpdxLicenseIndex(["MIT"], []);
        var component = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create("sbom", "id", "NOASSERTION"u8, index), [], []);
        components = new ScanComponent[ComponentCount];
        Array.Fill(components, component);
        service = new SourceRepositoryService(index, metadataCache, sourceCache, refresh: false, retryCount: 0);
    }

    [Benchmark]
    public int EnrichDuplicateCachedTarget()
    {
        var result = service.EnrichAsync((ScanComponent[])components.Clone(), concurrency: 4).GetAwaiter().GetResult();
        return result.Summary.CacheHitCount;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
