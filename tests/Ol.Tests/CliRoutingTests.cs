using System.Diagnostics;

namespace Ol.Tests;

public sealed class CliRoutingTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    [Arguments("not-a-command", "Command 'not-a-command' is not recognized.")]
    [Arguments("spdx not-a-command", "Command 'spdx not-a-command' is not recognized.")]
    [Arguments("cache not-a-command", "Command 'cache not-a-command' is not recognized.")]
    [Arguments("skill not-a-command", "Command 'skill not-a-command' is not recognized.")]
    public async Task Route_WithUnknownCommand_WritesDiagnosticToStderr(string commandLine, string expected)
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, commandLine.Split(' '));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr.Trim()).IsEqualTo(expected);
    }

    /// <summary>
    /// An option supplied twice is rejected rather than resolved by position. Ol has no layer for a later
    /// value to override — no configuration file, no environment defaults — so no invocation means "replace
    /// what I said before". Silently keeping one of them changed the policy a run enforced, and which one
    /// survived depended on argument order.
    /// </summary>
    [Test]
    [Arguments("check --report a.json --report b.json --allow-licenses MIT", "--report")]
    [Arguments("check --report a.json --allow-licenses MIT --allow-licenses Apache-2.0", "--allow-licenses")]
    [Arguments("check --report a.json --allow-licenses MIT --exclude-packages pkg:npm/ --exclude-packages pkg:nuget/", "--exclude-packages")]
    [Arguments("check --report a.json --allow-licenses MIT --baseline a.json --baseline b.json", "--baseline")]
    [Arguments("scan --input a --format json --format text", "--format")]
    [Arguments("scan --input a --verbose --verbose", "--verbose")]
    [Arguments("diff --previous a.json --previous b.json --current c.json", "--previous")]
    [Arguments("skill export-plugin --output a --output b", "--output")]
    public async Task Route_WithRepeatedSingleUseOption_WritesDiagnosticToStderr(string commandLine, string option)
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, commandLine.Split(' '));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr.Trim())
            .IsEqualTo($"Argument '{option}' was supplied more than once.");
    }

    /// <summary>Options are matched the way the parser matches them, so casing cannot slip a duplicate past.</summary>
    [Test]
    public async Task Route_WithRepeatedOptionDifferingInCase_WritesDiagnosticToStderr()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "check", "--report", "a.json", "--REPORT", "b.json", "--allow-licenses", "MIT");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stderr.Trim())
            .IsEqualTo("Argument '--report' was supplied more than once.");
    }

    /// <summary>
    /// The two options that accumulate are collapsed into one occurrence before the check runs, so the rule
    /// needs no list of exemptions and cannot fall out of step with which options actually accumulate.
    /// </summary>
    [Test]
    [Arguments("scan --input tests/Ol.Tests/Fixtures/package-lock.json --input tests/Ol.Tests/Fixtures/nuget-project.assets.json --no-external-evidence --quiet")]
    [Arguments("scan --input tests/Ol.Tests/Fixtures --exclude-input-path tests/Ol.Tests/Fixtures/package-lock.json --exclude-input-path tests/Ol.Tests/Fixtures/nuget-project.assets.json --no-external-evidence --quiet")]
    public async Task Route_WithRepeatedAccumulatingOption_IsAccepted(string commandLine)
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, commandLine.Split(' '));

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
    }

    /// <summary>Everything after the escape is a value, so a repeat there is not an invocation error.</summary>
    [Test]
    public async Task Route_WithRepeatedOptionAfterEscape_IsAccepted()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, "scan", "--input", "tests/Ol.Tests/Fixtures/package-lock.json", "--no-external-evidence", "--quiet", "--", "--input", "ignored");

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
    }

    [Test]
    [Arguments("scan", "Command 'scan' requires arguments. Use 'ol scan --help' for usage.")]
    [Arguments("check", "Command 'check' requires arguments. Use 'ol check --help' for usage.")]
    [Arguments("diff", "Command 'diff' requires arguments. Use 'ol diff --help' for usage.")]
    [Arguments("spdx", "Command 'spdx' requires a subcommand. Use 'ol spdx --help' for usage.")]
    [Arguments("cache", "Command 'cache' requires a subcommand. Use 'ol cache --help' for usage.")]
    [Arguments("skill", "Command 'skill' requires a subcommand. Use 'ol skill --help' for usage.")]
    [Arguments("skill export-plugin", "Command 'skill export-plugin' requires --output. Use 'ol skill export-plugin --help' for usage.")]
    [Arguments("spdx use", "Command 'spdx use' requires an argument. Use 'ol spdx use --help' for usage.")]
    public async Task Route_WithIncompleteCommand_WritesDiagnosticToStderr(string commandLine, string expected)
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root, commandLine.Split(' '));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr.Trim()).IsEqualTo(expected);
    }

    [Test]
    public async Task Route_WithoutArguments_ShowsRootHelp()
    {
        var root = FindRepositoryRoot();

        var result = await RunOlAsync(root);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stdout).StartsWith("Usage: [command]");
        await Assert.That(result.Stderr).IsEmpty();
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
