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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--skip-enrichment");

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
    public async Task Check_WithForbiddenLicense_ReturnsOneAndCompleteViolation()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteCycloneDxAsync("GPL-3.0-only");
        try
        {
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--skip-enrichment");

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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--skip-enrichment");

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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", allowLicenses, "--skip-enrichment");

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

        var result = await RunOlAsync(root, "check", "--input", "missing.json", "--skip-enrichment");

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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--skip-enrichment");

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
            var result = await RunOlAsync(root, "check", "--input", inputPath, "--allow-licenses", "MIT", "--dependency", "direct", "--skip-enrichment");

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
            var result = await RunOlAsync(root, "check", "--input", first, "--input", second, "--allow-licenses", "MIT", "--skip-enrichment");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(result.Stdout).Contains("2 components satisfy the allow-list.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> WriteCycloneDxAsync(string? license)
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-check-{Guid.NewGuid():N}.json");
        var licenseJson = license is null ? string.Empty : $", \"licenses\": [{{ \"expression\": \"{license}\" }}]";
        var json = string.Concat("{ \"bomFormat\": \"CycloneDX\", \"specVersion\": \"1.6\", \"components\": [{ \"type\": \"library\", \"name\": \"example\", \"version\": \"1.0.0\", \"purl\": \"pkg:npm/example@1.0.0\"", licenseJson, " }] }");
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
            startInfo.ArgumentList.Add(Path.Combine(root, "src", "Ol", "bin", "Debug", "net10.0", "ol.dll"));
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
