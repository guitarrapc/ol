using System.Diagnostics;

namespace Ol.Tests;

public sealed class CliSpdxTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task SpdxUse_Help_ShowsVersionAsPositionalArgument()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "spdx", "use", "--help");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stdout).Contains("Usage: spdx use [arguments...]");
        await Assert.That(result.Stdout).Contains("Arguments:");
        await Assert.That(result.Stdout).Contains("[0] <string>    Installed version to activate, or bundled.");
        await Assert.That(result.Stdout).DoesNotContain("--version <string>");
        await Assert.That(result.Stderr).IsEmpty();
    }

    [Test]
    public async Task SpdxUse_WithPositionalVersion_BindsVersionArgument()
    {
        var root = FindRepositoryRoot();
        var version = $"not-installed-{Guid.NewGuid():N}";

        var result = await RunOlAsync(root, "spdx", "use", version);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr.Trim()).IsEqualTo($"SPDX version is not installed: {version}");
        await Assert.That(result.Stderr).DoesNotContain("   at ");
    }

    [Test]
    public async Task WriteVersion_WithBundledActive_ShowsVersionsWithoutPath()
    {
        using var writer = new StringWriter();

        SpdxCommands.WriteVersion(writer, selectedVersion: null, bundledVersion: "3.26.0");

        await Assert.That(writer.ToString()).IsEqualTo(
            $"active: 3.26.0 (bundled){Environment.NewLine}" +
            $"user-selected: none{Environment.NewLine}" +
            $"bundled: 3.26.0{Environment.NewLine}");
    }

    [Test]
    public async Task WriteVersion_WithUserActive_ShowsSelectedAndBundledVersions()
    {
        using var writer = new StringWriter();

        SpdxCommands.WriteVersion(writer, selectedVersion: "3.27.0", bundledVersion: "3.26.0");

        await Assert.That(writer.ToString()).IsEqualTo(
            $"active: 3.27.0 (user){Environment.NewLine}" +
            $"user-selected: 3.27.0{Environment.NewLine}" +
            $"bundled: 3.26.0{Environment.NewLine}");
    }

    [Test]
    public async Task WriteList_WithBundledActive_MarksBundledVersion()
    {
        using var writer = new StringWriter();

        SpdxCommands.WriteList(writer, selectedVersion: null, bundledVersion: "3.26.0", installedVersions: ["3.27.0"]);

        await Assert.That(writer.ToString()).IsEqualTo(
            $"* 3.26.0 (bundled){Environment.NewLine}" +
            $"  3.27.0 (user){Environment.NewLine}");
    }

    [Test]
    public async Task WriteList_WithUserActive_MarksSelectedUserVersion()
    {
        using var writer = new StringWriter();

        SpdxCommands.WriteList(writer, selectedVersion: "3.27.0", bundledVersion: "3.27.0", installedVersions: ["3.27.0", "3.28.0"]);

        await Assert.That(writer.ToString()).IsEqualTo(
            $"  3.27.0 (bundled){Environment.NewLine}" +
            $"* 3.27.0 (user){Environment.NewLine}" +
            $"  3.28.0 (user){Environment.NewLine}");
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
