using System.Diagnostics;
using System.Text;

namespace Ol.Tests;

public sealed class CliCheckTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Check_Help_AdvertisesRequiredAllowListAndScanControls()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "check", "--help");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stderr).IsEmpty();
        await Assert.That(result.Stdout).Contains("--allow-licenses <string?>");
        await Assert.That(result.Stdout).Contains("--exclude-packages <string?>");
        await Assert.That(result.Stdout).Contains("--input <string[]?>");
        await Assert.That(result.Stdout).DoesNotContain("--dependency");
        await Assert.That(result.Stdout).DoesNotContain("--format");
    }

    [Test]
    public async Task Check_WithAllowedLicense_ReturnsZeroAndPassSummary()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1);
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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

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
    public async Task Check_WithForbiddenLicense_ReturnsOneAndCompleteViolation()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1);
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
    public async Task Check_WithUnknownLicense_ReturnsOneAndUnresolvedReason()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync(null);
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1);
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
    public async Task Check_WithInvalidAllowList_ReturnsTwoWithoutPolicyOutput(string allowLicenses)
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", allowLicenses, "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Invalid license policy:");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithoutAllowList_ReturnsTwoWithoutScanning()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "check", "--input", "missing.json", "--no-external-evidence");

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr).Contains("Invalid license policy: --allow-licenses must be specified.");
    }

    [Test]
    public async Task Check_WithMalformedInput_ReturnsTwoWithoutPartialPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, "{ malformed", Encoding.UTF8);
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Unable to scan input:");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithScanViewOption_ReturnsTwoAsConfigurationError()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--dependency", "direct", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).Contains("Argument '--dependency' is not recognized.");
            await Assert.That(result.Stderr).IsEmpty();
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithRepeatedInputs_EvaluatesCombinedInventory()
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
            var result = await RunOlAsync(root, "check", "--input", first, "--input", second, "--allow-licenses", "MIT", "--no-external-evidence");

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
            var update = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath, "--update-baseline");
            var rerun = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath);

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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath, "--update-baseline");
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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath, "--update-baseline");

            await Assert.That(result.ExitCode).IsEqualTo(1);
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
            await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath, "--update-baseline");
            var afterBump = await RunOlAsync(root, "check", "--input", bumpedPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath);

            await Assert.That(afterBump.ExitCode).IsEqualTo(1);
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
    public async Task Check_WithMissingBaseline_ReturnsTwoWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath);

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Unable to read baseline");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithMalformedBaseline_ReturnsTwoWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(baselinePath, "{ \"schemaVersion\": 99, \"acknowledged\": [] }", Encoding.UTF8);
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", baselinePath);

            await Assert.That(result.ExitCode).IsEqualTo(2);
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
    public async Task Check_UpdateBaselineWithoutBaselinePath_ReturnsTwo()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--update-baseline");

            await Assert.That(result.ExitCode).IsEqualTo(2);
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
            await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", first, "--update-baseline");
            await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence", "--baseline", second, "--update-baseline");

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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).DoesNotContain("Acknowledged by baseline");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithReport_ReachesSameVerdictAsScanningTheInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, scan.Stdout);
            var fromInput = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");
            var fromReport = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(fromReport.ExitCode).IsEqualTo(fromInput.ExitCode);
            await Assert.That(fromReport.ExitCode).IsEqualTo(1);
            await Assert.That(fromReport.Stdout).IsEqualTo(fromInput.Stdout);
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
    public async Task Check_WithReportAndInput_ReturnsTwo()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunOlAsync(root, "check", "--report", inputPath, "--input", inputPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("--report cannot be combined");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithMalformedReport_ReturnsTwo()
    {
        var root = FindRepositoryRoot();
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(reportPath, "{ \"schemaVersion\": 99, \"components\": [] }", Encoding.UTF8);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(2);
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
            var withoutDev = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");
            var withDev = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--no-external-evidence");

            await Assert.That(withoutDev.ExitCode).IsEqualTo(1).Because(withoutDev.Stderr);
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
    public async Task Check_WithAllowDevLicenses_DoesNotAllowRuntimeComponent()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "MIT", runtimeLicense: "CC-BY-4.0");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1).Because(result.Stderr);
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
            var result = await RunOlAsync(root, "check", "--input", first, "--input", second, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Allowed by development policy: 1 component.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Check_WithInvalidAllowDevLicenses_ReturnsTwoWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "CC-BY-4.0", runtimeLicense: "MIT");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "Unknown-License", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Invalid license policy:");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_ReportWithAllowDevLicenses_MatchesScanningTheInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "CC-BY-4.0", runtimeLicense: "MIT");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, scan.Stdout);

            var fromInput = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--no-external-evidence");
            var fromReport = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0");

            await Assert.That(fromInput.ExitCode).IsEqualTo(0).Because(fromInput.Stderr);
            await Assert.That(fromReport.ExitCode).IsEqualTo(fromInput.ExitCode).Because(fromReport.Stderr);
            await Assert.That(fromReport.Stdout).IsEqualTo(fromInput.Stdout);
            await Assert.That(fromReport.Stdout).Contains("Allowed by development policy: 1 component.");
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

            await Assert.That(result.ExitCode).IsEqualTo(1).Because(result.Stderr);
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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/example", "--no-external-evidence");

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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/exam", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(1).Because(result.Stderr);
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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).DoesNotContain("Excluded from evaluation");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_WithInvalidExcludePackages_ReturnsTwoWithoutPolicyOutput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Invalid license policy:");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Check_ReportWithExcludePackages_MatchesScanningTheInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, scan.Stdout);

            var fromInput = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/example", "--no-external-evidence");
            var fromReport = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/example");

            await Assert.That(fromInput.ExitCode).IsEqualTo(0).Because(fromInput.Stderr);
            await Assert.That(fromReport.ExitCode).IsEqualTo(fromInput.ExitCode).Because(fromReport.Stderr);
            await Assert.That(fromReport.Stdout).IsEqualTo(fromInput.Stdout);
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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--exclude-packages", "pkg:npm/example", "--baseline", baselinePath, "--update-baseline", "--no-external-evidence");

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
