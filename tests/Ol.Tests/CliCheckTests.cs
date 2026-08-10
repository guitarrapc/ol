using System.Diagnostics;
using System.Text;

namespace Ol.Tests;

public sealed class CliCheckTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Check_Help_AdvertisesReportOnlyPolicyControls()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "check", "--help");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stderr).IsEmpty();
        await Assert.That(result.Stdout).Contains("--report <string>");
        await Assert.That(result.Stdout).Contains("Persisted canonical JSON scan report to evaluate. [Required]");
        await Assert.That(result.Stdout).Contains("--allow-licenses <string>");
        await Assert.That(result.Stdout).Contains("Comma-separated SPDX License Identifiers. [Required]");
        await Assert.That(result.Stdout).Contains("--exclude-packages <string?>");
        await Assert.That(result.Stdout).DoesNotContain("--input ");
        await Assert.That(result.Stdout).DoesNotContain("--input-format");
        await Assert.That(result.Stdout).DoesNotContain("--refresh");
        await Assert.That(result.Stdout).DoesNotContain("--cache-dir");
        await Assert.That(result.Stdout).DoesNotContain("--no-external-evidence");
        await Assert.That(result.Stdout).DoesNotContain("--skip-evidence-packages");
        await Assert.That(result.Stdout).DoesNotContain("--concurrency");
        await Assert.That(result.Stdout).DoesNotContain("--retry");
        await Assert.That(result.Stdout).DoesNotContain("--dependency");
        await Assert.That(result.Stdout).DoesNotContain("--format");
    }

    [Test]
    [Arguments("--input", "missing.json")]
    [Arguments("--input-format", "auto")]
    [Arguments("--refresh", null)]
    [Arguments("--cache-dir", "cache")]
    [Arguments("--no-external-evidence", null)]
    [Arguments("--skip-evidence-packages", "pkg:nuget/example")]
    [Arguments("--concurrency", "1")]
    [Arguments("--retry", "0")]
    public async Task Check_WithRemovedCollectionOption_ReturnsFrameworkParseError(string option, string? value)
    {
        var root = FindRepositoryRoot();
        var arguments = value is null
            ? new[] { "check", "--report", "missing.json", "--allow-licenses", "MIT", option }
            : new[] { "check", "--report", "missing.json", "--allow-licenses", "MIT", option, value };

        var result = await RunOlAsync(root, arguments);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr.Trim()).IsEqualTo($"Argument '{option}' is not recognized.");
    }

    [Test]
    public async Task Check_WithoutReport_ReturnsFrameworkParseError()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "check", "--allow-licenses", "MIT");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr.Trim()).IsEqualTo("Required argument 'report' was not specified.");
    }

    [Test]
    public async Task Check_WithAllowedLicense_ReturnsZeroAndPassSummary()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("License check passed: 1 component satisfies the allow-list.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithUnknownRootAndAllowedDependency_IgnoresRoot()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxWithRootAsync(rootLicense: null, dependencyLicense: "MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("License check passed: 1 component satisfies the allow-list.");
            await Assert.That(result.Stdout).DoesNotContain("application");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithForbiddenRootAndUnknownDependency_EvaluatesOnlyDependency()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxWithRootAsync(rootLicense: "GPL-3.0-only", dependencyLicense: null);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("License check failed: 1 violation.");
            await Assert.That(result.Stdout).Contains("example");
            await Assert.That(result.Stdout).Contains("license is unresolved");
            await Assert.That(result.Stdout).DoesNotContain("application");
            await Assert.That(result.Stdout).DoesNotContain("GPL-3.0-only");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithOnlyUnknownRoot_PassesWithZeroPolicyComponents()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxRootOnlyAsync();
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("License check passed: 0 components satisfy the allow-list.");
            await Assert.That(result.Stdout).DoesNotContain("application");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithForbiddenLicense_ReturnsTwoAndCompleteViolation()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("License check failed: 1 violation.");
            await Assert.That(result.Stdout).Contains("example");
            await Assert.That(result.Stdout).Contains("GPL-3.0-only");
            await Assert.That(result.Stdout).Contains("license is not allowed");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithUnknownLicense_ReturnsTwoAndUnresolvedReason()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync(null);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("unknown");
            await Assert.That(result.Stdout).Contains("license is unresolved");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("MIT,,Apache-2.0")]
    [Arguments("Unknown-License")]
    [Arguments("MIT OR Apache-2.0")]
    public async Task Check_WithInvalidAllowList_ReturnsOneWithoutPolicyOutput(string allowLicenses)
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", allowLicenses, "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Invalid license policy:");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithoutAllowList_ReturnsFrameworkParseError()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "check", "--report", "missing.json");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr.Trim()).IsEqualTo("Required argument 'allow-licenses' was not specified.");
    }

    [Test]
    public async Task ScanBeforeCheck_WithMalformedInput_ReturnsOneWithoutPartialPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, "{ malformed", Encoding.UTF8);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Unable to scan input:");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithScanViewOption_ReturnsOneAsFrameworkParseError()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunOlAsync(root, "check", "--report", inputPath, "--allow-licenses", "MIT", "--dependency", "direct");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr.Trim()).IsEqualTo("Argument '--dependency' is not recognized.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task ScanThenCheck_WithRepeatedInputs_EvaluatesCombinedInventory()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(directory, "first");
        var secondDirectory = Path.Combine(directory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = Path.Combine(firstDirectory, "package-lock.json");
        var second = Path.Combine(secondDirectory, "package-lock.json");
        await File.WriteAllTextAsync(first, """{ "lockfileVersion": 3, "packages": { "": { "name": "first" }, "node_modules/a": { "name": "a", "version": "1.0.0", "license": "MIT" } } }""", Encoding.UTF8);
        await File.WriteAllTextAsync(second, """{ "lockfileVersion": 3, "packages": { "": { "name": "second" }, "node_modules/b": { "name": "b", "version": "1.0.0", "license": "MIT" } } }""", Encoding.UTF8);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", first, "--input", second, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("2 components satisfy the allow-list.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Check_UpdateBaseline_AcknowledgesUnresolvedAndPassesOnRerun()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync(null);
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var update = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath, "--update-baseline");
            var rerun = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath);

            await Assert.That(update.ExitCode).IsEqualTo(0);
            await Assert.That(rerun.ExitCode).IsEqualTo(0);
            await Assert.That(rerun.Stdout).Contains("Acknowledged by baseline: 1 component.");
            await Assert.That(File.Exists(baselinePath)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(baselinePath)).Contains("\"status\": \"unknown\"");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(baselinePath)) File.Delete(baselinePath);
        }
    }

    [Test]
    public async Task Check_UpdateBaseline_WithUnknownRootAndDependency_RecordsOnlyDependency()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxWithRootAsync(rootLicense: null, dependencyLicense: null);
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath, "--update-baseline");
            var baseline = await File.ReadAllTextAsync(baselinePath);

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).Contains("Acknowledged by baseline: 1 component.");
            await Assert.That(baseline).Contains("\"name\": \"example\"");
            await Assert.That(baseline).DoesNotContain("\"name\": \"application\"");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(baselinePath)) File.Delete(baselinePath);
        }
    }

    [Test]
    public async Task Check_UpdateBaseline_DoesNotAcknowledgeForbiddenLicense()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath, "--update-baseline");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).Contains("license is not allowed");
            await Assert.That(await File.ReadAllTextAsync(baselinePath)).DoesNotContain("GPL-3.0-only");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(baselinePath)) File.Delete(baselinePath);
        }
    }

    [Test]
    public async Task Check_WithBaseline_WhenVersionChanges_FailsAgain()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync(null);
        var bumpedPath = await WriteCycloneDxAsync(null, "2.0.0");
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath, "--update-baseline");
            var afterBump = await RunCheckWorkflowAsync(root, "--input", bumpedPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath);

            await Assert.That(afterBump.ExitCode).IsEqualTo(2);
            await Assert.That(afterBump.Stdout).Contains("Acknowledged by baseline: 0 components.");
            await Assert.That(afterBump.Stdout).Contains("license is unresolved");
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(bumpedPath);
            if (File.Exists(baselinePath)) File.Delete(baselinePath);
        }
    }

    [Test]
    public async Task Check_WithMissingBaseline_ReturnsOneWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath);

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Unable to read baseline");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithMalformedBaseline_ReturnsOneWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(baselinePath, "{ \"schemaVersion\": 99, \"acknowledged\": [] }", Encoding.UTF8);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath);

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Unable to read baseline");
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(baselinePath);
        }
    }

    [Test]
    public async Task Check_UpdateBaselineWithoutBaselinePath_ReturnsOne()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--update-baseline");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("--update-baseline requires --baseline");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_UpdateBaseline_IsByteStableAcrossRuns()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync(null);
        var first = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        var second = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", first, "--update-baseline");
            await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", second, "--update-baseline");

            await Assert.That(await File.ReadAllBytesAsync(first)).IsEquivalentTo(await File.ReadAllBytesAsync(second));
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
        }
    }

    [Test]
    public async Task Check_WithoutBaseline_ReportsNoAcknowledgementLine()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).DoesNotContain("Acknowledged by baseline");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithDisallowedReport_ReturnsPolicyViolation()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, scan.Stdout);
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).Contains("GPL-3.0-only");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    [Test]
    public async Task Check_WithReportContainingUnknownRoot_IgnoresRoot()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxWithRootAsync(rootLicense: null, dependencyLicense: "MIT");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, scan.Stdout);
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).Contains("License check passed: 1 component satisfies the allow-list.");
            await Assert.That(result.Stdout).DoesNotContain("application");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    [Test]
    public async Task Check_WithReportAndBaseline_AcknowledgesUnresolved()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync(null);
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, scan.Stdout);
            var update = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", baselinePath, "--update-baseline");

            await Assert.That(update.ExitCode).IsEqualTo(0);
            await Assert.That(update.Stdout).Contains("Acknowledged by baseline: 1 component.");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(reportPath)) File.Delete(reportPath);
            if (File.Exists(baselinePath)) File.Delete(baselinePath);
        }
    }

    [Test]
    public async Task Check_WithMalformedReport_ReturnsOne()
    {
        var root = FindRepositoryRoot();
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(reportPath, "{ \"schemaVersion\": 99, \"components\": [] }", Encoding.UTF8);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Unable to read report");
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Test]
    public async Task Check_WithAllowDevLicenses_AllowsDevelopmentOnlyComponent()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "CC-BY-4.0", runtimeLicense: "MIT");
        try
        {
            var withoutDev = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");
            var withDev = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--no-external-evidence");

            await Assert.That(withoutDev.ExitCode).IsEqualTo(2).Because(withoutDev.Stderr);
            await Assert.That(withDev.ExitCode).IsEqualTo(0).Because(withDev.Stderr);
            await Assert.That(withDev.Stderr).IsEmpty();
            await Assert.That(withDev.Stdout).Contains("Allowed by development policy: 1 component.");
            await Assert.That(withDev.Stdout).Contains("2 components satisfy the allow-list.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_AmbiguousListing_PassesWhenTheAllowListAdmitsEveryElement()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "MIT", runtimeLicense: "MIT; Apache-2.0");
        try
        {
            var withBoth = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT,Apache-2.0", "--no-external-evidence");
            var withoutApache = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(withBoth.ExitCode).IsEqualTo(0).Because(withBoth.Stderr);
            await Assert.That(withBoth.Stdout).Contains("Allowed on every reading of ambiguous evidence: 1 component.");
            await Assert.That(withoutApache.ExitCode).IsEqualTo(2).Because(withoutApache.Stderr);
            await Assert.That(withoutApache.Stdout).Contains("ambiguous");
            await Assert.That(withoutApache.Stdout).DoesNotContain("Allowed on every reading");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithAllowDevLicenses_DoesNotAllowRuntimeComponent()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "MIT", runtimeLicense: "CC-BY-4.0");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("License check failed: 1 violation.");
            await Assert.That(result.Stdout).Contains("CC-BY-4.0");
            await Assert.That(result.Stdout).Contains("Allowed by development policy: 0 components.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithAllowDevLicensesAcrossCombinedInputs_AllowsDevelopmentOnlyComponent()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(directory, "first");
        var secondDirectory = Path.Combine(directory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = Path.Combine(firstDirectory, "package-lock.json");
        var second = Path.Combine(secondDirectory, "package-lock.json");
        await File.WriteAllTextAsync(first, NpmLockJson(devLicense: "CC-BY-4.0", runtimeLicense: "MIT"), Encoding.UTF8);
        await File.WriteAllTextAsync(second, """{ "lockfileVersion": 3, "packages": { "": { "name": "second" }, "node_modules/b": { "name": "b", "version": "1.0.0", "license": "MIT" } } }""", Encoding.UTF8);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", first, "--input", second, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Allowed by development policy: 1 component.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Check_WithEmptyAllowDevLicenses_ReturnsOneWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "CC-BY-4.0", runtimeLicense: "MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Invalid license policy: Development allow-list entries must not be empty.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithInvalidAllowDevLicenses_ReturnsOneWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "CC-BY-4.0", runtimeLicense: "MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "Unknown-License", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Invalid license policy:");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_ReportWithDevelopmentUsage_UsesDevelopmentPolicy()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "CC-BY-4.0", runtimeLicense: "MIT");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, scan.Stdout);

            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Allowed by development policy: 1 component.");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    [Test]
    public async Task Check_ReportWithoutPersistedUsage_FailsClosedUnderAllowDevLicenses()
    {
        var root = FindRepositoryRoot();
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        // A report whose components carry no usage field must not be relaxed by --allow-dev-licenses.
        var report = """
            { "schemaVersion": 1, "metadata": { "input": { "kind": "package-manager", "format": "npm-package-lock" }, "spdx": { "licenseListVersion": "3.0" } },
              "components": [ { "name": "dev-pkg", "version": "1.0.0", "ecosystem": "npm", "purl": "pkg:npm/dev-pkg@1.0.0", "sourceId": "node_modules/dev-pkg", "dependency": "direct", "status": "matched", "license": "CC-BY-4.0" } ] }
            """;
        await File.WriteAllTextAsync(reportPath, report);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0");

            await Assert.That(result.ExitCode).IsEqualTo(2).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("License check failed: 1 violation.");
            await Assert.That(result.Stdout).Contains("Allowed by development policy: 0 components.");
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    [Test]
    public async Task Check_WithExcludePackages_RemovesComponentFromEvaluation()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/example", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Excluded from evaluation: 1 component.");
            await Assert.That(result.Stdout).Contains("License check passed: 0 components satisfy the allow-list.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithExcludePackages_ReportsZeroWhenNothingMatches()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        try
        {
            // The prefix ends inside a package name, so it must not silence a neighbouring package.
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/exam", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Excluded from evaluation: 0 components.");
            await Assert.That(result.Stdout).Contains("License check failed: 1 violation.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithoutExcludePackages_OmitsExclusionSummary()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).DoesNotContain("Excluded from evaluation");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_VerboseWithExcludePackages_ReportsMatchesPerPrefix()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/example,pkg:npm/absent", "--verbose", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stderr).Contains("Exclusion prefix pkg:npm/example matched 1 component.");
            await Assert.That(result.Stderr).Contains("Exclusion prefix pkg:npm/absent matched 0 components.");
            await Assert.That(result.Stdout).DoesNotContain("Exclusion prefix");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_ExcludingAWholeEcosystem_ReportsHowManyItTook()
    {
        // Selecting an ecosystem is allowed because a generator can inject one, but breadth has to state itself:
        // the count is what tells a reader whether the prefix took what they meant it to take.
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/", "--verbose", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stderr).Contains("Exclusion prefix pkg:npm/ matched 1 component.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithExcludePackagesWithoutVerbose_OmitsPerPrefixMatches()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/example", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stderr).DoesNotContain("Exclusion prefix");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithNoExternalEvidenceFor_LeavesComponentAcknowledgeable()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "internal", "version": "1.0.0", "purl": "pkg:nuget/MyCompany.Internal@1.0.0" }] }""",
            Encoding.UTF8);
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            // Skipped collection must leave the component unresolved rather than errored, so a baseline can acknowledge it.
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--skip-evidence-packages", "pkg:nuget/MyCompany.", "--baseline", baselinePath, "--update-baseline");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Acknowledged by baseline: 1 component.");
            await Assert.That(await File.ReadAllTextAsync(baselinePath)).Contains("pkg:nuget/MyCompany.Internal@1.0.0");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(baselinePath)) File.Delete(baselinePath);
        }
    }

    [Test]
    public async Task Check_WithInvalidNoExternalEvidenceFor_ReturnsOneWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--skip-evidence-packages", "pkg:nuget/@");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("must identify at least one package or namespace");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_ReportWithOnlyCollectionErrors_ReturnsThreeAsIncomplete()
    {
        var root = FindRepositoryRoot();
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        // A registry outage is not a licensing finding, so it must not carry the exit code that means one.
        var report = """
            { "schemaVersion": 1, "metadata": { "input": { "kind": "package-manager", "format": "npm-package-lock" }, "spdx": { "licenseListVersion": "3.0" } },
              "components": [ { "name": "unreachable", "version": "1.0.0", "ecosystem": "npm", "purl": "pkg:npm/unreachable@1.0.0", "dependency": "direct", "status": "error" } ] }
            """;
        await File.WriteAllTextAsync(reportPath, report);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(3);
            await Assert.That(result.Stdout).Contains("License check incomplete: 1 component could not be evaluated.");
            await Assert.That(result.Stdout).Contains("license evidence could not be completed");
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    [Test]
    public async Task Check_ReportWithCollectionErrorAndPolicyViolation_ReturnsTwo()
    {
        var root = FindRepositoryRoot();
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        // A genuine finding decides the exit code even when a collection failure is present in the same run.
        var report = """
            { "schemaVersion": 1, "metadata": { "input": { "kind": "package-manager", "format": "npm-package-lock" }, "spdx": { "licenseListVersion": "3.0" } },
              "components": [ { "name": "unreachable", "version": "1.0.0", "ecosystem": "npm", "purl": "pkg:npm/unreachable@1.0.0", "dependency": "direct", "status": "error" },
                              { "name": "forbidden", "version": "1.0.0", "ecosystem": "npm", "purl": "pkg:npm/forbidden@1.0.0", "dependency": "direct", "status": "matched", "license": "GPL-3.0-only" } ] }
            """;
        await File.WriteAllTextAsync(reportPath, report);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).Contains("License check failed: 2 violations.");
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    [Test]
    public async Task Check_ReportWithAcknowledgedErrorOnly_StillReturnsThree()
    {
        var root = FindRepositoryRoot();
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        // Status error cannot be acknowledged, so a baseline must not turn an incomplete run into a pass.
        var report = """
            { "schemaVersion": 1, "metadata": { "input": { "kind": "package-manager", "format": "npm-package-lock" }, "spdx": { "licenseListVersion": "3.0" } },
              "components": [ { "name": "unreachable", "version": "1.0.0", "ecosystem": "npm", "purl": "pkg:npm/unreachable@1.0.0", "dependency": "direct", "status": "error" } ] }
            """;
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(reportPath, report);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", baselinePath, "--update-baseline");

            await Assert.That(result.ExitCode).IsEqualTo(3);
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
            if (File.Exists(baselinePath)) File.Delete(baselinePath);
        }
    }

    [Test]
    public async Task Check_WithInvalidExcludePackages_ReturnsOneWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/@", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Invalid license policy:");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_ReportWithExcludedPackage_Passes()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, scan.Stdout);

            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/example");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("License check passed");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    [Test]
    public async Task Check_UpdateBaselineWithExcludePackages_OmitsExcludedComponent()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync(null);
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/example", "--baseline", baselinePath, "--update-baseline", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(await File.ReadAllTextAsync(baselinePath)).DoesNotContain("pkg:npm/example");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(baselinePath)) File.Delete(baselinePath);
        }
    }

    private static async Task<string> WriteNpmLockAsync(string devLicense, string runtimeLicense)
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}", "package-lock.json");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        await File.WriteAllTextAsync(inputPath, NpmLockJson(devLicense, runtimeLicense), Encoding.UTF8);
        return inputPath;
    }

    private static string NpmLockJson(string devLicense, string runtimeLicense) => string.Concat(
        "{ \"name\": \"app\", \"lockfileVersion\": 3, \"packages\": { ",
        "\"\": { \"name\": \"app\", \"dependencies\": { \"run-pkg\": \"1.0.0\" }, \"devDependencies\": { \"dev-pkg\": \"1.0.0\" } }, ",
        "\"node_modules/run-pkg\": { \"version\": \"1.0.0\", \"license\": \"", runtimeLicense, "\" }, ",
        "\"node_modules/dev-pkg\": { \"version\": \"1.0.0\", \"dev\": true, \"license\": \"", devLicense, "\" } } }");

    private static async Task<string> WriteCycloneDxAsync(string? license, string version = "1.0.0")
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        var licenseJson = license is null ? string.Empty : $", \"licenses\": [{{ \"expression\": \"{license}\" }}]";
        var json = string.Concat("{ \"bomFormat\": \"CycloneDX\", \"specVersion\": \"1.6\", \"components\": [{ \"type\": \"library\", \"name\": \"example\", \"version\": \"", version, "\", \"purl\": \"pkg:npm/example@", version, "\"", licenseJson, " }] }");
        await File.WriteAllTextAsync(inputPath, json, Encoding.UTF8);
        return inputPath;
    }

    private static async Task<string> WriteCycloneDxWithRootAsync(string? rootLicense, string? dependencyLicense)
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        var rootLicenseJson = rootLicense is null ? string.Empty : $", \"licenses\": [{{ \"expression\": \"{rootLicense}\" }}]";
        var dependencyLicenseJson = dependencyLicense is null ? string.Empty : $", \"licenses\": [{{ \"expression\": \"{dependencyLicense}\" }}]";
        var json = string.Concat(
            "{ \"bomFormat\": \"CycloneDX\", \"specVersion\": \"1.6\", ",
            "\"metadata\": { \"component\": { \"type\": \"application\", \"bom-ref\": \"application@1.0.0\", \"name\": \"application\", \"version\": \"1.0.0\"",
            rootLicenseJson,
            " } }, ",
            "\"components\": [{ \"type\": \"library\", \"bom-ref\": \"pkg:npm/example@1.0.0\", \"name\": \"example\", \"version\": \"1.0.0\", \"purl\": \"pkg:npm/example@1.0.0\"",
            dependencyLicenseJson,
            " }], ",
            "\"dependencies\": [{ \"ref\": \"application@1.0.0\", \"dependsOn\": [\"pkg:npm/example@1.0.0\"] }] }");
        await File.WriteAllTextAsync(inputPath, json, Encoding.UTF8);
        return inputPath;
    }

    private static async Task<string> WriteCycloneDxRootOnlyAsync()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        const string json =
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "metadata": {
                "component": {
                  "type": "application",
                  "bom-ref": "application@1.0.0",
                  "name": "application",
                  "version": "1.0.0"
                }
              },
              "dependencies": [
                {
                  "ref": "application@1.0.0",
                  "dependsOn": []
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(inputPath, json, Encoding.UTF8);
        return inputPath;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCheckWorkflowAsync(string root, params string[] args)
    {
        var scanArguments = new List<string> { "scan" };
        var checkArguments = new List<string> { "check" };
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "--input":
                case "--input-format":
                case "--cache-dir":
                case "--skip-evidence-packages":
                case "--concurrency":
                case "--retry":
                    scanArguments.Add(argument);
                    scanArguments.Add(args[++i]);
                    break;
                case "--refresh":
                case "--no-external-evidence":
                    scanArguments.Add(argument);
                    break;
                case "--spdx-data":
                    scanArguments.Add(argument);
                    checkArguments.Add(argument);
                    var value = args[++i];
                    scanArguments.Add(value);
                    checkArguments.Add(value);
                    break;
                default:
                    checkArguments.Add(argument);
                    break;
            }
        }

        scanArguments.Add("--format");
        scanArguments.Add("Json");
        var scan = await RunOlAsync(root, [.. scanArguments]);
        if (scan.ExitCode != 0) return scan;

        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(reportPath, scan.Stdout);
            checkArguments.Insert(1, reportPath);
            checkArguments.Insert(1, "--report");
            return await RunOlAsync(root, [.. checkArguments]);
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlAsync(string root, params string[] args)
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
            startInfo.ArgumentList.Add(CliTestAssembly.ResolveOlDllPath(AppContext.BaseDirectory));
            for (var i = 0; i < args.Length; i++) startInfo.ArgumentList.Add(args[i]);
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

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFilePath)! })
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx"))) return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
