using BenchmarkDotNet.Attributes;

/// <summary>Measures the <c>ol scan</c> command workflow end to end, including file I/O, scanning, cached enrichment, sorting, and rendering.</summary>
public class E2EBenchmark : IDisposable
{
    private readonly string cacheRoot;
    private readonly string? previousCacheRoot;
    private readonly ScanCommands scanCommands = new();
    private readonly string nugetAssetsPath;
    private readonly string sbomPath;
    private readonly string workingDirectory;

    public E2EBenchmark()
    {
        workingDirectory = Path.Combine(Path.GetTempPath(), $"ol-cli-benchmark-{Guid.NewGuid():N}");
        cacheRoot = Path.Combine(workingDirectory, "package-metadata");
        sbomPath = Path.Combine(workingDirectory, "bom.json");
        nugetAssetsPath = Path.Combine(workingDirectory, "project.assets.json");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(
            sbomPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                { "bom-ref": "pkg:npm/example@1.0.0", "name": "example", "version": "1.0.0", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] }
              ]
            }
            """);
        File.WriteAllText(
            nugetAssetsPath,
            """
            {
              "version": 3,
              "targets": {
                "net8.0": {
                  "Direct.Package/1.0.0": { "type": "package", "dependencies": { "Shared.Package": "2.0.0" } },
                  "Shared.Package/2.0.0": { "type": "package" }
                }
              },
              "libraries": {
                "Direct.Package/1.0.0": { "type": "package" },
                "Shared.Package/2.0.0": { "type": "package" }
              },
              "project": {
                "version": "1.0.0",
                "restore": { "projectName": "App", "projectPath": "src/App/App.csproj" },
                "frameworks": { "net8.0": { "dependencies": { "Direct.Package": { "target": "Package" } } } }
              }
            }
            """);
        var cache = new PackageMetadataCache(cacheRoot);
        cache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []))
            .GetAwaiter()
            .GetResult();
        cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Direct.Package@1.0.0", "nuget-registry", "MIT", string.Empty, [], []))
            .GetAwaiter()
            .GetResult();
        cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Shared.Package@2.0.0", "nuget-registry", "MIT", string.Empty, [], []))
            .GetAwaiter()
            .GetResult();

        previousCacheRoot = Environment.GetEnvironmentVariable("OL_PACKAGE_METADATA_CACHE_ROOT");
        Environment.SetEnvironmentVariable("OL_PACKAGE_METADATA_CACHE_ROOT", cacheRoot);
    }

    [Benchmark]
    public int ScanTextWithCachedMetadata() => Run(ReportFormat.Text);

    [Benchmark]
    public int ScanJsonWithCachedMetadata() => Run(ReportFormat.Json);

    [Benchmark]
    public int ScanNuGetTextWithCachedMetadata() => RunNuGet(ReportFormat.Text);

    [Benchmark]
    public int ScanNuGetJsonWithCachedMetadata() => RunNuGet(ReportFormat.Json);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OL_PACKAGE_METADATA_CACHE_ROOT", previousCacheRoot);
        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private int Run(ReportFormat format)
    {
        var standardOut = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            return scanCommands.Scan(sbom: sbomPath, format: format, quiet: true, concurrency: 1, retry: 0);
        }
        finally
        {
            Console.SetOut(standardOut);
        }
    }

    private int RunNuGet(ReportFormat format)
    {
        var standardOut = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            return scanCommands.Scan(input: [nugetAssetsPath], inputFormat: "nuget-assets", format: format, quiet: true, concurrency: 1, retry: 0);
        }
        finally
        {
            Console.SetOut(standardOut);
        }
    }
}
