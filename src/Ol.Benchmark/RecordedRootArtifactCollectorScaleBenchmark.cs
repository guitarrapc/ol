using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.Spdx;
using System.Text;

[MemoryDiagnoser]
public class RecordedRootArtifactCollectorScaleBenchmark : IDisposable
{
    private readonly SpdxLicenseIndex index = new(["MIT"], []);
    private readonly SpdxLicenseTextMatcher matcher = new("benchmark", [new("MIT", "MIT License")]);
    private string root = null!;
    private string npmInput = null!;
    private string cargoInput = null!;
    private string pipInput = null!;
    private string goInput = null!;
    private ScanComponent[] npmComponents = null!;
    private ScanComponent[] cargoComponents = null!;
    private ScanComponent[] pipComponents = null!;
    private ScanComponent[] goComponents = null!;

    [Params(1, 1_000, 10_000)]
    public int PackageCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), $"ol-recorded-artifact-scale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        npmInput = Path.Combine(root, "package-lock.json");
        cargoInput = Path.Combine(root, "cargo-metadata.json");
        pipInput = Path.Combine(root, "pip-inspect.json");
        goInput = Path.Combine(root, "go-list-modules.json");
        File.WriteAllText(npmInput, "{}", Encoding.UTF8);
        npmComponents = new ScanComponent[PackageCount];
        cargoComponents = new ScanComponent[PackageCount];
        pipComponents = new ScanComponent[PackageCount];
        goComponents = new ScanComponent[PackageCount];
        var cargo = new StringBuilder(PackageCount * 128).Append("{\"packages\":[");
        var pip = new StringBuilder(PackageCount * 128).Append("{\"installed\":[");
        var go = new StringBuilder(PackageCount * 112);
        for (var packageIndex = 0; packageIndex < PackageCount; packageIndex++)
        {
            var name = $"example{packageIndex}";
            npmComponents[packageIndex] = CreateComponent(name, "1.0.0", "npm", $"node_modules/{name}");
            cargoComponents[packageIndex] = CreateComponent(name, "1.0.0", "cargo", $"{name}@1.0.0");
            pipComponents[packageIndex] = CreateComponent(name, "1.0.0", "pypi", $"{name}@1.0.0");
            goComponents[packageIndex] = CreateComponent(name, "v1.0.0", "golang", $"example.com/{name}@v1.0.0");
            if (packageIndex != 0)
            {
                cargo.Append(',');
                pip.Append(',');
            }

            cargo.Append("{\"id\":\"").Append(name).Append("@1.0.0\",\"manifest_path\":")
                .Append(Json(Path.Combine(root, "missing", name, "Cargo.toml"))).Append('}');
            pip.Append("{\"metadata\":{\"name\":\"").Append(name)
                .Append("\",\"version\":\"1.0.0\"},\"metadata_location\":")
                .Append(Json(Path.Combine(root, "missing", name))).Append('}');
            go.Append("{\"Path\":\"example.com/").Append(name)
                .Append("\",\"Version\":\"v1.0.0\",\"Dir\":")
                .Append(Json(Path.Combine(root, "missing", name))).Append("}\n");
        }

        cargo.Append("]}");
        pip.Append("]}");
        File.WriteAllText(cargoInput, cargo.ToString(), Encoding.UTF8);
        File.WriteAllText(pipInput, pip.ToString(), Encoding.UTF8);
        File.WriteAllText(goInput, go.ToString(), Encoding.UTF8);
    }

    [Benchmark]
    public int NpmMissingArtifacts()
        => NpmRestoreArtifactCollector.Collect(npmInput, npmComponents, matcher, index).Summary.TargetCount;

    [Benchmark]
    public int CargoMissingArtifacts()
        => CargoRestoreArtifactCollector.Collect(cargoInput, cargoComponents, matcher, index).Summary.TargetCount;

    [Benchmark]
    public int PipMissingArtifacts()
        => PipRestoreArtifactCollector.Collect(pipInput, pipComponents, matcher, index).Summary.TargetCount;

    [Benchmark]
    public int GoMissingArtifacts()
        => GoRestoreArtifactCollector.Collect(goInput, goComponents, matcher, index).Summary.TargetCount;

    [GlobalCleanup]
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static ScanComponent CreateComponent(string name, string version, string ecosystem, string sourceId)
        => new(name, version, default, ecosystem, DependencyType.Transitive, LicenseStatus.Unknown, sourceId, sourceId, default, []);

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
