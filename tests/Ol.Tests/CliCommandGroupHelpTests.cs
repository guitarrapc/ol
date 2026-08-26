using System.Diagnostics;

namespace Ol.Tests;

public sealed class CliCommandGroupHelpTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    [Arguments("cache", """
        Usage: cache [command] [-h|--help] [--version]

        Manage locally cached scan evidence.

        Commands:
          clear     Clears cached evidence for the specified category.
          pack      Packs managed cache entries into one deterministic archive.
          prune     Removes managed cache entries older than the specified age.
          unpack    Unpacks an Ol cache archive into the managed cache directories.
        """)]
    [Arguments("spdx", """
        Usage: spdx [command] [-h|--help] [--version]

        Manage SPDX data.

        Commands:
          clear      Clear user-managed SPDX data.
          list       List installed SPDX data versions.
          update     Download SPDX data into the user data directory.
          use        Switch active SPDX data version.
          version    Show the active SPDX data source.
        """)]
    [Arguments("skill", """
        Usage: skill [command] [-h|--help] [--version]

        Install or export the bundled license-scan agent skill.

        Commands:
          export-plugin    Export a portable Agent Plugin package.
          install          Install the skill into the current workspace.
        """)]
    public async Task CommandGroup_Help_ShowsOnlyDirectSubcommands(string commandGroup, string expected)
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, commandGroup, "--help");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stdout.ReplaceLineEndings("\n")).IsEqualTo(expected.ReplaceLineEndings("\n") + "\n");
        await Assert.That(result.Stderr).IsEmpty();
    }

    [Test]
    [Arguments("cache")]
    [Arguments("spdx")]
    [Arguments("skill")]
    public async Task CommandGroup_ShortHelp_MatchesLongHelp(string commandGroup)
    {
        var root = FindRepositoryRoot();

        var longHelp = await RunOlAsync(root, commandGroup, "--help");
        var shortHelp = await RunOlAsync(root, commandGroup, "-h");

        await Assert.That(shortHelp.ExitCode).IsEqualTo(0);
        await Assert.That(shortHelp.Stdout).IsEqualTo(longHelp.Stdout);
        await Assert.That(shortHelp.Stderr).IsEmpty();
    }

    [Test]
    public async Task CacheClear_Help_ShowsCategoryAsPositionalArgument()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "cache", "clear", "--help");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stdout).Contains("Usage: cache clear [arguments...]");
        await Assert.That(result.Stdout).Contains("Arguments:");
        await Assert.That(result.Stdout).Contains("[0] <string>    Cache category: package-metadata, source-repository, github-file, or all. [Default: all]");
        await Assert.That(result.Stdout).DoesNotContain("--category");
        await Assert.That(result.Stderr).IsEmpty();
    }

    [Test]
    public async Task CacheArchive_Help_ShowsArchiveAndAgeArguments()
    {
        var root = FindRepositoryRoot();

        var pack = await RunOlAsync(root, "cache", "pack", "--help");
        var prune = await RunOlAsync(root, "cache", "prune", "--help");
        var unpack = await RunOlAsync(root, "cache", "unpack", "--help");

        await Assert.That(pack.ExitCode).IsEqualTo(0);
        await Assert.That(pack.Stdout).Contains("Usage: cache pack [arguments...] [options...]");
        await Assert.That(pack.Stdout).Contains("[0] <string>    Output .olcache archive path.");
        await Assert.That(pack.Stdout).Contains("--max-age <string?>");
        await Assert.That(prune.ExitCode).IsEqualTo(0);
        await Assert.That(prune.Stdout).Contains("Usage: cache prune [options...]");
        await Assert.That(prune.Stdout).Contains("--max-age <string>");
        await Assert.That(unpack.ExitCode).IsEqualTo(0);
        await Assert.That(unpack.Stdout).Contains("Usage: cache unpack [arguments...] [options...]");
        await Assert.That(unpack.Stdout).Contains("[0] <string>    Input .olcache archive path.");
        await Assert.That(pack.Stderr).IsEmpty();
        await Assert.That(prune.Stderr).IsEmpty();
        await Assert.That(unpack.Stderr).IsEmpty();
    }

    [Test]
    public async Task CacheClear_WithCategoryOption_RejectsRemovedOption()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "cache", "clear", "--category", "package-metadata");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr.Trim()).IsEqualTo("Argument '--category' is not recognized.");
    }

    [Test]
    public async Task CachePrune_WithoutMaxAge_RejectsMissingRequiredOption()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "cache", "prune");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr.Trim()).IsEqualTo("Required argument 'max-age' was not specified.");
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
