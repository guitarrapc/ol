using System.Text;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.Spdx;

namespace Ol.Tests;

public sealed class PackageArtifactCollectorRegistryTests
{
    [Test]
    [Arguments("npm-package-lock", "npm", "node_modules/example", "package-lock.json")]
    [Arguments("cargo-metadata", "cargo", "registry+https://github.com/rust-lang/crates.io-index#example@1.0.0", "cargo-metadata.json")]
    [Arguments("pip-inspect", "pypi", "example@1.0.0", "pip-inspect.json")]
    [Arguments("go-module-graph", "golang", "example.com/module@v1.0.0", "go-list-modules.json")]
    public async Task Collect_RecordedArtifactRoot_AddsMatchedEvidence(
        string formatName,
        string ecosystem,
        string sourceId,
        string inputFileName)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var artifactDirectory = formatName == "npm-package-lock"
                ? Path.Combine(root, "node_modules", "example")
                : Path.Combine(root, "artifact");
            Directory.CreateDirectory(artifactDirectory);
            var logicalPath = formatName switch
            {
                "cargo-metadata" => "legal/terms.md",
                "pip-inspect" => "licenses/LICENSE.txt",
                _ => "LICENSE",
            };
            var licensePath = Path.Combine(artifactDirectory, logicalPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(licensePath)!);
            await File.WriteAllTextAsync(licensePath, "MIT License", Encoding.UTF8);
            var inputPath = Path.Combine(root, inputFileName);
            await File.WriteAllTextAsync(inputPath, CreateInput(formatName, artifactDirectory), Encoding.UTF8);
            var spdx = new SpdxLicenseIndex(["MIT"], []);
            var matcher = new SpdxLicenseTextMatcher("test-corpus", [new("MIT", "MIT License")]);
            var components = new[] { CreateComponent(spdx, ecosystem, sourceId) };
            await Assert.That(PackageArtifactCollectorRegistry.Default.TryGet(new ScanInputFormat(formatName, "test", "test"), out var handler)).IsTrue();

            var result = handler.Collector(inputPath, components, matcher, spdx);

            await Assert.That(result.Summary.TargetCount).IsEqualTo(1);
            await Assert.That(result.Summary.DocumentCount).IsEqualTo(1);
            await Assert.That(result.Summary.MatchedCount).IsEqualTo(1);
            var candidate = result.Components[0].GetCandidate(result.Components[0].CandidateCount - 1);
            await Assert.That(candidate.Source).IsEqualTo(LicenseCandidateSource.PackageArtifact);
            await Assert.That(candidate.Evidence.PackageArtifact!.Path).IsEqualTo(logicalPath);
            await Assert.That(candidate.Evidence.PackageArtifact.ContentSha256).Length().IsEqualTo(64);
            await Assert.That(candidate.Evidence.PackageArtifact.CorpusVersion).IsEqualTo("test-corpus");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Default_RegistersOnlyInputsWithRecordedArtifactRoots()
    {
        await Assert.That(PackageArtifactCollectorRegistry.Default.TryGet(ScanInputFormat.NuGetAssets, out _)).IsTrue();
        await Assert.That(PackageArtifactCollectorRegistry.Default.TryGet(ScanInputFormat.NpmPackageLock, out _)).IsTrue();
        await Assert.That(PackageArtifactCollectorRegistry.Default.TryGet(ScanInputFormat.CargoMetadata, out _)).IsTrue();
        await Assert.That(PackageArtifactCollectorRegistry.Default.TryGet(ScanInputFormat.PipInspect, out _)).IsTrue();
        await Assert.That(PackageArtifactCollectorRegistry.Default.TryGet(ScanInputFormat.GoModuleGraph, out _)).IsTrue();
        await Assert.That(PackageArtifactCollectorRegistry.Default.TryGet(ScanInputFormat.ComposerLock, out _)).IsFalse();
        await Assert.That(PackageArtifactCollectorRegistry.Default.TryGet(ScanInputFormat.MavenDependencyTree, out _)).IsFalse();
    }

    [Test]
    public async Task GoCollector_JsonObjectSequence_CollectsEveryRecordedModule()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var firstDirectory = Path.Combine(root, "first");
            var secondDirectory = Path.Combine(root, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            await File.WriteAllTextAsync(Path.Combine(firstDirectory, "LICENSE"), "MIT License", Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(secondDirectory, "LICENSE"), "MIT License", Encoding.UTF8);
            var inputPath = Path.Combine(root, "go-list-modules.json");
            await File.WriteAllTextAsync(inputPath, $$"""
                { "Path": "example.com/first", "Version": "v1.0.0", "Dir": {{Json(firstDirectory)}} }
                { "Path": "example.com/second", "Version": "v1.0.0", "Dir": {{Json(secondDirectory)}} }
                """, Encoding.UTF8);
            var spdx = new SpdxLicenseIndex(["MIT"], []);
            var matcher = new SpdxLicenseTextMatcher("test-corpus", [new("MIT", "MIT License")]);
            var components = new[]
            {
                CreateGoComponent(spdx, "first"),
                CreateGoComponent(spdx, "second"),
            };

            var result = GoRestoreArtifactCollector.Collect(inputPath, components, matcher, spdx);

            await Assert.That(result.Summary.TargetCount).IsEqualTo(2);
            await Assert.That(result.Summary.MatchedCount).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ScanComponent CreateComponent(SpdxLicenseIndex spdx, string ecosystem, string sourceId)
        => new(
            "example",
            ecosystem == "golang" ? "v1.0.0" : "1.0.0",
            default,
            ecosystem,
            DependencyType.Transitive,
            LicenseStatus.Unknown,
            $"pkg:{ecosystem}/example@1.0.0",
            sourceId,
            LicenseCandidateFactory.Create(LicenseCandidateSource.DependencyInput, LicenseCandidateKind.Id, "NOASSERTION"u8, spdx),
            []);

    private static ScanComponent CreateGoComponent(SpdxLicenseIndex spdx, string name)
        => new(
            name,
            "v1.0.0",
            default,
            "golang",
            DependencyType.Transitive,
            LicenseStatus.Unknown,
            $"pkg:golang/example.com/{name}@v1.0.0",
            $"example.com/{name}@v1.0.0",
            LicenseCandidateFactory.Create(LicenseCandidateSource.DependencyInput, LicenseCandidateKind.Id, "NOASSERTION"u8, spdx),
            []);

    private static string CreateInput(string formatName, string artifactDirectory)
        => formatName switch
        {
            "npm-package-lock" => $$"""
                { "lockfileVersion": 3, "packages": { "": {}, "node_modules/example": { "name": "example", "version": "1.0.0" } } }
                """,
            "cargo-metadata" => $$"""
                { "packages": [{ "id": "registry+https://github.com/rust-lang/crates.io-index#example@1.0.0", "name": "example", "version": "1.0.0", "manifest_path": {{Json(artifactDirectory + Path.DirectorySeparatorChar + "Cargo.toml")}}, "license_file": "legal/terms.md" }], "workspace_members": [], "resolve": {}, "target_directory": "", "version": 1, "workspace_root": "" }
                """,
            "pip-inspect" => $$"""
                { "version": "1", "pip_version": "1", "installed": [{ "metadata": { "name": "example", "version": "1.0.0", "license_file": ["LICENSE.txt"] }, "metadata_location": {{Json(artifactDirectory)}} }], "environment": {} }
                """,
            "go-module-graph" => $$"""
                { "Path": "example.com/module", "Version": "v1.0.0", "Dir": {{Json(artifactDirectory)}} }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(formatName)),
        };

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
