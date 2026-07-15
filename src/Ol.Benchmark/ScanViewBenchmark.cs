using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;

public class ScanViewBenchmark : IDisposable
{
    private const int ComponentCount = 1_024;
    private readonly ScanComponent[] metadataComponents;
    private readonly ScanComponent[] metadataTemplate;
    private readonly PackageMetadataService metadataService;
    private readonly string cacheRoot;
    private readonly ScanComponent[] sortComponents;
    private readonly ScanComponent[] sortTemplate;

    public ScanViewBenchmark()
    {
        sortTemplate = new ScanComponent[ComponentCount];
        metadataTemplate = new ScanComponent[ComponentCount];
        sortComponents = new ScanComponent[ComponentCount];
        metadataComponents = new ScanComponent[ComponentCount];
        for (var i = 0; i < ComponentCount; i++)
        {
            sortTemplate[i] = CreateComponent(
                $"package-{ComponentCount - i:D4}",
                i % 2 == 0 ? "npm" : "cargo",
                $"pkg:npm/package-{i:D4}@1.0.0");
            metadataTemplate[i] = CreateComponent($"package-{i:D4}", "npm", "pkg:npm/example@1.0.0");
        }

        cacheRoot = Path.Combine(Path.GetTempPath(), $"ol-benchmark-{Guid.NewGuid():N}");
        var cache = new PackageMetadataCache(cacheRoot);
        cache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], [])).GetAwaiter().GetResult();
        metadataService = new PackageMetadataService(new SpdxLicenseIndex(["MIT"], []), cache, refresh: false, retryCount: 0);
        ResetInputs();
    }

    public void ResetInputs()
    {
        sortTemplate.CopyTo(sortComponents, 0);
        metadataTemplate.CopyTo(metadataComponents, 0);
    }

    [Benchmark]
    public int SortWithCaseInsensitiveFields()
    {
        ResetInputs();
        return ScanView.Apply(sortComponents, dependency: null, "ECOSYSTEM,NAME,VERSION,LICENSE,PURL", SortOrder.Asc);
    }

    [Benchmark]
    public int EnrichDuplicatePurls()
    {
        ResetInputs();
        return metadataService.EnrichAsync(metadataComponents, concurrency: 1).GetAwaiter().GetResult().Summary.CacheHitCount;
    }

    public void Dispose()
    {
        if (Directory.Exists(cacheRoot))
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static ScanComponent CreateComponent(string name, string ecosystem, string purl)
        => new(name, "1.0.0", "-", ecosystem, DependencyType.Unknown, LicenseStatus.Unknown, purl, purl, default, [], []);
}
