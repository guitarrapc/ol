using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ol.Core;

namespace Ol.Tests;

public sealed class CliScanTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Scan_WithExplicitCycloneDxInput_EmitsGenericAndLegacyInputMetadata()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "cyclonedx", "--format", "json", "--skip-enrichment");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("sbom");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("cyclonedx");
            await Assert.That(input.GetProperty("sourceRef").GetString()).IsEqualTo(Path.GetFileName(inputPath));
            await Assert.That(input.GetProperty("sourceSha256").GetString()!.Length).IsEqualTo(64);
            await Assert.That(input.GetProperty("parser").GetString()).IsEqualTo("cyclonedx-json");
            await Assert.That(input.GetProperty("specificationVersion").GetString()).IsEqualTo("1.6");
            await Assert.That(input.GetProperty("sbomRef").GetString()).IsEqualTo(Path.GetFileName(inputPath));
            await Assert.That(input.GetProperty("sbomFormat").GetString()).IsEqualTo("CycloneDX");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithExplicitSpdxInput_AcceptsMatchingFormat()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "spdxVersion": "SPDX-2.3", "packages": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "spdx", "--format", "json", "--skip-enrichment");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("spdx");
            await Assert.That(input.GetProperty("parser").GetString()).IsEqualTo("spdx-json");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithInvalidInputSelection_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var cases = new[]
            {
                (Arguments: Array.Empty<string>(), Message: "Exactly one of --sbom or --input must be specified."),
                (Arguments: new[] { "--sbom", inputPath, "--input", inputPath, "--input-format", "cyclonedx" }, Message: "--sbom and --input cannot be used together."),
                (Arguments: new[] { "--input", inputPath }, Message: "--input-format is required with --input."),
                (Arguments: new[] { "--sbom", inputPath, "--input-format", "cyclonedx" }, Message: "--input-format can only be used with --input."),
                (Arguments: new[] { "--input", inputPath, "--input-format", "unknown" }, Message: "Unsupported input format: unknown"),
            };

            foreach (var item in cases)
            {
                var arguments = new string[item.Arguments.Length + 2];
                arguments[0] = "scan";
                arguments[1] = "--skip-enrichment";
                item.Arguments.CopyTo(arguments, 2);
                var (exitCode, stdout, stderr) = await RunOlAsync(root, arguments);

                await Assert.That(exitCode).IsEqualTo(1);
                await Assert.That(stdout).IsEmpty();
                await Assert.That(stderr.Trim()).IsEqualTo($"Invalid scan input: {item.Message}");
            }
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithExplicitFormatThatDoesNotMatchContent_RejectsInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "spdxVersion": "SPDX-2.3", "packages": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "cyclonedx", "--skip-enrichment");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: Input format cyclonedx does not match the detected spdx format.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

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
            var sourceCandidate = component.GetProperty("licenseCandidates")[2];
            await Assert.That(sourceCandidate.GetProperty("source").GetString()).IsEqualTo("github-license-api");
            var sourceEvidence = sourceCandidate.GetProperty("evidence");
            await Assert.That(sourceEvidence.GetProperty("type").GetString()).IsEqualTo("source-repository");
            await Assert.That(sourceEvidence.GetProperty("repository").GetString()).IsEqualTo("owner/repository");
            await Assert.That(sourceEvidence.GetProperty("licensePath").GetString()).IsEqualTo("LICENSE");
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
    public async Task Scan_WithSharedUnknownSource_CountsComponentsSeparatelyFromTargets()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-shared-source-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "a", "purl": "pkg:npm/a@1.0.0" }, { "name": "b", "purl": "pkg:npm/b@1.0.0" } ] }""", Encoding.UTF8);
        var packageCache = new PackageMetadataCache(packageCacheRoot);
        await packageCache.WriteAsync(new PackageMetadataRecord("pkg:npm/a@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        await packageCache.WriteAsync(new PackageMetadataRecord("pkg:npm/b@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.NotFound, null, ["license_not_detected"], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--sbom", sbomPath, "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var source = report.RootElement.GetProperty("metadata").GetProperty("sourceRepository");
            await Assert.That(source.GetProperty("targetCount").GetInt32()).IsEqualTo(1);
            await Assert.That(source.GetProperty("unknownCount").GetInt32()).IsEqualTo(2);
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
            var packageCandidate = component.GetProperty("licenseCandidates")[1];
            await Assert.That(packageCandidate.GetProperty("source").GetString()).IsEqualTo("npm-registry");
            var packageEvidence = packageCandidate.GetProperty("evidence");
            await Assert.That(packageEvidence.GetProperty("type").GetString()).IsEqualTo("package-registry");
            await Assert.That(packageEvidence.GetProperty("cacheKeySha256").GetString()!.Length).IsEqualTo(64);
            await Assert.That(packageEvidence.GetProperty("collectedAt").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.MinValue);
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
            await Assert.That(component.TryGetProperty("evidence", out _)).IsFalse();
            var declaredEvidence = declared.GetProperty("evidence");
            await Assert.That(declaredEvidence.GetProperty("type").GetString()).IsEqualTo("sbom");
            await Assert.That(declaredEvidence.GetProperty("field").GetString()).IsEqualTo("licenseDeclared");
            await Assert.That(declaredEvidence.TryGetProperty("acknowledgement", out _)).IsFalse();
            var concludedEvidence = candidates[1].GetProperty("evidence");
            await Assert.That(concludedEvidence.GetProperty("field").GetString()).IsEqualTo("licenseConcluded");
            await Assert.That(concludedEvidence.TryGetProperty("acknowledgement", out _)).IsFalse();
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
    public async Task Scan_WithCycloneDxLicenseAcknowledgement_ReportsClaimProvenanceWithoutDuplicateEvidenceArray()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-acknowledgement-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            sbomPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                {
                  "bom-ref": "concluded",
                  "name": "concluded",
                  "licenses": [ { "license": { "id": "MIT", "acknowledgement": "concluded" } } ]
                },
                {
                  "bom-ref": "declared",
                  "name": "declared",
                  "licenses": [ { "license": { "id": "MIT", "acknowledgement": "declared" } } ]
                },
                {
                  "bom-ref": "unspecified",
                  "name": "unspecified",
                  "licenses": [ { "license": { "id": "MIT", "acknowledgement": "unsupported-value" } } ]
                }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("schemaVersion").GetInt32()).IsEqualTo(1);
            var components = report.RootElement.GetProperty("components");
            var component = components[0];
            await Assert.That(component.TryGetProperty("evidence", out _)).IsFalse();
            var evidence = component.GetProperty("licenseCandidates")[0].GetProperty("evidence");
            await Assert.That(evidence.GetProperty("type").GetString()).IsEqualTo("sbom");
            await Assert.That(evidence.GetProperty("field").GetString()).IsEqualTo("licenses");
            await Assert.That(evidence.GetProperty("acknowledgement").GetString()).IsEqualTo("concluded");
            await Assert.That(evidence.TryGetProperty("attested", out _)).IsFalse();
            await Assert.That(components[1].GetProperty("licenseCandidates")[0].GetProperty("evidence").GetProperty("acknowledgement").GetString()).IsEqualTo("declared");
            await Assert.That(components[2].GetProperty("licenseCandidates")[0].GetProperty("evidence").TryGetProperty("acknowledgement", out _)).IsFalse();
            await Assert.That(stderr).IsEmpty();
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
                await Assert.That(stderr).Contains("  License results: 1 displayed component; 1 matched; 0 conflict; 0 unknown; 0 ambiguous; 0 invalid; 0 error");
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
    public async Task Scan_WithGroupByLicense_RendersGroupedRowsAndCanonicalJsonSummary()
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

            var (jsonExitCode, jsonStdout, jsonStderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--group-by", "license", "--format", "json", "--skip-enrichment");

            await Assert.That(jsonExitCode).IsEqualTo(0);
            await Assert.That(jsonStderr).IsEmpty();
            using var report = JsonDocument.Parse(jsonStdout);
            var summary = report.RootElement.GetProperty("summary");
            await Assert.That(summary.GetProperty("matched").GetInt32()).IsEqualTo(3);
            await Assert.That(summary.GetProperty("error").GetInt32()).IsEqualTo(0);
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
            await Assert.That(component.TryGetProperty("evidence", out _)).IsFalse();
            var sourceCandidate = component.GetProperty("licenseCandidates")[2];
            var source = sourceCandidate.GetProperty("evidence");
            await Assert.That(source.GetProperty("type").GetString()).IsEqualTo("source-repository");
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
    public async Task Scan_WithOnlyFailedEvidence_IncludesErrorInJsonAndHumanSummaries()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-error-summary-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(packageCacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.Forbidden, null, [], ["source_repository_fetch_failed"]));

        try
        {
            var (jsonExitCode, jsonStdout, jsonStderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--sbom", sbomPath, "--format", "json");

            await Assert.That(jsonExitCode).IsEqualTo(0);
            await Assert.That(jsonStderr).IsEmpty();
            using var report = JsonDocument.Parse(jsonStdout);
            await Assert.That(report.RootElement.GetProperty("components")[0].GetProperty("status").GetString()).IsEqualTo("error");
            await Assert.That(report.RootElement.GetProperty("summary").GetProperty("error").GetInt32()).IsEqualTo(1);

            var (textExitCode, _, textStderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--sbom", sbomPath, "--format", "text");

            await Assert.That(textExitCode).IsEqualTo(0);
            await Assert.That(textStderr).Contains("1 error");
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

    [Test]
    public async Task Scan_WithCacheDir_UsesIsolatedCategorySubdirectories()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cache-dir-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var cacheDirectory = Path.Combine(temporaryDirectory, "cache");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(Path.Combine(cacheDirectory, "package-metadata")).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--format", "json", "--cache-dir", cacheDirectory, "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("components")[0].GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("packageMetadata").GetProperty("cacheHitCount").GetInt32()).IsEqualTo(1);
            await Assert.That(stdout).DoesNotContain(cacheDirectory);
            await Assert.That(stderr).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithSkipEnrichment_ProducesDeterministicSbomOnlyReport()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-sbom-only-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var unusedCacheFile = Path.Combine(temporaryDirectory, "unused-cache");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await File.WriteAllTextAsync(unusedCacheFile, "must remain untouched", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--format", "json", "--skip-enrichment", "--cache-dir", unusedCacheFile);

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("licenseCandidates").GetArrayLength()).IsEqualTo(1);
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("packageMetadata").GetProperty("supportedComponentCount").GetInt32()).IsEqualTo(0);
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("sourceRepository").GetProperty("targetCount").GetInt32()).IsEqualTo(0);
            await Assert.That(await File.ReadAllTextAsync(unusedCacheFile)).IsEqualTo("must remain untouched");
            await Assert.That(stderr).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task CacheClear_WithCacheDir_RemovesOnlyManagedCategorySubdirectories()
    {
        var root = FindRepositoryRoot();
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"ol-cache-clear-dir-{Guid.NewGuid():N}");
        var sentinelPath = Path.Combine(cacheDirectory, "keep.txt");
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(sentinelPath, "keep", Encoding.UTF8);
        await new PackageMetadataCache(Path.Combine(cacheDirectory, "package-metadata")).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(Path.Combine(cacheDirectory, "source-repository")).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.NotFound, null, [], []));

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "cache", "clear", "all", "--cache-dir", cacheDirectory);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(File.Exists(sentinelPath)).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(cacheDirectory, "package-metadata"))).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(cacheDirectory, "source-repository"))).IsFalse();
            await Assert.That(stderr).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, recursive: true);
        }
    }

    [Test]
    public async Task CacheClear_WithCacheDirPointingToFile_RejectsWithoutDeletingFile()
    {
        var root = FindRepositoryRoot();
        var filePath = Path.Combine(Path.GetTempPath(), $"ol-cache-file-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(filePath, "keep", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "cache", "clear", "--cache-dir", filePath);

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(File.Exists(filePath)).IsTrue();
            await Assert.That(stderr).Contains("Invalid cache directory");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public async Task Scan_WithInvalidViewOptions_ReturnsConciseErrorBeforeScanning()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-invalid-view-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var cases = new[]
            {
                (Option: "--dependency", Value: "nonsense", Message: "Unknown dependency value: nonsense"),
                (Option: "--sort", Value: "nonsense", Message: "Unknown sort key: nonsense"),
                (Option: "--group-by", Value: "nonsense", Message: "Unknown group key: nonsense"),
                (Option: "--dependency", Value: ",", Message: "Dependency filter must contain at least one value."),
                (Option: "--sort", Value: ",", Message: "Sort must contain at least one key."),
                (Option: "--group-by", Value: ",", Message: "Group-by must contain at least one key."),
            };

            foreach (var item in cases)
            {
                var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--skip-enrichment", item.Option, item.Value);

                await Assert.That(exitCode).IsEqualTo(1);
                await Assert.That(stdout).IsEmpty();
                await Assert.That(stderr.Trim()).IsEqualTo($"Invalid scan option: {item.Message}");
            }
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithMalformedSbom_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-malformed-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, "not-json", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--skip-enrichment");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).StartsWith("Unable to scan SBOM:");
            await Assert.That(stderr).DoesNotContain("   at ");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithIncompleteSpdxData_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-spdx-incomplete-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var spdxDirectory = Path.Combine(temporaryDirectory, "spdx");
        Directory.CreateDirectory(spdxDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--skip-enrichment", "--spdx-data", spdxDirectory);

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to load SPDX data: SPDX data directory must contain licenses.json and exceptions.json.");

            await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "licenses.json"), "{}", Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "exceptions.json"), """{ "exceptions": [] }""", Encoding.UTF8);
            var (invalidExitCode, invalidStdout, invalidStderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--skip-enrichment", "--spdx-data", spdxDirectory);

            await Assert.That(invalidExitCode).IsEqualTo(1);
            await Assert.That(invalidStdout).IsEmpty();
            await Assert.That(invalidStderr).StartsWith("Unable to load SPDX data:");
            await Assert.That(invalidStderr).DoesNotContain("   at ");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryAsOutFile_ReturnsConciseErrorWithoutPrimaryOutput()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-invalid-out-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var outDirectory = Path.Combine(temporaryDirectory, "report");
        Directory.CreateDirectory(outDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", sbomPath, "--skip-enrichment", "--out", outDirectory);

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).StartsWith("Unable to write report:");
            await Assert.That(stderr).DoesNotContain("   at ");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithNuGetAssetsAndSkipEnrichment_AcceptsRegisteredInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--skip-enrichment", "--format", "json");

        await Assert.That(exitCode).IsEqualTo(0);
        using var report = JsonDocument.Parse(stdout);
        var input = report.RootElement.GetProperty("metadata").GetProperty("input");
        await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("package-manager");
        await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("nuget-assets");
        await Assert.That(input.TryGetProperty("sbomRef", out _)).IsFalse();
        await Assert.That(report.RootElement.GetProperty("components").EnumerateArray().Any(static component => component.GetProperty("purl").GetString() == "pkg:nuget/Native.Package@4.0.0")).IsTrue();
        await Assert.That(stderr).IsEmpty();
    }

    [Test]
    public async Task Scan_WithNuGetAssetsAndCachedMetadata_ReusesNuGetEnrichment()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-nuget-assets-{Guid.NewGuid():N}");
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");
        var cache = new PackageMetadataCache(Path.Combine(temporaryDirectory, "package-metadata"));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Direct.Package@1.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Shared.Package@2.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Native.Package@4.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Project.Transitive@3.0.0", "nuget-registry", "MIT", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--cache-dir", temporaryDirectory, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("packageMetadata");
            await Assert.That(metadata.GetProperty("cacheHitCount").GetInt32()).IsEqualTo(6);
            await Assert.That(metadata.GetProperty("cacheMissCount").GetInt32()).IsEqualTo(0);
            await Assert.That(report.RootElement.GetProperty("components").EnumerateArray().Where(static component => component.GetProperty("ecosystem").GetString() == "nuget").All(static component => component.GetProperty("license").GetString() == "MIT")).IsTrue();
            await Assert.That(stderr).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
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
