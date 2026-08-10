using System.Diagnostics;
using System.Text;

namespace Ol.Tests;

/// <summary>
/// Covers the split of the scan summary's finding count by whether the component resolved.
/// </summary>
/// <remarks>
/// One total answers no question a reader has. Collecting evidence Ol could not read is routine and
/// usually harmless: a repository outside GitHub, a registry with no license field. Those warnings attach
/// to components that resolved anyway, and reporting them in the same number as the ones sitting on an
/// unresolved component made a fully resolved report announce dozens of findings. Measured across nine
/// Cysharp repositories, 397 of 737 warnings were on components that already had a license. Splitting the
/// count keeps every warning visible while saying which ones sit on an unsettled result.
///
/// Every case declares its warnings through a deprecated SPDX identifier and runs with
/// <c>--no-external-evidence</c>. The identifier is a fact the input carries, so it lands on a component
/// whatever its status and no collection can add or remove one; the counts below are therefore exact
/// rather than dependent on what a registry happened to answer.
/// </remarks>
public sealed class ScanFindingsSummaryTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    /// <summary>Two resolved and two unresolved components, each carrying one warning.</summary>
    [Test]
    public async Task Scan_WithWarningsOnBothSides_ReportsThemSeparately()
    {
        var input = await WriteSbomAsync(resolved: 2, unresolved: 2);
        try
        {
            var result = await RunOlAsync("scan", "--input", input, "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stderr).Contains("License results: 4 displayed components; 2 matched; 0 conflict; 0 unknown; 2 ambiguous;");
            await Assert.That(result.Stderr).Contains("Findings: 2 warnings on unresolved components; 2 on resolved components; 4 deprecated SPDX identifiers");
        }
        finally
        {
            Cleanup(input);
        }
    }

    /// <summary>
    /// The case that motivated the split: nothing is unresolved, yet the report still carries warnings.
    /// </summary>
    [Test]
    public async Task Scan_WithEverythingResolved_ReportsNoUnresolvedWarnings()
    {
        var input = await WriteSbomAsync(resolved: 2, unresolved: 0);
        try
        {
            var result = await RunOlAsync("scan", "--input", input, "--no-external-evidence");

            await Assert.That(result.Stderr).Contains("License results: 2 displayed components; 2 matched;");
            await Assert.That(result.Stderr).Contains("Findings: 0 warnings on unresolved components; 2 on resolved components; 2 deprecated SPDX identifiers");
        }
        finally
        {
            Cleanup(input);
        }
    }

    /// <summary>The mirror case: every warning sits on a component the scan did not settle.</summary>
    [Test]
    public async Task Scan_WithNothingResolved_ReportsNoResolvedWarnings()
    {
        var input = await WriteSbomAsync(resolved: 0, unresolved: 2);
        try
        {
            var result = await RunOlAsync("scan", "--input", input, "--no-external-evidence");

            await Assert.That(result.Stderr).Contains("License results: 2 displayed components; 0 matched; 0 conflict; 0 unknown; 2 ambiguous;");
            await Assert.That(result.Stderr).Contains("Findings: 2 warnings on unresolved components; 0 on resolved components; 2 deprecated SPDX identifiers");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Test]
    public async Task Scan_WithoutWarnings_ReportsZeroOnBothSides()
    {
        var input = await WriteCleanSbomAsync();
        try
        {
            var result = await RunOlAsync("scan", "--input", input, "--no-external-evidence");

            await Assert.That(result.Stderr).Contains("Findings: 0 warnings on unresolved components; 0 on resolved components; 0 deprecated SPDX identifiers");
        }
        finally
        {
            Cleanup(input);
        }
    }

    /// <summary>One finding on each side, which is where the leading noun turns singular.</summary>
    [Test]
    public async Task Scan_WithOneWarningOnEachSide_UsesSingularWording()
    {
        var input = await WriteSbomAsync(resolved: 1, unresolved: 1);
        try
        {
            var result = await RunOlAsync("scan", "--input", input, "--no-external-evidence");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stderr).Contains("Findings: 1 warning on unresolved components; 1 on resolved components; 2 deprecated SPDX identifiers");
        }
        finally
        {
            Cleanup(input);
        }
    }

    /// <summary>A grouped view aggregates the summary through a second path, and both must agree.</summary>
    [Test]
    public async Task Scan_GroupedView_ReportsTheSameSplitAsTheComponentView()
    {
        var input = await WriteSbomAsync(resolved: 2, unresolved: 2);
        try
        {
            var components = await RunOlAsync("scan", "--input", input, "--no-external-evidence");
            var grouped = await RunOlAsync("scan", "--input", input, "--no-external-evidence", "--group-by", "license");

            await Assert.That(grouped.ExitCode).IsEqualTo(0).Because(grouped.Stderr);
            await Assert.That(SelectLine(grouped.Stderr, "Findings:")).IsEqualTo(SelectLine(components.Stderr, "Findings:"));
            await Assert.That(SelectLine(grouped.Stderr, "Findings:")).Contains("2 warnings on unresolved components; 2 on resolved components");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Test]
    public async Task Scan_WithQuiet_StillSuppressesTheSummary()
    {
        var input = await WriteSbomAsync(resolved: 2, unresolved: 2);
        try
        {
            var result = await RunOlAsync("scan", "--input", input, "--no-external-evidence", "--quiet");

            await Assert.That(result.Stderr).DoesNotContain("Findings:");
        }
        finally
        {
            Cleanup(input);
        }
    }

    private static string SelectLine(string text, string marker)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(marker, StringComparison.Ordinal)) return line.TrimEnd('\r');
        }

        throw new InvalidOperationException($"No line containing '{marker}' was found in:{Environment.NewLine}{text}");
    }

    /// <summary>
    /// Writes an SBOM with the requested number of components on each side of the split.
    /// </summary>
    /// <remarks>
    /// A deprecated identifier declared alone still resolves the component; declared beside a second
    /// expression it leaves the component ambiguous. Both carry exactly one warning, so the two counts
    /// follow from the component counts alone.
    /// </remarks>
    private static async Task<string> WriteSbomAsync(int resolved, int unresolved)
    {
        var deprecated = new[] { "GPL-2.0", "LGPL-2.1" };
        var accompanying = new[] { "MIT", "Apache-2.0" };
        var entries = new List<string>();
        for (var i = 0; i < resolved; i++)
        {
            entries.Add($$"""{ "bom-ref": "r{{i}}", "type": "library", "name": "Resolved{{i}}", "version": "1.0.0", "purl": "pkg:nuget/Resolved{{i}}@1.0.0", "licenses": [ { "expression": "{{deprecated[i % deprecated.Length]}}" } ] }""");
        }

        for (var i = 0; i < unresolved; i++)
        {
            entries.Add($$"""{ "bom-ref": "u{{i}}", "type": "library", "name": "Unresolved{{i}}", "version": "1.0.0", "purl": "pkg:nuget/Unresolved{{i}}@1.0.0", "licenses": [ { "expression": "{{accompanying[i % accompanying.Length]}}" }, { "expression": "{{deprecated[i % deprecated.Length]}}" } ] }""");
        }

        return await WriteComponentsAsync(entries);
    }

    private static async Task<string> WriteCleanSbomAsync()
        => await WriteComponentsAsync([
            """{ "bom-ref": "a", "type": "library", "name": "Resolved", "version": "1.0.0", "purl": "pkg:nuget/Resolved@1.0.0", "licenses": [ { "expression": "MIT" } ] }"""
        ]);

    private static async Task<string> WriteComponentsAsync(List<string> entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ol-findings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [ {{string.Join(",\n    ", entries)}} ]
            }
            """,
            Encoding.UTF8);
        return path;
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlAsync(params string[] args)
    {
        var root = FindRepositoryRoot();
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
