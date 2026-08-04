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
        await Assert.That(result.Stdout).Contains("[0] <string>    Version to activate.");
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
