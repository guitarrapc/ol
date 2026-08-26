using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class SarifOutputTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Sarif_WithDirectViolation_EmitsStableRuleAndLogicalLocation()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "GPL-3.0-only", transitiveLicense: "MIT");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));

            var run = document.RootElement.GetProperty("runs")[0];
            var results = run.GetProperty("results");

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(document.RootElement.GetProperty("version").GetString()).IsEqualTo("2.1.0");
            await Assert.That(document.RootElement.GetProperty("$schema").GetString())
                .IsEqualTo("https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json");
            await Assert.That(run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString()).IsEqualTo("ol");
            await Assert.That(results.GetArrayLength()).IsEqualTo(1);
            await Assert.That(results[0].GetProperty("ruleId").GetString()).IsEqualTo("OL0001");
            await Assert.That(results[0].GetProperty("level").GetString()).IsEqualTo("error");
            await Assert.That(results[0].GetProperty("properties").GetProperty("purl").GetString()).IsEqualTo("pkg:npm/direct@1.0.0");
            await Assert.That(results[0].GetProperty("locations")[0].GetProperty("logicalLocations")[0].GetProperty("kind").GetString()).IsEqualTo("package");
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_WithTransitiveViolation_NamesTheIntroducingDirectDependency()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "MIT", transitiveLicense: "GPL-3.0-only");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));

            var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
            var path = result.GetProperty("properties").GetProperty("dependencyPath");

            await Assert.That(result.GetProperty("properties").GetProperty("dependency").GetString()).IsEqualTo("transitive");
            await Assert.That(path.GetArrayLength()).IsEqualTo(2);
            await Assert.That(path[0].GetString()).IsEqualTo("pkg:npm/direct@1.0.0");
            await Assert.That(path[1].GetString()).IsEqualTo("pkg:npm/transitive@1.0.0");
            await Assert.That(result.GetProperty("message").GetProperty("text").GetString()).Contains("Introduced through pkg:npm/direct@1.0.0 > pkg:npm/transitive@1.0.0");
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_FromPersistedReport_PreservesTheDependencyPath()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "MIT", transitiveLicense: "GPL-3.0-only");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.json");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            var scan = await RunOlAsync(
                root,
                "scan",
                "--input",
                input,
                "--no-external-evidence",
                "--format",
                "Json",
                "--sort",
                "name",
                "--sort-order",
                "desc");
            await File.WriteAllTextAsync(reportPath, scan.Stdout);
            var check = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--sarif", sarifPath);

            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await Assert.That(check.ExitCode).IsEqualTo(2).Because($"{check.Stderr}\n{check.Stdout}");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));

            var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
            var path = result.GetProperty("properties").GetProperty("dependencyPath");

            await Assert.That(path.GetArrayLength()).IsEqualTo(2);
            await Assert.That(path[0].GetString()).IsEqualTo("pkg:npm/direct@1.0.0");
            await Assert.That(path[1].GetString()).IsEqualTo("pkg:npm/transitive@1.0.0");
        }
        finally
        {
            Cleanup(input, reportPath, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_MatchesTheTextViolationSet()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "GPL-3.0-only", transitiveLicense: null);
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));

            var results = document.RootElement.GetProperty("runs")[0].GetProperty("results");
            var textViolations = result.Stdout.Split('\n').Count(line => line.Contains("pkg:npm/"));

            await Assert.That(results.GetArrayLength()).IsEqualTo(textViolations);
            await Assert.That(results.GetArrayLength()).IsEqualTo(2);
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_WithNoViolations_EmitsEmptyResultsAndExitsZero()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "MIT", transitiveLicense: "MIT");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(document.RootElement.GetProperty("runs")[0].GetProperty("results").GetArrayLength()).IsEqualTo(0);
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_WithUnknownRootAndAllowedDependency_EmitsNoRootViolation()
    {
        var root = FindRepositoryRoot();
        var input = await WriteCycloneDxWithUnknownRootAsync();
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(document.RootElement.GetProperty("runs")[0].GetProperty("results").GetArrayLength()).IsEqualTo(0);
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_AcknowledgedComponents_AreAbsent()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "MIT", transitiveLicense: null);
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        var baselinePath = Path.Combine(Path.GetTempPath(), $"ol-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--baseline", baselinePath, "--update-baseline", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(document.RootElement.GetProperty("runs")[0].GetProperty("results").GetArrayLength()).IsEqualTo(0);
        }
        finally
        {
            Cleanup(input, sarifPath, baselinePath);
        }
    }

    [Test]
    public async Task Sarif_ContainsNoAbsolutePathsOrTokens()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "GPL-3.0-only", transitiveLicense: "MIT");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--sarif", sarifPath);
            var text = await File.ReadAllTextAsync(sarifPath);

            await Assert.That(text).DoesNotContain(Path.GetTempPath().Replace("\\", "\\\\"));
            await Assert.That(text).DoesNotContain(root.Replace("\\", "\\\\"));
            await Assert.That(text.Contains("token", StringComparison.OrdinalIgnoreCase)).IsFalse();
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_WithAllowDevLicenses_RecordsAllowanceInRunPropertiesNotResults()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmDevLockAsync(devLicense: "CC-BY-4.0", runtimeLicense: "MIT");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
            var run = document.RootElement.GetProperty("runs")[0];

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            // A component admitted by the development policy is not a finding, so it must not appear in results.
            await Assert.That(run.GetProperty("results").GetArrayLength()).IsEqualTo(0);

            var allowances = run.GetProperty("properties").GetProperty("developmentPolicyAllowances");
            await Assert.That(allowances.GetArrayLength()).IsEqualTo(1);
            await Assert.That(allowances[0].GetProperty("purl").GetString()).IsEqualTo("pkg:npm/dev-pkg@1.0.0");
            await Assert.That(allowances[0].GetProperty("license").GetString()).IsEqualTo("CC-BY-4.0");
            await Assert.That(allowances[0].GetProperty("policySource").GetString()).IsEqualTo("allow-dev-licenses");
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_WithoutDevAllowance_OmitsRunProperties()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "GPL-3.0-only", transitiveLicense: "MIT");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
            var run = document.RootElement.GetProperty("runs")[0];

            await Assert.That(run.TryGetProperty("properties", out _)).IsFalse();
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    /// <summary>
    /// A CI job that reads only the SARIF file learns nothing from a result set that is complete for a narrowed
    /// population. The view is run-level metadata rather than a finding, for the same reason a development allowance
    /// is: it explains what the run covered instead of asking a reviewer to change something.
    /// </summary>
    [Test]
    public async Task Sarif_WithDependencyFilteredReport_RecordsTheEvaluatedViewInRunProperties()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "GPL-3.0-only", transitiveLicense: "MIT");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            await RunCheckWorkflowAsync(root, input, ["--dependency", "direct"], "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
            var view = document.RootElement.GetProperty("runs")[0].GetProperty("properties").GetProperty("evaluatedView");

            await Assert.That(view.GetProperty("dependencyFilter").GetString()).IsEqualTo("direct");
            await Assert.That(view.GetProperty("excludedCount").GetInt32()).IsEqualTo(1);
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_WithExcludedInputPaths_RecordsTheAuditBoundaryInRunProperties()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "GPL-3.0-only", transitiveLicense: "MIT");
        var scanRoot = Path.Combine(Path.GetTempPath(), $"ol-sarif-scope-{Guid.NewGuid():N}");
        var excludedDirectory = Path.Combine(scanRoot, "documents");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        Directory.CreateDirectory(excludedDirectory);
        var includedDirectory = Path.Combine(scanRoot, "server");
        Directory.CreateDirectory(includedDirectory);
        File.Copy(input, Path.Combine(includedDirectory, "package-lock.json"));
        File.Copy(input, Path.Combine(excludedDirectory, "package-lock.json"));

        try
        {
            var scan = await RunOlAsync(root, "scan", "--input", scanRoot, "--exclude-input-path", excludedDirectory, "--no-external-evidence", "--format", "Json");
            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            await File.WriteAllTextAsync(reportPath, scan.Stdout);

            var check = await RunOlAsync(root, "check", "--report", reportPath, "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
            var properties = document.RootElement.GetProperty("runs")[0].GetProperty("properties");
            var inputScope = properties.GetProperty("inputScope");

            await Assert.That(check.ExitCode).IsEqualTo(2).Because($"{check.Stderr}\n{check.Stdout}");
            await Assert.That(inputScope.GetProperty("excludedPathCount").GetInt32()).IsEqualTo(1);
            await Assert.That(inputScope.GetProperty("excludedPaths")[0].GetString()).IsEqualTo("documents");
        }
        finally
        {
            Cleanup(input, scanRoot, reportPath, sarifPath);
        }
    }

    /// <summary>
    /// Both facts are run-level metadata, so they share one <c>properties</c> object. Writing a second one would
    /// emit a duplicate key that a reader resolves by taking whichever came first, silently losing the other.
    /// </summary>
    [Test]
    public async Task Sarif_WithFilteredReportAndDevAllowance_WritesBothUnderOneRunProperties()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmDevLockAsync(devLicense: "CC-BY-4.0", runtimeLicense: "MIT");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            await RunCheckWorkflowAsync(root, input, ["--dependency", "direct"], "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
            var run = document.RootElement.GetProperty("runs")[0];

            await Assert.That(run.EnumerateObject().Count(property => property.Name == "properties")).IsEqualTo(1);
            var properties = run.GetProperty("properties");
            await Assert.That(properties.TryGetProperty("evaluatedView", out _)).IsTrue();
            await Assert.That(properties.GetProperty("developmentPolicyAllowances").GetArrayLength()).IsEqualTo(1);
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    [Test]
    public async Task Sarif_WithDevAllowanceOnAnUnfilteredReport_OmitsTheEvaluatedView()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmDevLockAsync(devLicense: "CC-BY-4.0", runtimeLicense: "MIT");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--allow-dev-licenses", "CC-BY-4.0", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
            var properties = document.RootElement.GetProperty("runs")[0].GetProperty("properties");

            await Assert.That(properties.TryGetProperty("evaluatedView", out _)).IsFalse();
            await Assert.That(properties.GetProperty("developmentPolicyAllowances").GetArrayLength()).IsEqualTo(1);
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    /// <summary>
    /// An empty result set over a report that resolved nothing reads exactly like a clean run, which is the same
    /// defect the evaluated view exists to prevent one projection over. A CI job consuming only the SARIF file has
    /// nothing else to learn that the run proved nothing rather than found nothing.
    /// </summary>
    [Test]
    public async Task Sarif_WithReportDeclaringNoComponents_RecordsTheInconclusiveReasonInRunProperties()
    {
        var root = FindRepositoryRoot();
        var input = await WriteEmptySbomAsync();
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, [], "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
            var run = document.RootElement.GetProperty("runs")[0];

            await Assert.That(result.ExitCode).IsEqualTo(3);
            await Assert.That(run.GetProperty("results").GetArrayLength()).IsEqualTo(0);
            await Assert.That(run.EnumerateObject().Count(property => property.Name == "properties")).IsEqualTo(1);
            await Assert.That(run.GetProperty("properties").GetProperty("inconclusive").GetProperty("reason").GetString())
                .IsEqualTo("input_declares_no_components");
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    /// <summary>
    /// The property states a fact about the report rather than a default the writer supplies, so a run that
    /// evaluated a real population never carries it.
    /// </summary>
    [Test]
    public async Task Sarif_WithEvaluatedReport_OmitsTheInconclusiveReason()
    {
        var root = FindRepositoryRoot();
        var input = await WriteNpmLockAsync(directLicense: "GPL-3.0-only", transitiveLicense: "MIT");
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-{Guid.NewGuid():N}.sarif");
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, [], "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
            var run = document.RootElement.GetProperty("runs")[0];

            var hasInconclusive = run.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("inconclusive", out _);

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(hasInconclusive).IsFalse();
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    private static async Task<string> WriteEmptySbomAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ol-sarif-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);
        return path;
    }

    private static async Task<string> WriteNpmDevLockAsync(string devLicense, string runtimeLicense)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ol-sarif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "package-lock.json");
        var json = $$"""
        {
          "lockfileVersion": 3,
          "packages": {
            "": { "name": "app", "dependencies": { "run-pkg": "^1.0.0" }, "devDependencies": { "dev-pkg": "^1.0.0" } },
            "node_modules/run-pkg": { "name": "run-pkg", "version": "1.0.0", "license": "{{runtimeLicense}}" },
            "node_modules/dev-pkg": { "name": "dev-pkg", "version": "1.0.0", "dev": true, "license": "{{devLicense}}" }
          }
        }
        """;
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        return path;
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<string> WriteNpmLockAsync(string? directLicense, string? transitiveLicense)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ol-sarif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "package-lock.json");
        var direct = directLicense is null ? string.Empty : $", \"license\": \"{directLicense}\"";
        var transitive = transitiveLicense is null ? string.Empty : $", \"license\": \"{transitiveLicense}\"";
        var json = $$"""
        {
          "lockfileVersion": 3,
          "packages": {
            "": { "name": "app", "dependencies": { "direct": "^1.0.0" } },
            "node_modules/direct": { "name": "direct", "version": "1.0.0"{{direct}}, "dependencies": { "transitive": "^1.0.0" } },
            "node_modules/transitive": { "name": "transitive", "version": "1.0.0"{{transitive}} }
          }
        }
        """;
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        return path;
    }

    private static async Task<string> WriteCycloneDxWithUnknownRootAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ol-sarif-{Guid.NewGuid():N}.json");
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
              "components": [
                {
                  "type": "library",
                  "bom-ref": "pkg:npm/example@1.0.0",
                  "name": "example",
                  "version": "1.0.0",
                  "purl": "pkg:npm/example@1.0.0",
                  "licenses": [{ "expression": "MIT" }]
                }
              ],
              "dependencies": [
                {
                  "ref": "application@1.0.0",
                  "dependsOn": ["pkg:npm/example@1.0.0"]
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        return path;
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunCheckWorkflowAsync(string root, string input, params string[] checkArguments)
        => RunCheckWorkflowAsync(root, input, [], checkArguments);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCheckWorkflowAsync(string root, string input, string[] scanArguments, params string[] checkArguments)
    {
        var scan = await RunOlAsync(root, ["scan", "--input", input, "--no-external-evidence", .. scanArguments, "--format", "Json"]);
        if (scan.ExitCode != 0) return scan;

        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-report-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(reportPath, scan.Stdout);
            return await RunOlAsync(root, ["check", "--report", reportPath, .. checkArguments]);
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
