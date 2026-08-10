using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Ol.Core.Licensing;
using Ol.Core.PackageMetadata;

namespace Ol.Tests;

public sealed class CliScanTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Scan_Help_DoesNotAdvertiseRemovedSbomOption()
    {
        var root = FindRepositoryRoot();

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--help");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        await Assert.That(stdout).Contains("--input <string[]>");
        await Assert.That(stdout).Contains("Repeatable resolved dependency input files or directories. [Required]");
        await Assert.That(stdout).Contains("--input-format <string>");
        await Assert.That(stdout).Contains("[Default: @\"auto\"]");
        await Assert.That(stdout).Contains("auto (default), cyclonedx, spdx, nuget-assets, npm-package-lock, pnpm-lock, yarn-classic-lock, yarn-berry-lock, cargo-metadata, go-module-graph, pip-inspect, composer-lock, bundler-lock, maven-dependency-tree, swift-package-resolved, or cocoapods-lock");
        await Assert.That(stdout).Contains("Maximum concurrent package metadata and source repository lookups.");
        await Assert.That(stdout).Contains("Retry count for package registry and GitHub License API requests.");
        await Assert.That(stdout).DoesNotContain("--sbom");
        await Assert.That(stdout).DoesNotContain("--out");
    }

    [Test]
    public async Task Scan_WithoutInput_WritesFrameworkParseErrorToStderr()
    {
        var root = FindRepositoryRoot();

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--no-external-evidence");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr.Trim()).IsEqualTo("Required argument 'input' was not specified.");
    }

    [Test]
    public async Task Scan_WithRemovedSbomOption_ReturnsUnknownOptionError()
    {
        var root = FindRepositoryRoot();

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", "removed.json");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr.Trim()).IsEqualTo("Argument '--sbom' is not recognized.");
    }

    [Test]
    [Arguments("--out")]
    [Arguments("--out-file")]
    public async Task Scan_WithRemovedOutputFileOption_ReturnsUnknownOptionError(string option)
    {
        var root = FindRepositoryRoot();

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", option, "removed.json");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr.Trim()).IsEqualTo($"Argument '{option}' is not recognized.");
    }

    [Test]
    public async Task Scan_WithInputFormatOmitted_AutoDetectsCycloneDx()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            await Assert.That(stdout[^2]).IsEqualTo('}');
            await Assert.That(stdout[^1]).IsEqualTo('\n');
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata");
            var tool = metadata.GetProperty("tool");
            await Assert.That(tool.GetProperty("name").GetString()).IsEqualTo("ol");
            await Assert.That(tool.GetProperty("version").GetString()).IsEqualTo(ToolVersion);
            await Assert.That(tool.GetProperty("informationUri").GetString()).IsEqualTo("https://github.com/guitarrapc/ol");
            await Assert.That(metadata.GetProperty("input").GetProperty("format").GetString()).IsEqualTo("cyclonedx");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithNoExternalEvidenceFor_RecordsSkippedCollectionAsUnknown()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "internal", "version": "1.0.0", "purl": "pkg:nuget/MyCompany.Internal@1.0.0" }] }""",
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--skip-evidence-packages", "pkg:nuget/MyCompany.");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("unknown");
            await Assert.That(stdout).Contains("external_evidence_not_collected");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_VerboseWithNoExternalEvidenceFor_ReportsMatchesPerPrefix()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "internal", "version": "1.0.0", "purl": "pkg:nuget/MyCompany.Internal@1.0.0" }] }""",
            Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--verbose", "--skip-evidence-packages", "pkg:nuget/MyCompany.,pkg:npm/absent");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            await Assert.That(stderr).Contains("Skipped evidence prefix pkg:nuget/MyCompany. matched 1 component.");
            await Assert.That(stderr).Contains("Skipped evidence prefix pkg:npm/absent matched 0 components.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithInvalidNoExternalEvidenceFor_ReturnsNonZeroWithoutReport()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--skip-evidence-packages", "pkg:nuget/@");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("must identify at least one package or namespace");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithVerbose_AutoDetectionWritesDetectedFormatToStderr()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence", "--verbose");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr.Trim()).IsEqualTo("Detected input format: sbom/cyclonedx");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithExplicitAutoInputFormat_AutoDetectsNuGetAssets()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "auto", "--format", "json", "--no-external-evidence");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        using var report = JsonDocument.Parse(stdout);
        await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("input").GetProperty("format").GetString()).IsEqualTo("nuget-assets");
    }

    [Test]
    public async Task Scan_WithAutoInputFormatThatIsAmbiguousOrUnknown_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        var cases = new[]
        {
            (Input: """{ "bomFormat": "CycloneDX", "spdxVersion": "SPDX-2.3", "components": [], "packages": [] }""", Message: "Ambiguous dependency input format: multiple registered format signatures matched."),
            (Input: """{ "targets": {} }""", Message: "Unsupported dependency input format: no registered format signature matched."),
        };

        try
        {
            foreach (var item in cases)
            {
                await File.WriteAllTextAsync(inputPath, item.Input, Encoding.UTF8);
                var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence");

                await Assert.That(exitCode).IsEqualTo(1);
                await Assert.That(stdout).IsEmpty();
                await Assert.That(stderr.Trim()).IsEqualTo($"Unable to scan input: {item.Message}");
            }
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithExplicitCycloneDxInput_EmitsGenericAndLegacyInputMetadata()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "cyclonedx", "--format", "json", "--no-external-evidence");

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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "spdx", "--format", "json", "--no-external-evidence");

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
                (Arguments: new[] { "--input", inputPath, "--input-format", "unknown" }, Message: "Unsupported input format: unknown"),
            };

            foreach (var item in cases)
            {
                var arguments = new string[item.Arguments.Length + 2];
                arguments[0] = "scan";
                arguments[1] = "--no-external-evidence";
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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "cyclonedx", "--no-external-evidence");

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
            var (exitCode, stdout, stderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

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
    public async Task Scan_WithInputDeclaringNoComponents_ReportsAnEmptyInputWarningInJson()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var warnings = report.RootElement.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!).ToArray();
            await Assert.That(warnings).IsEquivalentTo(new[] { "input_declares_no_components" });
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    [Arguments("text")]
    [Arguments("markdown")]
    public async Task Scan_WithInputDeclaringNoComponents_StatesItInThePrimaryResultEvenWhenQuiet(string format)
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", format, "--no-external-evidence", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("No components");
            await Assert.That(stdout).Contains("declares no resolved dependencies");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    // A view that displays nothing is not an input that declares nothing: the inventory decides.
    [Test]
    public async Task Scan_WithEveryComponentFilteredOutOfTheView_DoesNotReportAnEmptyInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [ { "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0" } ] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "text", "--no-external-evidence", "--dependency", "direct", "--quiet");
            var (jsonExitCode, json, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence", "--dependency", "direct");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).DoesNotContain("No components");
            await Assert.That(jsonExitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(json);
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(0);
            await Assert.That(report.RootElement.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEmpty();
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithComponents_DoesNotReportAnEmptyInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [ { "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEmpty();
        }
        finally
        {
            File.Delete(inputPath);
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
            var (exitCode, stdout, stderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", sbomPath, "--format", "json");

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
            var (exitCode, stdout, stderr) = await RunOlWithCacheAsync(root, cacheRoot, "scan", "--input", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

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
            var (exitCode, stdout, _) = await RunOlWithCacheAsync(root, cacheRoot, "scan", "--input", sbomPath, "--format", "json", "--refresh");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("packageMetadata");
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(component.GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(metadata.GetProperty("cacheHitCount").GetInt32()).IsEqualTo(0);
            await Assert.That(metadata.GetProperty("cacheMissCount").GetInt32()).IsEqualTo(1);
            await Assert.That(metadata.GetProperty("refreshedCount").GetInt32()).IsEqualTo(0);
            // The registry answers 404 for an unpublished package when the network is available and fails transport
            // otherwise, and those are now different warnings. Either way the collection outcome must be recorded and
            // must not override the SBOM claim.
            var warnings = component.GetProperty("warnings").EnumerateArray().Select(static value => value.GetString()).ToArray();
            await Assert.That(warnings.Any(static warning => warning is not null && warning.StartsWith("package_metadata_", StringComparison.Ordinal))).IsTrue();
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
        await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "licenses.json"), """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "GPL-2.0", "isDeprecatedLicenseId": true }, { "licenseId": "MIT", "isDeprecatedLicenseId": false } ] }""", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "exceptions.json"), """{ "licenseListVersion": "3.27.0", "exceptions": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json", "--spdx-data", spdxDirectory);

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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--dependency", "direct");

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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json");

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
                var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", format);

                await Assert.That(exitCode).IsEqualTo(0);
                await Assert.That(stderr).StartsWith($"{Environment.NewLine}Scan summary{Environment.NewLine}");
                await Assert.That(stderr).Contains("  License results: 1 displayed component; 1 matched; 0 conflict; 0 unknown; 0 ambiguous; 0 invalid; 0 error");
                await Assert.That(stderr).Contains("  Package metadata (full scan):");
                await Assert.That(stderr).Contains("  Source repositories (full scan):");
                await Assert.That(stderr).Contains("  Input:");
            }

            var (quietExitCode, _, quietStderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "text", "--quiet");
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
        await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "licenses.json"), """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "MIT", "isDeprecatedLicenseId": false } ] }""", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "exceptions.json"), """{ "licenseListVersion": "3.27.0", "exceptions": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json", "--spdx-data", spdxDirectory);

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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--group-by", "license");
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"ol exited with {exitCode}. stdout: {stdout} stderr: {stderr}");
            }

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("LICENSE COUNT");
            await Assert.That(stdout).Contains("Apache-2.0 1");
            await Assert.That(stdout).Contains("MIT 2");
            await Assert.That(stderr).Contains("License results: 3 displayed components");

            var (jsonExitCode, jsonStdout, jsonStderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--group-by", "license", "--format", "json", "--no-external-evidence");

            await Assert.That(jsonExitCode).IsEqualTo(0);
            await Assert.That(jsonStderr).IsEmpty();
            using var report = JsonDocument.Parse(jsonStdout);
            var tool = report.RootElement.GetProperty("metadata").GetProperty("tool");
            await Assert.That(tool.GetProperty("name").GetString()).IsEqualTo("ol");
            await Assert.That(tool.GetProperty("version").GetString()).IsEqualTo(ToolVersion);
            await Assert.That(tool.GetProperty("informationUri").GetString()).IsEqualTo("https://github.com/guitarrapc/ol");
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
            var (exitCode, stdout, _) = await RunOlWithCacheAsync(root, cacheRoot, "scan", "--input", sbomPath, "--format", "json", "--sort", "ECOSYSTEM,NAME", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var components = report.RootElement.GetProperty("components");
            // "zebra" and "zebra-copy" state one purl, which is what CycloneDX declares identity to be, so they are
            // one component named by the first entry. Both entries remain as occurrences of it.
            await Assert.That(components.GetArrayLength()).IsEqualTo(2);
            await Assert.That(components[0].GetProperty("name").GetString()).IsEqualTo("alpha");
            await Assert.That(components[1].GetProperty("name").GetString()).IsEqualTo("zebra");
            await Assert.That(components[0].GetProperty("license").GetString()).IsEqualTo("Apache-2.0");
            await Assert.That(components[1].GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(report.RootElement.GetProperty("inventory").GetProperty("occurrences").GetArrayLength()).IsEqualTo(3);
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
            var (exitCode, stdout, _) = await RunOlWithEnvironmentAsync(root, packageCacheRoot, sourceCacheRoot, environment, "scan", "--input", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

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
            var (exitCode, stdout, stderr) = await RunOlWithEnvironmentAsync(root, packageCacheRoot, sourceCacheRoot, environment, "scan", "--input", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

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
            var (jsonExitCode, jsonStdout, jsonStderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", sbomPath, "--format", "json");

            await Assert.That(jsonExitCode).IsEqualTo(0);
            await Assert.That(jsonStderr).IsEmpty();
            using var report = JsonDocument.Parse(jsonStdout);
            await Assert.That(report.RootElement.GetProperty("components")[0].GetProperty("status").GetString()).IsEqualTo("error");
            await Assert.That(report.RootElement.GetProperty("summary").GetProperty("error").GetInt32()).IsEqualTo(1);

            var (textExitCode, _, textStderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", sbomPath, "--format", "text");

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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json", "--cache-dir", cacheDirectory, "--concurrency", "1", "--retry", "0");

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
    public async Task Scan_WithNoExternalEvidence_ProducesDeterministicSbomOnlyReport()
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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json", "--no-external-evidence", "--cache-dir", unusedCacheFile);

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
    public async Task Scan_WithNoExternalEvidence_ReportsUncollectedEvidenceInsteadOfZeroCounters()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-no-external-summary-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).Contains("  External evidence: not collected; package registries, source repositories, and their caches were not read");
            await Assert.That(stderr).DoesNotContain("(full scan)");
            await Assert.That(stderr).DoesNotContain("GitHub auth");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithRemovedSkipEnrichmentOption_RejectsUnknownOption()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-removed-option-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--skip-enrichment");

            await Assert.That(exitCode).IsNotEqualTo(0);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("'--skip-enrichment' is not recognized");
        }
        finally
        {
            File.Delete(sbomPath);
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
                var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", item.Option, item.Value);

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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).StartsWith("Unable to scan input:");
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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--spdx-data", spdxDirectory);

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to load SPDX data: SPDX data directory must contain licenses.json and exceptions.json.");

            await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "licenses.json"), "{}", Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "exceptions.json"), """{ "exceptions": [] }""", Encoding.UTF8);
            var (invalidExitCode, invalidStdout, invalidStderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--spdx-data", spdxDirectory);

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
    public async Task Scan_WithNuGetAssetsAndNoExternalEvidence_AcceptsRegisteredInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "json");

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
    public async Task Scan_WithNpmPackageLockDirectoryAndNoExternalEvidence_PreservesInventoryVariants()
    {
        var root = FindRepositoryRoot();
        var inputDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputDirectory, "--input-format", "npm-package-lock", "--no-external-evidence", "--format", "json");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        using var report = JsonDocument.Parse(stdout);
        var input = report.RootElement.GetProperty("metadata").GetProperty("input");
        await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("package-manager");
        await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("npm-package-lock");
        await Assert.That(input.GetProperty("specificationVersion").GetString()).IsEqualTo("3");
        await Assert.That(input.TryGetProperty("sbomRef", out _)).IsFalse();
        var inventory = report.RootElement.GetProperty("inventory");
        await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
        await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(7);
        await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(9);
        await Assert.That(inventory.GetProperty("occurrences").EnumerateArray().Any(static occurrence => occurrence.TryGetProperty("variant", out var variant) && variant.GetString() == "optional;os=linux,!win32;cpu=x64")).IsTrue();
        await Assert.That(stdout).DoesNotContain("node_modules/workspace-a\"");
    }

    [Test]
    public async Task Scan_WithGoResolvedPairDirectory_CombinesCompanionFilesAsOneInput()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-go-module-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "go-list-modules.json"), Path.Combine(temporaryDirectory, "go-list-modules.json"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "go-mod-graph.txt"), Path.Combine(temporaryDirectory, "go-mod-graph.txt"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("package-manager");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("go-module-graph");
            await Assert.That(report.RootElement.GetProperty("inventory").GetProperty("components").GetArrayLength()).IsEqualTo(5);
            await Assert.That(stdout).DoesNotContain("/private/repo/local");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithIncompleteGoResolvedPair_ReturnsCompanionError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-go-module-graph-incomplete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "go-list-modules.json"), Path.Combine(temporaryDirectory, "go-list-modules.json"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("requires companion file go-mod-graph.txt in the same directory");
            await Assert.That(stderr).DoesNotContain("   at ");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithComposerResolvedPairDirectory_CombinesCompanionFilesAsOneInput()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-composer-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "composer.json"), Path.Combine(temporaryDirectory, "composer.json"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "composer.lock"), Path.Combine(temporaryDirectory, "composer.lock"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("package-manager");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("composer-lock");
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(5);
            await Assert.That(inventory.GetProperty("edges").GetArrayLength()).IsEqualTo(5);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithPipInspect_ReportsResolvedEnvironmentWithoutPrivatePaths()
    {
        var root = FindRepositoryRoot();
        var input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pip-inspect.json");

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", input, "--no-external-evidence", "--format", "json");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        using var report = JsonDocument.Parse(stdout);
        var metadata = report.RootElement.GetProperty("metadata").GetProperty("input");
        await Assert.That(metadata.GetProperty("kind").GetString()).IsEqualTo("package-manager");
        await Assert.That(metadata.GetProperty("format").GetString()).IsEqualTo("pip-inspect");
        await Assert.That(report.RootElement.GetProperty("inventory").GetProperty("components").GetArrayLength()).IsEqualTo(5);
        await Assert.That(stdout).DoesNotContain("C:/private/project");
        await Assert.That(stdout).DoesNotContain("file:///");
    }

    [Test]
    public async Task Scan_WithBundlerLockDirectory_ReportsPlatformContextsWithoutPrivateSources()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-bundler-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gemfile.lock"), Path.Combine(temporaryDirectory, "Gemfile.lock"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(metadata.GetProperty("kind").GetString()).IsEqualTo("package-manager");
            await Assert.That(metadata.GetProperty("format").GetString()).IsEqualTo("bundler-lock");
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(7);
            await Assert.That(stdout).DoesNotContain("vendor/local-gem");
            await Assert.That(stdout).DoesNotContain("github.com/example/private-gem");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithSwiftAndCocoaPodsDirectory_CombinesResolvedAppleInputs()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-apple-locks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.resolved"), Path.Combine(temporaryDirectory, "Package.resolved"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Podfile.lock"), Path.Combine(temporaryDirectory, "Podfile.lock"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(metadata.GetProperty("kind").GetString()).IsEqualTo("package-manager");
            await Assert.That(metadata.GetProperty("format").GetString()).IsEqualTo("collection");
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithRepeatedNpmPackageLocks_CombinesSparseVariantIndexes()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-npm-directory-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(temporaryDirectory, "First");
        var secondDirectory = Path.Combine(temporaryDirectory, "Second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "package-lock.json");
        File.Copy(fixture, Path.Combine(firstDirectory, "package-lock.json"));
        File.Copy(fixture, Path.Combine(secondDirectory, "package-lock.json"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", firstDirectory, "--input", secondDirectory, "--input-format", "npm-package-lock", "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(7);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(18);
            await Assert.That(inventory.GetProperty("occurrences").EnumerateArray().Count(static occurrence => occurrence.TryGetProperty("variant", out _))).IsEqualTo(6);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectory_CombinesNestedNuGetAssetsWithoutDuplicateComponents()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-nuget-directory-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(temporaryDirectory, "First", "obj");
        var secondDirectory = Path.Combine(temporaryDirectory, "Second", "obj");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"));
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "project.assets.json"), fixture, Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(secondDirectory, "project.assets.json"),
            fixture.Replace("/private/src/App/App.csproj", "/private/src/Second/Second.csproj", StringComparison.Ordinal),
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("nuget-assets");
            await Assert.That(input.GetProperty("sourceRef").GetString()).IsEqualTo(Path.GetFileName(temporaryDirectory));
            await Assert.That(input.GetProperty("sourceSha256").GetString()!.Length).IsEqualTo(64);
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(12);
            await Assert.That(inventory.GetProperty("edges").GetArrayLength()).IsEqualTo(10);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithRepeatedInputs_CombinesBothDirectories()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-repeated-input-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(temporaryDirectory, "First,WithComma");
        var secondDirectory = Path.Combine(temporaryDirectory, "Second");
        Directory.CreateDirectory(Path.Combine(firstDirectory, "obj"));
        Directory.CreateDirectory(Path.Combine(secondDirectory, "obj"));
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"));
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "obj", "project.assets.json"), fixture, Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(secondDirectory, "obj", "project.assets.json"),
            fixture.Replace("/private/src/App/App.csproj", "/private/src/Second/Second.csproj", StringComparison.Ordinal),
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", firstDirectory, "--input", secondDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("sourceRef").GetString()).IsEqualTo("2 inputs");
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(12);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryWithoutNuGetAssets_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-empty-nuget-directory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: No registered dependency input files were found in the input directories.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory);
        }
    }

    [Test]
    public async Task Scan_WithOverlappingRepeatedDirectories_ScansEachFileOnce()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-overlapping-input-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "obj"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "obj", "project.assets.json"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--input", projectDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(6);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithUnresolvedRootComponent_OmitsItFromTheUnresolvedSection()
    {
        // The section answers "what does a reviewer do next", and policy never evaluates a root component, so
        // listing one asks for work that no check will ever require. A generator scanning a directory names its
        // root after that directory, which is also how an absolute local path reaches the section.
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-root-unresolved-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "metadata": { "component": { "bom-ref": "root-app", "type": "application", "name": "/private/build/workspace", "version": "0.0.0", "licenses": [ { "license": { "name": "See URL", "url": "https://example.test/root-LICENSE.txt" } } ] } },
          "components": [
            { "bom-ref": "dep", "type": "library", "name": "Example", "version": "1.0.0", "purl": "pkg:nuget/Example@1.0.0", "licenses": [ { "license": { "name": "See URL", "url": "https://example.test/LICENSE.txt" } } ] }
          ],
          "dependencies": [ { "ref": "root-app", "dependsOn": ["dep"] } ]
        }
        """, Encoding.UTF8);
        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--format", "text", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            // The root still appears in the table: the report must not stop saying what the input described.
            await Assert.That(stdout).Contains("/private/build/workspace 0.0.0");
            await Assert.That(stdout).Contains("Unresolved components");
            await Assert.That(stdout).Contains("Example 1.0.0 declared_license_location_not_collected https://example.test/LICENSE.txt");
            var unresolved = stdout[stdout.IndexOf("Unresolved components", StringComparison.Ordinal)..];
            await Assert.That(unresolved).DoesNotContain("/private/build/workspace");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithSbomAndPackageManagerInput_ScansThemAsOneCollection()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-mixed-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var assetsPath = Path.Combine(temporaryDirectory, "project.assets.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"), assetsPath);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--input", assetsPath, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("collection");
            // The SBOM declares no components, so the collection is exactly the package-manager population.
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithNuGetAssetsJson_PreservesDeterministicInventoryGraph()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");

        var first = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "json");
        var second = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "json");

        await Assert.That(first.ExitCode).IsEqualTo(0);
        await Assert.That(second.ExitCode).IsEqualTo(0);
        await Assert.That(first.Stdout).IsEqualTo(second.Stdout);
        using var report = JsonDocument.Parse(first.Stdout);
        var inventory = report.RootElement.GetProperty("inventory");
        await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
        await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(4);
        await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(6);
        await Assert.That(inventory.GetProperty("edges").GetArrayLength()).IsEqualTo(5);
        var winContext = inventory.GetProperty("contexts")[1];
        await Assert.That(winContext.GetProperty("projectOrigin").GetString()).IsEqualTo("App.csproj");
        await Assert.That(first.Stdout).DoesNotContain("/private/src");
        await Assert.That(winContext.GetProperty("target").GetString()).IsEqualTo("net8.0");
        await Assert.That(winContext.GetProperty("runtime").GetString()).IsEqualTo("win-x64");
        await Assert.That(winContext.GetProperty("platform").GetString()).IsEmpty();
        await Assert.That(winContext.GetProperty("architecture").GetString()).IsEmpty();
        await Assert.That(inventory.GetProperty("occurrences")[1].GetProperty("componentIndex").GetInt32()).IsEqualTo(1);
        await Assert.That(inventory.GetProperty("edges")[0].GetProperty("fromOccurrenceIndex").GetInt32()).IsEqualTo(-1);
    }

    [Test]
    public async Task Scan_WithUnresolvedComponents_ExplainsEachOneInHumanFormats()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-unresolved-{Guid.NewGuid():N}");
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source");
        var cache = new PackageMetadataCache(packageCacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Shared.Package@2.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Native.Package@4.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Project.Transitive@3.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        try
        {
            var text = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--skip-evidence-packages", "pkg:nuget/Direct.Package", "--format", "text", "--quiet");
            var markdown = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--skip-evidence-packages", "pkg:nuget/Direct.Package", "--format", "markdown", "--quiet");

            await Assert.That(text.ExitCode).IsEqualTo(0);
            await Assert.That(text.Stdout).Contains("Unresolved components");
            await Assert.That(text.Stdout).Contains("Direct.Package 1.0.0 external_evidence_not_collected");
            // A resolved component is not restated in the section.
            await Assert.That(text.Stdout).DoesNotContain("  Shared.Package 2.0.0");
            await Assert.That(markdown.ExitCode).IsEqualTo(0);
            await Assert.That(markdown.Stdout).Contains("## Unresolved components");
            await Assert.That(markdown.Stdout).Contains("| Direct.Package | 1.0.0 | external_evidence_not_collected |");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    // A publisher that could not state an SPDX expression often states where the license is instead.
    // That pointer is the one actionable fact about such a component, so it has to reach the report
    // even though no collection mechanism failed and therefore no warning names one.
    [Test]
    public async Task Scan_WithDeclaredLicenseLocation_ListsItInTheUnresolvedSection()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "Example",
              "version": "1.0.0",
              "purl": "pkg:nuget/Example@1.0.0",
              "licenses": [ { "license": { "name": "Unknown - See URL", "url": "https://example.test/LICENSE.txt" } } ]
            }
          ]
        }
        """, Encoding.UTF8);
        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--format", "text", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("Unresolved components");
            await Assert.That(stdout).Contains("Example 1.0.0 declared_license_location_not_collected https://example.test/LICENSE.txt");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    // The reason a declared license went unread is the same fact in every ecosystem, and the kind of
    // place the publisher named is what a reviewer acts on. Neither depends on which registry answered.
    [Test]
    [Arguments("pkg:cargo/example@1.0.0", DeclaredLicenseReferenceKind.ArtifactPath, "LICENSE-APACHE", "declared_license_file_not_collected LICENSE-APACHE")]
    [Arguments("pkg:pypi/example@1.0.0", DeclaredLicenseReferenceKind.ArtifactPath, "LICENSE.rst", "declared_license_file_not_collected LICENSE.rst")]
    [Arguments("pkg:cocoapods/Example@1.0.0", DeclaredLicenseReferenceKind.InlineText, "", "declared_license_text_not_collected")]
    [Arguments("pkg:nuget/Example@1.0.0", DeclaredLicenseReferenceKind.Location, "https://example.test/eula", "declared_license_location_not_collected https://example.test/eula")]
    public async Task Scan_WithDeclaredLicenseReference_NamesTheSameMechanismInEveryEcosystem(string purl, DeclaredLicenseReferenceKind kind, string reference, string expected)
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-declared-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "input.cdx.json");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, $$"""
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            { "type": "library", "name": "Example", "version": "1.0.0", "purl": "{{purl}}" }
          ]
        }
        """, Encoding.UTF8);
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var cache = new PackageMetadataCache(packageCacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord(purl, "package-registry", string.Empty, string.Empty, [], [], DateTimeOffset.UtcNow, string.Empty, kind, reference));
        try
        {
            var text = await RunOlWithCachesAsync(root, packageCacheRoot, Path.Combine(temporaryDirectory, "source"), "scan", "--input", sbomPath, "--format", "text", "--quiet");

            await Assert.That(text.ExitCode).IsEqualTo(0);
            await Assert.That(text.Stdout).Contains($"Example 1.0.0 {expected}");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    // A classifier that names a license family is unresolvable by construction, and saying so is the
    // only thing a report can add: the reviewer's next step is to ask the publisher or read the
    // artifact, not to wait for Ol to gain a capability.
    [Test]
    [Arguments("License :: OSI Approved :: BSD License", "license_classifier_not_specific")]
    [Arguments("License :: OSI Approved :: Apache Software License", "license_classifier_not_specific")]
    public async Task Scan_WithLicenseFamilyClassifier_ExplainsWhyItCannotResolve(string declared, string expectedReason)
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-classifier-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, $$"""
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "example",
              "version": "1.0.0",
              "purl": "pkg:pypi/example@1.0.0",
              "licenses": [ { "license": { "name": "{{declared}}" } } ]
            }
          ]
        }
        """, Encoding.UTF8);
        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--format", "text", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains($"example 1.0.0 {expectedReason}");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    // Embedded license text is a declaration with no place to name, and Ol deliberately never retains
    // the text. Its empty value must not be presented as the reference, nor suppress one that exists.
    [Test]
    public async Task Scan_WithEmbeddedLicenseTextBesideALocation_ReportsTheLocationRatherThanAnEmptyReference()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-inline-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "input.cdx.json");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "Example",
              "version": "1.0.0",
              "purl": "pkg:cocoapods/Example@1.0.0",
              "licenses": [ { "license": { "name": "Facebook Platform License", "url": "https://example.test/platform-license" } } ]
            }
          ]
        }
        """, Encoding.UTF8);
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var cache = new PackageMetadataCache(packageCacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:cocoapods/Example@1.0.0", "package-registry", string.Empty, string.Empty, [], [], DateTimeOffset.UtcNow, string.Empty, DeclaredLicenseReferenceKind.InlineText, string.Empty));
        try
        {
            var text = await RunOlWithCachesAsync(root, packageCacheRoot, Path.Combine(temporaryDirectory, "source"), "scan", "--input", sbomPath, "--format", "text", "--quiet");
            var json = await RunOlWithCachesAsync(root, packageCacheRoot, Path.Combine(temporaryDirectory, "source"), "scan", "--input", sbomPath, "--format", "json", "--quiet");

            await Assert.That(text.ExitCode).IsEqualTo(0);
            await Assert.That(text.Stdout).Contains("Example 1.0.0 declared_license_text_not_collected https://example.test/platform-license");

            using var report = JsonDocument.Parse(json.Stdout);
            var kinds = report.RootElement.GetProperty("components")[0].GetProperty("licenseCandidates").EnumerateArray()
                .Select(static candidate => candidate.TryGetProperty("evidence", out var evidence) && evidence.TryGetProperty("declaredLicenseReferenceKind", out var kind) ? kind.GetString() : null)
                .Where(static value => value is not null)
                .ToArray();

            await Assert.That(kinds).Contains("inline-text");
            await Assert.That(kinds).Contains("location");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task Scan_WithEveryComponentResolved_OmitsUnresolvedSection()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-resolved-{Guid.NewGuid():N}");
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var cache = new PackageMetadataCache(packageCacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Direct.Package@1.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Shared.Package@2.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Native.Package@4.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Project.Transitive@3.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        try
        {
            var text = await RunOlWithCachesAsync(root, packageCacheRoot, Path.Combine(temporaryDirectory, "source"), "scan", "--input", inputPath, "--input-format", "nuget-assets", "--format", "text", "--quiet");

            await Assert.That(text.ExitCode).IsEqualTo(0);
            await Assert.That(text.Stdout).DoesNotContain("Unresolved components");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task Scan_WithNuGetAssetsHumanFormats_DisplaysInputKindAndFormat()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");

        var text = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "text", "--quiet");
        var markdown = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "markdown", "--quiet");

        await Assert.That(text.ExitCode).IsEqualTo(0);
        await Assert.That(text.Stdout).StartsWith("Input: package-manager/nuget-assets");
        await Assert.That(markdown.ExitCode).IsEqualTo(0);
        await Assert.That(markdown.Stdout).StartsWith("Input: `package-manager/nuget-assets`");
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
            await Assert.That(metadata.GetProperty("targetCount").GetInt32()).IsEqualTo(4);
            await Assert.That(metadata.GetProperty("cacheHitCount").GetInt32()).IsEqualTo(4);
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

            startInfo.ArgumentList.Add(CliTestAssembly.ResolveOlDllPath(AppContext.BaseDirectory));
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

    private static string ToolVersion
        => typeof(ScanCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFilePath)! })
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
