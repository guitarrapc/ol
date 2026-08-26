using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// A `--dependency`-filtered report is a narrower population than the scan resolved, and `check` gates whatever
    /// the report holds. The report states the filter in <c>metadata.view</c>; a gate that reads it and says nothing
    /// makes a partial evaluation read exactly like a complete one.
    /// </summary>
    [Test]
    public async Task Check_WithDependencyFilteredReport_StatesTheFilterAndTheExcludedCount()
    {
        var root = FindRepositoryRoot();

        var result = await RunCheckWorkflowAsync(
            root,
            "--input", FixturePath("package-lock.json"),
            "--dependency", "transitive",
            "--allow-licenses", "Apache-2.0",
            "--no-external-evidence");

        await Assert.That(result.Stdout).Contains("Dependency filter: transitive; 6 components excluded by the producing scan.");
    }

    /// <summary>
    /// Filtering to a relationship other than <c>unknown</c> also drops the components whose relationship no input
    /// proved, and those are the ones policy keeps fail-closed. The count is stated separately because it is the
    /// consequential half of the exclusion.
    /// </summary>
    [Test]
    public async Task Check_WithFilterExcludingUnknownRelationships_StatesThatCountSeparately()
    {
        var root = FindRepositoryRoot();

        var result = await RunCheckWorkflowAsync(
            root,
            "--input", FixturePath("Package.resolved"),
            "--dependency", "direct",
            "--allow-licenses", "MIT",
            "--no-external-evidence");

        await Assert.That(result.Stdout).Contains("2 with an unknown relationship");
    }

    [Test]
    public async Task Check_WithUnfilteredReport_StatesNoDependencyFilter()
    {
        var root = FindRepositoryRoot();

        var result = await RunCheckWorkflowAsync(
            root,
            "--input", FixturePath("package-lock.json"),
            "--allow-licenses", "Apache-2.0",
            "--no-external-evidence");

        await Assert.That(result.Stdout).DoesNotContain("Dependency filter:");
    }

    /// <summary>
    /// Policy skips a root, so an SBOM that names a resolved dependency as its own root must not be able to withdraw
    /// that dependency from the gate. Scanning an SBOM beside the resolved tree is a recommended configuration, and a
    /// second input merely mentioning a component may not change the verdict on it.
    /// </summary>
    [Test]
    public async Task Check_WithSbomRootNamingAResolvedDependency_StillEvaluatesThatDependency()
    {
        var root = FindRepositoryRoot();

        var result = await RunCheckWorkflowAsync(
            root,
            "--input", FixturePath("package-lock.json"),
            "--input", FixturePath("mixed-npm-root-direct.cdx.json"),
            "--allow-licenses", "Apache-2.0",
            "--no-external-evidence");

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Stdout).Contains("pkg:npm/alpha@1.0.0");
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

    /// <summary>
    /// Covers every combination of "does a mechanism explain this violation" and "did evidence name a
    /// place", because a column that is only ever populated is as untrustworthy as one that never is.
    /// </summary>
    [Test]
    [Arguments("located", "declared_license_location_not_collected", "https://example.com/LICENSE")]
    [Arguments("classified", "license_classifier_not_specific", "-")]
    [Arguments("silent", "-", "-")]
    public async Task Check_WithUnresolvedViolation_ReportsMechanismAndReference(string package, string mechanism, string reference)
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxUnresolvedMechanismsAsync();
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).Contains("Package     Version  Ecosystem  Purl");
            await Assert.That(result.Stdout).Contains("----------  -------  ---------  ");
            await Assert.That(result.Stdout).DoesNotContain('\t');
            var row = Array.Find(
                result.Stdout.Split('\n'),
                line => line.StartsWith(package, StringComparison.Ordinal));
            await Assert.That(row).IsNotNull();
            var columns = row!.TrimEnd('\r').Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await Assert.That(columns[6]).IsEqualTo(mechanism);
            await Assert.That(columns[7]).IsEqualTo(reference);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// A reviewer facing many unresolved rows needs to know how many populations they are, not just how
    /// many rows. The tally is ordered by count so the largest population is the first thing read.
    /// </summary>
    [Test]
    public async Task Check_WithUnresolvedViolations_TalliesMechanisms()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxUnresolvedMechanismsAsync();
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).Contains("Unresolved mechanisms");
            await Assert.That(result.Stdout).Contains("  declared_license_location_not_collected: 1");
            await Assert.That(result.Stdout).Contains("  license_classifier_not_specific: 1");
            await Assert.That(result.Stdout).Contains("  no mechanism reported: 1");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>Check writes its UTF-8 projection directly to stdout without passing through a UTF-16 report.</summary>
    [Test]
    public async Task Check_WithUtf8ComponentName_PreservesStdoutText()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-utf8-{Guid.NewGuid():N}.json");
        const string Json = """
            { "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
              { "type": "library", "name": "日本語-package", "version": "1.0.0", "purl": "pkg:npm/example@1.0.0" } ] }
            """;
        await File.WriteAllTextAsync(inputPath, Json, Encoding.UTF8);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).Contains("日本語-package");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// The tally exists to show which population is worth attacking first, so the largest has to come
    /// first. With every count equal the order would be decided by dictionary insertion, which is not a
    /// contract; this document makes one mechanism the majority so the ordering is actually observable.
    /// </summary>
    [Test]
    public async Task Check_WithRepeatedMechanism_OrdersTallyByCount()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        const string Json = """
            { "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
              { "type": "library", "name": "first", "version": "1.0.0", "purl": "pkg:npm/first@1.0.0", "licenses": [{ "license": { "url": "https://example.com/A" } }] },
              { "type": "library", "name": "second", "version": "1.0.0", "purl": "pkg:npm/second@1.0.0", "licenses": [{ "license": { "url": "https://example.com/B" } }] },
              { "type": "library", "name": "third", "version": "1.0.0", "purl": "pkg:npm/third@1.0.0", "licenses": [{ "license": { "name": "License :: OSI Approved :: BSD License" } }] } ] }
            """;
        await File.WriteAllTextAsync(inputPath, Json, Encoding.UTF8);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            var tally = result.Stdout[result.Stdout.IndexOf("Unresolved mechanisms", StringComparison.Ordinal)..];
            await Assert.That(tally.IndexOf("declared_license_location_not_collected: 2", StringComparison.Ordinal))
                .IsLessThan(tally.IndexOf("license_classifier_not_specific: 1", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// A shared baseline states the acknowledgements a whole organization made once — the legacy .NET
    /// corpus is the same population in every repository that targets netstandard2.0 — while the
    /// repository's own file states what only it has to accept. Composing them is what keeps the shared
    /// population from being copied into every repository.
    /// </summary>
    [Test]
    public async Task Check_WithComposedBaselines_AcknowledgesEntriesFromEach()
    {
        var root = FindRepositoryRoot();
        var (reportPath, first, second) = await WritePartitionedBaselinesAsync(root);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", first, "--baseline", second);

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Acknowledged by baseline: 2 components.");
        }
        finally
        {
            Delete(reportPath, first, second);
        }
    }

    /// <summary>
    /// A component is acknowledged when any supplied baseline states it, so the composition is a union and
    /// the answer cannot depend on which file was named first.
    /// </summary>
    [Test]
    public async Task Check_WithComposedBaselines_IsOrderIndependent()
    {
        var root = FindRepositoryRoot();
        var (reportPath, first, second) = await WritePartitionedBaselinesAsync(root);
        try
        {
            var forward = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", first, "--baseline", second);
            var reversed = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", second, "--baseline", first);

            await Assert.That(reversed.ExitCode).IsEqualTo(forward.ExitCode);
            await Assert.That(reversed.Stdout).IsEqualTo(forward.Stdout);
        }
        finally
        {
            Delete(reportPath, first, second);
        }
    }

    /// <summary>Proves the composition is what passed: either file alone leaves a violation.</summary>
    [Test]
    public async Task Check_WithOnlyOneOfTheComposedBaselines_StillFails()
    {
        var root = FindRepositoryRoot();
        var (reportPath, first, second) = await WritePartitionedBaselinesAsync(root);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", first);

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Stdout).Contains("Acknowledged by baseline: 1 component.");
        }
        finally
        {
            Delete(reportPath, first, second);
        }
    }

    /// <summary>An unreadable file among several is still a command failure, not a silently smaller union.</summary>
    [Test]
    public async Task Check_WithComposedBaselineMissingOneFile_ReturnsOne()
    {
        var root = FindRepositoryRoot();
        var (reportPath, first, second) = await WritePartitionedBaselinesAsync(root);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", first, "--baseline", "missing-baseline.json");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("missing-baseline.json");
        }
        finally
        {
            Delete(reportPath, first, second);
        }
    }

    /// <summary>
    /// Updating writes only what the earlier baselines do not already state. Writing the complete snapshot
    /// into the repository's own file would copy the shared population back into it, which is the
    /// duplication composing the files exists to remove.
    /// </summary>
    [Test]
    public async Task Check_WithComposedBaselinesAndUpdate_WritesOnlyWhatTheEarlierFilesLack()
    {
        var root = FindRepositoryRoot();
        var (reportPath, shared, unused) = await WritePartitionedBaselinesAsync(root);
        var own = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        var sharedBefore = await File.ReadAllTextAsync(shared);
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", shared, "--baseline", own, "--update-baseline");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            using var written = JsonDocument.Parse(await File.ReadAllTextAsync(own));
            var names = written.RootElement.GetProperty("acknowledged").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
            await Assert.That(names).IsEquivalentTo(new[] { "beta" });

            // The earlier file is read, never rewritten.
            await Assert.That(await File.ReadAllTextAsync(shared)).IsEqualTo(sharedBefore);
        }
        finally
        {
            Delete(reportPath, shared, unused, own);
        }
    }

    /// <summary>One baseline has no earlier file to subtract, so updating still writes the whole snapshot.</summary>
    [Test]
    public async Task Check_WithSingleBaselineAndUpdate_WritesTheCompleteSnapshot()
    {
        var root = FindRepositoryRoot();
        var (reportPath, first, second) = await WritePartitionedBaselinesAsync(root);
        var own = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", own, "--update-baseline");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            using var written = JsonDocument.Parse(await File.ReadAllTextAsync(own));
            var names = written.RootElement.GetProperty("acknowledged").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
            await Assert.That(names).IsEquivalentTo(new[] { "alpha", "beta" });
        }
        finally
        {
            Delete(reportPath, first, second, own);
        }
    }

    /// <summary>
    /// Two unresolved components, and one baseline for each, so a test can prove the union rather than
    /// assume it. The partition is produced by Ol itself, because a baseline entry is identified by a
    /// fingerprint of its evidence that a hand-written fixture cannot reproduce.
    /// </summary>
    private static async Task<(string ReportPath, string First, string Second)> WritePartitionedBaselinesAsync(string root)
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        const string Json = """
            { "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
              { "type": "library", "name": "alpha", "version": "1.0.0", "purl": "pkg:npm/alpha@1.0.0" },
              { "type": "library", "name": "beta", "version": "1.0.0", "purl": "pkg:npm/beta@1.0.0" } ] }
            """;
        await File.WriteAllTextAsync(inputPath, Json, Encoding.UTF8);

        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json", "--quiet");
        File.Delete(inputPath);
        await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
        await File.WriteAllTextAsync(reportPath, scan.Stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var first = await WriteBaselineExcludingAsync(root, reportPath, "pkg:npm/beta");
        var second = await WriteBaselineExcludingAsync(root, reportPath, "pkg:npm/alpha");
        return (reportPath, first, second);
    }

    private static async Task<string> WriteBaselineExcludingAsync(string root, string reportPath, string excludedPurl)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, string.Empty, Encoding.UTF8);
        var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--baseline", path, "--update-baseline", "--exclude-packages", excludedPurl);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
        return path;
    }

    private static void Delete(params string[] paths)
    {
        for (var i = 0; i < paths.Length; i++)
        {
            if (File.Exists(paths[i])) File.Delete(paths[i]);
        }
    }

    /// <summary>
    /// A component the generator emitted without a package identity used to reach the violation table
    /// saying a repository was unavailable, which is the one mechanism a reviewer cannot act on: no
    /// repository was ever sought, and no rerun can produce one. The action is to fix the generator.
    /// </summary>
    [Test]
    public async Task Check_WithComponentLackingPurl_NamesTheMissingIdentity()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        const string Json = """
            { "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
              { "type": "library", "name": "bare", "version": "1.0.0" } ] }
            """;
        await File.WriteAllTextAsync(inputPath, Json, Encoding.UTF8);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            var row = Array.Find(result.Stdout.Split('\n'), line => line.StartsWith("bare", StringComparison.Ordinal));
            await Assert.That(row).IsNotNull();
            var columns = row!.TrimEnd('\r').Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await Assert.That(columns[3]).IsEqualTo("-");
            await Assert.That(columns[6]).IsEqualTo("package_metadata_no_purl");
            await Assert.That(columns[7]).IsEqualTo("-");
            await Assert.That(result.Stdout).Contains("  package_metadata_no_purl: 1");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>A resolved license the allow-list rejects has no collection mechanism to explain.</summary>
    [Test]
    public async Task Check_WithNotAllowedLicense_ReportsNoMechanismAndNoTally()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            var row = Array.Find(
                result.Stdout.Split('\n'),
                line => line.StartsWith("example", StringComparison.Ordinal));
            await Assert.That(row).IsNotNull();
            var columns = row!.TrimEnd('\r').Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await Assert.That(columns[6]).IsEqualTo("-");
            await Assert.That(columns[7]).IsEqualTo("-");
            await Assert.That(result.Stdout).DoesNotContain("Unresolved mechanisms");
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
    public async Task Check_WithExcludedInputPaths_PreservesAuditBoundary()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, AddInputScope(scan.Stdout, "product-a/docs", "product-b/docs"));

            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).Contains("Excluded input paths: product-a/docs, product-b/docs.");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    // A report whose input declared no resolved dependencies proves nothing about licenses, so the gate
    // reports the same inconclusive state it reports for a run where every finding was a collection
    // failure. Reporting a pass would make an unrestored project indistinguishable from a project whose
    // dependencies are all allowed, which is the false negative the scan warning exists to prevent.
    [Test]
    public async Task Check_WithReportDeclaringNoComponents_ReturnsInconclusive()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(3);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("License check incomplete: the report states its input declared no resolved dependencies.");
            await Assert.That(result.Stdout).DoesNotContain("License check passed");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    // A project that legitimately resolves no dependencies still declares a root, so the inventory is not
    // empty and the gate stays green. This is the boundary that lets the empty-input case be inconclusive
    // without an opt-out flag.
    [Test]
    public async Task Check_WithRootOnlyReportAndNoDependencies_ReturnsPass()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "metadata": { "component": { "type": "application", "bom-ref": "SoloApp@1.0.0", "name": "SoloApp", "version": "1.0.0" } }, "components": [] }""",
            Encoding.UTF8);
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("License check passed: 0 components satisfy the allow-list.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    // The reader restores every top-level warning and the gate reacts only to the identifiers it knows.
    // A warning about SPDX deprecation describes an identifier the report already carries per component
    // and changes nothing about what the run proved, so it must not gate.
    [Test]
    [Arguments("deprecated_spdx_identifier", 0)]
    [Arguments("some_future_warning_ol_does_not_know", 0)]
    public async Task Check_WithTopLevelWarningThatIsNotAnEmptyInput_DoesNotChangeTheGate(string warning, int expectedExitCode)
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, SetWarnings(scan.Stdout, warning));

            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(expectedExitCode);
            await Assert.That(result.Stdout).Contains("License check passed: 1 component satisfies the allow-list.");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    // A report written before top-level warnings existed is read as a report that stated no warning,
    // which is what it was. The absent array is not the same claim as an empty one, but neither gates.
    [Test]
    public async Task Check_WithReportOmittingTopLevelWarnings_ReturnsPass()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            var document = JsonNode.Parse(scan.Stdout)!.AsObject();
            document.Remove("warnings");
            await File.WriteAllTextAsync(reportPath, document.ToJsonString());

            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).Contains("License check passed: 1 component satisfies the allow-list.");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    // An empty input is reported even when it arrives beside warnings the gate ignores, so the identifier
    // is selected from the array rather than assumed to be the only entry.
    [Test]
    public async Task Check_WithEmptyInputWarningBesideAnotherWarning_ReturnsInconclusive()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("MIT");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, SetWarnings(scan.Stdout, "deprecated_spdx_identifier", "input_declares_no_components"));

            var result = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(3);
            await Assert.That(result.Stdout).Contains("License check incomplete: the report states its input declared no resolved dependencies.");
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

    // Only a listing Ol built from a source that enumerates licenses is read as one.
    [Test]
    public async Task Check_PublisherLicenseTextContainingASemicolon_IsNotReadAsAListing()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteNpmLockAsync(devLicense: "MIT", runtimeLicense: "MIT; Apache-2.0");
        try
        {
            var result = await RunCheckWorkflowAsync(root, "--input", inputPath, "--allow-licenses", "MIT,Apache-2.0", "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(2).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("ambiguous");
            await Assert.That(result.Stdout).DoesNotContain("Allowed on every reading");
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

    private static string SetWarnings(string report, params string[] warnings)
    {
        var values = new JsonArray();
        for (var i = 0; i < warnings.Length; i++) values.Add(warnings[i]);
        var document = JsonNode.Parse(report)!.AsObject();
        document["warnings"] = values;
        return document.ToJsonString();
    }

    private static string AddInputScope(string report, params string[] excludedPaths)
    {
        var paths = new JsonArray();
        for (var i = 0; i < excludedPaths.Length; i++) paths.Add(excludedPaths[i]);
        var document = JsonNode.Parse(report)!.AsObject();
        document["metadata"]!["inputScope"] = new JsonObject
        {
            ["excludedPathCount"] = excludedPaths.Length,
            ["excludedPaths"] = paths,
        };
        return document.ToJsonString();
    }

    /// <summary>
    /// Writes one document covering the three ways a violated component can explain itself: a declared
    /// location that names a place, a mechanism that names none, and evidence that carries no mechanism
    /// at all. All three resolve offline, so the expected output does not depend on a registry.
    /// </summary>
    private static async Task<string> WriteCycloneDxUnresolvedMechanismsAsync()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        const string Json = """
            { "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
              { "type": "library", "name": "located", "version": "1.0.0", "purl": "pkg:npm/located@1.0.0", "licenses": [{ "license": { "url": "https://example.com/LICENSE" } }] },
              { "type": "library", "name": "classified", "version": "1.0.0", "purl": "pkg:npm/classified@1.0.0", "licenses": [{ "license": { "name": "License :: OSI Approved :: BSD License" } }] },
              { "type": "library", "name": "silent", "version": "1.0.0", "purl": "pkg:npm/silent@1.0.0" } ] }
            """;
        await File.WriteAllTextAsync(inputPath, Json, Encoding.UTF8);
        return inputPath;
    }

    private static string FixturePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

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
                case "--dependency":
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
