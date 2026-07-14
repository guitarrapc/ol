using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ol.Core;

namespace Ol.Tests;

public sealed class CliScanTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Scan_WithCachedNpmMetadata_ReconcilesPackageEvidenceAndReportsCacheHit()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v2-cache-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var cacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(
                sbomPath,
                """
                        {
                            "bomFormat": "CycloneDX",
                            "components": [
                                {
                                    "bom-ref": "pkg:npm/example@1.0.0",
                                    "name": "example",
                                    "version": "1.0.0",
                                    "purl": "pkg:npm/example@1.0.0",
                                    "licenses": [ { "license": { "id": "NOASSERTION" } } ]
                                }
                            ]
                        }
                        """,
                Encoding.UTF8);
        var cache = new PackageMetadataCache(cacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", "https://example.test/repository", [], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlWithCacheAsync(root, cacheRoot, "scan", "--sbom", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(component.GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(component.GetProperty("licenseCandidates")[1].GetProperty("source").GetString()).IsEqualTo("npm-registry");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("packageMetadata").GetProperty("cacheHitCount").GetInt32()).IsEqualTo(1);
            await Assert.That(stderr).Contains("package-metadata-supported: 1; cache-hit: 1; cache-miss: 0; refreshed: 0; fetch-error: 0; unsupported-ecosystem: 0; concurrency: 1; retry: 0");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task CacheClear_PackageMetadata_RemovesMetadataCache()
    {
        var root = FindRepositoryRoot();
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"ol-v2-clear-{Guid.NewGuid():N}");
        var cache = new PackageMetadataCache(cacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlWithCacheAsync(root, cacheRoot, "cache", "clear", "package-metadata");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("package-metadata cache cleared");
            await Assert.That(stderr).IsEmpty();
            await Assert.That(Directory.Exists(cacheRoot)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task Scan_WithRefresh_SkipsCachedMetadataWithoutChangingMatchedSbomEvidence()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v2-refresh-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var cacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(cacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "Apache-2.0", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, _) = await RunOlWithCacheAsync(root, cacheRoot, "scan", "--sbom", sbomPath, "--format", "json", "--refresh");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("packageMetadata");
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(component.GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(metadata.GetProperty("cacheHitCount").GetInt32()).IsEqualTo(0);
            await Assert.That(metadata.GetProperty("cacheMissCount").GetInt32()).IsEqualTo(1);
            await Assert.That(metadata.GetProperty("refreshedCount").GetInt32()).IsEqualTo(0);
            await Assert.That(component.GetProperty("warnings")[0].GetString()).IsEqualTo("package_metadata_fetch_failed");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDeprecatedSpdxCandidate_RetainsCandidatesEvidenceAndWarningsInJson()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-evidence-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var spdxDirectory = Path.Combine(temporaryDirectory, "spdx");
        Directory.CreateDirectory(spdxDirectory);
        await File.WriteAllTextAsync(
                sbomPath,
                """
                        {
                            "spdxVersion": "SPDX-2.3",
                            "packages": [
                                {
                                    "SPDXID": "SPDXRef-Package",
                                    "name": "example",
                                    "licenseDeclared": "gpl-2.0",
                                    "licenseConcluded": "MIT"
                                }
                            ]
                        }
                        """,
                Encoding.UTF8);
        await File.WriteAllTextAsync(spdxDirectory + "\\licenses.json", """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "GPL-2.0", "isDeprecatedLicenseId": true }, { "licenseId": "MIT", "isDeprecatedLicenseId": false } ] }""", Encoding.UTF8);
        await File.WriteAllTextAsync(spdxDirectory + "\\exceptions.json", """{ "licenseListVersion": "3.27.0", "exceptions": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--format", "json", "--spdx-data", spdxDirectory);

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            var candidates = component.GetProperty("licenseCandidates");
            await Assert.That(candidates.GetArrayLength()).IsEqualTo(2);
            var declared = candidates[0];
            await Assert.That(declared.GetProperty("source").GetString()).IsEqualTo("sbom");
            await Assert.That(declared.GetProperty("kind").GetString()).IsEqualTo("declared");
            await Assert.That(declared.GetProperty("raw").GetString()).IsEqualTo("gpl-2.0");
            await Assert.That(declared.GetProperty("normalized").GetString()).IsEqualTo("GPL-2.0");
            await Assert.That(declared.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(declared.GetProperty("deprecated").GetBoolean()).IsTrue();
            await Assert.That(declared.GetProperty("warnings")[0].GetString()).IsEqualTo("deprecated_spdx_identifier");
            await Assert.That(component.GetProperty("evidence").GetArrayLength()).IsEqualTo(2);
            await Assert.That(component.GetProperty("warnings")[0].GetString()).IsEqualTo("deprecated_spdx_identifier");
            await Assert.That(stderr).Contains("warnings: 1");
            await Assert.That(stderr).Contains("deprecated-spdx: 1");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDependencyFilter_ReportsExcludedUnknownComponents()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-filter-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            sbomPath,
            """
            {
              "bomFormat": "CycloneDX",
              "metadata": { "component": { "bom-ref": "root", "name": "root", "licenses": [ { "license": { "id": "MIT" } } ] } },
              "components": [
                { "bom-ref": "direct", "name": "direct", "licenses": [ { "license": { "id": "MIT" } } ] },
                { "bom-ref": "unknown", "name": "unknown", "licenses": [ { "license": { "id": "MIT" } } ] }
              ],
              "dependencies": [ { "ref": "root", "dependsOn": [ "direct" ] } ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--dependency", "direct");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("direct");
            await Assert.That(stdout).DoesNotContain("unknown");
            await Assert.That(stderr).Contains("dependency-filtered: 2");
            await Assert.That(stderr).Contains("excluded-unknown: 1");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithJsonFormat_EmitsPrivacySafeInputAndSpdxMetadata()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-json-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var spdxDirectory = Path.Combine(temporaryDirectory, "spdx");
        Directory.CreateDirectory(spdxDirectory);
        await File.WriteAllTextAsync(
            sbomPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                { "bom-ref": "pkg:nuget/example@1.0.0", "name": "example", "version": "1.0.0", "purl": "pkg:nuget/example@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] }
              ]
            }
            """,
            Encoding.UTF8);
        await File.WriteAllTextAsync(spdxDirectory + "\\licenses.json", """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "MIT", "isDeprecatedLicenseId": false } ] }""", Encoding.UTF8);
        await File.WriteAllTextAsync(spdxDirectory + "\\exceptions.json", """{ "licenseListVersion": "3.27.0", "exceptions": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--format", "json", "--spdx-data", spdxDirectory);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"ol exited with {exitCode}. stdout: {stdout} stderr: {stderr}");
            }

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata");
            await Assert.That(metadata.GetProperty("input").GetProperty("sbomRef").GetString()).IsEqualTo("bom.json");
            await Assert.That(metadata.GetProperty("input").GetProperty("sbomSpecVersion").GetString()).IsEqualTo("1.6");
            await Assert.That(metadata.GetProperty("input").GetProperty("sbomSha256").GetString()!.Length).IsEqualTo(64);
            await Assert.That(metadata.GetProperty("spdx").GetProperty("source").GetString()).IsEqualTo("cli-argument");
            await Assert.That(metadata.GetProperty("spdx").GetProperty("licenseListVersion").GetString()).IsEqualTo("3.27.0");
            await Assert.That(metadata.GetProperty("spdx").GetProperty("licensesSha256").GetString()!.Length).IsEqualTo(64);
            await Assert.That(report.RootElement.GetProperty("components")[0].GetProperty("sourceId").GetString()).IsEqualTo("pkg:nuget/example@1.0.0");
            await Assert.That(report.RootElement.GetProperty("warnings").GetArrayLength()).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithOutFile_WritesFileAndKeepsPrimaryOutputOnStdout()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-out-{Guid.NewGuid():N}.json");
        var outPath = Path.Combine(Path.GetTempPath(), $"ol-out-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(
            sbomPath,
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                { "name": "a", "version": "1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--format", "markdown", "--out", outPath);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(File.Exists(outPath)).IsTrue();
            var fileText = await File.ReadAllTextAsync(outPath);
            await Assert.That(stdout).Contains("| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |");
            await Assert.That(stdout).IsEqualTo(fileText);
        }
        finally
        {
            File.Delete(sbomPath);
            File.Delete(outPath);
        }
    }

    [Test]
    public async Task Scan_WithGroupByLicense_RendersGroupedTextRows()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-group-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            sbomPath,
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                { "name": "a", "licenses": [ { "license": { "id": "MIT" } } ] },
                { "name": "b", "licenses": [ { "license": { "id": "MIT" } } ] },
                { "name": "c", "licenses": [ { "license": { "id": "Apache-2.0" } } ] }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--group-by", "license");
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"ol exited with {exitCode}. stdout: {stdout} stderr: {stderr}");
            }

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("LICENSE COUNT");
            await Assert.That(stdout).Contains("Apache-2.0 1");
            await Assert.That(stdout).Contains("MIT 2");
            await Assert.That(stderr).Contains("components: 3");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithCaseInsensitiveSortAndDuplicatePurls_SortsAndEnrichesEveryComponent()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-sort-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var cacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(
            sbomPath,
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                { "name": "zebra", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] },
                { "name": "alpha", "purl": "pkg:cargo/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] },
                { "name": "zebra-copy", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] }
              ]
            }
            """,
            Encoding.UTF8);
        var cache = new PackageMetadataCache(cacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:cargo/example@1.0.0", "cargo-registry", "Apache-2.0", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, _) = await RunOlWithCacheAsync(root, cacheRoot, "scan", "--sbom", sbomPath, "--format", "json", "--sort", "ECOSYSTEM,NAME", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var components = report.RootElement.GetProperty("components");
            await Assert.That(components[0].GetProperty("name").GetString()).IsEqualTo("alpha");
            await Assert.That(components[1].GetProperty("name").GetString()).IsEqualTo("zebra");
            await Assert.That(components[2].GetProperty("name").GetString()).IsEqualTo("zebra-copy");
            await Assert.That(components[1].GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(components[2].GetProperty("license").GetString()).IsEqualTo("MIT");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlAsync(string root, params string[] args)
        => await RunOlWithCacheAsync(root, cacheRoot: null, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlWithCacheAsync(string root, string? cacheRoot, params string[] args)
    {
        await CliGate.WaitAsync();
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (cacheRoot is not null)
            {
                startInfo.Environment["OL_PACKAGE_METADATA_CACHE_ROOT"] = cacheRoot;
            }

            startInfo.ArgumentList.Add(Path.Combine(root, "src", "Ol", "bin", "Debug", "net10.0", "ol.dll"));
            for (var i = 0; i < args.Length; i++)
            {
                startInfo.ArgumentList.Add(args[i]);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ol CLI.");

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            CliGate.Release();
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
