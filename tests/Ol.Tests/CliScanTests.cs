using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Ol.Core.GitHub;
using Ol.Core.Licensing;

namespace Ol.Tests;

public sealed class CliScanTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Scan_Help_DoesNotAdvertiseRemovedSbomOption()
    {
        var root = FindRepositoryRoot();

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--help");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        await Assert.That(stdout).Contains("--input <string[]>");
        await Assert.That(stdout).Contains("Repeatable resolved dependency input files or directories. [Required]");
        await Assert.That(stdout).Contains("--input-format <string>");
        await Assert.That(stdout).Contains("--exclude-input-path <string[]?>");
        await Assert.That(stdout).Contains("Repeatable file or directory paths excluded from directory input discovery.");
        await Assert.That(stdout).Contains("[Default: @\"auto\"]");
        await Assert.That(stdout).Contains("auto (default), cyclonedx, spdx, nuget-assets, npm-package-lock, pnpm-lock, yarn-classic-lock, yarn-berry-lock, cargo-metadata, go-module-graph, pip-inspect, composer-lock, bundler-lock, maven-dependency-tree, swift-package-resolved, or cocoapods-lock");
        await Assert.That(stdout).Contains("Maximum concurrent package metadata and source repository lookups.");
        await Assert.That(stdout).Contains("Retry count for package registry and GitHub License API requests.");
        await Assert.That(stdout).DoesNotContain("--sbom");
        await Assert.That(stdout).DoesNotContain("--out");
    }

    [Test]
    public async Task Scan_WithoutInput_WritesFrameworkParseErrorToStderr()
    {
        var root = FindRepositoryRoot();

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--no-external-evidence");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr.Trim()).IsEqualTo("Required argument 'input' was not specified.");
    }

    [Test]
    public async Task Scan_WithRemovedSbomOption_ReturnsUnknownOptionError()
    {
        var root = FindRepositoryRoot();

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--sbom", "removed.json");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr.Trim()).IsEqualTo("Argument '--sbom' is not recognized.");
    }

    [Test]
    [Arguments("--out")]
    [Arguments("--out-file")]
    public async Task Scan_WithRemovedOutputFileOption_ReturnsUnknownOptionError(string option)
    {
        var root = FindRepositoryRoot();

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", option, "removed.json");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr.Trim()).IsEqualTo($"Argument '{option}' is not recognized.");
    }

    [Test]
    public async Task Scan_WithTextFormat_AlignsComponentColumnsAsAnAsciiTable()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0", "licenses": [{ "expression": "MIT" }] }, { "type": "library", "name": "longer", "version": "2", "purl": "pkg:npm/longer@2", "licenses": [{ "expression": "Apache-2.0" }] }] }""",
            Encoding.UTF8);

        try
        {
            var result = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "text", "--quiet", "--no-external-evidence");
            var output = result.Stdout.ReplaceLineEndings("\n");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(output).Contains(string.Join(
                '\n',
                "NAME    VERSION  LICENSE     ECOSYSTEM  DEPENDENCY  STATUS   SUPPLIED",
                "------  -------  ----------  ---------  ----------  -------  --------",
                "a       1.0.0    MIT         npm        unknown     matched  sbom",
                "longer  2        Apache-2.0  npm        unknown     matched  sbom"));
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// Verbose adds PURL as the last column, which moves where the row stops padding. Nothing else
    /// exercises the eight-column shape, so the header, the separator and the rows are pinned together.
    /// </summary>
    [Test]
    public async Task Scan_WithVerboseTextFormat_AlignsThePurlColumnLast()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0", "licenses": [{ "expression": "MIT" }] }, { "type": "library", "name": "longer", "version": "2", "purl": "pkg:npm/longer@2", "licenses": [{ "expression": "Apache-2.0" }] }] }""",
            Encoding.UTF8);

        try
        {
            var result = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "text", "--verbose", "--quiet", "--no-external-evidence");
            var output = result.Stdout.ReplaceLineEndings("\n");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(output).Contains(string.Join(
                '\n',
                "NAME    VERSION  LICENSE     ECOSYSTEM  DEPENDENCY  STATUS   SUPPLIED  PURL",
                "------  -------  ----------  ---------  ----------  -------  --------  ----------------",
                "a       1.0.0    MIT         npm        unknown     matched  sbom      pkg:npm/a@1.0.0",
                "longer  2        Apache-2.0  npm        unknown     matched  sbom      pkg:npm/longer@2"));
            await Assert.That(output).DoesNotContain(" \n");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// Column width is the widest cell, so an oversized name must not pad every other row to its length.
    /// A name no registry can issue still reaches the table from a hand-written or broken-generator SBOM.
    /// </summary>
    [Test]
    public async Task Scan_WithTextFormatAndOversizedComponentName_DoesNotPadEveryRowToIt()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        var oversized = new string('x', 8192);
        await File.WriteAllTextAsync(
            inputPath,
            $$"""{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "{{oversized}}", "version": "1.0.0", "purl": "pkg:npm/big@1.0.0", "licenses": [{ "expression": "MIT" }] }, { "type": "library", "name": "small", "version": "2.0.0", "purl": "pkg:npm/small@2.0.0", "licenses": [{ "expression": "MIT" }] }] }""",
            Encoding.UTF8);

        try
        {
            var result = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "text", "--quiet", "--no-external-evidence");
            var lines = result.Stdout.ReplaceLineEndings("\n").Split('\n');
            var small = Array.Find(lines, line => line.StartsWith("small", StringComparison.Ordinal));

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(small).IsNotNull();
            await Assert.That(small!.Length).IsLessThanOrEqualTo(TextTable.MaxColumnWidth + 128);
            await Assert.That(result.Stdout).Contains(oversized);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// A terminal aligns on display columns, so a CJK name padded by its UTF-8 byte count drags every
    /// following column of that row left while widening the column for every other row.
    /// </summary>
    [Test]
    public async Task Scan_WithTextFormatAndWideComponentName_AlignsOnDisplayColumns()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "日本語", "version": "1.0.0", "purl": "pkg:npm/wide@1.0.0", "licenses": [{ "expression": "MIT" }] }, { "type": "library", "name": "abcdef", "version": "2.0.0", "purl": "pkg:npm/ascii@2.0.0", "licenses": [{ "expression": "MIT" }] }] }""",
            Encoding.UTF8);

        try
        {
            var result = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "text", "--quiet", "--no-external-evidence");
            var output = result.Stdout.ReplaceLineEndings("\n");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(output).Contains(string.Join(
                '\n',
                "NAME    VERSION  LICENSE  ECOSYSTEM  DEPENDENCY  STATUS   SUPPLIED",
                "------  -------  -------  ---------  ----------  -------  --------",
                "abcdef  2.0.0    MIT      npm        unknown     matched  sbom",
                "日本語  1.0.0    MIT      npm        unknown     matched  sbom"));
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithInputFormatOmitted_AutoDetectsCycloneDx()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            await Assert.That(stdout[^2]).IsEqualTo('}');
            await Assert.That(stdout[^1]).IsEqualTo('\n');
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata");
            var tool = metadata.GetProperty("tool");
            await Assert.That(tool.GetProperty("name").GetString()).IsEqualTo("ol");
            await Assert.That(tool.GetProperty("version").GetString()).IsEqualTo(ToolVersion);
            await Assert.That(tool.GetProperty("informationUri").GetString()).IsEqualTo("https://github.com/guitarrapc/ol");
            await Assert.That(metadata.GetProperty("input").GetProperty("format").GetString()).IsEqualTo("cyclonedx");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithNoExternalEvidenceFor_RecordsSkippedCollectionAsUnknown()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "internal", "version": "1.0.0", "purl": "pkg:nuget/MyCompany.Internal@1.0.0" }] }""",
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--skip-evidence-packages", "pkg:nuget/MyCompany.");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("unknown");
            await Assert.That(stdout).Contains("external_evidence_not_collected");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// Keeps the four "no registry request" outcomes distinct when they occur in one input.
    /// </summary>
    /// <remarks>
    /// A skipped component, an ecosystem with no provider, a purl without a version, and a component
    /// with no purl all end the plan without a request, and each asks the reader for something
    /// different. They share one negative index space, so a collision is silent: a skipped component was
    /// reported as an unsupported ecosystem and counted as one. Only a multi-component input reaches the
    /// planner at all, which is why the single-component tests above never showed it.
    /// </remarks>
    /// <summary>
    /// A component with no package identity was never a lookup subject, so no collection outcome is true
    /// of it. Recording one asserted that a repository had been sought and not found, which named a place
    /// nothing produced and implied a retry that can never help.
    /// </summary>
    [Test]
    public async Task Scan_WithComponentLackingPurl_RecordsNoSourceRepositoryOutcome()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteIdentitylessComponentsAsync();
        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(SelectWarnings(report, "bare")).DoesNotContain("source_repository_unavailable");
            await Assert.That(SelectCandidateSources(report, "bare")).IsEmpty();

            // A declared license still reaches a component with no identity, so the SBOM's own claim stays.
            await Assert.That(SelectCandidateSources(report, "resolved")).IsEquivalentTo(new[] { "sbom" });
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// The mechanism is derived from the empty purl rather than recorded as a warning, so it is stated
    /// whether or not the run collected anything. Both modes used to fail differently: collection invented
    /// a repository outcome, and <c>--no-external-evidence</c> left the component with nothing said at all.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Scan_WithComponentLackingPurl_NamesTheMechanismInEitherCollectionMode(bool collectExternalEvidence)
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteIdentitylessComponentsAsync();
        var arguments = collectExternalEvidence
            ? new[] { "scan", "--input", inputPath, "--format", "text", "--quiet" }
            : ["scan", "--input", inputPath, "--format", "text", "--quiet", "--no-external-evidence"];
        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, arguments);

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            var section = stdout[stdout.IndexOf("Unresolved components", StringComparison.Ordinal)..];
            var columns = SelectRow(section, "bare");
            await Assert.That(columns[1]).IsEqualTo("1.0.0");
            await Assert.That(columns[2]).IsEqualTo("package_metadata_no_purl");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// A document the publisher named outranks the structural fact, because opening it is an action and
    /// "this has no identity" is not.
    /// </summary>
    [Test]
    public async Task Scan_WithComponentLackingPurlButDeclaringLocation_PrefersTheDeclaredLocation()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteIdentitylessComponentsAsync();
        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "text", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            var section = stdout[stdout.IndexOf("Unresolved components", StringComparison.Ordinal)..];
            var row = Array.Find(section.Split('\n'), static line => line.StartsWith("with-location", StringComparison.Ordinal));
            await Assert.That(row).IsNotNull();
            var columns = row!.TrimEnd('\r').Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await Assert.That(columns[1]).IsEqualTo("1.0.0");
            await Assert.That(columns[2]).IsEqualTo("declared_license_location_not_collected");
            await Assert.That(columns[3]).IsEqualTo("https://example.com/LICENSE");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// The population is invisible in every counter today: a component with no purl is not a metadata
    /// target, and is neither an unsupported ecosystem nor an unversioned purl.
    /// </summary>
    [Test]
    public async Task Scan_WithComponentsLackingPurl_CountsThemInPackageMetadata()
    {
        var root = FindRepositoryRoot();
        var inputPath = await WriteIdentitylessComponentsAsync();
        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            var packageMetadata = report.RootElement.GetProperty("metadata").GetProperty("packageMetadata");

            // All three components in the document lack a purl, including the one that resolved.
            await Assert.That(packageMetadata.GetProperty("noPurlCount").GetInt32()).IsEqualTo(3);
            await Assert.That(packageMetadata.GetProperty("targetCount").GetInt32()).IsEqualTo(0);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// A component that did have an identity keeps its repository outcome: Ol had a subject, asked, and
    /// learned no repository, which is a true record of where it looked.
    /// </summary>
    [Test]
    public async Task Scan_WithComponentHavingPurl_KeepsItsSourceRepositoryOutcome()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                { "type": "library", "name": "bare", "version": "1.0.0" },
                { "type": "library", "name": "absent", "version": "1.0.0", "purl": "pkg:nuget/Ol.Test.Package.That.Does.Not.Exist@1.0.0" }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(SelectCandidateSources(report, "absent")).Contains("source-repository");
            await Assert.That(SelectCandidateSources(report, "bare")).IsEmpty();
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// A purl that exists but cannot be queried keeps its own mechanism. The three members of this family
    /// state different things — nothing to ask with, a version missing, an ecosystem Ol cannot ask — and
    /// collapsing them would send the reader to the wrong fix.
    /// </summary>
    [Test]
    [Arguments("Versionless", "pkg:nuget/NoVersion", "package_metadata_unversioned_purl")]
    [Arguments("Unsupported", "pkg:generic/thing@1.0.0", "unsupported_package_metadata")]
    public async Task Scan_WithUnqueryablePurl_KeepsItsOwnMechanism(string name, string purl, string mechanism)
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            $$"""
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [ { "type": "library", "name": "{{name}}", "version": "1.0.0", "purl": "{{purl}}" } ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "text", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            var columns = SelectUnresolvedColumns(stdout, name);
            await Assert.That(columns[1]).IsEqualTo("1.0.0");
            await Assert.That(columns[2]).IsEqualTo(mechanism);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>Three components with no purl, covering resolved, declared-location, and bare evidence.</summary>
    private static async Task<string> WriteIdentitylessComponentsAsync()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                { "type": "library", "name": "bare", "version": "1.0.0" },
                { "type": "library", "name": "with-location", "version": "1.0.0", "licenses": [{ "license": { "url": "https://example.com/LICENSE" } }] },
                { "type": "library", "name": "resolved", "version": "1.0.0", "licenses": [{ "expression": "MIT" }] }
              ]
            }
            """,
            Encoding.UTF8);
        return inputPath;
    }

    private static string[] SelectCandidateSources(JsonDocument report, string name)
    {
        foreach (var component in report.RootElement.GetProperty("components").EnumerateArray())
        {
            if (component.GetProperty("name").GetString() != name) continue;
            return [.. component.GetProperty("licenseCandidates").EnumerateArray().Select(c => c.GetProperty("source").GetString() ?? string.Empty)];
        }

        throw new InvalidOperationException($"Component '{name}' was not found in the report.");
    }

    [Test]
    public async Task Scan_WithSeveralUnqueryableComponents_KeepsEachOutcomeDistinct()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                { "type": "library", "name": "Skipped", "version": "1.0.0", "purl": "pkg:nuget/MyCompany.Internal@1.0.0" },
                { "type": "library", "name": "Unsupported", "version": "1.0.0", "purl": "pkg:generic/thing@1.0.0" },
                { "type": "library", "name": "Versionless", "version": "1.0.0", "purl": "pkg:nuget/NoVersion" },
                { "type": "library", "name": "NoPurl", "version": "1.0.0" }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--skip-evidence-packages", "pkg:nuget/MyCompany.");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);

            // A component Ol was told not to collect for says exactly that, and nothing about its ecosystem.
            await Assert.That(SelectWarnings(report, "Skipped")).IsEquivalentTo(new[] { "external_evidence_not_collected" });
            await Assert.That(SelectWarnings(report, "Unsupported")).Contains("unsupported_package_metadata");
            await Assert.That(SelectWarnings(report, "Versionless")).Contains("package_metadata_unversioned_purl");
            await Assert.That(SelectWarnings(report, "NoPurl")).DoesNotContain("unsupported_package_metadata");
            await Assert.That(SelectWarnings(report, "Unsupported")).DoesNotContain("external_evidence_not_collected");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// Counts an unqueryable purl under the reason that applies to it.
    /// </summary>
    /// <remarks>
    /// A purl naming no version is not an ecosystem Ol lacks a provider for, and a summary that adds them
    /// together says Ol does not support the ecosystem — the exact wrong conclusion the per-component
    /// reasons were split to prevent. A multi-module Maven build emits every module without the version
    /// its parent supplies, so this is the common shape rather than a corner case. The single-component
    /// path bypasses the planner, so both sizes are pinned.
    /// </remarks>
    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task Scan_WithUnversionedPurls_CountsThemApartFromUnsupportedEcosystems(int componentCount)
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        var entries = new string[componentCount];
        for (var i = 0; i < componentCount; i++)
        {
            entries[i] = $$"""{ "type": "library", "name": "mod-{{i}}", "version": "1.0.0", "purl": "pkg:maven/com.example/mod-{{i}}" }""";
        }

        await File.WriteAllTextAsync(
            inputPath,
            $$"""{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [ {{string.Join(", ", entries)}} ] }""",
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("packageMetadata");
            await Assert.That(metadata.GetProperty("unversionedPurlCount").GetInt32()).IsEqualTo(componentCount);
            await Assert.That(metadata.GetProperty("unsupportedEcosystemCount").GetInt32()).IsEqualTo(0);
            await Assert.That(SelectWarnings(report, "mod-0")).Contains("package_metadata_unversioned_purl");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>The two reasons in one input, which is what the shared counter used to merge.</summary>
    [Test]
    public async Task Scan_WithUnversionedAndUnsupportedPurls_ReportsEachCountInTheSummary()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                { "type": "library", "name": "Versionless", "version": "1.0.0", "purl": "pkg:maven/com.example/module" },
                { "type": "library", "name": "Unsupported", "version": "1.0.0", "purl": "pkg:generic/thing@1.0.0" },
                { "type": "library", "name": "AlsoUnsupported", "version": "1.0.0", "purl": "pkg:generic/other@1.0.0" }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", inputPath);

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            // Each counter here pluralizes from its own count, as every other counted noun in the summary does.
            await Assert.That(stderr).Contains("    Package metadata: 0 refreshed; 2 unsupported ecosystems; 1 unversioned purl;");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>The counter that reports the same collision as the warning above.</summary>
    [Test]
    public async Task Scan_WithSkippedAndUnsupportedComponents_CountsOnlyTheUnsupportedEcosystem()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                { "type": "library", "name": "SkippedOne", "version": "1.0.0", "purl": "pkg:nuget/MyCompany.One@1.0.0" },
                { "type": "library", "name": "SkippedTwo", "version": "1.0.0", "purl": "pkg:nuget/MyCompany.Two@1.0.0" },
                { "type": "library", "name": "Unsupported", "version": "1.0.0", "purl": "pkg:generic/thing@1.0.0" }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--skip-evidence-packages", "pkg:nuget/MyCompany.");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            await Assert.That(stderr).Contains("    Package metadata: 0 refreshed; 1 unsupported ecosystem;");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    private static string[] SelectWarnings(JsonDocument report, string name)
    {
        foreach (var component in report.RootElement.GetProperty("components").EnumerateArray())
        {
            if (component.GetProperty("name").GetString() != name) continue;
            var warnings = component.GetProperty("warnings");
            var values = new string[warnings.GetArrayLength()];
            for (var i = 0; i < values.Length; i++) values[i] = warnings[i].GetString()!;
            return values;
        }

        throw new InvalidOperationException($"Component '{name}' was not present in the report.");
    }

    [Test]
    public async Task Scan_VerboseWithNoExternalEvidenceFor_ReportsMatchesPerPrefix()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "internal", "version": "1.0.0", "purl": "pkg:nuget/MyCompany.Internal@1.0.0" }] }""",
            Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--verbose", "--skip-evidence-packages", "pkg:nuget/MyCompany.,pkg:npm/absent");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            await Assert.That(stderr).Contains("Skipped evidence prefix pkg:nuget/MyCompany. matched 1 component.");
            await Assert.That(stderr).Contains("Skipped evidence prefix pkg:npm/absent matched 0 components.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithInvalidNoExternalEvidenceFor_ReturnsNonZeroWithoutReport()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--skip-evidence-packages", "pkg:nuget/@");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("must identify at least one package or namespace");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithVerbose_AutoDetectionWritesDetectedFormatToStderr()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence", "--verbose");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEqualTo("Detected input format: sbom/cyclonedx");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithExplicitAutoInputFormat_AutoDetectsNuGetAssets()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "auto", "--format", "json", "--no-external-evidence");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        using var report = JsonDocument.Parse(stdout);
        await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("input").GetProperty("format").GetString()).IsEqualTo("nuget-assets");
    }

    [Test]
    public async Task Scan_WithAutoInputFormatThatIsAmbiguousOrUnknown_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        var cases = new[]
        {
            (Input: """{ "bomFormat": "CycloneDX", "spdxVersion": "SPDX-2.3", "components": [], "packages": [] }""", Message: "Ambiguous dependency input format: multiple registered format signatures matched."),
            (Input: """{ "targets": {} }""", Message: "Unsupported dependency input format: no registered format signature matched."),
        };

        try
        {
            foreach (var item in cases)
            {
                await File.WriteAllTextAsync(inputPath, item.Input, Encoding.UTF8);
                var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence");

                await Assert.That(exitCode).IsEqualTo(1);
                await Assert.That(stdout).IsEmpty();
                await Assert.That(stderr.Trim()).IsEqualTo($"Unable to scan input: {item.Message}");
            }
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithUnsupportedResolvedInputName_ReturnsActionableError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-unsupported-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var cases = new[]
        {
            (
                FileName: "Cargo.lock",
                Content: "version = 3\n",
                Message: "Cargo.lock is not a supported input. Run 'cargo metadata --format-version 1 --locked > cargo-metadata.json', then scan cargo-metadata.json."),
            (
                FileName: "Example.csproj",
                Content: "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                Message: ".csproj is not a resolved dependency input. Run 'dotnet restore', then scan obj/project.assets.json."),
        };

        try
        {
            foreach (var item in cases)
            {
                var inputPath = Path.Combine(temporaryDirectory, item.FileName);
                await File.WriteAllTextAsync(inputPath, item.Content, Encoding.UTF8);
                var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence");

                await Assert.That(exitCode).IsEqualTo(1);
                await Assert.That(stdout).IsEmpty();
                await Assert.That(stderr.Trim()).IsEqualTo($"Unable to scan input: {item.Message}");
                File.Delete(inputPath);
            }
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithSupportedContentNamedCargoLock_UsesContentDetection()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-supported-cargo-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var inputPath = Path.Combine(temporaryDirectory, "Cargo.lock");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("input").GetProperty("format").GetString()).IsEqualTo("cyclonedx");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithExplicitCycloneDxInput_EmitsGenericAndLegacyInputMetadata()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "cyclonedx", "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("sbom");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("cyclonedx");
            await Assert.That(input.GetProperty("sourceRef").GetString()).IsEqualTo(Path.GetFileName(inputPath));
            await Assert.That(input.GetProperty("sourceSha256").GetString()!.Length).IsEqualTo(64);
            await Assert.That(input.GetProperty("parser").GetString()).IsEqualTo("cyclonedx-json");
            await Assert.That(input.GetProperty("specificationVersion").GetString()).IsEqualTo("1.6");
            await Assert.That(input.GetProperty("sbomRef").GetString()).IsEqualTo(Path.GetFileName(inputPath));
            await Assert.That(input.GetProperty("sbomFormat").GetString()).IsEqualTo("CycloneDX");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithExplicitSpdxInput_AcceptsMatchingFormat()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "spdxVersion": "SPDX-2.3", "packages": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "spdx", "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("spdx");
            await Assert.That(input.GetProperty("parser").GetString()).IsEqualTo("spdx-json");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithInvalidInputSelection_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var cases = new[]
            {
                (Arguments: new[] { "--input", inputPath, "--input-format", "unknown" }, Message: "Unsupported input format: unknown"),
            };

            foreach (var item in cases)
            {
                var arguments = new string[item.Arguments.Length + 2];
                arguments[0] = "scan";
                arguments[1] = "--no-external-evidence";
                item.Arguments.CopyTo(arguments, 2);
                var (exitCode, stdout, stderr) = await RunOlAsync(root, arguments);

                await Assert.That(exitCode).IsEqualTo(1);
                await Assert.That(stdout).IsEmpty();
                await Assert.That(stderr.Trim()).IsEqualTo($"Invalid scan input: {item.Message}");
            }
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithExplicitFormatThatDoesNotMatchContent_RejectsInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "spdxVersion": "SPDX-2.3", "packages": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "cyclonedx", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: Input format cyclonedx does not match the detected spdx format.");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithCachedGitHubSourceEvidence_FillsUnknownLicenseAndReportsSafeAuthMode()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-cache-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(packageCacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.OK, new GitHubLicenseResult("MIT", "mit", "MIT License", "LICENSE", "sha", "https://github.com/owner/repository/blob/main/LICENSE"), [], []));

        try
        {
            // The asserted auth mode is a property of the run, not of the machine running it, and
            // OL_GITHUB_TOKEN is what the README tells users to export. Clearing it keeps the test from
            // depending on whether the developer follows that advice.
            var environment = new Dictionary<string, string?> { ["OL_GITHUB_TOKEN"] = null };
            var (exitCode, stdout, stderr) = await RunOlWithEnvironmentAsync(root, packageCacheRoot, sourceCacheRoot, environment, "scan", "--input", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(component.GetProperty("license").GetString()).IsEqualTo("MIT");
            var sourceCandidate = component.GetProperty("licenseCandidates")[2];
            await Assert.That(sourceCandidate.GetProperty("source").GetString()).IsEqualTo("github-license-api");
            var sourceEvidence = sourceCandidate.GetProperty("evidence");
            await Assert.That(sourceEvidence.GetProperty("type").GetString()).IsEqualTo("source-repository");
            await Assert.That(sourceEvidence.GetProperty("repository").GetString()).IsEqualTo("owner/repository");
            await Assert.That(sourceEvidence.GetProperty("licensePath").GetString()).IsEqualTo("LICENSE");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("network").GetProperty("githubAuth").GetString()).IsEqualTo("none");
            var sourceMetadata = report.RootElement.GetProperty("metadata").GetProperty("sourceRepository");
            await Assert.That(sourceMetadata.GetProperty("targetCount").GetInt32()).IsEqualTo(1);
            await Assert.That(sourceMetadata.GetProperty("githubLicenseRequestCount").GetInt32()).IsEqualTo(0);
            await Assert.That(sourceMetadata.GetProperty("cacheHitCount").GetInt32()).IsEqualTo(1);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The canonical report is exempt from the stderr summary only while it states everything that summary
    /// states. Input discovery was the part it did not state, so a consumer reading only the report could not
    /// tell a scan that read every input from one that skipped an ecosystem.
    /// </summary>
    [Test]
    public async Task Scan_WithSingleInput_StatesInputDiscoveryInJson()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [ { "type": "library", "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0" } ] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var discovery = report.RootElement.GetProperty("metadata").GetProperty("inputDiscovery");

            // Stated even when every count is trivial, for the reason inputScope is: a field that appeared only
            // when it had something to say leaves "nothing was ignored" indistinguishable from an older report.
            await Assert.That(discovery.GetProperty("detectedFileCount").GetInt32()).IsEqualTo(1);
            await Assert.That(discovery.GetProperty("ignoredCandidateCount").GetInt32()).IsEqualTo(0);
            await Assert.That(discovery.GetProperty("ignoredCandidates").GetArrayLength()).IsEqualTo(0);
            await Assert.That(discovery.GetProperty("incompleteInputSetCount").GetInt32()).IsEqualTo(0);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <summary>
    /// A silently unscanned ecosystem is the failure the discovery hint exists to prevent, so the report names
    /// the candidate rather than only counting it. The names are the closed set of directory patterns Ol
    /// recognizes, never a path, so the value carries nothing about the machine that produced it.
    /// </summary>
    [Test]
    public async Task Scan_WithIgnoredInputCandidate_NamesItInInputDiscoveryJson()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "package-lock.json"), NpmLockWithOneDependency, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(directory, "Cargo.toml"), CargoManifest, Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", directory, "--format", "json", "--no-external-evidence");
            var (_, _, textStderr) = await RunOlAsync(root, "scan", "--input", directory, "--format", "text", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var discovery = report.RootElement.GetProperty("metadata").GetProperty("inputDiscovery");
            var candidates = discovery.GetProperty("ignoredCandidates").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();

            await Assert.That(discovery.GetProperty("detectedFileCount").GetInt32()).IsEqualTo(1);
            await Assert.That(discovery.GetProperty("ignoredCandidateCount").GetInt32()).IsEqualTo(1);
            await Assert.That(candidates).IsEquivalentTo(new[] { "Cargo.toml" });
            await Assert.That(discovery.GetProperty("incompleteInputSetCount").GetInt32()).IsEqualTo(0);

            // The document and the summary describe one scan, so they state the same discovery — and the JSON run
            // writes that summary too, so the two projections agree on every stream.
            await Assert.That(textStderr).Contains("Input discovery: 1 detected file; 1 ignored candidate (Cargo.toml); 0 incomplete input sets");
            await Assert.That(stderr).Contains("Input discovery: 1 detected file; 1 ignored candidate (Cargo.toml); 0 incomplete input sets");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A companion set discovery found incomplete is skipped rather than failed, and the remaining inputs are
    /// still reported. The report therefore describes fewer ecosystems than it read files, which only this
    /// count explains.
    /// </summary>
    [Test]
    public async Task Scan_WithIncompleteInputSet_CountsItInInputDiscoveryJson()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "vendored"));
        await File.WriteAllTextAsync(Path.Combine(directory, "package-lock.json"), NpmLockWithOneDependency, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(directory, "vendored", "composer.json"), ComposerManifest, Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", directory, "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var discovery = report.RootElement.GetProperty("metadata").GetProperty("inputDiscovery");

            await Assert.That(discovery.GetProperty("detectedFileCount").GetInt32()).IsEqualTo(2);
            await Assert.That(discovery.GetProperty("ignoredCandidateCount").GetInt32()).IsEqualTo(0);
            await Assert.That(discovery.GetProperty("incompleteInputSetCount").GetInt32()).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Grouping changes the rows a report displays, never what the scan read, so a grouped report states the
    /// same discovery as the component report beside it.
    /// </summary>
    [Test]
    public async Task Scan_WithGroupedReport_StatesTheSameInputDiscoveryInJson()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "package-lock.json"), NpmLockWithOneDependency, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(directory, "Cargo.toml"), CargoManifest, Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", directory, "--format", "json", "--no-external-evidence", "--group-by", "license");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var discovery = report.RootElement.GetProperty("metadata").GetProperty("inputDiscovery");
            var candidates = discovery.GetProperty("ignoredCandidates").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();

            await Assert.That(discovery.GetProperty("detectedFileCount").GetInt32()).IsEqualTo(1);
            await Assert.That(candidates).IsEquivalentTo(new[] { "Cargo.toml" });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string NpmLockWithOneDependency =
        """{ "name": "app", "lockfileVersion": 3, "packages": { "": { "name": "app", "dependencies": { "a": "1.0.0" } }, "node_modules/a": { "version": "1.0.0", "license": "MIT" } } }""";

    private const string CargoManifest = "[package]\nname = \"x\"\nversion = \"0.1.0\"\n";

    private const string ComposerManifest = """{ "name": "acme/app", "require": { "php": ">=8.0" } }""";

    /// <summary>
    /// The document and the terminal have different readers. A CI job redirects the report to a file and the person
    /// reading the log cannot open it, so withholding the summary because the document repeats it left the
    /// recommended path the one path that produced no trace of having run.
    /// </summary>
    [Test]
    public async Task Scan_WithJsonFormat_WritesTheSameStderrSummaryAsText()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [ { "type": "library", "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0", "licenses": [{ "expression": "MIT" }] } ] }""", Encoding.UTF8);

        try
        {
            var json = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");
            var text = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "text", "--no-external-evidence");

            await Assert.That(json.ExitCode).IsEqualTo(0);
            await Assert.That(json.Stderr).Contains("Scan summary");

            // One vocabulary describes the run whatever the report format is; only the stdout projection differs.
            await Assert.That(json.Stderr).IsEqualTo(text.Stderr);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithJsonFormatAndQuiet_WritesNoStderrSummary()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [ { "type": "library", "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0", "licenses": [{ "expression": "MIT" }] } ] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);

            // Strict, not DiagnosticsOnly: --quiet must suppress the summary itself, not merely leave it alone.
            await Assert.That(stderr).IsEmpty();
            await Assert.That(stdout).Contains("\"schemaVersion\"");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithInputDeclaringNoComponents_ReportsAnEmptyInputWarningInJson()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var warnings = report.RootElement.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!).ToArray();
            await Assert.That(warnings).IsEquivalentTo(new[] { "input_declares_no_components" });
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    [Arguments("text")]
    [Arguments("markdown")]
    public async Task Scan_WithInputDeclaringNoComponents_StatesItInThePrimaryResultEvenWhenQuiet(string format)
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", format, "--no-external-evidence", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("No components");
            await Assert.That(stdout).Contains("declares no resolved dependencies");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    // A view that displays nothing is not an input that declares nothing: the inventory decides.
    [Test]
    public async Task Scan_WithEveryComponentFilteredOutOfTheView_DoesNotReportAnEmptyInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [ { "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0" } ] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "text", "--no-external-evidence", "--dependency", "direct", "--quiet");
            var (jsonExitCode, json, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence", "--dependency", "direct");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).DoesNotContain("No components");
            await Assert.That(jsonExitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(json);
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(0);
            await Assert.That(report.RootElement.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEmpty();
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithComponents_DoesNotReportAnEmptyInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [ { "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEmpty();
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithSharedUnknownSource_CountsComponentsSeparatelyFromTargets()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-shared-source-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "a", "purl": "pkg:npm/a@1.0.0" }, { "name": "b", "purl": "pkg:npm/b@1.0.0" } ] }""", Encoding.UTF8);
        var packageCache = new PackageMetadataCache(packageCacheRoot);
        await packageCache.WriteAsync(new PackageMetadataRecord("pkg:npm/a@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        await packageCache.WriteAsync(new PackageMetadataRecord("pkg:npm/b@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.NotFound, null, ["license_not_detected"], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", sbomPath, "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var source = report.RootElement.GetProperty("metadata").GetProperty("sourceRepository");
            await Assert.That(source.GetProperty("targetCount").GetInt32()).IsEqualTo(1);
            await Assert.That(source.GetProperty("unknownCount").GetInt32()).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithCachedNpmMetadata_ReconcilesPackageEvidenceAndReportsCacheHit()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v2-cache-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var cacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(
                sbomPath,
                """
                        {
                            "bomFormat": "CycloneDX",
                            "components": [
                                {
                                    "bom-ref": "pkg:npm/example@1.0.0",
                                    "name": "example",
                                    "version": "1.0.0",
                                    "purl": "pkg:npm/example@1.0.0",
                                    "licenses": [ { "license": { "id": "NOASSERTION" } } ]
                                }
                            ]
                        }
                        """,
                Encoding.UTF8);
        var cache = new PackageMetadataCache(cacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", "https://example.test/repository", [], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlWithCacheAsync(root, cacheRoot, "scan", "--input", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(component.GetProperty("license").GetString()).IsEqualTo("MIT");
            var packageCandidate = component.GetProperty("licenseCandidates")[1];
            await Assert.That(packageCandidate.GetProperty("source").GetString()).IsEqualTo("npm-registry");
            var packageEvidence = packageCandidate.GetProperty("evidence");
            await Assert.That(packageEvidence.GetProperty("type").GetString()).IsEqualTo("package-registry");
            await Assert.That(packageEvidence.GetProperty("cacheKeySha256").GetString()!.Length).IsEqualTo(64);
            await Assert.That(packageEvidence.GetProperty("collectedAt").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.MinValue);
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("packageMetadata").GetProperty("cacheHitCount").GetInt32()).IsEqualTo(1);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task CacheClear_PackageMetadata_RemovesMetadataCache()
    {
        var root = FindRepositoryRoot();
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"ol-v2-clear-{Guid.NewGuid():N}");
        var cache = new PackageMetadataCache(cacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlWithCacheAsync(root, cacheRoot, "cache", "clear", "package-metadata");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("package-metadata cache cleared");
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            await Assert.That(Directory.Exists(cacheRoot)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task Scan_WithRefresh_SkipsCachedMetadataWithoutChangingMatchedSbomEvidence()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v2-refresh-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var cacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(cacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "Apache-2.0", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, _) = await RunOlWithCacheAsync(root, cacheRoot, "scan", "--input", sbomPath, "--format", "json", "--refresh");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("packageMetadata");
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(component.GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(metadata.GetProperty("cacheHitCount").GetInt32()).IsEqualTo(0);
            await Assert.That(metadata.GetProperty("cacheMissCount").GetInt32()).IsEqualTo(1);
            await Assert.That(metadata.GetProperty("refreshedCount").GetInt32()).IsEqualTo(0);
            // The registry answers 404 for an unpublished package when the network is available and fails transport
            // otherwise, and those are now different warnings. Either way the collection outcome must be recorded and
            // must not override the SBOM claim.
            var warnings = component.GetProperty("warnings").EnumerateArray().Select(static value => value.GetString()).ToArray();
            await Assert.That(warnings.Any(static warning => warning is not null && warning.StartsWith("package_metadata_", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDeprecatedSpdxCandidate_RetainsCandidatesEvidenceAndWarningsInJson()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-evidence-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var spdxDirectory = Path.Combine(temporaryDirectory, "spdx");
        Directory.CreateDirectory(spdxDirectory);
        await File.WriteAllTextAsync(
                sbomPath,
                """
                        {
                            "spdxVersion": "SPDX-2.3",
                            "packages": [
                                {
                                    "SPDXID": "SPDXRef-Package",
                                    "name": "example",
                                    "licenseDeclared": "gpl-2.0",
                                    "licenseConcluded": "MIT"
                                }
                            ]
                        }
                        """,
                Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "licenses.json"), """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "GPL-2.0", "isDeprecatedLicenseId": true }, { "licenseId": "MIT", "isDeprecatedLicenseId": false } ] }""", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "exceptions.json"), """{ "licenseListVersion": "3.27.0", "exceptions": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json", "--spdx-data", spdxDirectory);

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            var candidates = component.GetProperty("licenseCandidates");
            // Two, not three: this package declares no purl, so no source-repository lookup ever had a
            // subject and no "unavailable" outcome is recorded for it.
            await Assert.That(candidates.GetArrayLength()).IsEqualTo(2);
            var declared = candidates[0];
            await Assert.That(declared.GetProperty("source").GetString()).IsEqualTo("sbom");
            await Assert.That(declared.GetProperty("kind").GetString()).IsEqualTo("declared");
            await Assert.That(declared.GetProperty("raw").GetString()).IsEqualTo("gpl-2.0");
            await Assert.That(declared.GetProperty("normalized").GetString()).IsEqualTo("GPL-2.0");
            await Assert.That(declared.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(declared.GetProperty("deprecated").GetBoolean()).IsTrue();
            await Assert.That(declared.GetProperty("warnings")[0].GetString()).IsEqualTo("deprecated_spdx_identifier");
            await Assert.That(component.TryGetProperty("evidence", out _)).IsFalse();
            var declaredEvidence = declared.GetProperty("evidence");
            await Assert.That(declaredEvidence.GetProperty("type").GetString()).IsEqualTo("sbom");
            await Assert.That(declaredEvidence.GetProperty("field").GetString()).IsEqualTo("licenseDeclared");
            await Assert.That(declaredEvidence.TryGetProperty("acknowledgement", out _)).IsFalse();
            var concludedEvidence = candidates[1].GetProperty("evidence");
            await Assert.That(concludedEvidence.GetProperty("field").GetString()).IsEqualTo("licenseConcluded");
            await Assert.That(concludedEvidence.TryGetProperty("acknowledgement", out _)).IsFalse();
            await Assert.That(component.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()))
                .DoesNotContain("source_repository_unavailable");
            await Assert.That(component.GetProperty("warnings")[0].GetString()).IsEqualTo("deprecated_spdx_identifier");
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--dependency", "direct");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("direct");
            await Assert.That(stdout).DoesNotContain("unknown");
            await Assert.That(stderr).Contains("Filter: 2 components excluded; 1 with unknown dependency type");

            // The singular case, which check and diff already get right for the same phrase.
            var (singularExitCode, _, singularStderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--dependency", "direct,unknown");

            await Assert.That(singularExitCode).IsEqualTo(0);
            await Assert.That(singularStderr).Contains("Filter: 1 component excluded; 0 with unknown dependency type");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithCycloneDxLicenseAcknowledgement_ReportsClaimProvenanceWithoutDuplicateEvidenceArray()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-acknowledgement-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            sbomPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                {
                  "bom-ref": "concluded",
                  "name": "concluded",
                  "licenses": [ { "license": { "id": "MIT", "acknowledgement": "concluded" } } ]
                },
                {
                  "bom-ref": "declared",
                  "name": "declared",
                  "licenses": [ { "license": { "id": "MIT", "acknowledgement": "declared" } } ]
                },
                {
                  "bom-ref": "unspecified",
                  "name": "unspecified",
                  "licenses": [ { "license": { "id": "MIT", "acknowledgement": "unsupported-value" } } ]
                }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("schemaVersion").GetInt32()).IsEqualTo(1);
            var components = report.RootElement.GetProperty("components");
            var component = components[0];
            await Assert.That(component.TryGetProperty("evidence", out _)).IsFalse();
            var evidence = component.GetProperty("licenseCandidates")[0].GetProperty("evidence");
            await Assert.That(evidence.GetProperty("type").GetString()).IsEqualTo("sbom");
            await Assert.That(evidence.GetProperty("field").GetString()).IsEqualTo("licenses");
            await Assert.That(evidence.GetProperty("acknowledgement").GetString()).IsEqualTo("concluded");
            await Assert.That(evidence.TryGetProperty("attested", out _)).IsFalse();
            await Assert.That(components[1].GetProperty("licenseCandidates")[0].GetProperty("evidence").GetProperty("acknowledgement").GetString()).IsEqualTo("declared");
            await Assert.That(components[2].GetProperty("licenseCandidates")[0].GetProperty("evidence").TryGetProperty("acknowledgement", out _)).IsFalse();
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithHumanReadableFormat_SeparatesAndLabelsSummary()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-summary-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);

        try
        {
            foreach (var format in new[] { "text", "markdown" })
            {
                var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", format);

                await Assert.That(exitCode).IsEqualTo(0);
                await Assert.That(stderr).StartsWith($"{Environment.NewLine}Scan summary{Environment.NewLine}");
                await Assert.That(stderr).Contains("  License results: 1 displayed component; 1 matched; 0 conflict; 0 unknown; 0 ambiguous; 0 invalid; 0 error");
                // One block, because the point of the table is that a counter lands in the same column on every row.
                await Assert.That(stderr).Contains(string.Join(
                    Environment.NewLine,
                    "  Evidence (full scan)     targets  requests  cache hits  cache misses  docs  matched  errors",
                    "    Package artifacts            0         -           -             -     0        0       -",
                    "    Declared GitHub files        0         0           0             0     0        0       0",
                    "    Package metadata             0         -           0             0     -        -       0",
                    "    Source repositories          0         0           0             0     -        -       0",
                    "    Package metadata: 0 refreshed; 0 unsupported ecosystems; 0 unversioned purls; 1 without purl",
                    "    Source repositories: 1 component without source license"));
                await Assert.That(stderr).Contains("  Input discovery: 1 detected file; 0 ignored candidates; 0 incomplete input sets; 0 excluded input paths; ecosystems none");
                await Assert.That(stderr).Contains("  Input:");
            }

            var (quietExitCode, _, quietStderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "text", "--quiet");
            await Assert.That(quietExitCode).IsEqualTo(0);
            // Strict, not DiagnosticsOnly: --quiet must suppress the summary itself, which the filter strips.
            await Assert.That(quietStderr).IsEmpty();
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
        await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "licenses.json"), """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "MIT", "isDeprecatedLicenseId": false } ] }""", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "exceptions.json"), """{ "licenseListVersion": "3.27.0", "exceptions": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json", "--spdx-data", spdxDirectory);

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
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithGroupByLicense_RendersGroupedRowsAndCanonicalJsonSummary()
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
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--group-by", "license");
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"ol exited with {exitCode}. stdout: {stdout} stderr: {stderr}");
            }

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains(string.Join(
                Environment.NewLine,
                "LICENSE     COUNT",
                "----------  -----",
                "Apache-2.0  1",
                "MIT         2"));
            await Assert.That(stderr).Contains("License results: 3 displayed components");

            var (jsonExitCode, jsonStdout, jsonStderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--group-by", "license", "--format", "json", "--no-external-evidence");

            await Assert.That(jsonExitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(jsonStderr)).IsEmpty();
            using var report = JsonDocument.Parse(jsonStdout);
            var metadata = report.RootElement.GetProperty("metadata");
            var tool = metadata.GetProperty("tool");
            await Assert.That(tool.GetProperty("name").GetString()).IsEqualTo("ol");
            await Assert.That(tool.GetProperty("version").GetString()).IsEqualTo(ToolVersion);
            await Assert.That(tool.GetProperty("informationUri").GetString()).IsEqualTo("https://github.com/guitarrapc/ol");
            await Assert.That(metadata.GetProperty("packageArtifacts").GetProperty("targetCount").GetInt32()).IsEqualTo(0);
            await Assert.That(metadata.GetProperty("declaredGitHubFiles").GetProperty("targetCount").GetInt32()).IsEqualTo(0);
            var summary = report.RootElement.GetProperty("summary");
            await Assert.That(summary.GetProperty("matched").GetInt32()).IsEqualTo(3);
            await Assert.That(summary.GetProperty("error").GetInt32()).IsEqualTo(0);
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithCaseInsensitiveSortAndDuplicatePurls_SortsAndEnrichesEveryComponent()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-sort-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var cacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(
            sbomPath,
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                { "name": "zebra", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] },
                { "name": "alpha", "purl": "pkg:cargo/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] },
                { "name": "zebra-copy", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] }
              ]
            }
            """,
            Encoding.UTF8);
        var cache = new PackageMetadataCache(cacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:cargo/example@1.0.0", "cargo-registry", "Apache-2.0", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, _) = await RunOlWithCacheAsync(root, cacheRoot, "scan", "--input", sbomPath, "--format", "json", "--sort", "ECOSYSTEM,NAME", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var components = report.RootElement.GetProperty("components");
            // "zebra" and "zebra-copy" state one purl, which is what CycloneDX declares identity to be, so they are
            // one component named by the first entry. Both entries remain as occurrences of it.
            await Assert.That(components.GetArrayLength()).IsEqualTo(2);
            await Assert.That(components[0].GetProperty("name").GetString()).IsEqualTo("alpha");
            await Assert.That(components[1].GetProperty("name").GetString()).IsEqualTo("zebra");
            await Assert.That(components[0].GetProperty("license").GetString()).IsEqualTo("Apache-2.0");
            await Assert.That(components[1].GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(report.RootElement.GetProperty("inventory").GetProperty("occurrences").GetArrayLength()).IsEqualTo(3);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithPackageAndSourceConflict_ReportsRefBoundSafeSourceEvidence()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-conflict-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        const string repositoryRef = "0123456789abcdef";
        const string token = "must-never-appear";
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(packageCacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", "https://github.com/owner/repository", [], [], RepositoryRef: repositoryRef));
        var target = new SourceRepositoryTarget("owner", "repository", repositoryRef);
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.OK, new GitHubLicenseResult("Apache-2.0", "apache-2.0", "Apache License 2.0", "LICENSE", "license-sha", "https://github.com/owner/repository/blob/0123456789abcdef/LICENSE"), [], []));

        try
        {
            var environment = new Dictionary<string, string?> { ["OL_GITHUB_TOKEN"] = token };
            var (exitCode, stdout, _) = await RunOlWithEnvironmentAsync(root, packageCacheRoot, sourceCacheRoot, environment, "scan", "--input", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).DoesNotContain(token);
            await Assert.That(stdout).DoesNotContain(temporaryDirectory);
            await Assert.That(stdout).DoesNotContain(sourceCacheRoot);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("conflict");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("network").GetProperty("githubAuth").GetString()).IsEqualTo("ol_github_token");
            await Assert.That(component.TryGetProperty("evidence", out _)).IsFalse();
            var sourceCandidate = component.GetProperty("licenseCandidates")[2];
            var source = sourceCandidate.GetProperty("evidence");
            await Assert.That(source.GetProperty("type").GetString()).IsEqualTo("source-repository");
            await Assert.That(sourceCandidate.GetProperty("warnings").GetArrayLength()).IsEqualTo(0);
            await Assert.That(source.GetProperty("repository").GetString()).IsEqualTo("owner/repository");
            await Assert.That(source.GetProperty("ref").GetString()).IsEqualTo(repositoryRef);
            await Assert.That(source.GetProperty("httpStatus").GetInt32()).IsEqualTo(200);
            await Assert.That(source.GetProperty("licensePath").GetString()).IsEqualTo("LICENSE");
            await Assert.That(source.GetProperty("licenseSha").GetString()).IsEqualTo("license-sha");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithCachedSourceFailureAndValidSbom_KeepsMatchedStatusAndWarning()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-failure-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(packageCacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.Forbidden, null, [], ["source_repository_fetch_failed"]));

        try
        {
            const string ignoredGitHubToken = "github-token-must-not-appear";
            var environment = new Dictionary<string, string?> { ["OL_GITHUB_TOKEN"] = null, ["GITHUB_TOKEN"] = ignoredGitHubToken };
            var (exitCode, stdout, stderr) = await RunOlWithEnvironmentAsync(root, packageCacheRoot, sourceCacheRoot, environment, "scan", "--input", sbomPath, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("status").GetString()).IsEqualTo("matched");
            await Assert.That(component.GetProperty("warnings").EnumerateArray().Select(static value => value.GetString())).Contains("source_repository_fetch_failed");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("network").GetProperty("githubAuth").GetString()).IsEqualTo("none");
            await Assert.That(stdout).DoesNotContain(ignoredGitHubToken);
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("sourceRepository").GetProperty("fetchErrorCount").GetInt32()).IsEqualTo(1);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithOnlyFailedEvidence_IncludesErrorInJsonAndHumanSummaries()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-v3-error-summary-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source-repository");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(packageCacheRoot).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.Forbidden, null, [], ["source_repository_fetch_failed"]));

        try
        {
            var (jsonExitCode, jsonStdout, jsonStderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", sbomPath, "--format", "json");

            await Assert.That(jsonExitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(jsonStderr)).IsEmpty();
            using var report = JsonDocument.Parse(jsonStdout);
            await Assert.That(report.RootElement.GetProperty("components")[0].GetProperty("status").GetString()).IsEqualTo("error");
            await Assert.That(report.RootElement.GetProperty("summary").GetProperty("error").GetInt32()).IsEqualTo(1);

            var (textExitCode, _, textStderr) = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", sbomPath, "--format", "text");

            await Assert.That(textExitCode).IsEqualTo(0);
            await Assert.That(textStderr).Contains("1 error");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task CacheClear_SourceRepository_RemovesSourceCache()
    {
        var root = FindRepositoryRoot();
        var sourceCacheRoot = Path.Combine(Path.GetTempPath(), $"ol-v3-clear-{Guid.NewGuid():N}");
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(sourceCacheRoot).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.NotFound, null, ["license_not_detected"], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlWithCachesAsync(root, null, sourceCacheRoot, "cache", "clear", "source-repository");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("source-repository cache cleared");
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            await Assert.That(Directory.Exists(sourceCacheRoot)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(sourceCacheRoot)) Directory.Delete(sourceCacheRoot, recursive: true);
        }
    }

    [Test]
    public async Task CacheClear_GitHubFile_RemovesDeclaredFileCache()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), $"ol-github-file-clear-{Guid.NewGuid():N}");
        var cacheRoot = Path.Combine(root, "github-file");
        DeclaredGitHubFileTarget.TryCreate("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT", out var target);
        new DeclaredGitHubFileCache(cacheRoot).Write(target, System.Net.HttpStatusCode.OK, "MIT License"u8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(repositoryRoot, "cache", "clear", "github-file", "--cache-dir", root);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("github-file cache cleared");
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            await Assert.That(Directory.Exists(cacheRoot)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithCacheDir_UsesIsolatedCategorySubdirectories()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cache-dir-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var cacheDirectory = Path.Combine(temporaryDirectory, "cache");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await new PackageMetadataCache(Path.Combine(cacheDirectory, "package-metadata")).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json", "--cache-dir", cacheDirectory, "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("components")[0].GetProperty("license").GetString()).IsEqualTo("MIT");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("packageMetadata").GetProperty("cacheHitCount").GetInt32()).IsEqualTo(1);
            await Assert.That(stdout).DoesNotContain(cacheDirectory);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithNoExternalEvidence_ProducesDeterministicSbomOnlyReport()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-sbom-only-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var unusedCacheFile = Path.Combine(temporaryDirectory, "unused-cache");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "NOASSERTION" } } ] } ] }""", Encoding.UTF8);
        await File.WriteAllTextAsync(unusedCacheFile, "must remain untouched", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--format", "json", "--no-external-evidence", "--cache-dir", unusedCacheFile);

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var component = report.RootElement.GetProperty("components")[0];
            await Assert.That(component.GetProperty("licenseCandidates").GetArrayLength()).IsEqualTo(1);
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("packageMetadata").GetProperty("supportedComponentCount").GetInt32()).IsEqualTo(0);
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("sourceRepository").GetProperty("targetCount").GetInt32()).IsEqualTo(0);
            await Assert.That(await File.ReadAllTextAsync(unusedCacheFile)).IsEqualTo("must remain untouched");
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithNoExternalEvidence_ReportsUncollectedEvidenceInsteadOfZeroCounters()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-no-external-summary-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [ { "name": "example", "licenses": [ { "license": { "id": "MIT" } } ] } ] }""", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).Contains("  External evidence: not collected; package registries, source repositories, and their caches were not read");
            await Assert.That(stderr).DoesNotContain("(full scan)");
            await Assert.That(stderr).DoesNotContain("GitHub auth");
            // The two lines under the table carry no mode marker, so the guards above would miss them.
            await Assert.That(stderr).DoesNotContain("unsupported ecosystem");
            await Assert.That(stderr).DoesNotContain("without source license");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithRemovedSkipEnrichmentOption_RejectsUnknownOption()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-removed-option-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--skip-enrichment");

            await Assert.That(exitCode).IsNotEqualTo(0);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("'--skip-enrichment' is not recognized");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task CacheClear_WithCacheDir_RemovesOnlyManagedCategorySubdirectories()
    {
        var root = FindRepositoryRoot();
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"ol-cache-clear-dir-{Guid.NewGuid():N}");
        var sentinelPath = Path.Combine(cacheDirectory, "keep.txt");
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(sentinelPath, "keep", Encoding.UTF8);
        await new PackageMetadataCache(Path.Combine(cacheDirectory, "package-metadata")).WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await new SourceRepositoryCache(Path.Combine(cacheDirectory, "source-repository")).WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, System.Net.HttpStatusCode.NotFound, null, [], []));

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "cache", "clear", "all", "--cache-dir", cacheDirectory);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(File.Exists(sentinelPath)).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(cacheDirectory, "package-metadata"))).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(cacheDirectory, "source-repository"))).IsFalse();
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, recursive: true);
        }
    }

    [Test]
    public async Task CacheClear_WithCacheDirPointingToFile_RejectsWithoutDeletingFile()
    {
        var root = FindRepositoryRoot();
        var filePath = Path.Combine(Path.GetTempPath(), $"ol-cache-file-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(filePath, "keep", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "cache", "clear", "--cache-dir", filePath);

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(File.Exists(filePath)).IsTrue();
            await Assert.That(stderr).Contains("Invalid cache directory");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public async Task Scan_WithInvalidViewOptions_ReturnsConciseErrorBeforeScanning()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-invalid-view-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var cases = new[]
            {
                (Option: "--dependency", Value: "nonsense", Message: "Unknown dependency value: nonsense"),
                (Option: "--sort", Value: "nonsense", Message: "Unknown sort key: nonsense"),
                (Option: "--group-by", Value: "nonsense", Message: "Unknown group key: nonsense"),
                (Option: "--dependency", Value: ",", Message: "Dependency filter must contain at least one value."),
                (Option: "--sort", Value: ",", Message: "Sort must contain at least one key."),
                (Option: "--group-by", Value: ",", Message: "Group-by must contain at least one key."),
            };

            foreach (var item in cases)
            {
                var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", item.Option, item.Value);

                await Assert.That(exitCode).IsEqualTo(1);
                await Assert.That(stdout).IsEmpty();
                await Assert.That(stderr.Trim()).IsEqualTo($"Invalid scan option: {item.Message}");
            }
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithMalformedSbom_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-malformed-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, "not-json", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).StartsWith("Unable to scan input:");
            await Assert.That(stderr).DoesNotContain("   at ");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithIncompleteSpdxData_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-spdx-incomplete-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var spdxDirectory = Path.Combine(temporaryDirectory, "spdx");
        Directory.CreateDirectory(spdxDirectory);
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--spdx-data", spdxDirectory);

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to load SPDX data: SPDX data directory must contain licenses.json and exceptions.json.");

            await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "licenses.json"), "{}", Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(spdxDirectory, "exceptions.json"), """{ "exceptions": [] }""", Encoding.UTF8);
            var (invalidExitCode, invalidStdout, invalidStderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--spdx-data", spdxDirectory);

            await Assert.That(invalidExitCode).IsEqualTo(1);
            await Assert.That(invalidStdout).IsEmpty();
            await Assert.That(invalidStderr).StartsWith("Unable to load SPDX data:");
            await Assert.That(invalidStderr).DoesNotContain("   at ");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithNuGetAssetsAndNoExternalEvidence_AcceptsRegisteredInput()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "json");

        await Assert.That(exitCode).IsEqualTo(0);
        using var report = JsonDocument.Parse(stdout);
        var input = report.RootElement.GetProperty("metadata").GetProperty("input");
        await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("package-manager");
        await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("nuget-assets");
        await Assert.That(input.TryGetProperty("sbomRef", out _)).IsFalse();
        await Assert.That(report.RootElement.GetProperty("components").EnumerateArray().Any(static component => component.GetProperty("purl").GetString() == "pkg:nuget/Native.Package@4.0.0")).IsTrue();
        await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
    }

    [Test]
    public async Task Scan_WithNpmPackageLockDirectoryAndNoExternalEvidence_PreservesInventoryVariants()
    {
        var root = FindRepositoryRoot();
        var inputDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputDirectory, "--input-format", "npm-package-lock", "--no-external-evidence", "--format", "json");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        using var report = JsonDocument.Parse(stdout);
        var input = report.RootElement.GetProperty("metadata").GetProperty("input");
        await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("package-manager");
        await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("npm-package-lock");
        await Assert.That(input.GetProperty("specificationVersion").GetString()).IsEqualTo("3");
        await Assert.That(input.TryGetProperty("sbomRef", out _)).IsFalse();
        var inventory = report.RootElement.GetProperty("inventory");
        await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
        await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(7);
        await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(9);
        await Assert.That(inventory.GetProperty("occurrences").EnumerateArray().Any(static occurrence => occurrence.TryGetProperty("variant", out var variant) && variant.GetString() == "optional;os=linux,!win32;cpu=x64")).IsTrue();
        await Assert.That(stdout).DoesNotContain("node_modules/workspace-a\"");
    }

    [Test]
    public async Task Scan_WithGoResolvedPairDirectory_CombinesCompanionFilesAsOneInput()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-go-module-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "go-list-modules.json"), Path.Combine(temporaryDirectory, "go-list-modules.json"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "go-mod-graph.txt"), Path.Combine(temporaryDirectory, "go-mod-graph.txt"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("package-manager");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("go-module-graph");
            await Assert.That(report.RootElement.GetProperty("inventory").GetProperty("components").GetArrayLength()).IsEqualTo(5);
            await Assert.That(stdout).DoesNotContain("/private/repo/local");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithIncompleteGoResolvedPair_ReturnsCompanionError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-go-module-graph-incomplete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "go-list-modules.json"), Path.Combine(temporaryDirectory, "go-list-modules.json"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("requires companion file go-mod-graph.txt in the same directory");
            await Assert.That(stderr).DoesNotContain("   at ");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithUnreadableDiscoveredInput_NamesTheFileInTheFailure()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-unreadable-discovered-{Guid.NewGuid():N}");
        var nestedDirectory = Path.Combine(temporaryDirectory, "vendored", "obj");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "project.assets.json"), """{ "targets": {} }""", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo($"Unable to scan input: {Path.GetFileName(temporaryDirectory)}/vendored/obj/project.assets.json: Unsupported dependency input format: no registered format signature matched.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDiscoveredIncompleteComposerSet_SkipsItAndScansTheRest()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-incomplete-composer-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project", "obj");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, "vendored"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "project.assets.json"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "composer.json"),
            Path.Combine(temporaryDirectory, "vendored", "composer.json"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--no-external-evidence",
                "--format",
                "json",
                "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            await Assert.That(stderr.Trim()).IsEqualTo($"Warning: {Path.GetFileName(temporaryDirectory)}/vendored/composer.json was not scanned: input format composer-lock requires companion file composer.lock in the same directory.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDiscoveredIncompleteComposerSet_CountsItInInputDiscovery()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-incomplete-composer-summary-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project", "obj");
        Directory.CreateDirectory(projectDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "project.assets.json"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "composer.json"),
            Path.Combine(temporaryDirectory, "composer.json"));

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).Contains("  Input discovery: 2 detected files; 0 ignored candidates; 1 incomplete input set; 0 excluded input paths; ecosystems nuget");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectlyNamedIncompleteComposerSet_ReturnsCompanionError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-named-incomplete-composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var composerJson = Path.Combine(temporaryDirectory, "composer.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "composer.json"), composerJson);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", composerJson, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: Input format composer-lock requires companion file composer.lock in the same directory.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithExplicitComposerFormatAndIncompleteSet_ReturnsCompanionError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-explicit-incomplete-composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "composer.json"),
            Path.Combine(temporaryDirectory, "composer.json"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--input-format", "composer-lock", "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: Input format composer-lock requires companion file composer.lock in the same directory.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithComposerResolvedPairDirectory_CombinesCompanionFilesAsOneInput()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-composer-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "composer.json"), Path.Combine(temporaryDirectory, "composer.json"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "composer.lock"), Path.Combine(temporaryDirectory, "composer.lock"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("package-manager");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("composer-lock");
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(5);
            await Assert.That(inventory.GetProperty("edges").GetArrayLength()).IsEqualTo(5);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithPipInspect_ReportsResolvedEnvironmentWithoutPrivatePaths()
    {
        var root = FindRepositoryRoot();
        var input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pip-inspect.json");

        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", input, "--no-external-evidence", "--format", "json");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        using var report = JsonDocument.Parse(stdout);
        var metadata = report.RootElement.GetProperty("metadata").GetProperty("input");
        await Assert.That(metadata.GetProperty("kind").GetString()).IsEqualTo("package-manager");
        await Assert.That(metadata.GetProperty("format").GetString()).IsEqualTo("pip-inspect");
        await Assert.That(report.RootElement.GetProperty("inventory").GetProperty("components").GetArrayLength()).IsEqualTo(5);
        await Assert.That(stdout).DoesNotContain("C:/private/project");
        await Assert.That(stdout).DoesNotContain("file:///");
    }

    [Test]
    public async Task Scan_WithBundlerLockDirectory_ReportsPlatformContextsWithoutPrivateSources()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-bundler-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gemfile.lock"), Path.Combine(temporaryDirectory, "Gemfile.lock"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(metadata.GetProperty("kind").GetString()).IsEqualTo("package-manager");
            await Assert.That(metadata.GetProperty("format").GetString()).IsEqualTo("bundler-lock");
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(7);
            await Assert.That(stdout).DoesNotContain("vendor/local-gem");
            await Assert.That(stdout).DoesNotContain("github.com/example/private-gem");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithSwiftAndCocoaPodsDirectory_CombinesResolvedAppleInputs()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-apple-locks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.resolved"), Path.Combine(temporaryDirectory, "Package.resolved"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Podfile.lock"), Path.Combine(temporaryDirectory, "Podfile.lock"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(metadata.GetProperty("kind").GetString()).IsEqualTo("package-manager");
            await Assert.That(metadata.GetProperty("format").GetString()).IsEqualTo("collection");
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithRepeatedNpmPackageLocks_CombinesSparseVariantIndexes()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-npm-directory-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(temporaryDirectory, "First");
        var secondDirectory = Path.Combine(temporaryDirectory, "Second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "package-lock.json");
        File.Copy(fixture, Path.Combine(firstDirectory, "package-lock.json"));
        File.Copy(fixture, Path.Combine(secondDirectory, "package-lock.json"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", firstDirectory, "--input", secondDirectory, "--input-format", "npm-package-lock", "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(7);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(18);
            await Assert.That(inventory.GetProperty("occurrences").EnumerateArray().Count(static occurrence => occurrence.TryGetProperty("variant", out _))).IsEqualTo(6);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectory_CombinesNestedNuGetAssetsWithoutDuplicateComponents()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-nuget-directory-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(temporaryDirectory, "First", "obj");
        var secondDirectory = Path.Combine(temporaryDirectory, "Second", "obj");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"));
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "project.assets.json"), fixture, Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(secondDirectory, "project.assets.json"),
            fixture.Replace("/private/src/App/App.csproj", "/private/src/Second/Second.csproj", StringComparison.Ordinal),
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("nuget-assets");
            await Assert.That(input.GetProperty("sourceRef").GetString()).IsEqualTo(Path.GetFileName(temporaryDirectory));
            await Assert.That(input.GetProperty("sourceSha256").GetString()!.Length).IsEqualTo(64);
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(12);
            await Assert.That(inventory.GetProperty("edges").GetArrayLength()).IsEqualTo(10);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryContainingCargoLock_WarnsThatRustWasNotScanned()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cargo-lock-directory-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project", "obj");
        Directory.CreateDirectory(projectDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "project.assets.json"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.lock"), "version = 3\n", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--no-external-evidence",
                "--format",
                "json",
                "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            await Assert.That(stderr.Trim()).IsEqualTo("Warning: Rust dependencies were not scanned: Cargo.lock is not a supported input. Run 'cargo metadata --format-version 1 --locked > cargo-metadata.json', then scan cargo-metadata.json.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// A Cargo library does not commit Cargo.lock, so detecting only the lockfile leaves the whole Rust
    /// ecosystem unscanned with nothing said about it. The manifest is the file every Cargo project has.
    /// </summary>
    [Test]
    public async Task Scan_WithDirectoryContainingCargoTomlWithoutLock_WarnsThatRustWasNotScanned()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cargo-toml-directory-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project", "obj");
        Directory.CreateDirectory(projectDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "project.assets.json"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.toml"), "[package]\nname = \"example\"\n", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            // Without a committed lockfile --locked cannot succeed, so the advice must not carry it.
            await Assert.That(stderr).Contains("Warning: Rust dependencies were not scanned: Cargo.toml is not a supported input. Run 'cargo metadata --format-version 1 > cargo-metadata.json', then scan cargo-metadata.json.");
            await Assert.That(stderr).Contains("1 ignored candidate (Cargo.toml)");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Every Cargo project carrying a lockfile also carries a manifest, so detecting both would report the
    /// same unscanned ecosystem twice. The lockfile wins because its advice is the reproducible one.
    /// </summary>
    [Test]
    public async Task Scan_WithDirectoryContainingCargoTomlAndLock_ReportsTheLockfileOnce()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cargo-both-directory-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project", "obj");
        Directory.CreateDirectory(projectDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "project.assets.json"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.toml"), "[package]\nname = \"example\"\n", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.lock"), "version = 3\n", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).Contains("1 ignored candidate (Cargo.lock)");
            await Assert.That(stderr).DoesNotContain("Cargo.toml");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>A scan that already covers the ecosystem must stay silent, however the candidate was found.</summary>
    [Test]
    public async Task Scan_WithDirectoryContainingCargoTomlAndMetadata_DoesNotWarn()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cargo-toml-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cargo-metadata.json"),
            Path.Combine(temporaryDirectory, "cargo-metadata.json"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.toml"), "[package]\nname = \"example\"\n", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).DoesNotContain("Warning: Rust dependencies were not scanned");
            await Assert.That(stderr).Contains("0 ignored candidates");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// A directory holding a manifest and a lockfile that Ol already covers must stay silent on both, or
    /// suppressing the superseded rule would have traded one redundant warning for one false one.
    /// </summary>
    [Test]
    public async Task Scan_WithDirectoryContainingCargoTomlLockAndMetadata_DoesNotWarn()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cargo-all-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cargo-metadata.json"),
            Path.Combine(temporaryDirectory, "cargo-metadata.json"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.toml"), "[package]\nname = \"example\"\n", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.lock"), "version = 3\n", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).DoesNotContain("Warning: Rust dependencies were not scanned");
            await Assert.That(stderr).Contains("0 ignored candidates");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// When discovery found no supported input at all, an empty report would read as a project without
    /// dependencies, so the candidate's advice becomes the command failure rather than a warning beside it.
    /// </summary>
    [Test]
    public async Task Scan_WithDirectoryContainingOnlyCargoToml_FailsWithGuidance()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cargo-toml-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.toml"), "[package]\nname = \"example\"\n", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("Cargo.toml is not a supported input. Run 'cargo metadata --format-version 1 > cargo-metadata.json'");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>A file the user named directly gets the same guidance the discovery warning gives.</summary>
    [Test]
    public async Task Scan_WithCargoTomlInput_ReportsUnsupportedInputGuidance()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cargo-toml-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var inputPath = Path.Combine(temporaryDirectory, "Cargo.toml");
        await File.WriteAllTextAsync(inputPath, "[package]\nname = \"example\"\n", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: Cargo.toml is not a supported input. Run 'cargo metadata --format-version 1 > cargo-metadata.json', then scan cargo-metadata.json.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryContainingIgnoredCandidate_SummarizesInputDiscovery()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-input-summary-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project", "obj");
        Directory.CreateDirectory(projectDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "project.assets.json"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.lock"), "version = 3\n", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).Contains("  Input discovery: 1 detected file; 1 ignored candidate (Cargo.lock); 0 incomplete input sets; 0 excluded input paths; ecosystems nuget");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryContainingCargoMetadata_DoesNotWarnForCargoLock()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cargo-metadata-directory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cargo-metadata.json"),
            Path.Combine(temporaryDirectory, "cargo-metadata.json"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.lock"), "version = 3\n", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--no-external-evidence",
                "--format",
                "json",
                "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("input").GetProperty("format").GetString()).IsEqualTo("cargo-metadata");
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithMultipleEcosystems_SummarizesEveryDetectedInputInStableOrder()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-mixed-input-summary-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project", "obj");
        Directory.CreateDirectory(projectDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "project.assets.json"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cargo-metadata.json"),
            Path.Combine(temporaryDirectory, "cargo-metadata.json"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.lock"), "version = 3\n", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr).Contains("  Input discovery: 2 detected files; 0 ignored candidates; 0 incomplete input sets; 0 excluded input paths; ecosystems cargo, nuget");
            await Assert.That(stderr).DoesNotContain("Warning: Rust dependencies were not scanned");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryContainingOnlyCargoLock_ReturnsActionableError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-cargo-lock-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.lock"), "version = 3\n", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: Cargo.lock is not a supported input. Run 'cargo metadata --format-version 1 --locked > cargo-metadata.json', then scan cargo-metadata.json.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryContainingUnrestoredProject_WarnsThatNuGetWasNotScanned()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-unrestored-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", Encoding.UTF8);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cargo-metadata.json"),
            Path.Combine(temporaryDirectory, "cargo-metadata.json"));

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stderr.Trim()).IsEqualTo("Warning: .NET dependencies were not scanned: .csproj is not a resolved dependency input. Run 'dotnet restore', then scan obj/project.assets.json.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryContainingOnlyProject_ReturnsActionableError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-project-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: .csproj is not a resolved dependency input. Run 'dotnet restore', then scan obj/project.assets.json.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryContainingRestoredProject_DoesNotWarnForProjectFile()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-restored-project-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "obj"));
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", Encoding.UTF8);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "obj", "project.assets.json"));

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence", "--format", "json", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithExplicitInputFormat_DoesNotHintAtOtherEcosystems()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-explicit-format-hint-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project", "obj");
        Directory.CreateDirectory(projectDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "project.assets.json"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Cargo.lock"), "version = 3\n", Encoding.UTF8);

        try
        {
            var (exitCode, _, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--input-format",
                "nuget-assets",
                "--no-external-evidence",
                "--format",
                "json",
                "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithRepeatedInputs_CombinesBothDirectories()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-repeated-input-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(temporaryDirectory, "First,WithComma");
        var secondDirectory = Path.Combine(temporaryDirectory, "Second");
        Directory.CreateDirectory(Path.Combine(firstDirectory, "obj"));
        Directory.CreateDirectory(Path.Combine(secondDirectory, "obj"));
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"));
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "obj", "project.assets.json"), fixture, Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(secondDirectory, "obj", "project.assets.json"),
            fixture.Replace("/private/src/App/App.csproj", "/private/src/Second/Second.csproj", StringComparison.Ordinal),
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", firstDirectory, "--input", secondDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("sourceRef").GetString()).IsEqualTo("2 inputs");
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(4);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(12);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithRelativeExcludedPath_ResolvesFromWorkingDirectory()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-relative-excluded-input-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(temporaryDirectory, "product-a");
        var secondDirectory = Path.Combine(temporaryDirectory, "product-b");
        Directory.CreateDirectory(Path.Combine(firstDirectory, "docs"));
        Directory.CreateDirectory(Path.Combine(secondDirectory, "docs"));
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "package-lock.json"), CreatePackageLock("product-a", "first-dependency"), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(secondDirectory, "package-lock.json"), CreatePackageLock("product-b", "second-dependency"), Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                firstDirectory,
                "--input",
                secondDirectory,
                "--exclude-input-path",
                "docs",
                "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("Excluded input path must be inside a directory input: docs");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithRelativeExcludedPathInsideInput_ExcludesOnlyNamedPath()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-relative-included-exclusion-{Guid.NewGuid():N}");
        var productDirectory = Path.Combine(temporaryDirectory, "product-a");
        var serverDirectory = Path.Combine(productDirectory, "server");
        var documentsDirectory = Path.Combine(productDirectory, "docs");
        Directory.CreateDirectory(serverDirectory);
        Directory.CreateDirectory(documentsDirectory);
        await File.WriteAllTextAsync(Path.Combine(serverDirectory, "package-lock.json"), CreatePackageLock("server", "server-dependency"), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(documentsDirectory, "package-lock.json"), CreatePackageLock("docs", "docs-dependency"), Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlInDirectoryAsync(
                root,
                temporaryDirectory,
                "scan",
                "--input",
                "product-a",
                "--exclude-input-path",
                Path.Combine("product-a", "docs"),
                "--no-external-evidence",
                "--format",
                "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            var components = report.RootElement.GetProperty("components");
            await Assert.That(components.GetArrayLength()).IsEqualTo(1);
            await Assert.That(components[0].GetProperty("name").GetString()).IsEqualTo("server-dependency");
            var excludedPaths = report.RootElement.GetProperty("metadata").GetProperty("inputScope").GetProperty("excludedPaths");
            await Assert.That(excludedPaths[0].GetString()).IsEqualTo("product-a/docs");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithCaseInsensitiveInputAndExclusion_RecordsFileSystemCasing()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-input-casing-{Guid.NewGuid():N}");
        var productDirectory = Path.Combine(temporaryDirectory, "Product-A");
        var serverDirectory = Path.Combine(productDirectory, "Server");
        var documentsDirectory = Path.Combine(productDirectory, "Docs");
        Directory.CreateDirectory(serverDirectory);
        Directory.CreateDirectory(documentsDirectory);
        await File.WriteAllTextAsync(Path.Combine(serverDirectory, "package-lock.json"), CreatePackageLock("server", "server-dependency"), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(documentsDirectory, "package-lock.json"), CreatePackageLock("docs", "docs-dependency"), Encoding.UTF8);

        try
        {
            var differentlyCasedInput = Path.Combine(temporaryDirectory, "product-a");
            if (!Directory.Exists(differentlyCasedInput))
            {
                return;
            }

            var (exitCode, stdout, stderr) = await RunOlInDirectoryAsync(
                root,
                temporaryDirectory,
                "scan",
                "--input",
                "product-a",
                "--exclude-input-path",
                Path.Combine("product-a", "docs"),
                "--exclude-input-path",
                Path.Combine("Product-A", "DOCS"),
                "--no-external-evidence",
                "--format",
                "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("sourceRef").GetString()).IsEqualTo("Product-A");
            var inputScope = report.RootElement.GetProperty("metadata").GetProperty("inputScope");
            await Assert.That(inputScope.GetProperty("excludedPathCount").GetInt32()).IsEqualTo(1);
            await Assert.That(inputScope.GetProperty("excludedPaths")[0].GetString()).IsEqualTo("Product-A/Docs");
            var components = report.RootElement.GetProperty("components");
            await Assert.That(components.GetArrayLength()).IsEqualTo(1);
            await Assert.That(components[0].GetProperty("name").GetString()).IsEqualTo("server-dependency");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithCaseDistinctDirectories_ExcludesOrdinalMatch_WhenSupported()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-case-distinct-input-{Guid.NewGuid():N}");
        var upperDirectory = Path.Combine(temporaryDirectory, "Docs");
        var lowerDirectory = Path.Combine(temporaryDirectory, "docs");
        Directory.CreateDirectory(upperDirectory);
        Directory.CreateDirectory(lowerDirectory);

        try
        {
            var distinctNames = Directory.EnumerateDirectories(temporaryDirectory)
                .Select(Path.GetFileName)
                .Count(static name => string.Equals(name, "Docs", StringComparison.Ordinal) || string.Equals(name, "docs", StringComparison.Ordinal));
            if (distinctNames != 2)
            {
                return;
            }

            await File.WriteAllTextAsync(Path.Combine(upperDirectory, "package-lock.json"), CreatePackageLock("upper", "included-dependency"), Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(lowerDirectory, "package-lock.json"), CreatePackageLock("lower", "excluded-dependency"), Encoding.UTF8);

            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--exclude-input-path",
                lowerDirectory,
                "--no-external-evidence",
                "--format",
                "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            var components = report.RootElement.GetProperty("components");
            await Assert.That(components.GetArrayLength()).IsEqualTo(1);
            await Assert.That(components[0].GetProperty("name").GetString()).IsEqualTo("included-dependency");
            await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("inputScope").GetProperty("excludedPaths")[0].GetString()).IsEqualTo("docs");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithMissingExcludedPath_ReturnsInputError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-missing-excluded-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "package-lock.json"), CreatePackageLock("server", "server-dependency"), Encoding.UTF8);
        var missingPath = Path.Combine(temporaryDirectory, "missing");

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--exclude-input-path",
                missingPath,
                "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains($"Excluded input path not found: {missingPath}");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithExcludedInputPaths_ScansOnlyIncludedSubtreesAndRecordsScope()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-excluded-input-{Guid.NewGuid():N}");
        var serverDirectory = Path.Combine(temporaryDirectory, "src", "server");
        var documentsDirectory = Path.Combine(temporaryDirectory, "src", "documents");
        var pagesDirectory = Path.Combine(temporaryDirectory, "Pages");
        Directory.CreateDirectory(serverDirectory);
        Directory.CreateDirectory(documentsDirectory);
        Directory.CreateDirectory(pagesDirectory);
        await File.WriteAllTextAsync(Path.Combine(serverDirectory, "package-lock.json"), CreatePackageLock("server", "server-dependency"), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(documentsDirectory, "package-lock.json"), CreatePackageLock("documents", "documents-dependency"), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(pagesDirectory, "Cargo.toml"), "[package]\nname = \"pages\"\nversion = \"1.0.0\"\n", Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--exclude-input-path",
                Path.Combine(temporaryDirectory, "src", "other", "..", "documents"),
                "--exclude-input-path",
                Path.Combine(pagesDirectory, "Cargo.toml"),
                "--no-external-evidence",
                "--format",
                "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var components = report.RootElement.GetProperty("components");
            await Assert.That(components.GetArrayLength()).IsEqualTo(1);
            await Assert.That(components[0].GetProperty("name").GetString()).IsEqualTo("server-dependency");
            var inputScope = report.RootElement.GetProperty("metadata").GetProperty("inputScope");
            await Assert.That(inputScope.GetProperty("excludedPathCount").GetInt32()).IsEqualTo(2);
            var excludedPaths = inputScope.GetProperty("excludedPaths");
            await Assert.That(excludedPaths[0].GetString()).IsEqualTo("src/documents");
            await Assert.That(excludedPaths[1].GetString()).IsEqualTo("Pages/Cargo.toml");

            var (summaryExitCode, _, summaryStderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--exclude-input-path",
                Path.Combine(temporaryDirectory, "src", "other", "..", "documents"),
                "--exclude-input-path",
                Path.Combine(pagesDirectory, "Cargo.toml"),
                "--no-external-evidence");
            await Assert.That(summaryExitCode).IsEqualTo(0).Because(summaryStderr);
            await Assert.That(summaryStderr).Contains("2 excluded input paths (src/documents, Pages/Cargo.toml)");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithExplicitFileInsideExcludedPath_ReturnsInputError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-explicit-excluded-input-{Guid.NewGuid():N}");
        var documentsDirectory = Path.Combine(temporaryDirectory, "src", "documents");
        Directory.CreateDirectory(documentsDirectory);
        var packageLock = Path.Combine(documentsDirectory, "package-lock.json");
        await File.WriteAllTextAsync(packageLock, CreatePackageLock("documents", "documents-dependency"), Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--input",
                packageLock,
                "--exclude-input-path",
                documentsDirectory,
                "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("Explicit input file is inside an excluded input path");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithExplicitDirectoryInsideExcludedPath_SkipsThatInput()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-explicit-excluded-directory-{Guid.NewGuid():N}");
        var includedDirectory = Path.Combine(temporaryDirectory, "server");
        var excludedDirectory = Path.Combine(temporaryDirectory, "documents");
        Directory.CreateDirectory(includedDirectory);
        Directory.CreateDirectory(excludedDirectory);
        await File.WriteAllTextAsync(Path.Combine(includedDirectory, "package-lock.json"), CreatePackageLock("server", "server-dependency"), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(excludedDirectory, "package-lock.json"), CreatePackageLock("documents", "documents-dependency"), Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--input",
                excludedDirectory,
                "--exclude-input-path",
                excludedDirectory,
                "--no-external-evidence",
                "--format",
                "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("components")[0].GetProperty("name").GetString()).IsEqualTo("server-dependency");
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithOnlyExplicitDirectoryInsideExcludedPath_ReportsNoInputs()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-only-explicit-excluded-directory-{Guid.NewGuid():N}");
        var excludedDirectory = Path.Combine(temporaryDirectory, "documents");
        var nestedDirectory = Path.Combine(excludedDirectory, "site");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "package-lock.json"), CreatePackageLock("documents", "documents-dependency"), Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                nestedDirectory,
                "--exclude-input-path",
                excludedDirectory,
                "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr).Contains("No registered dependency input files were found in the input directories.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithExcludedPathOutsideStrictDescendant_ReturnsInputError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-invalid-excluded-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "package-lock.json"), CreatePackageLock("server", "server-dependency"), Encoding.UTF8);

        try
        {
            var self = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--exclude-input-path",
                temporaryDirectory,
                "--no-external-evidence");

            await Assert.That(self.ExitCode).IsEqualTo(1);
            await Assert.That(self.Stdout).IsEmpty();
            await Assert.That(self.Stderr).Contains("No registered dependency input files were found in the input directories.");

            var outside = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--exclude-input-path",
                root,
                "--no-external-evidence");

            await Assert.That(outside.ExitCode).IsEqualTo(1);
            await Assert.That(outside.Stdout).IsEmpty();
            await Assert.That(outside.Stderr).Contains("must be inside a directory input");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithExcludedPathPrefix_DoesNotExcludeSiblingPathSegment()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-excluded-input-boundary-{Guid.NewGuid():N}");
        var documentDirectory = Path.Combine(temporaryDirectory, "src", "document");
        var documentsDirectory = Path.Combine(temporaryDirectory, "src", "documents");
        Directory.CreateDirectory(documentDirectory);
        Directory.CreateDirectory(documentsDirectory);
        await File.WriteAllTextAsync(Path.Combine(documentsDirectory, "package-lock.json"), CreatePackageLock("documents", "documents-dependency"), Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input",
                temporaryDirectory,
                "--exclude-input-path",
                documentDirectory,
                "--no-external-evidence",
                "--format",
                "json");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(report.RootElement.GetProperty("components")[0].GetProperty("name").GetString()).IsEqualTo("documents-dependency");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithDirectoryWithoutNuGetAssets_ReturnsConciseError()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-empty-nuget-directory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--no-external-evidence");

            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdout).IsEmpty();
            await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: No registered dependency input files were found in the input directories.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory);
        }
    }

    [Test]
    public async Task Scan_WithOverlappingRepeatedDirectories_ScansEachFileOnce()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-overlapping-input-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(temporaryDirectory, "Project");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "obj"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"),
            Path.Combine(projectDirectory, "obj", "project.assets.json"));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", temporaryDirectory, "--input", projectDirectory, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(6);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithUnresolvedRootComponent_OmitsItFromTheUnresolvedSection()
    {
        // The section answers "what does a reviewer do next", and policy never evaluates a root component, so
        // listing one asks for work that no check will ever require. A generator scanning a directory names its
        // root after that directory, which is also how an absolute local path reaches the section.
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-root-unresolved-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "metadata": { "component": { "bom-ref": "root-app", "type": "application", "name": "/private/build/workspace", "version": "0.0.0", "licenses": [ { "license": { "name": "See URL", "url": "https://example.test/root-LICENSE.txt" } } ] } },
          "components": [
            { "bom-ref": "dep", "type": "library", "name": "Example", "version": "1.0.0", "purl": "pkg:nuget/Example@1.0.0", "licenses": [ { "license": { "name": "See URL", "url": "https://example.test/LICENSE.txt" } } ] }
          ],
          "dependencies": [ { "ref": "root-app", "dependsOn": ["dep"] } ]
        }
        """, Encoding.UTF8);
        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--format", "text", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            // The root still appears in the table: the report must not stop saying what the input described.
            await Assert.That(stdout).Contains("/private/build/workspace");
            await Assert.That(stdout).Contains("Unresolved components");
            await Assert.That(stdout).Contains("declared_license_location_not_collected");
            await Assert.That(stdout).Contains("https://example.test/LICENSE.txt");
            var unresolved = stdout[stdout.IndexOf("Unresolved components", StringComparison.Ordinal)..];
            await Assert.That(unresolved).DoesNotContain("/private/build/workspace");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithSbomAndPackageManagerInput_ScansThemAsOneCollection()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-mixed-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var sbomPath = Path.Combine(temporaryDirectory, "bom.json");
        var assetsPath = Path.Combine(temporaryDirectory, "project.assets.json");
        await File.WriteAllTextAsync(sbomPath, """{ "bomFormat": "CycloneDX", "components": [] }""", Encoding.UTF8);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json"), assetsPath);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--input", assetsPath, "--no-external-evidence", "--format", "json");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
            using var report = JsonDocument.Parse(stdout);
            var input = report.RootElement.GetProperty("metadata").GetProperty("input");
            await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("collection");
            // The SBOM declares no components, so the collection is exactly the package-manager population.
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithNuGetAssetsJson_PreservesDeterministicInventoryGraph()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");

        var first = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "json");
        var second = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "json");

        await Assert.That(first.ExitCode).IsEqualTo(0);
        await Assert.That(second.ExitCode).IsEqualTo(0);
        await Assert.That(first.Stdout).IsEqualTo(second.Stdout);
        using var report = JsonDocument.Parse(first.Stdout);
        var inventory = report.RootElement.GetProperty("inventory");
        await Assert.That(inventory.GetProperty("contexts").GetArrayLength()).IsEqualTo(2);
        await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(4);
        await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(6);
        await Assert.That(inventory.GetProperty("edges").GetArrayLength()).IsEqualTo(5);
        var winContext = inventory.GetProperty("contexts")[1];
        await Assert.That(winContext.GetProperty("projectOrigin").GetString()).IsEqualTo("App.csproj");
        await Assert.That(first.Stdout).DoesNotContain("/private/src");
        await Assert.That(winContext.GetProperty("target").GetString()).IsEqualTo("net8.0");
        await Assert.That(winContext.GetProperty("runtime").GetString()).IsEqualTo("win-x64");
        await Assert.That(winContext.GetProperty("platform").GetString()).IsEmpty();
        await Assert.That(winContext.GetProperty("architecture").GetString()).IsEmpty();
        await Assert.That(inventory.GetProperty("occurrences")[1].GetProperty("componentIndex").GetInt32()).IsEqualTo(1);
        await Assert.That(inventory.GetProperty("edges")[0].GetProperty("fromOccurrenceIndex").GetInt32()).IsEqualTo(-1);
    }

    [Test]
    public async Task Scan_WithUnresolvedComponents_ExplainsEachOneInHumanFormats()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-unresolved-{Guid.NewGuid():N}");
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var sourceCacheRoot = Path.Combine(temporaryDirectory, "source");
        var cache = new PackageMetadataCache(packageCacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Shared.Package@2.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Native.Package@4.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Project.Transitive@3.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        try
        {
            var text = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--skip-evidence-packages", "pkg:nuget/Direct.Package", "--format", "text", "--quiet");
            var markdown = await RunOlWithCachesAsync(root, packageCacheRoot, sourceCacheRoot, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--skip-evidence-packages", "pkg:nuget/Direct.Package", "--format", "markdown", "--quiet");

            await Assert.That(text.ExitCode).IsEqualTo(0);
            await Assert.That(text.Stdout).Contains("Unresolved components");
            await Assert.That(text.Stdout).Contains("external_evidence_not_collected");
            // A resolved component is not restated in the section.
            var textSection = text.Stdout[text.Stdout.IndexOf("Unresolved components", StringComparison.Ordinal)..];
            await Assert.That(textSection).DoesNotContain("Shared.Package");
            await Assert.That(markdown.ExitCode).IsEqualTo(0);
            await Assert.That(markdown.Stdout).Contains("## Unresolved components");
            await Assert.That(markdown.Stdout).Contains("| Direct.Package | 1.0.0 | external_evidence_not_collected |");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    // A publisher that could not state an SPDX expression often states where the license is instead.
    // That pointer is the one actionable fact about such a component, so it has to reach the report
    // even though no collection mechanism failed and therefore no warning names one.
    [Test]
    public async Task Scan_WithDeclaredLicenseLocation_ListsItInTheUnresolvedSection()
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "Example",
              "version": "1.0.0",
              "purl": "pkg:nuget/Example@1.0.0",
              "licenses": [ { "license": { "name": "Unknown - See URL", "url": "https://example.test/LICENSE.txt" } } ]
            }
          ]
        }
        """, Encoding.UTF8);
        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--format", "text", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("Unresolved components");
            await Assert.That(stdout).Contains("declared_license_location_not_collected");
            await Assert.That(stdout).Contains("https://example.test/LICENSE.txt");
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    // Resolving the members where the listing is built gives one kind whatever they spell, names a
    // deprecated member as one, and lets check admit the listing without recognizing punctuation.
    [Test]
    [Arguments("MIT; Apache-2.0", "license-set", false, 0)]
    [Arguments("MIT OR Apache-2.0; BSD-3-Clause", "license-set", false, 0)]
    [Arguments("MIT; GPL-2.0", "license-set", true, 0)]
    [Arguments("non-standard; Apache-2.0", "license", false, 2)]
    public async Task Scan_DepsDevListing_ResolvesItsMembersAndCheckReadsTheResolvedSet(string license, string expectedKind, bool expectedDeprecated, int expectedCheckExit)
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-depsdev-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "input.cdx.json");
        var reportPath = Path.Combine(temporaryDirectory, "report.json");
        Directory.CreateDirectory(temporaryDirectory);
        const string purl = "pkg:golang/example.test/module@v1.0.0";
        await File.WriteAllTextAsync(sbomPath, $$"""
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            { "type": "library", "name": "module", "version": "v1.0.0", "purl": "{{purl}}" }
          ]
        }
        """, Encoding.UTF8);
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var cache = new PackageMetadataCache(packageCacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord(purl, "deps.dev", license, string.Empty, [], [], DateTimeOffset.UtcNow, string.Empty, DeclaredLicenseReferenceKind.None, string.Empty));
        try
        {
            var scan = await RunOlWithCachesAsync(root, packageCacheRoot, Path.Combine(temporaryDirectory, "source"), "scan", "--input", sbomPath, "--format", "json");
            await File.WriteAllTextAsync(reportPath, scan.Stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var check = await RunOlWithCachesAsync(root, packageCacheRoot, null, "check", "--report", reportPath, "--allow-licenses", "MIT,Apache-2.0,BSD-3-Clause,GPL-2.0");

            await Assert.That(scan.ExitCode).IsEqualTo(0).Because(scan.Stderr);
            var candidate = JsonDocument.Parse(scan.Stdout).RootElement
                .GetProperty("components").EnumerateArray()
                .First(component => component.GetProperty("purl").GetString() == purl)
                .GetProperty("licenseCandidates").EnumerateArray()
                .First(value => value.GetProperty("source").GetString() == "deps.dev");

            await Assert.That(candidate.GetProperty("kind").GetString()).IsEqualTo(expectedKind);
            await Assert.That(candidate.GetProperty("status").GetString()).IsEqualTo("ambiguous");
            await Assert.That(candidate.GetProperty("raw").GetString()).IsEqualTo(license);
            // The scan summary's deprecated count is this flag, so asserting it covers both.
            await Assert.That(candidate.GetProperty("deprecated").GetBoolean()).IsEqualTo(expectedDeprecated);
            await Assert.That(check.ExitCode).IsEqualTo(expectedCheckExit).Because(check.Stdout + check.Stderr);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    // The reason a declared license went unread is the same fact in every ecosystem, and the kind of
    // place the publisher named is what a reviewer acts on. Neither depends on which registry answered.
    [Test]
    [Arguments("pkg:cargo/example@1.0.0", DeclaredLicenseReferenceKind.ArtifactPath, "LICENSE-APACHE", "declared_license_file_not_collected LICENSE-APACHE")]
    [Arguments("pkg:pypi/example@1.0.0", DeclaredLicenseReferenceKind.ArtifactPath, "LICENSE.rst", "declared_license_file_not_collected LICENSE.rst")]
    [Arguments("pkg:cocoapods/Example@1.0.0", DeclaredLicenseReferenceKind.InlineText, "", "declared_license_text_not_collected")]
    [Arguments("pkg:nuget/Example@1.0.0", DeclaredLicenseReferenceKind.Location, "https://example.test/eula", "declared_license_location_not_collected https://example.test/eula")]
    public async Task Scan_WithDeclaredLicenseReference_NamesTheSameMechanismInEveryEcosystem(string purl, DeclaredLicenseReferenceKind kind, string reference, string expected)
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-declared-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "input.cdx.json");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, $$"""
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            { "type": "library", "name": "Example", "version": "1.0.0", "purl": "{{purl}}" }
          ]
        }
        """, Encoding.UTF8);
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var cache = new PackageMetadataCache(packageCacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord(purl, "package-registry", string.Empty, string.Empty, [], [], DateTimeOffset.UtcNow, string.Empty, kind, reference));
        try
        {
            var text = await RunOlWithCachesAsync(root, packageCacheRoot, Path.Combine(temporaryDirectory, "source"), "scan", "--input", sbomPath, "--format", "text", "--quiet");

            await Assert.That(text.ExitCode).IsEqualTo(0);
            var columns = SelectUnresolvedColumns(text.Stdout, "Example");
            var rendered = columns[3] == "-" ? columns[2] : $"{columns[2]} {columns[3]}";
            await Assert.That(rendered).IsEqualTo(expected);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    // A classifier that names a license family is unresolvable by construction, and saying so is the
    // only thing a report can add: the reviewer's next step is to ask the publisher or read the
    // artifact, not to wait for Ol to gain a capability.
    [Test]
    [Arguments("License :: OSI Approved :: BSD License", "license_classifier_not_specific")]
    [Arguments("License :: OSI Approved :: Apache Software License", "license_classifier_not_specific")]
    public async Task Scan_WithLicenseFamilyClassifier_ExplainsWhyItCannotResolve(string declared, string expectedReason)
    {
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-classifier-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, $$"""
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "example",
              "version": "1.0.0",
              "purl": "pkg:pypi/example@1.0.0",
              "licenses": [ { "license": { "name": "{{declared}}" } } ]
            }
          ]
        }
        """, Encoding.UTF8);
        try
        {
            var (exitCode, stdout, _) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--format", "text", "--quiet");

            await Assert.That(exitCode).IsEqualTo(0);
            var section = stdout[stdout.IndexOf("Unresolved components", StringComparison.Ordinal)..];
            var columns = SelectRow(section, "example");
            await Assert.That(columns[1]).IsEqualTo("1.0.0");
            await Assert.That(columns[2]).IsEqualTo(expectedReason);
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    // Embedded license text is a declaration with no place to name, and Ol deliberately never retains
    // the text. Its empty value must not be presented as the reference, nor suppress one that exists.
    [Test]
    public async Task Scan_WithEmbeddedLicenseTextBesideALocation_ReportsTheLocationRatherThanAnEmptyReference()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-inline-{Guid.NewGuid():N}");
        var sbomPath = Path.Combine(temporaryDirectory, "input.cdx.json");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(sbomPath, """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "Example",
              "version": "1.0.0",
              "purl": "pkg:cocoapods/Example@1.0.0",
              "licenses": [ { "license": { "name": "Facebook Platform License", "url": "https://example.test/platform-license" } } ]
            }
          ]
        }
        """, Encoding.UTF8);
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var cache = new PackageMetadataCache(packageCacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:cocoapods/Example@1.0.0", "package-registry", string.Empty, string.Empty, [], [], DateTimeOffset.UtcNow, string.Empty, DeclaredLicenseReferenceKind.InlineText, string.Empty));
        try
        {
            var text = await RunOlWithCachesAsync(root, packageCacheRoot, Path.Combine(temporaryDirectory, "source"), "scan", "--input", sbomPath, "--format", "text", "--quiet");
            var json = await RunOlWithCachesAsync(root, packageCacheRoot, Path.Combine(temporaryDirectory, "source"), "scan", "--input", sbomPath, "--format", "json", "--quiet");

            await Assert.That(text.ExitCode).IsEqualTo(0);
            var columns = SelectUnresolvedColumns(text.Stdout, "Example");
            await Assert.That(columns[2]).IsEqualTo("declared_license_text_not_collected");
            await Assert.That(columns[3]).IsEqualTo("https://example.test/platform-license");

            using var report = JsonDocument.Parse(json.Stdout);
            var kinds = report.RootElement.GetProperty("components")[0].GetProperty("licenseCandidates").EnumerateArray()
                .Select(static candidate => candidate.TryGetProperty("evidence", out var evidence) && evidence.TryGetProperty("declaredLicenseReferenceKind", out var kind) ? kind.GetString() : null)
                .Where(static value => value is not null)
                .ToArray();

            await Assert.That(kinds).Contains("inline-text");
            await Assert.That(kinds).Contains("location");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task Scan_WithEveryComponentResolved_OmitsUnresolvedSection()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-resolved-{Guid.NewGuid():N}");
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");
        var packageCacheRoot = Path.Combine(temporaryDirectory, "package-metadata");
        var cache = new PackageMetadataCache(packageCacheRoot);
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Direct.Package@1.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Shared.Package@2.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Native.Package@4.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Project.Transitive@3.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        try
        {
            var text = await RunOlWithCachesAsync(root, packageCacheRoot, Path.Combine(temporaryDirectory, "source"), "scan", "--input", inputPath, "--input-format", "nuget-assets", "--format", "text", "--quiet");

            await Assert.That(text.ExitCode).IsEqualTo(0);
            await Assert.That(text.Stdout).DoesNotContain("Unresolved components");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task Scan_WithNuGetAssetsHumanFormats_DisplaysInputKindAndFormat()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");

        var text = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "text", "--quiet");
        var markdown = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--no-external-evidence", "--format", "markdown", "--quiet");

        await Assert.That(text.ExitCode).IsEqualTo(0);
        await Assert.That(text.Stdout).StartsWith("Input: package-manager/nuget-assets");
        await Assert.That(markdown.ExitCode).IsEqualTo(0);
        await Assert.That(markdown.Stdout).StartsWith("Input: `package-manager/nuget-assets`");
    }

    [Test]
    public async Task Scan_WithMarkdownFormat_EscapesHtmlInUntrustedTableValues()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-markdown-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(inputPath, """
                {
                  "bomFormat": "CycloneDX",
                  "specVersion": "1.6",
                  "components": [
                    {
                      "name": "<details>&example|next\nline",
                      "version": "1.0.0",
                      "purl": "pkg:npm/example@1.0.0",
                      "licenses": [{ "license": { "id": "MIT" } }]
                    }
                  ]
                }
                """);

            var result = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--format", "markdown", "--quiet");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("| &lt;details&gt;&amp;example\\|next line | 1.0.0 | MIT | npm | unknown | matched | sbom |");
            await Assert.That(result.Stdout).DoesNotContain("<details>");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_WithNuGetAssetsAndCachedMetadata_ReusesNuGetEnrichment()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-nuget-assets-{Guid.NewGuid():N}");
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nuget-project.assets.json");
        var cache = new PackageMetadataCache(Path.Combine(temporaryDirectory, "package-metadata"));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Direct.Package@1.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Shared.Package@2.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Native.Package@4.0.0", "nuget-registry", "MIT", string.Empty, [], []));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:nuget/Project.Transitive@3.0.0", "nuget-registry", "MIT", string.Empty, [], []));

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--input-format", "nuget-assets", "--cache-dir", temporaryDirectory, "--format", "json", "--concurrency", "1", "--retry", "0");

            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata").GetProperty("packageMetadata");
            await Assert.That(metadata.GetProperty("targetCount").GetInt32()).IsEqualTo(4);
            await Assert.That(metadata.GetProperty("cacheHitCount").GetInt32()).IsEqualTo(4);
            await Assert.That(metadata.GetProperty("cacheMissCount").GetInt32()).IsEqualTo(0);
            await Assert.That(report.RootElement.GetProperty("components").EnumerateArray().Where(static component => component.GetProperty("ecosystem").GetString() == "nuget").All(static component => component.GetProperty("license").GetString() == "MIT")).IsTrue();
            await Assert.That(CliTestAssembly.DiagnosticsOnly(stderr)).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_CsbindgenEquivalentFixture_ResolvesCoreFxGitHubLicenseAndReportsCollectors()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-csbindgen-e2e-{Guid.NewGuid():N}");
        var packageRoot = Path.Combine(temporaryDirectory, "packages");
        var cacheRoot = Path.Combine(temporaryDirectory, "cache");
        var assetsPath = Path.Combine(temporaryDirectory, "project.assets.json");
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var packages = new[]
        {
            (Name: "System.Buffers", Version: "4.5.1"),
            (Name: "System.Memory", Version: "4.5.4"),
            (Name: "System.Numerics.Vectors", Version: "4.4.0"),
            (Name: "System.Threading.Tasks.Extensions", Version: "4.5.4"),
        };
        const string licenseLocation = "https://github.com/dotnet/corefx/blob/master/LICENSE.TXT";

        Directory.CreateDirectory(temporaryDirectory);
        var packageRootWithSeparator = Path.EndsInDirectorySeparator(packageRoot) ? packageRoot : packageRoot + Path.DirectorySeparatorChar;
        var assets = await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "csbindgen-project.assets.json"));
        assets = assets.Replace("\"__PACKAGE_ROOT__\"", JsonSerializer.Serialize(packageRootWithSeparator), StringComparison.Ordinal);
        await File.WriteAllTextAsync(assetsPath, assets, Encoding.UTF8);
        for (var index = 0; index < packages.Length; index++)
        {
            Directory.CreateDirectory(Path.Combine(packageRoot, packages[index].Name.ToLowerInvariant(), packages[index].Version));
        }

        var packageMetadataCache = new PackageMetadataCache(Path.Combine(cacheRoot, "package-metadata"));
        for (var index = 0; index < packages.Length; index++)
        {
            var package = packages[index];
            await packageMetadataCache.WriteAsync(new PackageMetadataRecord(
                $"pkg:nuget/{package.Name}@{package.Version}",
                "nuget-registry",
                string.Empty,
                string.Empty,
                [],
                [],
                DeclaredLicenseReferenceKind: DeclaredLicenseReferenceKind.Location,
                DeclaredLicenseReference: licenseLocation));
        }

        DeclaredGitHubFileTarget.TryCreate(licenseLocation, out var target);
        new DeclaredGitHubFileCache(Path.Combine(cacheRoot, "github-file")).Write(
            target,
            System.Net.HttpStatusCode.OK,
            await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "corefx-LICENSE.TXT")));

        try
        {
            var json = await RunOlAsync(
                repositoryRoot,
                "scan", "--input", assetsPath,
                "--input-format", "nuget-assets",
                "--cache-dir", cacheRoot,
                "--format", "json",
                "--concurrency", "1",
                "--retry", "0");

            await Assert.That(json.ExitCode).IsEqualTo(0).Because(json.Stderr);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(json.Stderr)).IsEmpty();
            using var report = JsonDocument.Parse(json.Stdout);
            var components = report.RootElement.GetProperty("components").EnumerateArray().ToArray();
            await Assert.That(components).Count().IsEqualTo(4);
            await Assert.That(components.All(static component => component.GetProperty("license").GetString() == "MIT")).IsTrue();
            await Assert.That(components.All(static component => component.GetProperty("status").GetString() == "matched")).IsTrue();

            var metadata = report.RootElement.GetProperty("metadata");
            var artifacts = metadata.GetProperty("packageArtifacts");
            await Assert.That(artifacts.GetProperty("targetCount").GetInt32()).IsEqualTo(4);
            await Assert.That(artifacts.GetProperty("documentCount").GetInt32()).IsEqualTo(0);
            await Assert.That(artifacts.GetProperty("matchedCount").GetInt32()).IsEqualTo(0);
            var declaredFiles = metadata.GetProperty("declaredGitHubFiles");
            await Assert.That(declaredFiles.GetProperty("targetCount").GetInt32()).IsEqualTo(1);
            await Assert.That(declaredFiles.GetProperty("githubRequestCount").GetInt32()).IsEqualTo(0);
            await Assert.That(declaredFiles.GetProperty("cacheHitCount").GetInt32()).IsEqualTo(1);
            await Assert.That(declaredFiles.GetProperty("cacheMissCount").GetInt32()).IsEqualTo(0);
            await Assert.That(declaredFiles.GetProperty("documentCount").GetInt32()).IsEqualTo(1);
            await Assert.That(declaredFiles.GetProperty("matchedCount").GetInt32()).IsEqualTo(4);
            await Assert.That(declaredFiles.GetProperty("fetchErrorCount").GetInt32()).IsEqualTo(0);
            await Assert.That(metadata.GetProperty("packageMetadata").GetProperty("cacheHitCount").GetInt32()).IsEqualTo(4);

            foreach (var format in new[] { "text", "markdown" })
            {
                var human = await RunOlAsync(
                    repositoryRoot,
                    "scan", "--input", assetsPath,
                    "--input-format", "nuget-assets",
                    "--cache-dir", cacheRoot,
                    "--format", format,
                    "--concurrency", "1",
                    "--retry", "0");

                await Assert.That(human.ExitCode).IsEqualTo(0).Because(human.Stderr);
                for (var index = 0; index < packages.Length; index++)
                {
                    await Assert.That(human.Stdout).Contains(packages[index].Name);
                }

                await Assert.That(human.Stderr).Contains(string.Join(
                    Environment.NewLine,
                    "  Evidence (full scan)     targets  requests  cache hits  cache misses  docs  matched  errors",
                    "    Package artifacts            4         -           -             -     0        0       -",
                    "    Declared GitHub files        1         0           1             0     1        4       0"));
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_JsonReport_SeparatesUncollectedExternalEvidenceFromNothingToCollect()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "internal", "version": "1.0.0", "purl": "pkg:nuget/MyCompany.Internal@1.0.0" }] }""",
            Encoding.UTF8);

        try
        {
            var skipped = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");
            var collected = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--skip-evidence-packages", "pkg:nuget/");

            await Assert.That(skipped.ExitCode).IsEqualTo(0).Because(skipped.Stderr);
            await Assert.That(collected.ExitCode).IsEqualTo(0).Because(collected.Stderr);
            using var skippedReport = JsonDocument.Parse(skipped.Stdout);
            using var collectedReport = JsonDocument.Parse(collected.Stdout);
            var skippedMetadata = skippedReport.RootElement.GetProperty("metadata");
            var collectedMetadata = collectedReport.RootElement.GetProperty("metadata");

            // Both runs report zero collection work. Only the stated mode distinguishes "never attempted"
            // from "attempted and nothing was needed", and stderr carries no summary for JSON.
            await Assert.That(skippedMetadata.GetProperty("packageMetadata").GetProperty("targetCount").GetInt32()).IsEqualTo(0);
            await Assert.That(collectedMetadata.GetProperty("packageMetadata").GetProperty("targetCount").GetInt32()).IsEqualTo(0);
            await Assert.That(skippedMetadata.GetProperty("collection").GetProperty("externalEvidence").GetString()).IsEqualTo("not-collected");
            await Assert.That(collectedMetadata.GetProperty("collection").GetProperty("externalEvidence").GetString()).IsEqualTo("collected");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_JsonReport_StatesDependencyFilterExclusions()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "metadata": { "component": { "type": "application", "name": "app", "version": "0.0.0", "bom-ref": "app" } },
              "components": [
                { "type": "library", "name": "a", "version": "1.0.0", "bom-ref": "a", "purl": "pkg:nuget/a@1.0.0", "licenses": [{ "license": { "id": "MIT" } }] },
                { "type": "library", "name": "b", "version": "2.0.0", "bom-ref": "b", "purl": "pkg:nuget/b@2.0.0", "licenses": [{ "license": { "id": "MIT" } }] },
                { "type": "library", "name": "c", "version": "3.0.0", "bom-ref": "c", "purl": "pkg:nuget/c@3.0.0", "licenses": [{ "license": { "id": "MIT" } }] }
              ],
              "dependencies": [
                { "ref": "app", "dependsOn": ["a"] },
                { "ref": "a", "dependsOn": ["b"] },
                { "ref": "b", "dependsOn": [] }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
            var unfiltered = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence");
            var filtered = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence", "--dependency", "direct");
            var text = await RunOlAsync(root, "scan", "--input", inputPath, "--no-external-evidence", "--dependency", "direct");

            await Assert.That(filtered.ExitCode).IsEqualTo(0).Because(filtered.Stderr);
            using var unfilteredReport = JsonDocument.Parse(unfiltered.Stdout);
            using var filteredReport = JsonDocument.Parse(filtered.Stdout);
            var unfilteredView = unfilteredReport.RootElement.GetProperty("metadata").GetProperty("view");
            var filteredView = filteredReport.RootElement.GetProperty("metadata").GetProperty("view");

            await Assert.That(unfilteredView.GetProperty("dependencyFilter").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(unfilteredView.GetProperty("excludedCount").GetInt32()).IsEqualTo(0);
            await Assert.That(unfilteredView.GetProperty("excludedUnknownCount").GetInt32()).IsEqualTo(0);

            await Assert.That(filteredView.GetProperty("dependencyFilter").GetString()).IsEqualTo("direct");

            // The filtered JSON must state the same exclusion facts the text view puts on stderr,
            // which is what lets a consumer tell a filtered view from a complete one.
            var excluded = filteredView.GetProperty("excludedCount").GetInt32();
            var excludedUnknown = filteredView.GetProperty("excludedUnknownCount").GetInt32();
            await Assert.That(excluded).IsGreaterThan(0);
            var componentWord = excluded == 1 ? "component" : "components";
            await Assert.That(text.Stderr).Contains($"Filter: {excluded} {componentWord} excluded; {excludedUnknown} with unknown dependency type");
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Test]
    public async Task Scan_GroupedJsonReport_CarriesCollectionAndViewMetadata()
    {
        var root = FindRepositoryRoot();
        var inputPath = Path.Combine(Path.GetTempPath(), $"ol-input-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            inputPath,
            """{ "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [{ "type": "library", "name": "a", "version": "1.0.0", "purl": "pkg:nuget/a@1.0.0", "licenses": [{ "license": { "id": "MIT" } }] }] }""",
            Encoding.UTF8);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", inputPath, "--format", "json", "--no-external-evidence", "--group-by", "license");

            await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
            using var report = JsonDocument.Parse(stdout);
            var metadata = report.RootElement.GetProperty("metadata");
            await Assert.That(metadata.GetProperty("collection").GetProperty("externalEvidence").GetString()).IsEqualTo("not-collected");
            await Assert.That(metadata.GetProperty("view").GetProperty("excludedCount").GetInt32()).IsEqualTo(0);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlAsync(string root, params string[] args)
        => await RunOlWithCacheAsync(root, cacheRoot: null, args);

    private static string[] SelectUnresolvedColumns(string output, string name)
        => SelectRow(output[output.IndexOf("Unresolved components", StringComparison.Ordinal)..], name);

    /// <summary>
    /// Splits the table row whose first cell is exactly this name. A prefix match would read a
    /// neighbouring row once a fixture gains a package this name is a prefix of.
    /// </summary>
    private static string[] SelectRow(string text, string name)
    {
        foreach (var line in text.Split('\n'))
        {
            var columns = line.TrimEnd('\r').Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length != 0 && columns[0] == name) return columns;
        }

        throw new InvalidOperationException($"No row named '{name}' was found in:{Environment.NewLine}{text}");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlInDirectoryAsync(string root, string workingDirectory, params string[] args)
        => await RunOlWithEnvironmentAsync(root, workingDirectory, null, null, null, args);

    private static string CreatePackageLock(string rootName, string dependencyName)
        => $$"""
            {
              "name": "{{rootName}}",
              "version": "1.0.0",
              "lockfileVersion": 3,
              "packages": {
                "": {
                  "name": "{{rootName}}",
                  "version": "1.0.0",
                  "dependencies": { "{{dependencyName}}": "1.0.0" }
                },
                "node_modules/{{dependencyName}}": {
                  "version": "1.0.0",
                  "license": "MIT"
                }
              }
            }
            """;

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlWithCacheAsync(string root, string? cacheRoot, params string[] args)
        => await RunOlWithCachesAsync(root, cacheRoot, sourceCacheRoot: null, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlWithCachesAsync(string root, string? cacheRoot, string? sourceCacheRoot, params string[] args)
        => await RunOlWithEnvironmentAsync(root, root, cacheRoot, sourceCacheRoot, null, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlWithEnvironmentAsync(string root, string? cacheRoot, string? sourceCacheRoot, IReadOnlyDictionary<string, string?>? environment, params string[] args)
        => await RunOlWithEnvironmentAsync(root, root, cacheRoot, sourceCacheRoot, environment, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlWithEnvironmentAsync(string root, string workingDirectory, string? cacheRoot, string? sourceCacheRoot, IReadOnlyDictionary<string, string?>? environment, params string[] args)
    {
        await CliGate.WaitAsync();
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (cacheRoot is not null)
            {
                startInfo.Environment["OL_PACKAGE_METADATA_CACHE_ROOT"] = cacheRoot;
            }

            if (sourceCacheRoot is not null)
            {
                startInfo.Environment["OL_SOURCE_REPOSITORY_CACHE_ROOT"] = sourceCacheRoot;
            }

            if (environment is not null)
            {
                foreach (var item in environment)
                {
                    startInfo.Environment[item.Key] = item.Value;
                }
            }

            startInfo.ArgumentList.Add(CliTestAssembly.ResolveOlDllPath(AppContext.BaseDirectory));
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
        finally
        {
            CliGate.Release();
        }
    }

    private static string ToolVersion
        => typeof(ScanCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFilePath)! })
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
