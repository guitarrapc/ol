using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Ol.Tests;

/// <summary>
/// Holds the committed self-scan snapshots equal to what Ol currently produces from the committed SBOM.
/// </summary>
/// <remarks>
/// The snapshots are the one place Ol's whole report surface is pinned byte-for-byte, and until now only
/// CI enforced that. A change to the report schema or to a digest definition therefore merged while the
/// snapshots still described the previous shape, and the break surfaced on an unrelated pull request as a
/// failure nobody had touched. Two such changes accumulated on <c>main</c> at once. Running the same
/// comparison as a test moves the signal to the commit that causes it.
///
/// The comparison reproduces <c>sandbox/Update-SelfScan.ps1</c> rather than invoking it, so it needs no
/// PowerShell and runs wherever <c>dotnet test</c> does. It regenerates only the reports, exactly as the
/// CI step does; the committed SBOM is an input here, not an output.
/// </remarks>
public sealed class SelfScanSnapshotTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    /// <summary>Redacts the volatile tool version the same way the generator does. See <see cref="Normalize"/>.</summary>
    private const string ToolVersionPattern = """("tool"\s*:\s*\{[\s\S]*?"version"\s*:\s*")[^"]*""";

    private const string RegenerateHint =
        "Run ./sandbox/Update-SelfScan.ps1 -NoBuild -ReportsOnly and commit the result. "
        + "If the only difference is metadata.spdx, check `ol spdx list`: user-installed SPDX data replaces the bundled data this snapshot records.";

    [Test]
    public async Task SelfScan_CommittedSbom_UsesLfNewlines()
    {
        var root = FindRepositoryRoot();
        var sbom = Path.Combine(root, "sandbox", "self", "ol.cdx.json");
        var content = await File.ReadAllBytesAsync(sbom);

        await Assert.That(content.AsSpan().IndexOf("\r\n"u8)).IsEqualTo(-1)
            .Because("the self-scan input is hashed byte-for-byte and must match its LF checkout in Linux CI");
    }

    [Test]
    [Arguments("text", "txt")]
    [Arguments("markdown", "md")]
    [Arguments("json", "json")]
    public async Task SelfScan_RegeneratedReport_MatchesTheCommittedSnapshot(string format, string extension)
    {
        var root = FindRepositoryRoot();
        var sbom = Path.Combine(root, "sandbox", "self", "ol.cdx.json");
        var snapshot = Path.Combine(root, "sandbox", "self", $"ol.{extension}");

        var result = await RunOlAsync(
            root,
            "scan",
            "--input",
            sbom,
            "--format",
            format,
            "--no-external-evidence",
            "--quiet",
            "--concurrency",
            "4");

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);

        var regenerated = Normalize(format, result.Stdout);
        var committed = Normalize(format, await File.ReadAllTextAsync(snapshot));

        await Assert.That(regenerated).IsEqualTo(committed)
            .Because($"sandbox/self/ol.{extension} is stale. {DescribeFirstDifference(committed, regenerated)} {RegenerateHint}");
    }

    /// <summary>
    /// Names the first line that differs, because the assertion itself truncates a whole report.
    /// </summary>
    /// <remarks>
    /// The remedy is always to regenerate, but a reviewer still has to decide whether the change is the
    /// one their commit intended. A snapshot failure that only says "these differ" makes that judgement
    /// require a manual regeneration first.
    /// </remarks>
    private static string DescribeFirstDifference(string committed, string regenerated)
    {
        var committedLines = committed.Split('\n');
        var regeneratedLines = regenerated.Split('\n');
        var shared = Math.Min(committedLines.Length, regeneratedLines.Length);
        for (var i = 0; i < shared; i++)
        {
            if (string.Equals(committedLines[i], regeneratedLines[i], StringComparison.Ordinal)) continue;
            return $"First difference at line {i + 1}: committed '{Clip(committedLines[i])}', regenerated '{Clip(regeneratedLines[i])}'.";
        }

        return $"The reports agree for {shared} lines; committed has {committedLines.Length} lines and regenerated has {regeneratedLines.Length}.";
    }

    private static string Clip(string value)
        => value.Length <= 160 ? value.Trim() : string.Concat(value.AsSpan(0, 160).Trim(), "...");

    /// <summary>
    /// Applies the generator's normalization so the comparison sees the snapshot's own definition.
    /// </summary>
    /// <remarks>
    /// Newlines are collapsed because the snapshots are LF on every platform, and the tool version is
    /// redacted because a report embeds the build's commit, which would otherwise make the snapshot churn
    /// on every commit. Both rules live in <c>sandbox/Update-SelfScan.ps1</c>; changing one without the
    /// other makes this test disagree with the file it guards.
    /// </remarks>
    private static string Normalize(string format, string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        return format == "json"
            ? Regex.Replace(normalized, ToolVersionPattern, "${1}0.0.0-selfscan")
            : normalized;
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
                StandardOutputEncoding = new UTF8Encoding(false),
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
