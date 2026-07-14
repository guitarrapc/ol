using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace Ol.Tests;

public sealed class CliScanTests
{
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlAsync(string root, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("src/Ol");
        startInfo.ArgumentList.Add("--");
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
