using System.Diagnostics;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace Ol.Tests;

public sealed class CliScanTests
{
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
