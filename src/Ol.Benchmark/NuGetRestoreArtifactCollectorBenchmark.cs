using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.Spdx;
using System.Text;

[MemoryDiagnoser]
public class NuGetRestoreArtifactCollectorBenchmark : IDisposable
{
    private readonly string assetsPath;
    private readonly ScanComponent[] components = new ScanComponent[1];
    private readonly ScanComponent[] emptyComponents = [];
    private readonly SpdxLicenseIndex index = new(["MIT"], []);
    private readonly SpdxLicenseTextMatcher matcher = new("benchmark", [new("MIT", "MIT License")]);
    private readonly string missingAssetsPath;
    private readonly ScanComponent[] missingComponents = new ScanComponent[1];
    private readonly string root;
    private readonly ScanComponent template;

    public NuGetRestoreArtifactCollectorBenchmark()
    {
        root = Path.Combine(Path.GetTempPath(), $"ol-nuget-artifact-{Guid.NewGuid():N}");
        var packageRoot = Path.Combine(root, "packages");
        var packageDirectory = Path.Combine(packageRoot, "example", "1.0.0");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "LICENSE"), "MIT License", Encoding.UTF8);
        assetsPath = Path.Combine(root, "project.assets.json");
        File.WriteAllText(
            assetsPath,
            CreateAssets(packageRoot, "example/1.0.0"),
            Encoding.UTF8);
        var missingPackageRoot = Path.Combine(root, "missing-packages");
        Directory.CreateDirectory(Path.Combine(missingPackageRoot, "example", "1.0.0"));
        missingAssetsPath = Path.Combine(root, "missing-project.assets.json");
        File.WriteAllText(missingAssetsPath, CreateAssets(missingPackageRoot, "example/1.0.0"), Encoding.UTF8);
        template = new ScanComponent(
            "example",
            "1.0.0",
            default,
            "nuget",
            DependencyType.Transitive,
            LicenseStatus.Unknown,
            "pkg:nuget/example@1.0.0",
            default,
            LicenseCandidateFactory.Create(LicenseCandidateSource.DependencyInput, LicenseCandidateKind.Id, "NOASSERTION"u8, index),
            []);
    }

    [Benchmark]
    public int CollectOneMatchedDocument()
    {
        components[0] = template;
        return NuGetRestoreArtifactCollector.Collect(assetsPath, components, matcher, index).Summary.MatchedCount;
    }

    [Benchmark]
    public int ParseOneWithoutMatchingComponent()
        => NuGetRestoreArtifactCollector.Collect(assetsPath, emptyComponents, matcher, index).Summary.TargetCount;

    [Benchmark]
    public int CollectOneMissingDocument()
    {
        missingComponents[0] = template;
        return NuGetRestoreArtifactCollector.Collect(missingAssetsPath, missingComponents, matcher, index).Summary.TargetCount;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static string CreateAssets(string packageRoot, string identity)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot + Path.DirectorySeparatorChar] = new { } },
            libraries = new Dictionary<string, object> { [identity] = new { type = "package", path = identity } },
        });
}
