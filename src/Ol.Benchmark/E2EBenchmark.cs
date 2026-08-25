using BenchmarkDotNet.Attributes;

/// <summary>Measures the <c>ol scan</c> command workflow end to end, including file I/O, scanning, cached enrichment, sorting, and rendering.</summary>
public class E2EBenchmark : IDisposable
{
    private readonly string cacheRoot;
    private readonly string? previousCacheRoot;
    private readonly ScanCommands scanCommands = new(Stream.Null);
    private readonly string nugetAssetsPath;
    private readonly string sbomPath;
    private readonly string directoryInputPath;
    private readonly string excludedDirectoryPath;
    private readonly string workingDirectory;

    public E2EBenchmark()
    {
        workingDirectory = Path.Combine(Path.GetTempPath(), $"ol-cli-benchmark-{Guid.NewGuid():N}");
        cacheRoot = Path.Combine(workingDirectory, "package-metadata");
        sbomPath = Path.Combine(workingDirectory, "bom.json");
        nugetAssetsPath = Path.Combine(workingDirectory, "project.assets.json");
        directoryInputPath = Path.Combine(workingDirectory, "directory-input");
        excludedDirectoryPath = Path.Combine(directoryInputPath, "documents");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(Path.Combine(directoryInputPath, "server"));
        Directory.CreateDirectory(excludedDirectoryPath);
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
        const string packageLock =
            """
            {
              "name": "benchmark-app",
              "version": "1.0.0",
              "lockfileVersion": 3,
              "requires": true,
              "packages": {
                "": { "name": "benchmark-app", "version": "1.0.0", "dependencies": { "example": "1.0.0" } },
                "node_modules/example": { "version": "1.0.0", "license": "MIT" }
              }
            }
            """;
        File.WriteAllText(Path.Combine(directoryInputPath, "server", "package-lock.json"), packageLock);
        File.WriteAllText(Path.Combine(excludedDirectoryPath, "package-lock.json"), packageLock);
        CreateExcludedDirectoryNoise();
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

    // Keep an unfiltered directory scan beside the excluded scan. A single excluded case cannot tell whether
    // pruning paid for itself or merely added option handling overhead.
    [Benchmark(Baseline = true)]
    public int ScanDirectoryWithoutExcludedSubtree() => RunDirectory(ReportFormat.Json, excludeInputPath: false);

    [Benchmark]
    public int ScanDirectoryWithExcludedSubtree() => RunDirectory(ReportFormat.Json, excludeInputPath: true);

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
            return scanCommands.Scan(input: [sbomPath], format: format, quiet: true, concurrency: 1, retry: 0);
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

    private int RunDirectory(ReportFormat format, bool excludeInputPath)
    {
        var standardOut = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            return scanCommands.Scan(
                input: [directoryInputPath],
                excludeInputPath: excludeInputPath ? [excludedDirectoryPath] : null,
                format: format,
                quiet: true,
                noExternalEvidence: true,
                concurrency: 1,
                retry: 0);
        }
        finally
        {
            Console.SetOut(standardOut);
        }
    }

    private void CreateExcludedDirectoryNoise()
    {
        // A monorepo documentation tree is usually both deep and file-heavy. Keep it outside the timed section,
        // while making enough entries for directory enumeration and pruning to be visible in the measurement.
        const int branchCount = 8;
        const int depth = 3;
        const int filesPerDirectory = 32;
        for (var branch = 0; branch < branchCount; branch++)
        {
            var directory = Path.Combine(excludedDirectoryPath, $"site-{branch}");
            for (var level = 0; level < depth; level++)
            {
                Directory.CreateDirectory(directory);
                for (var file = 0; file < filesPerDirectory; file++)
                {
                    File.WriteAllText(Path.Combine(directory, $"generated-{file}.json"), "{}\n");
                }

                directory = Path.Combine(directory, $"section-{level}");
            }
        }
    }
}
