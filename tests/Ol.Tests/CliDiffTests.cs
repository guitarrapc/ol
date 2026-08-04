using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class CliDiffTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Diff_Help_AdvertisesRequiredReportsWithoutPolicyOptions()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "diff", "--help");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stderr).IsEmpty();
        await Assert.That(result.Stdout).Contains("--previous <string>");
        await Assert.That(result.Stdout).Contains("Previously persisted JSON scan report. [Required]");
        await Assert.That(result.Stdout).Contains("--current <string>");
        await Assert.That(result.Stdout).Contains("Current JSON scan report. [Required]");
        await Assert.That(result.Stdout).DoesNotContain("--allow-licenses");
        await Assert.That(result.Stdout).DoesNotContain("--spdx-data");
    }

    [Test]
    public async Task Diff_WithIdenticalReports_ReportsNoChanges()
    {
        var root = FindRepositoryRoot();
        var (previous, current) = await WriteReportsAsync(root, "MIT", "MIT");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current);

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("No license-relevant changes.");
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WhenLicenseChanges_EmitsVerticalDiffWithVersionContext()
    {
        var root = FindRepositoryRoot();
        var (previous, current) = await WriteReportsAsync(root, "MIT", "GPL-3.0-only");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current);

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).IsEqualTo(string.Join(Environment.NewLine,
            [
                "License-relevant changes: 1 change in 1 component.",
                "",
                "~ npm:example@1.0.0",
                "    license: MIT -> GPL-3.0-only",
                "",
            ]));
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WhenVersionAndLicenseChange_ReportsBothTransitions()
    {
        var root = FindRepositoryRoot();
        var previous = await WriteReportAsync(root, "MIT", "1.0.0");
        var current = await WriteReportAsync(root, "GPL-3.0-only", "2.0.0");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current);

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).IsEqualTo(string.Join(Environment.NewLine,
            [
                "License-relevant changes: 2 changes in 1 component.",
                "",
                "~ npm:example",
                "    version: 1.0.0 -> 2.0.0",
                "    license: MIT -> GPL-3.0-only",
                "",
            ]));
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WhenComponentIsAdded_ReportsVersionLicenseAndStatus()
    {
        var root = FindRepositoryRoot();
        var previous = await WriteReportAsync(root, "MIT", includeComponent: false);
        var current = await WriteReportAsync(root, "GPL-3.0-only", "2.0.0");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current);

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).IsEqualTo(string.Join(Environment.NewLine,
            [
                "License-relevant changes: 1 change in 1 component.",
                "",
                "+ npm:example@2.0.0",
                "    license: GPL-3.0-only",
                "    status: matched",
                "",
            ]));
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WhenComponentIsRemoved_ReportsVersionLicenseAndStatus()
    {
        var root = FindRepositoryRoot();
        var previous = await WriteReportAsync(root, "MIT");
        var current = await WriteReportAsync(root, "MIT", includeComponent: false);
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current);

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).IsEqualTo(string.Join(Environment.NewLine,
            [
                "License-relevant changes: 1 change in 1 component.",
                "",
                "- npm:example@1.0.0",
                "    license: MIT",
                "    status: matched",
                "",
            ]));
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WhenTwoComponentsChange_ReportsBothComponentBlocks()
    {
        var root = FindRepositoryRoot();
        var previous = await WriteReportAsync(root, "MIT");
        var current = await WriteReportAsync(root, "Apache-2.0", "2.0.0", name: "replacement");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current);

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).IsEqualTo(string.Join(Environment.NewLine,
            [
                "License-relevant changes: 2 changes in 2 components.",
                "",
                "- npm:example@1.0.0",
                "    license: MIT",
                "    status: matched",
                "",
                "+ npm:replacement@2.0.0",
                "    license: Apache-2.0",
                "    status: matched",
                "",
            ]));
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    [Arguments("--allow-licenses", "MIT")]
    [Arguments("--spdx-data", "missing")]
    public async Task Diff_WithRemovedPolicyOption_ReturnsFrameworkParseError(string option, string value)
    {
        var root = FindRepositoryRoot();
        var (previous, current) = await WriteReportsAsync(root, "MIT", "GPL-3.0-only");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current, option, value);

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).Contains($"Argument '{option}' is not recognized.");
            await Assert.That(result.Stderr).IsEmpty();
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WithJsonFormat_EmitsDeterministicDocument()
    {
        var root = FindRepositoryRoot();
        var (previous, current) = await WriteReportsAsync(root, "MIT", "Apache-2.0");
        try
        {
            var first = await RunOlAsync(root, "diff", "--previous", previous, "--current", current, "--format", "Json");
            var second = await RunOlAsync(root, "diff", "--previous", previous, "--current", current, "--format", "Json");

            await Assert.That(first.ExitCode).IsEqualTo(0);
            await Assert.That(first.Stdout).IsEqualTo(second.Stdout);
            await Assert.That(first.Stdout).Contains("\"kind\": \"license-changed\"");
            await Assert.That(first.Stdout).Contains("\"schemaVersion\": 1");
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WithJsonFormat_WhenVersionAndLicenseChange_EmitsBothTransitions()
    {
        var root = FindRepositoryRoot();
        var previous = await WriteReportAsync(root, "MIT", "1.0.0");
        var current = await WriteReportAsync(root, "GPL-3.0-only", "2.0.0");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current, "--format", "Json");
            using var document = JsonDocument.Parse(result.Stdout);
            var changes = document.RootElement.GetProperty("changes");
            var versionChange = changes[0];
            var licenseChange = changes[1];

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(changes.GetArrayLength()).IsEqualTo(2);
            await Assert.That(document.RootElement.GetProperty("componentCount").GetInt32()).IsEqualTo(1);
            await Assert.That(document.RootElement.GetProperty("changeCount").GetInt32()).IsEqualTo(2);
            await Assert.That(versionChange.GetProperty("kind").GetString()).IsEqualTo("version-changed");
            await Assert.That(versionChange.GetProperty("version").GetProperty("previous").GetString()).IsEqualTo("1.0.0");
            await Assert.That(versionChange.GetProperty("version").GetProperty("current").GetString()).IsEqualTo("2.0.0");
            await Assert.That(versionChange.TryGetProperty("license", out _)).IsFalse();
            await Assert.That(licenseChange.GetProperty("kind").GetString()).IsEqualTo("license-changed");
            await Assert.That(licenseChange.GetProperty("version").GetProperty("previous").GetString()).IsEqualTo("1.0.0");
            await Assert.That(licenseChange.GetProperty("version").GetProperty("current").GetString()).IsEqualTo("2.0.0");
            await Assert.That(licenseChange.GetProperty("license").GetProperty("previous").GetString()).IsEqualTo("MIT");
            await Assert.That(licenseChange.GetProperty("license").GetProperty("current").GetString()).IsEqualTo("GPL-3.0-only");
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WithMissingReport_ReturnsOne()
    {
        var root = FindRepositoryRoot();
        var (previous, current) = await WriteReportsAsync(root, "MIT", "MIT");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", "does-not-exist.json");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("Unable to read report");
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WithoutCurrentReport_ReturnsFrameworkParseError()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "diff", "--previous", "a.json");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).Contains("Required argument 'current' was not specified.");
        await Assert.That(result.Stderr).IsEmpty();
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<(string Previous, string Current)> WriteReportsAsync(string root, string previousLicense, string currentLicense)
    {
        var previous = await WriteReportAsync(root, previousLicense);
        var current = await WriteReportAsync(root, currentLicense);
        return (previous, current);
    }

    private static async Task<string> WriteReportAsync(
        string root,
        string license,
        string version = "1.0.0",
        bool includeComponent = true,
        string name = "example")
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-diff-in-{Guid.NewGuid():N}.json");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-diff-{Guid.NewGuid():N}.json");
        var component = includeComponent
            ? $$"""{ "type": "library", "name": "{{name}}", "version": "{{version}}", "purl": "pkg:npm/{{name}}@{{version}}", "licenses": [{ "expression": "{{license}}" }] }"""
            : string.Empty;
        var json = $$"""
        { "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
          {{component}}
        ] }
        """;
        await File.WriteAllTextAsync(inputPath, json, Encoding.UTF8);
        var scan = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "Json");
        await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
        await File.WriteAllTextAsync(reportPath, scan.Stdout);
        File.Delete(inputPath);
        return reportPath;
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
