using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.Spdx;
using System.Text;

[MemoryDiagnoser]
public class RecordedRootArtifactCollectorBenchmark : IDisposable
{
    private readonly ScanComponent[] components = new ScanComponent[1];
    private readonly SpdxLicenseIndex index = new(["MIT"], []);
    private readonly SpdxLicenseTextMatcher matcher = new("benchmark", [new("MIT", "MIT License")]);
    private readonly string root;
    private readonly string npmInput;
    private readonly string cargoInput;
    private readonly string pipInput;
    private readonly string goInput;
    private readonly ScanComponent npmTemplate;
    private readonly ScanComponent cargoTemplate;
    private readonly ScanComponent pipTemplate;
    private readonly ScanComponent goTemplate;

    public RecordedRootArtifactCollectorBenchmark()
    {
        root = Path.Combine(Path.GetTempPath(), $"ol-recorded-artifact-{Guid.NewGuid():N}");
        var npmDirectory = Path.Combine(root, "node_modules", "example");
        var cargoDirectory = Path.Combine(root, "cargo");
        var pipDirectory = Path.Combine(root, "example-1.0.0.dist-info");
        var goDirectory = Path.Combine(root, "go");
        Directory.CreateDirectory(npmDirectory);
        Directory.CreateDirectory(Path.Combine(cargoDirectory, "legal"));
        Directory.CreateDirectory(Path.Combine(pipDirectory, "licenses"));
        Directory.CreateDirectory(goDirectory);
        File.WriteAllText(Path.Combine(npmDirectory, "LICENSE"), "MIT License", Encoding.UTF8);
        File.WriteAllText(Path.Combine(cargoDirectory, "legal", "terms.md"), "MIT License", Encoding.UTF8);
        File.WriteAllText(Path.Combine(pipDirectory, "licenses", "LICENSE.txt"), "MIT License", Encoding.UTF8);
        File.WriteAllText(Path.Combine(goDirectory, "LICENSE"), "MIT License", Encoding.UTF8);
        npmInput = Path.Combine(root, "package-lock.json");
        cargoInput = Path.Combine(root, "cargo-metadata.json");
        pipInput = Path.Combine(root, "pip-inspect.json");
        goInput = Path.Combine(root, "go-list-modules.json");
        File.WriteAllText(npmInput, "{}", Encoding.UTF8);
        File.WriteAllText(cargoInput, $$"""{"packages":[{"id":"example@1.0.0","manifest_path":{{Json(Path.Combine(cargoDirectory, "Cargo.toml"))}},"license_file":"legal/terms.md"}]}""", Encoding.UTF8);
        File.WriteAllText(pipInput, $$"""{"installed":[{"metadata":{"name":"example","version":"1.0.0","license_file":["LICENSE.txt"]},"metadata_location":{{Json(pipDirectory)}}}]}""", Encoding.UTF8);
        File.WriteAllText(goInput, $$"""{"Path":"example.com/module","Version":"v1.0.0","Dir":{{Json(goDirectory)}}}""", Encoding.UTF8);
        npmTemplate = CreateComponent("npm", "node_modules/example", "1.0.0");
        cargoTemplate = CreateComponent("cargo", "example@1.0.0", "1.0.0");
        pipTemplate = CreateComponent("pypi", "example@1.0.0", "1.0.0");
        goTemplate = CreateComponent("golang", "example.com/module@v1.0.0", "v1.0.0");
    }

    [Benchmark]
    public int NpmOneMatchedDocument()
    {
        components[0] = npmTemplate;
        return NpmRestoreArtifactCollector.Collect(npmInput, components, matcher, index).Summary.MatchedCount;
    }

    [Benchmark]
    public int CargoOneMatchedDocument()
    {
        components[0] = cargoTemplate;
        return CargoRestoreArtifactCollector.Collect(cargoInput, components, matcher, index).Summary.MatchedCount;
    }

    [Benchmark]
    public int PipOneMatchedDocument()
    {
        components[0] = pipTemplate;
        return PipRestoreArtifactCollector.Collect(pipInput, components, matcher, index).Summary.MatchedCount;
    }

    [Benchmark]
    public int GoOneMatchedDocument()
    {
        components[0] = goTemplate;
        return GoRestoreArtifactCollector.Collect(goInput, components, matcher, index).Summary.MatchedCount;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private ScanComponent CreateComponent(string ecosystem, string sourceId, string version)
        => new(
            "example",
            version,
            default,
            ecosystem,
            DependencyType.Transitive,
            LicenseStatus.Unknown,
            $"pkg:{ecosystem}/example@{version}",
            sourceId,
            LicenseCandidateFactory.Create(LicenseCandidateSource.DependencyInput, LicenseCandidateKind.Id, "NOASSERTION"u8, index),
            []);

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
