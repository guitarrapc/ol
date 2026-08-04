using System.Diagnostics;
using System.Text;

namespace Ol.Tests;

public sealed class CliDiffTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

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
    public async Task Diff_WhenLicenseChanges_ReportsTheChange()
    {
        var root = FindRepositoryRoot();
        var (previous, current) = await WriteReportsAsync(root, "MIT", "GPL-3.0-only");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current);

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).Contains("license-changed");
            await Assert.That(result.Stdout).Contains("example");
            await Assert.That(result.Stdout).Contains("MIT");
            await Assert.That(result.Stdout).Contains("GPL-3.0-only");
        }
        finally
        {
            Cleanup(previous, current);
        }
    }

    [Test]
    public async Task Diff_WithAllowList_ReportsPolicyTransition()
    {
        var root = FindRepositoryRoot();
        var (previous, current) = await WriteReportsAsync(root, "MIT", "GPL-3.0-only");
        try
        {
            var result = await RunOlAsync(root, "diff", "--previous", previous, "--current", current, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stdout).Contains("policy-changed");
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
    public async Task Diff_WithoutBothReports_ReturnsOne()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "diff", "--previous", "a.json");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stderr).Contains("--previous and --current");
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

    private static async Task<string> WriteReportAsync(string root, string license)
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-diff-in-{Guid.NewGuid():N}.json");
        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-diff-{Guid.NewGuid():N}.json");
        var json = $$"""
        { "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
          { "type": "library", "name": "example", "version": "1.0.0", "purl": "pkg:npm/example@1.0.0", "licenses": [{ "expression": "{{license}}" }] }
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
