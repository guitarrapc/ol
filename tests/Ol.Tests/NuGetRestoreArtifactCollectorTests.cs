using System.Text;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.Spdx;

namespace Ol.Tests;

public sealed class NuGetRestoreArtifactCollectorTests
{
    [Test]
    public async Task Collect_RestoredPackageLicenseText_AddsMatchedArtifactEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-nuget-artifact-{Guid.NewGuid():N}");
        try
        {
            var packageRoot = Path.Combine(root, "packages");
            var packageDirectory = Path.Combine(packageRoot, "system.buffers", "4.5.1");
            Directory.CreateDirectory(packageDirectory);
            await File.WriteAllTextAsync(Path.Combine(packageDirectory, "LICENSE.TXT"), "MIT License", Encoding.UTF8);
            var assetsPath = Path.Combine(root, "project.assets.json");
            await File.WriteAllTextAsync(assetsPath, CreateAssets(packageRoot, "System.Buffers/4.5.1", "system.buffers/4.5.1"), Encoding.UTF8);
            var spdx = new SpdxLicenseIndex(["MIT"], []);
            var matcher = new SpdxLicenseTextMatcher("test-corpus", [new("MIT", "MIT License")]);
            var components = new[] { CreateComponent(spdx, "System.Buffers", "4.5.1") };

            var result = NuGetRestoreArtifactCollector.Collect(assetsPath, components, matcher, spdx);

            await Assert.That(result.Summary.TargetCount).IsEqualTo(1);
            await Assert.That(result.Summary.DocumentCount).IsEqualTo(1);
            await Assert.That(result.Summary.MatchedCount).IsEqualTo(1);
            await Assert.That(result.Components[0].Status).IsEqualTo(LicenseStatus.Matched);
            await Assert.That(result.Components[0].License.ToString()).IsEqualTo("MIT");
            var candidate = result.Components[0].GetCandidate(result.Components[0].CandidateCount - 1);
            await Assert.That(candidate.Source).IsEqualTo(LicenseCandidateSource.PackageArtifact);
            await Assert.That(candidate.Evidence.PackageArtifact!.Artifact).IsEqualTo("pkg:nuget/System.Buffers@4.5.1");
            await Assert.That(candidate.Evidence.PackageArtifact.Path).IsEqualTo("LICENSE.TXT");
            await Assert.That(candidate.Evidence.PackageArtifact.CorpusVersion).IsEqualTo("test-corpus");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Collect_NuspecLicenseFile_UsesDeclaredNestedPath()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (assetsPath, packageDirectory) = await CreateRestoredPackageAsync(root);
            Directory.CreateDirectory(Path.Combine(packageDirectory, "legal"));
            await File.WriteAllTextAsync(Path.Combine(packageDirectory, "legal", "terms.md"), "MIT License", Encoding.UTF8);
            await File.WriteAllTextAsync(
                Path.Combine(packageDirectory, "system.buffers.nuspec"),
                """<package><metadata><license type="file">legal/terms.md</license></metadata></package>""",
                Encoding.UTF8);
            var (spdx, matcher, components) = CreateCollectionInputs();

            var result = NuGetRestoreArtifactCollector.Collect(assetsPath, components, matcher, spdx);

            var evidence = result.Components[0].GetCandidate(result.Components[0].CandidateCount - 1).Evidence.PackageArtifact!;
            await Assert.That(evidence.Path).IsEqualTo("legal/terms.md");
            await Assert.That(result.Summary.DocumentCount).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Collect_UnrecognizedLicenseText_RetainsUnknownHashedEvidence()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (assetsPath, packageDirectory) = await CreateRestoredPackageAsync(root);
            await File.WriteAllTextAsync(Path.Combine(packageDirectory, "LICENSE"), "custom terms", Encoding.UTF8);
            var (spdx, matcher, components) = CreateCollectionInputs();

            var result = NuGetRestoreArtifactCollector.Collect(assetsPath, components, matcher, spdx);

            var candidate = result.Components[0].GetCandidate(result.Components[0].CandidateCount - 1);
            await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Unknown);
            await Assert.That(candidate.Warnings).IsEqualTo(LicenseCandidateWarnings.SourceLicenseNotDetected);
            await Assert.That(candidate.Evidence.PackageArtifact!.ContentSha256).Length().IsEqualTo(64);
            await Assert.That(result.Summary.MatchedCount).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Collect_LibraryPathOutsidePackageRoot_DoesNotReadDocument()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageRoot = Path.Combine(root, "packages");
            Directory.CreateDirectory(packageRoot);
            await File.WriteAllTextAsync(Path.Combine(root, "LICENSE"), "MIT License", Encoding.UTF8);
            var assetsPath = Path.Combine(root, "project.assets.json");
            await File.WriteAllTextAsync(assetsPath, CreateAssets(packageRoot, "System.Buffers/4.5.1", "../"), Encoding.UTF8);
            var (spdx, matcher, components) = CreateCollectionInputs();

            var result = NuGetRestoreArtifactCollector.Collect(assetsPath, components, matcher, spdx);

            await Assert.That(result.Summary).IsEqualTo(default(NuGetArtifactCollectionSummary));
            await Assert.That(result.Components[0].CandidateCount).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Collect_LicenseDocumentOverMatcherLimit_DoesNotReadDocument()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (assetsPath, packageDirectory) = await CreateRestoredPackageAsync(root);
            await File.WriteAllTextAsync(Path.Combine(packageDirectory, "LICENSE"), "MIT License", Encoding.UTF8);
            var spdx = new SpdxLicenseIndex(["MIT"], []);
            var matcher = new SpdxLicenseTextMatcher("test-corpus", [new("MIT", "MIT License")], maximumTextBytes: 4);
            var components = new[] { CreateComponent(spdx, "System.Buffers", "4.5.1") };

            var result = NuGetRestoreArtifactCollector.Collect(assetsPath, components, matcher, spdx);

            await Assert.That(result.Summary.TargetCount).IsEqualTo(1);
            await Assert.That(result.Summary.DocumentCount).IsEqualTo(0);
            await Assert.That(result.Components[0].CandidateCount).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ScanComponent CreateComponent(SpdxLicenseIndex spdx, string name, string version)
        => new(
            name,
            version,
            default,
            "nuget",
            DependencyType.Transitive,
            LicenseStatus.Unknown,
            $"pkg:nuget/{name}@{version}",
            default,
            LicenseCandidateFactory.Create(LicenseCandidateSource.DependencyInput, LicenseCandidateKind.Id, "NOASSERTION"u8, spdx),
            []);

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-nuget-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<(string AssetsPath, string PackageDirectory)> CreateRestoredPackageAsync(string root)
    {
        var packageRoot = Path.Combine(root, "packages");
        var packageDirectory = Path.Combine(packageRoot, "system.buffers", "4.5.1");
        Directory.CreateDirectory(packageDirectory);
        var assetsPath = Path.Combine(root, "project.assets.json");
        await File.WriteAllTextAsync(assetsPath, CreateAssets(packageRoot, "System.Buffers/4.5.1", "system.buffers/4.5.1"), Encoding.UTF8);
        return (assetsPath, packageDirectory);
    }

    private static (SpdxLicenseIndex Spdx, SpdxLicenseTextMatcher Matcher, ScanComponent[] Components) CreateCollectionInputs()
    {
        var spdx = new SpdxLicenseIndex(["MIT"], []);
        var matcher = new SpdxLicenseTextMatcher("test-corpus", [new("MIT", "MIT License")]);
        return (spdx, matcher, [CreateComponent(spdx, "System.Buffers", "4.5.1")]);
    }

    private static string CreateAssets(string packageRoot, string identity, string path)
        => $$"""
            {
              "version": 3,
              "packageFolders": { {{System.Text.Json.JsonSerializer.Serialize(Path.EndsInDirectorySeparator(packageRoot) ? packageRoot : packageRoot + Path.DirectorySeparatorChar)}}: {} },
              "libraries": { {{System.Text.Json.JsonSerializer.Serialize(identity)}}: { "type": "package", "path": {{System.Text.Json.JsonSerializer.Serialize(path)}} } }
            }
            """;
}
