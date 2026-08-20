using System.Diagnostics;
using System.Text;

namespace Ol.Tests;

/// <summary>
/// Covers the root-to-component dependency path in the human-readable reports.
/// </summary>
/// <remarks>
/// A reviewer reading that a transitive component is ambiguous cannot act until they know which direct
/// dependency introduced it. SARIF already carries that path, so these tests pin the same fact to the
/// text and Markdown reports. One input covers every reachability class: a direct dependency has no
/// introducer, a transitive one has a path through it, and a component the input never linked to the
/// root has no path at all.
/// </remarks>
public sealed class DependencyPathReportTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    private const string TransitivePath = "pkg:nuget/Direct@1.0.0 > pkg:nuget/Transitive@2.0.0";

    [Test]
    public async Task Check_WithTransitiveViolation_ShowsTheIntroducingPath()
    {
        var root = FindRepositoryRoot();
        var input = await WriteSbomAsync();
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT");

            await Assert.That(result.ExitCode).IsEqualTo(2).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Reason\tMechanism\tReference\tPath");
            await Assert.That(LastColumn(SelectLine(result.Stdout, "pkg:nuget/Transitive@2.0.0"))).IsEqualTo(TransitivePath);
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Test]
    [Arguments("pkg:nuget/Direct@1.0.0")]
    [Arguments("pkg:nuget/Orphan@3.0.0")]
    public async Task Check_WithoutIntroducingDependency_ShowsNoPath(string purl)
    {
        var root = FindRepositoryRoot();
        var input = await WriteSbomAsync();
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT");
            var row = SelectLine(result.Stdout, purl);

            await Assert.That(LastColumn(row)).IsEqualTo("-");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Test]
    public async Task Scan_TextUnresolvedSection_ShowsTheIntroducingPath()
    {
        var root = FindRepositoryRoot();
        var input = await WriteSbomAsync();
        try
        {
            var result = await RunOlAsync(root, "scan", "--input", input, "--no-external-evidence", "--format", "text", "--quiet");
            var section = result.Stdout[result.Stdout.IndexOf("Unresolved components", StringComparison.Ordinal)..];

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(section).Contains($"Transitive 2.0.0 declared_license_location_not_collected https://example.test/transitive-LICENSE.txt via {TransitivePath}");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Test]
    [Arguments("Direct 1.0.0")]
    [Arguments("Orphan 3.0.0")]
    public async Task Scan_TextUnresolvedSection_WithoutIntroducingDependency_OmitsThePath(string component)
    {
        var root = FindRepositoryRoot();
        var input = await WriteSbomAsync();
        try
        {
            var result = await RunOlAsync(root, "scan", "--input", input, "--no-external-evidence", "--format", "text", "--quiet");
            var section = result.Stdout[result.Stdout.IndexOf("Unresolved components", StringComparison.Ordinal)..];
            var row = SelectLine(section, component);

            await Assert.That(row).DoesNotContain(" via ");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Test]
    public async Task Scan_MarkdownUnresolvedSection_ShowsTheIntroducingPath()
    {
        var root = FindRepositoryRoot();
        var input = await WriteSbomAsync();
        try
        {
            var result = await RunOlAsync(root, "scan", "--input", input, "--no-external-evidence", "--format", "markdown", "--quiet");
            var section = result.Stdout[result.Stdout.IndexOf("## Unresolved components", StringComparison.Ordinal)..];

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(section).Contains("| NAME | VERSION | REASON | REFERENCE | PATH |");
            await Assert.That(section).Contains($"| Transitive | 2.0.0 | declared_license_location_not_collected | https://example.test/transitive-LICENSE.txt | {TransitivePath} |");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Test]
    [Arguments("| Direct | 1.0.0 |")]
    [Arguments("| Orphan | 3.0.0 |")]
    public async Task Scan_MarkdownUnresolvedSection_WithoutIntroducingDependency_MarksThePathAbsent(string component)
    {
        var root = FindRepositoryRoot();
        var input = await WriteSbomAsync();
        try
        {
            var result = await RunOlAsync(root, "scan", "--input", input, "--no-external-evidence", "--format", "markdown", "--quiet");
            var section = result.Stdout[result.Stdout.IndexOf("## Unresolved components", StringComparison.Ordinal)..];
            var row = SelectLine(section, component);

            // The Markdown report already renders an absent value as "-", which is also what the check table uses.
            await Assert.That(row).EndsWith("| - |");
        }
        finally
        {
            Cleanup(input);
        }
    }

    /// <summary>
    /// An input that records no dependency edges cannot name an introducer, and inventing one from
    /// the direct/transitive classification would state a relationship the input never described.
    /// </summary>
    [Test]
    public async Task Scan_WithoutDependencyGraph_OmitsThePath()
    {
        var root = FindRepositoryRoot();
        var input = Path.Combine(Path.GetTempPath(), $"ol-path-flat-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(input, """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "Lonely",
              "version": "1.0.0",
              "purl": "pkg:nuget/Lonely@1.0.0",
              "licenses": [ { "license": { "name": "Unknown - See URL", "url": "https://example.test/LICENSE.txt" } } ]
            }
          ]
        }
        """, Encoding.UTF8);
        try
        {
            var result = await RunOlAsync(root, "scan", "--input", input, "--no-external-evidence", "--format", "text", "--quiet");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Lonely 1.0.0 declared_license_location_not_collected https://example.test/LICENSE.txt");
            await Assert.That(result.Stdout).DoesNotContain(" via ");
        }
        finally
        {
            Cleanup(input);
        }
    }

    /// <summary>The path is a fact about the graph, so both projections of one run must state the same one.</summary>
    [Test]
    public async Task Check_TextPath_MatchesTheSarifDependencyPath()
    {
        var root = FindRepositoryRoot();
        var input = await WriteSbomAsync();
        var sarifPath = Path.Combine(Path.GetTempPath(), $"ol-path-{Guid.NewGuid():N}.sarif");
        try
        {
            var result = await RunCheckWorkflowAsync(root, input, "--allow-licenses", "MIT", "--sarif", sarifPath);
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));

            var results = document.RootElement.GetProperty("runs")[0].GetProperty("results");
            string? sarifPathText = null;
            for (var i = 0; i < results.GetArrayLength(); i++)
            {
                if (results[i].GetProperty("properties").GetProperty("purl").GetString() != "pkg:nuget/Transitive@2.0.0") continue;
                var elements = results[i].GetProperty("properties").GetProperty("dependencyPath");
                var parts = new string[elements.GetArrayLength()];
                for (var j = 0; j < parts.Length; j++) parts[j] = elements[j].GetString()!;
                sarifPathText = string.Join(" > ", parts);
            }

            await Assert.That(sarifPathText).IsEqualTo(TransitivePath);
            await Assert.That(result.Stdout).Contains(sarifPathText!);
        }
        finally
        {
            Cleanup(input, sarifPath);
        }
    }

    /// <summary>Reads the Path column, which is last however many evidence columns precede it.</summary>
    private static string LastColumn(string row)
    {
        var columns = row.Split('\t');
        return columns[^1];
    }

    private static string SelectLine(string text, string marker)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(marker, StringComparison.Ordinal)) return line.TrimEnd('\r');
        }

        throw new InvalidOperationException($"No line containing '{marker}' was found in:{Environment.NewLine}{text}");
    }

    private static async Task<string> WriteSbomAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ol-path-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "metadata": {
            "component": { "bom-ref": "root-app", "type": "application", "name": "app", "version": "0.0.0", "licenses": [ { "expression": "MIT" } ] }
          },
          "components": [
            { "bom-ref": "direct", "type": "library", "name": "Direct", "version": "1.0.0", "purl": "pkg:nuget/Direct@1.0.0", "licenses": [ { "license": { "name": "Unknown - See URL", "url": "https://example.test/direct-LICENSE.txt" } } ] },
            { "bom-ref": "transitive", "type": "library", "name": "Transitive", "version": "2.0.0", "purl": "pkg:nuget/Transitive@2.0.0", "licenses": [ { "license": { "name": "Unknown - See URL", "url": "https://example.test/transitive-LICENSE.txt" } } ] },
            { "bom-ref": "orphan", "type": "library", "name": "Orphan", "version": "3.0.0", "purl": "pkg:nuget/Orphan@3.0.0", "licenses": [ { "license": { "name": "Unknown - See URL", "url": "https://example.test/orphan-LICENSE.txt" } } ] }
          ],
          "dependencies": [
            { "ref": "root-app", "dependsOn": ["direct"] },
            { "ref": "direct", "dependsOn": ["transitive"] }
          ]
        }
        """, Encoding.UTF8);
        return path;
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCheckWorkflowAsync(string root, string input, params string[] checkArguments)
    {
        var scan = await RunOlAsync(root, "scan", "--input", input, "--no-external-evidence", "--format", "Json");
        if (scan.ExitCode != 0) return scan;

        var reportPath = Path.Combine(Path.GetTempPath(), $"ol-path-report-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(reportPath, scan.Stdout);
            return await RunOlAsync(root, ["check", "--report", reportPath, .. checkArguments]);
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
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
