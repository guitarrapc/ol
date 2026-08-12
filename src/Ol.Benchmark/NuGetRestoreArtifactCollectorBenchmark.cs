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
    private readonly SpdxLicenseIndex index = new(["MIT"], []);
    private readonly SpdxLicenseTextMatcher matcher = new("benchmark", [new("MIT", "MIT License")]);
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
            System.Text.Json.JsonSerializer.Serialize(new
            {
                packageFolders = new Dictionary<string, object> { [packageRoot + Path.DirectorySeparatorChar] = new { } },
                libraries = new Dictionary<string, object> { ["example/1.0.0"] = new { type = "package", path = "example/1.0.0" } },
            }),
            Encoding.UTF8);
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

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
