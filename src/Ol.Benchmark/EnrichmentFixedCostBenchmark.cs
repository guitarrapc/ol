using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;
using Ol.Internals;

[MemoryDiagnoser]
public class EnrichmentFixedCostBenchmark : IDisposable
{
    private readonly ScanComponent[] emptyComponents = [];
    private readonly PackageMetadataResolution?[] emptyResolutions = new PackageMetadataResolution?[0];
    private readonly ScanComponent[] packageComponents = new ScanComponent[1];
    private readonly PackageMetadataResolution?[] packageResolutions = new PackageMetadataResolution?[1];
    private readonly PackageMetadataService packageService;
    private readonly ScanComponent packageTemplate;
    private readonly string root;
    private readonly ScanComponent[] sourceCachedComponents = new ScanComponent[1];
    private readonly PackageMetadataResolution?[] sourceCachedResolutions = new PackageMetadataResolution?[1];
    private readonly ScanComponent sourceCachedTemplate;
    private readonly ScanComponent[] sourceComponents = new ScanComponent[1];
    private readonly PackageMetadataResolution?[] sourceResolutions = new PackageMetadataResolution?[1];
    private readonly SourceRepositoryService sourceService;
    private readonly ScanComponent sourceTemplate;

    public EnrichmentFixedCostBenchmark()
    {
        root = Path.Combine(Path.GetTempPath(), $"ol-enrichment-fixed-{Guid.NewGuid():N}");
        var index = new SpdxLicenseIndex(["MIT"], []);
        var packageCache = new PackageMetadataCache(Path.Combine(root, "package"));
        packageCache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], [])).GetAwaiter().GetResult();
        packageService = new PackageMetadataService(index, packageCache, refresh: false, retryCount: 0);
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        var sourceTarget = new SourceRepositoryTarget("owner", "repository", "default");
        sourceCache.WriteAsync(new SourceRepositoryRecord(sourceTarget.CacheKey, "github-license-api", "none", sourceTarget.Repository, sourceTarget.Ref, System.Net.HttpStatusCode.OK, new GitHubLicenseResult("MIT", "mit", "MIT License", "LICENSE", "sha", string.Empty), [], [])).GetAwaiter().GetResult();
        sourceService = new SourceRepositoryService(index, sourceCache, refresh: false, retryCount: 0);
        packageTemplate = CreateComponent(index, "pkg:npm/example@1.0.0");
        sourceCachedTemplate = CreateComponent(index, "pkg:npm/source-cached@1.0.0");
        sourceCachedResolutions[0] = new PackageMetadataResolution("pkg:npm/source-cached@1.0.0", "https://github.com/owner/repository", string.Empty);
        sourceTemplate = CreateComponent(index, "pkg:npm/source@1.0.0");
    }

    [Benchmark]
    public int PackageEmpty()
        => packageService.EnrichAsync(emptyComponents, emptyResolutions, concurrency: 1).GetAwaiter().GetResult().Summary.TargetCount;

    [Benchmark]
    public int PackageOneCached()
    {
        packageComponents[0] = packageTemplate;
        return packageService.EnrichAsync(packageComponents, packageResolutions, concurrency: 1).GetAwaiter().GetResult().Summary.CacheHitCount;
    }

    [Benchmark]
    public int SourceEmpty()
        => sourceService.EnrichAsync(emptyComponents, emptyResolutions, concurrency: 1).GetAwaiter().GetResult().Summary.TargetCount;

    [Benchmark]
    public int SourceOneCached()
    {
        sourceCachedComponents[0] = sourceCachedTemplate;
        return sourceService.EnrichAsync(sourceCachedComponents, sourceCachedResolutions, concurrency: 1).GetAwaiter().GetResult().Summary.CacheHitCount;
    }

    [Benchmark]
    public int SourceOneUnavailable()
    {
        sourceComponents[0] = sourceTemplate;
        sourceResolutions[0] = null;
        return sourceService.EnrichAsync(sourceComponents, sourceResolutions, concurrency: 1).GetAwaiter().GetResult().Summary.UnknownCount;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static ScanComponent CreateComponent(SpdxLicenseIndex index, string purl)
        => new("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, purl, default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), []);
}
