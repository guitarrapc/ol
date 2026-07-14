using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ol.Core;

namespace Ol.Tests;

public sealed class CliScanTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Scan_WithCachedGitHubSourceEvidence_FillsUnknownLicenseAndReportsSafeAuthMode()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-cache-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(packageCacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.OK, new GitHubLicenseResult("MIT", "mit", "MIT License", "LICENSE", "sha", "https://github.com/owner/repository/blob/main/LICENSE"), [], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--sbom", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(component.GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(component.GetProperty("licenseCandidates")[2].GetProperty("source").GetString()).IsEqualTo("github-license-api");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("network").GetProperty("githubAuth").GetString()).IsEqualTo("none");
            var sourceMetadata = report.RootElement.GetProperty("metadata").GetProperty("sourceRepository");
            await Assert.That(sourceMetadata.GetProperty("targetCount").GetInt32()).IsEqualTo(1);
            await Assert.That(sourceMetadata.GetProperty("githubLicenseRequestCount").GetInt32()).IsEqualTo(0);
            await Assert.That(sourceMetadata.GetProperty("cacheHitCount").GetInt32()).IsEqualTo(1);
            await Assert.That(stderr).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

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
            await Assert.That(stderr).IsEmpty();
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
            await Assert.That(candidates.GetArrayLength()).IsEqualTo(3);
            var declared = candidates[0];
            await Assert.That(declared.GetProperty("source").GetString()).IsEqualTo("sbom");
            await Assert.That(declared.GetProperty("kind").GetString()).IsEqualTo("declared");
            await Assert.That(declared.GetProperty("raw").GetString()).IsEqualTo("gpl-2.0");
            await Assert.That(declared.GetProperty("normalized").GetString()).IsEqualTo("GPL-2.0");
            await Assert.That(declared.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(declared.GetProperty("deprecated").GetBoolean()).IsTrue();
            await Assert.That(declared.GetProperty("warnings")[0].GetString()).IsEqualTo("deprecated_spdx_identifier");
            await Assert.That(component.GetProperty("evidence").GetArrayLength()).IsEqualTo(3);
            await Assert.That(candidates[2].GetProperty("kind").GetString()).IsEqualTo("unavailable");
            await Assert.That(component.GetProperty("warnings")[0].GetString()).IsEqualTo("deprecated_spdx_identifier");
            await Assert.That(stderr).IsEmpty();
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
            await Assert.That(stderr).Contains("Filter: 2 components excluded; 1 with unknown dependency type");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithHumanReadableFormat_SeparatesAndLabelsSummary()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-summary-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);

        try
        {
            foreach (var format in new[] { "text", "markdown" })
            {
                var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--format", format);

                await Assert.That(exitCode).IsEqualTo(0);
                await Assert.That(stderr).StartsWith($"{Environment.NewLine}Scan summary{Environment.NewLine}");
                await Assert.That(stderr).Contains("  License results: 1 displayed component; 1 matched; 0 conflict; 0 unknown; 0 ambiguous; 0 invalid");
                await Assert.That(stderr).Contains("  Package metadata (full scan):");
                await Assert.That(stderr).Contains("  Source repositories (full scan):");
                await Assert.That(stderr).Contains("  Input:");
            }

            var (quietExitCode, _, quietStderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--format", "text", "--quiet");
            await Assert.That(quietExitCode).IsEqualTo(0);
            await Assert.That(quietStderr).IsEmpty();
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
            await Assert.That(stderr).IsEmpty();
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
            await Assert.That(stderr).Contains("License results: 3 displayed components");
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

    [Test]
    public async Task Scan_WithPackageAndSourceConflict_ReportsRefBoundSafeSourceEvidence()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-conflict-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        const string repositoryRef = "0123456789abcdef";
        const string token = "must-never-appear";
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(packageCacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", "https://github.com/owner/repository", [], [], RepositoryRef: repositoryRef));
        var target = new SourceRepositoryTarget("owner", "repository", repositoryRef);
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.OK, new GitHubLicenseResult("Apache-2.0", "apache-2.0", "Apache License 2.0", "LICENSE", "license-sha", "https://github.com/owner/repository/blob/0123456789abcdef/LICENSE"), [], []));

        try
        {
            var environment = new Dictionary<string, string?> { ["OL_GITHUB_TOKEN"] = token };
            var (exitCode, stdout, _) = await RunOlWithEnvironmentAsync(root, packageCacheRoot, sourceCacheRoot, environment, "scan", "--sbom", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).DoesNotContain(token);
            await Assert.That(stdout).DoesNotContain(temporaryDirectory);
            await Assert.That(stdout).DoesNotContain(sourceCacheRoot);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("conflict");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("network").GetProperty("githubAuth").GetString()).IsEqualTo("ol_github_token");
            var sourceCandidate = component.GetProperty("evidence")[2];
            var source = sourceCandidate.GetProperty("sourceRepository");
            await Assert.That(sourceCandidate.GetProperty("warnings").GetArrayLength()).IsEqualTo(0);
            await Assert.That(source.GetProperty("repository").GetString()).IsEqualTo("owner/repository");
            await Assert.That(source.GetProperty("ref").GetString()).IsEqualTo(repositoryRef);
            await Assert.That(source.GetProperty("httpStatus").GetInt32()).IsEqualTo(200);
            await Assert.That(source.GetProperty("licensePath").GetString()).IsEqualTo("LICENSE");
            await Assert.That(source.GetProperty("licenseSha").GetString()).IsEqualTo("license-sha");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithCachedSourceFailureAndValidSbom_KeepsMatchedStatusAndWarning()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-failure-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(packageCacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.Forbidden, null, [], ["source_repository_fetch_failed"]));

        try
        {
            const string ignoredGitHubToken = "github-token-must-not-appear";
            var environment = new Dictionary<string, string?> { ["OL_GITHUB_TOKEN"] = null, ["GITHUB_TOKEN"] = ignoredGitHubToken };
            var (exitCode, stdout, stderr) = await RunOlWithEnvironmentAsync(root, packageCacheRoot, sourceCacheRoot, environment, "scan", "--sbom", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(component.GetProperty("warnings").EnumerateArray().Select(static value => value.GetString())).Contains("source_repository_fetch_failed");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("network").GetProperty("githubAuth").GetString()).IsEqualTo("none");
            await Assert.That(stdout).DoesNotContain(ignoredGitHubToken);
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("sourceRepository").GetProperty("fetchErrorCount").GetInt32()).IsEqualTo(1);
            await Assert.That(stderr).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task CacheClear_SourceRepository_RemovesSourceCache()
    {
        var root = FindRepositoryRoot();
        var sourceCacheRoot = Path.Combine(Path.GetTempPath(), $"ol-v3-clear-{Guid.NewGuid():N}");
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.NotFound, null, ["license_not_detected"], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlWithCachesAsync(root, null, sourceCacheRoot, "cache", "clear", "source-repository");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("source-repository cache cleared");
            await Assert.That(stderr).IsEmpty();
            await Assert.That(Directory.Exists(sourceCacheRoot)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(sourceCacheRoot)) Directory.Delete(sourceCacheRoot, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlAsync(string root, params string[] args)
        => await RunOlWithCacheAsync(root, cacheRoot: null, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlWithCacheAsync(string root, string? cacheRoot, params string[] args)
        => await RunOlWithCachesAsync(root, cacheRoot, sourceCacheRoot: null, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlWithCachesAsync(string root, string? cacheRoot, string? sourceCacheRoot, params string[] args)
        => await RunOlWithEnvironmentAsync(root, cacheRoot, sourceCacheRoot, null, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlWithEnvironmentAsync(string root, string? cacheRoot, string? sourceCacheRoot, IReadOnlyDictionary<string, string?>? environment, params string[] args)
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

            if (sourceCacheRoot is not null)
            {
                startInfo.Environment["OL_SOURCE_REPOSITORY_CACHE_ROOT"] = sourceCacheRoot;
            }

            if (environment is not null)
            {
                foreach (var item in environment)
                {
                    startInfo.Environment[item.Key] = item.Value;
                }
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
