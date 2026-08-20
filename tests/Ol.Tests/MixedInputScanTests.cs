using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;
using Ol.Internals;

namespace Ol.Tests;

/// <summary>
/// Covers scanning a repository-wide SBOM together with resolved package-manager inputs in one collection.
/// The two are matched on package URL identity so evidence combines, while each input keeps its own
/// population, contexts, and graph.
/// </summary>
public sealed class MixedInputScanTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task Scan_WithSbomAndPackageManagerSharingPurl_RetainsOneRowCarryingBothDeclarations()
    {
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm.cdx.json");

        var rows = FindComponents(report, "pkg:npm/alpha@1.0.0");
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(CandidateSources(rows[0])).Contains("dependency-input");
        await Assert.That(CandidateSources(rows[0])).Contains("sbom");
        await Assert.That(SuppliedBy(rows[0])).IsEquivalentTo(new[] { "sbom", "package-manager" });
        await Assert.That(rows[0].GetProperty("status").GetString()).IsEqualTo("matched");
    }

    /// <summary>
    /// The per-component supply answers "which input saw this one"; only the tally answers "was the
    /// second input worth passing", which is the question a combined scan is configured to ask.
    /// </summary>
    [Test]
    public async Task Scan_WithCombinedInputs_ReportsSupplyTallyInJsonSummary()
    {
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm.cdx.json");

        var supply = report.RootElement.GetProperty("summary").GetProperty("supply");
        await Assert.That(supply.GetProperty("sbomOnly").GetInt32()).IsEqualTo(2);
        await Assert.That(supply.GetProperty("packageManagerOnly").GetInt32()).IsEqualTo(4);
        await Assert.That(supply.GetProperty("both").GetInt32()).IsEqualTo(3);
    }

    /// <summary>
    /// Present in a single-input report too, for the reason the per-component field is: an absent tally
    /// would leave "this scan had one input" indistinguishable from "an older Ol wrote this report".
    /// </summary>
    [Test]
    public async Task Scan_WithSbomOnly_StillReportsSupplyTally()
    {
        var root = FindRepositoryRoot();
        var (exitCode, stdout, _) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath("mixed-npm.cdx.json"),
            "--no-external-evidence",
            "--format", "json");

        await Assert.That(exitCode).IsEqualTo(0);
        using var report = JsonDocument.Parse(stdout);
        var supply = report.RootElement.GetProperty("summary").GetProperty("supply");
        await Assert.That(supply.GetProperty("packageManagerOnly").GetInt32()).IsEqualTo(0);
        await Assert.That(supply.GetProperty("both").GetInt32()).IsEqualTo(0);
        await Assert.That(supply.GetProperty("sbomOnly").GetInt32()).IsEqualTo(report.RootElement.GetProperty("components").GetArrayLength());
    }

    /// <summary>
    /// A grouped report totals its groups instead of walking components once, so it reaches the tally by a
    /// second path. Both paths describe the same run and must agree.
    /// </summary>
    [Test]
    public async Task Scan_WithGroupedView_ReportsTheSameSupplyTally()
    {
        var root = FindRepositoryRoot();
        var (exitCode, stdout, _) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath("package-lock.json"),
            "--input", FixturePath("mixed-npm.cdx.json"),
            "--no-external-evidence",
            "--group-by", "ecosystem",
            "--format", "json");

        await Assert.That(exitCode).IsEqualTo(0);
        using var report = JsonDocument.Parse(stdout);
        var supply = report.RootElement.GetProperty("summary").GetProperty("supply");
        await Assert.That(supply.GetProperty("sbomOnly").GetInt32()).IsEqualTo(2);
        await Assert.That(supply.GetProperty("packageManagerOnly").GetInt32()).IsEqualTo(4);
        await Assert.That(supply.GetProperty("both").GetInt32()).IsEqualTo(3);
    }

    /// <summary>
    /// The totals say whether the second input earned its place; only the per-ecosystem split says where.
    /// A repository whose SBOM covers one ecosystem and not another reads as a half-useless input in the
    /// totals and as an exact statement here, and the rows must sum to the totals so both can be trusted.
    /// </summary>
    [Test]
    public async Task Scan_WithVerboseCombinedInputs_ReportsSupplyPerEcosystem()
    {
        var root = FindRepositoryRoot();
        var (exitCode, _, stderr) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath("nuget-project.assets.json"),
            "--input", FixturePath("package-lock.json"),
            "--input", FixturePath("mixed-npm.cdx.json"),
            "--no-external-evidence",
            "--verbose");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).Contains("  Supplied by: 2 sbom only; 8 package-manager only; 3 both");
        await Assert.That(stderr).Contains("    -: 1 sbom only; 0 package-manager only; 0 both");
        await Assert.That(stderr).Contains("    npm: 1 sbom only; 4 package-manager only; 3 both");
        await Assert.That(stderr).Contains("    nuget: 0 sbom only; 4 package-manager only; 0 both");
    }

    /// <summary>
    /// Ordinal order, so two runs over one report print the same block and a diff of two runs shows only
    /// what changed.
    /// </summary>
    [Test]
    public async Task Scan_WithVerboseCombinedInputs_OrdersEcosystemsOrdinally()
    {
        var root = FindRepositoryRoot();
        var (exitCode, _, stderr) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath("nuget-project.assets.json"),
            "--input", FixturePath("package-lock.json"),
            "--input", FixturePath("mixed-npm.cdx.json"),
            "--no-external-evidence",
            "--verbose");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr.IndexOf("    -: ", StringComparison.Ordinal))
            .IsLessThan(stderr.IndexOf("    npm: ", StringComparison.Ordinal));
        await Assert.That(stderr.IndexOf("    npm: ", StringComparison.Ordinal))
            .IsLessThan(stderr.IndexOf("    nuget: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The default summary already runs to eight lines; one row per ecosystem belongs behind the option
    /// whose job is extra diagnostics.
    /// </summary>
    [Test]
    public async Task Scan_WithoutVerbose_OmitsSupplyPerEcosystem()
    {
        var root = FindRepositoryRoot();
        var (exitCode, _, stderr) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath("nuget-project.assets.json"),
            "--input", FixturePath("package-lock.json"),
            "--input", FixturePath("mixed-npm.cdx.json"),
            "--no-external-evidence");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).Contains("  Supplied by: 2 sbom only; 8 package-manager only; 3 both");
        await Assert.That(stderr).DoesNotContain("    npm: ");
        await Assert.That(stderr).DoesNotContain("    nuget: ");
    }

    /// <summary>A single-input scan states one row that repeats the totals, rather than none.</summary>
    [Test]
    public async Task Scan_WithVerboseSingleInput_StillReportsOneEcosystemRow()
    {
        var root = FindRepositoryRoot();
        var (exitCode, _, stderr) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath("package-lock.json"),
            "--no-external-evidence",
            "--verbose");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).Contains("    npm: 0 sbom only; 7 package-manager only; 0 both");
    }

    /// <summary>The rows are part of the summary, so <c>--quiet</c> suppresses them with it.</summary>
    [Test]
    public async Task Scan_WithVerboseAndQuiet_OmitsTheWholeSummary()
    {
        var root = FindRepositoryRoot();
        var (exitCode, _, stderr) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath("package-lock.json"),
            "--no-external-evidence",
            "--verbose",
            "--quiet");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).DoesNotContain("Supplied by:");
        await Assert.That(stderr).DoesNotContain("    npm: ");
    }

    /// <summary>
    /// An SBOM that also lists a component folds onto the lockfile's row for evidence, but it observed no
    /// installation and states no scope, so it must not cancel the resolver's classification. The two
    /// recommended configurations — scanning an SBOM beside the resolved tree, and relaxing policy for
    /// development-only dependencies — otherwise cannot be used together.
    /// </summary>
    [Test]
    [Arguments("dev-direct", "development")]
    [Arguments("dev-trans", "development")]
    [Arguments("both-direct", "runtime")]
    [Arguments("both-trans", "runtime")]
    public async Task Scan_WithSbomFoldedOntoResolvedRow_KeepsTheResolverClassification(string name, string expected)
    {
        var root = FindRepositoryRoot();
        var directory = await WriteDevelopmentUsageInputsAsync();
        try
        {
            var report = await ScanUsageAsync(root, Path.Combine(directory, "package-lock.json"), Path.Combine(directory, "bom.cdx.json"));
            await Assert.That(SelectUsage(report, name)).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A package installed at two paths is two rows, and an SBOM component attaches its occurrence to the
    /// first matching one. While that occurrence could cancel a classification, the same package at the
    /// same version was development on one row and unclassified on the other, so a policy allowance applied
    /// to one copy and not the other for no reason a reader could act on.
    /// </summary>
    [Test]
    public async Task Scan_WithSbomAndDuplicateInstalls_ClassifiesEveryCopyTheSame()
    {
        var root = FindRepositoryRoot();
        var directory = await WriteDuplicateInstallInputsAsync();
        try
        {
            var report = await ScanUsageAsync(root, Path.Combine(directory, "package-lock.json"), Path.Combine(directory, "bom.cdx.json"));
            var usages = report.RootElement.GetProperty("components").EnumerateArray()
                .Where(c => c.GetProperty("name").GetString() == "dup")
                .Select(c => c.TryGetProperty("usage", out var usage) ? usage.GetString() : "(none)")
                .ToArray();

            await Assert.That(usages).Count().IsEqualTo(2);
            await Assert.That(usages).IsEquivalentTo(new[] { "development", "development" });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<JsonDocument> ScanUsageAsync(string root, params string[] inputs)
    {
        var arguments = new List<string> { "scan" };
        for (var i = 0; i < inputs.Length; i++)
        {
            arguments.Add("--input");
            arguments.Add(inputs[i]);
        }

        arguments.AddRange(["--no-external-evidence", "--format", "json", "--quiet"]);
        var (exitCode, stdout, stderr) = await RunOlAsync(root, [.. arguments]);
        await Assert.That(exitCode).IsEqualTo(0).Because(stderr);
        return JsonDocument.Parse(stdout);
    }

    private static string SelectUsage(JsonDocument report, string name)
    {
        foreach (var component in report.RootElement.GetProperty("components").EnumerateArray())
        {
            if (component.GetProperty("name").GetString() != name) continue;
            return component.TryGetProperty("usage", out var usage) ? usage.GetString() ?? "(none)" : "(none)";
        }

        throw new InvalidOperationException($"Component '{name}' was not found in the report.");
    }

    /// <summary>Covers direct and transitive development reachability, each with and without a runtime path.</summary>
    private static async Task<string> WriteDevelopmentUsageInputsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ol-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "package-lock.json"), """
            {
              "name": "app", "version": "1.0.0", "lockfileVersion": 3, "requires": true,
              "packages": {
                "": { "name": "app", "version": "1.0.0",
                  "dependencies": { "runtime-root": "1.0.0" },
                  "devDependencies": { "dev-direct": "1.0.0", "both-direct": "1.0.0" } },
                "node_modules/runtime-root": { "version": "1.0.0", "license": "MIT",
                  "dependencies": { "both-direct": "1.0.0", "both-trans": "1.0.0" } },
                "node_modules/dev-direct": { "version": "1.0.0", "license": "MIT", "dev": true,
                  "dependencies": { "dev-trans": "1.0.0" } },
                "node_modules/dev-trans": { "version": "1.0.0", "license": "MIT", "dev": true },
                "node_modules/both-direct": { "version": "1.0.0", "license": "MIT" },
                "node_modules/both-trans": { "version": "1.0.0", "license": "MIT" }
              }
            }
            """, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(directory, "bom.cdx.json"), """
            { "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
              { "type": "library", "name": "runtime-root", "version": "1.0.0", "purl": "pkg:npm/runtime-root@1.0.0" },
              { "type": "library", "name": "dev-direct", "version": "1.0.0", "purl": "pkg:npm/dev-direct@1.0.0" },
              { "type": "library", "name": "dev-trans", "version": "1.0.0", "purl": "pkg:npm/dev-trans@1.0.0" },
              { "type": "library", "name": "both-direct", "version": "1.0.0", "purl": "pkg:npm/both-direct@1.0.0" },
              { "type": "library", "name": "both-trans", "version": "1.0.0", "purl": "pkg:npm/both-trans@1.0.0" } ] }
            """, Encoding.UTF8);
        return directory;
    }

    /// <summary>One package installed at two paths, both reached only through development dependencies.</summary>
    private static async Task<string> WriteDuplicateInstallInputsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ol-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "package-lock.json"), """
            {
              "name": "app", "version": "1.0.0", "lockfileVersion": 3, "requires": true,
              "packages": {
                "": { "name": "app", "version": "1.0.0", "devDependencies": { "dev-a": "1.0.0", "dev-b": "1.0.0" } },
                "node_modules/dev-a": { "version": "1.0.0", "license": "MIT", "dev": true, "dependencies": { "dup": "1.0.0" } },
                "node_modules/dev-a/node_modules/dup": { "version": "1.0.0", "license": "MIT", "dev": true },
                "node_modules/dev-b": { "version": "1.0.0", "license": "MIT", "dev": true, "dependencies": { "dup": "1.0.0" } },
                "node_modules/dup": { "version": "1.0.0", "license": "MIT", "dev": true }
              }
            }
            """, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(directory, "bom.cdx.json"), """
            { "bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
              { "type": "library", "name": "dup", "version": "1.0.0", "purl": "pkg:npm/dup@1.0.0" } ] }
            """, Encoding.UTF8);
        return directory;
    }

    /// <summary>The stderr summary must state what the JSON document states, or the JSON exemption breaks.</summary>
    [Test]
    public async Task Scan_WithCombinedInputs_ReportsSupplyTallyInTextSummary()
    {
        var root = FindRepositoryRoot();
        var (exitCode, _, stderr) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath("package-lock.json"),
            "--input", FixturePath("mixed-npm.cdx.json"),
            "--no-external-evidence");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).Contains("  Supplied by: 2 sbom only; 4 package-manager only; 3 both");
    }

    [Test]
    public async Task Scan_WithPurlPresentOnlyInSbom_RetainsRowAndNamesTheSbomAsItsOnlySupply()
    {
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm.cdx.json");

        var rows = FindComponents(report, "pkg:npm/sbom-only@9.0.0");
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(SuppliedBy(rows[0])).IsEquivalentTo(new[] { "sbom" });
        await Assert.That(rows[0].GetProperty("license").GetString()).IsEqualTo("BSD-3-Clause");
    }

    [Test]
    public async Task Scan_WithPurlPresentOnlyInPackageManager_RetainsRowAndNamesThePackageManagerAsItsOnlySupply()
    {
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm.cdx.json");

        var rows = FindComponents(report, "pkg:npm/native-addon@3.0.0");
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(SuppliedBy(rows[0])).IsEquivalentTo(new[] { "package-manager" });
    }

    [Test]
    public async Task Scan_WithPurlInstalledAtSeveralPaths_FansSbomEvidenceOutToEveryRow()
    {
        // npm keeps one component per install path, so a single SBOM component answers for all of them.
        // Fanning the declaration out preserves the package-manager population instead of collapsing it.
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm.cdx.json");

        var rows = FindComponents(report, "pkg:npm/shared@1.0.0");
        await Assert.That(rows).Count().IsEqualTo(2);
        for (var i = 0; i < rows.Count; i++)
        {
            await Assert.That(CandidateSources(rows[i])).Contains("sbom");
            await Assert.That(SuppliedBy(rows[i])).IsEquivalentTo(new[] { "sbom", "package-manager" });
        }
    }

    [Test]
    public async Task Scan_WithNuGetPurlDifferingOnlyInCase_MatchesTheSameComponent()
    {
        var report = await ScanMixedAsync("nuget-project.assets.json", "mixed-nuget.cdx.json");

        var rows = FindComponents(report, "pkg:nuget/Direct.Package@1.0.0");
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0].GetProperty("license").GetString()).IsEqualTo("MIT");
        await Assert.That(SuppliedBy(rows[0])).IsEquivalentTo(new[] { "sbom", "package-manager" });
        await Assert.That(FindComponents(report, "pkg:nuget/direct.package@1.0.0")).IsEmpty();
    }

    [Test]
    public async Task Scan_WithMavenPurlQualifiers_MatchesOnIdentityWithoutThem()
    {
        var report = await ScanMixedAsync("maven-dependency-tree.json", "mixed-maven.cdx.json");

        var rows = FindComponents(report, "pkg:maven/org.example/transitive@3.0.0?classifier=tests&type=test-jar");
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0].GetProperty("license").GetString()).IsEqualTo("Apache-2.0");
        await Assert.That(SuppliedBy(rows[0])).IsEquivalentTo(new[] { "sbom", "package-manager" });
    }

    [Test]
    public async Task Scan_WithGoMajorVersionWrittenAsSubpath_MatchesTheSameModule()
    {
        // A Go module at major version 2 or above carries the major in its module path. Generators disagree about
        // where that lands in the purl: Ol's module-graph input writes ".../go-md2man/v2@v2.0.6" while syft writes
        // ".../go-md2man@v2.0.6#v2". Both name one module, so a collection must not report it twice.
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ol-go-major-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "go-list-modules.json"),
            """
            { "Path": "example.com/app", "Main": true, "Dir": "/repo", "GoMod": "/repo/go.mod", "GoVersion": "1.25" }
            { "Path": "github.com/cpuguy83/go-md2man/v2", "Version": "v2.0.6", "Indirect": false }
            { "Path": "github.com/go-playground/validator/v10", "Version": "v10.30.3", "Indirect": false }
            { "Path": "github.com/sirupsen/logrus", "Version": "v1.9.3", "Indirect": false }
            { "Path": "github.com/ugorji/go/codec", "Version": "v1.3.1", "Indirect": false }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "go-mod-graph.txt"),
            """
            example.com/app github.com/cpuguy83/go-md2man/v2@v2.0.6
            example.com/app github.com/go-playground/validator/v10@v10.30.3
            example.com/app github.com/sirupsen/logrus@v1.9.3
            example.com/app github.com/ugorji/go/codec@v1.3.1
            """);

        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(
                root,
                "scan",
                "--input", temporaryDirectory,
                "--input", FixturePath("mixed-go-major.cdx.json"),
                "--no-external-evidence",
                "--format", "json");

            await Assert.That(stderr).IsEmpty();
            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            // v10 exercises the whole-number test: a suffix starting with "1" is still a major-version suffix.
            foreach (var purl in new[]
                     {
                         "pkg:golang/github.com/cpuguy83/go-md2man/v2@v2.0.6",
                         "pkg:golang/github.com/go-playground/validator/v10@v10.30.3",
                         // Not a major-version suffix but the same split: the module is github.com/ugorji/go/codec.
                         "pkg:golang/github.com/ugorji/go/codec@v1.3.1",
                     })
            {
                var matched = FindComponents(report, purl);
                await Assert.That(matched).Count().IsEqualTo(1);
                await Assert.That(SuppliedBy(matched[0])).IsEquivalentTo(new[] { "sbom", "package-manager" });
                await Assert.That(matched[0].GetProperty("license").GetString()).IsEqualTo("MIT");
            }

            // A module below v2 has no suffix to fold away, and must still match on its plain module path.
            var single = FindComponents(report, "pkg:golang/github.com/sirupsen/logrus@v1.9.3");
            await Assert.That(single).Count().IsEqualTo(1);
            await Assert.That(SuppliedBy(single[0])).IsEquivalentTo(new[] { "sbom", "package-manager" });
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Scan_WithOneSbomListingAPurlTwice_AppliesTheSameIdentityRuleAsACollection()
    {
        // A format declares what makes two observations the same package, and CycloneDX declares that to be the purl.
        // A collection already folds on that rule, so a single input that skipped it made the same document report a
        // different shape depending on whether a lockfile was scanned beside it. The occurrences stay, because the
        // document really did list the package twice; only the component identity is one.
        var root = FindRepositoryRoot();
        var sbomPath = Path.Combine(Path.GetTempPath(), $"ol-duplicate-purl-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(sbomPath, """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            { "bom-ref": "a", "type": "library", "name": "Example", "version": "1.0.0", "purl": "pkg:nuget/Example@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] },
            { "bom-ref": "b", "type": "library", "name": "Example", "version": "1.0.0", "purl": "pkg:nuget/Example@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] },
            { "bom-ref": "c", "type": "library", "name": "Other", "version": "2.0.0", "purl": "pkg:nuget/Other@2.0.0", "licenses": [ { "license": { "id": "MIT" } } ] }
          ]
        }
        """);
        try
        {
            var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", sbomPath, "--no-external-evidence", "--format", "json");

            await Assert.That(stderr).IsEmpty();
            await Assert.That(exitCode).IsEqualTo(0);
            using var report = JsonDocument.Parse(stdout);
            await Assert.That(FindComponents(report, "pkg:nuget/Example@1.0.0")).Count().IsEqualTo(1);
            await Assert.That(report.RootElement.GetProperty("components").GetArrayLength()).IsEqualTo(2);
            var inventory = report.RootElement.GetProperty("inventory");
            await Assert.That(inventory.GetProperty("components").GetArrayLength()).IsEqualTo(2);
            await Assert.That(inventory.GetProperty("occurrences").GetArrayLength()).IsEqualTo(3);
        }
        finally
        {
            File.Delete(sbomPath);
        }
    }

    [Test]
    public async Task Scan_WithTwoPackageManagerFormatsSharingPurl_KeepsThemSeparate()
    {
        // Two lockfiles describe two installations, so the same purl in each is two observations rather
        // than one. Only the SBOM boundary carries the projection relationship that justifies matching.
        var report = await ScanMixedAsync("package-lock.json", "pnpm-lock.yaml");

        await Assert.That(FindComponents(report, "pkg:npm/dev-tool@4.0.0")).Count().IsEqualTo(2);
        await Assert.That(report.RootElement.GetProperty("metadata").GetProperty("input").GetProperty("kind").GetString())
            .IsEqualTo("package-manager");
    }

    [Test]
    public async Task Scan_WithTwoSbomDocuments_RejectsTheInput()
    {
        var root = FindRepositoryRoot();
        var (exitCode, stdout, stderr) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath("mixed-npm.cdx.json"),
            "--input", FixturePath("mixed-nuget.cdx.json"),
            "--no-external-evidence");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr.Trim()).IsEqualTo("Unable to scan input: A collection accepts at most one SBOM document.");
    }

    [Test]
    public async Task Scan_WithMixedInputs_ReportsCollectionKindWithoutSbomIdentityFields()
    {
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm.cdx.json");

        var input = report.RootElement.GetProperty("metadata").GetProperty("input");
        await Assert.That(input.GetProperty("kind").GetString()).IsEqualTo("collection");
        await Assert.That(input.GetProperty("format").GetString()).IsEqualTo("collection");
        // The aggregate hash describes the collection, so publishing it as the SBOM's own identity would be a lie.
        await Assert.That(input.TryGetProperty("sbomRef", out _)).IsFalse();
        await Assert.That(input.TryGetProperty("sbomSha256", out _)).IsFalse();
        await Assert.That(input.TryGetProperty("sbomFormat", out _)).IsFalse();
        await Assert.That(input.TryGetProperty("sbomSpecVersion", out _)).IsFalse();
    }

    [Test]
    public async Task Scan_WithSingleInput_StillReportsSuppliedBy()
    {
        var sbomOnly = await ScanAsync("mixed-npm.cdx.json");
        var packageManagerOnly = await ScanAsync("package-lock.json");

        await Assert.That(SuppliedBy(FindComponents(sbomOnly, "pkg:npm/alpha@1.0.0")[0])).IsEquivalentTo(new[] { "sbom" });
        await Assert.That(SuppliedBy(FindComponents(packageManagerOnly, "pkg:npm/alpha@1.0.0")[0])).IsEquivalentTo(new[] { "package-manager" });
    }

    [Test]
    public async Task Scan_WithInputsDeclaringDifferentLicenses_ReportsConflict()
    {
        // The lockfile declares Apache-2.0 for the nested copy of shared@1.0.0 while the SBOM declares MIT.
        // Neither satisfies the other, so combining the inputs surfaces a disagreement single inputs hide.
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm.cdx.json");

        var rows = FindComponents(report, "pkg:npm/shared@1.0.0");
        var statuses = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            statuses.Add(rows[i].GetProperty("status").GetString()!);
        }

        await Assert.That(statuses).Contains("conflict");
    }

    /// <summary>
    /// Both inputs state a relationship and they disagree, which the closest-to-a-root rule used to settle in the
    /// SBOM's favour whenever the SBOM said the stronger word. Relationship was the last field where a fold could
    /// overwrite what the receiving row already said, so it now follows the rest of the row: the resolver's graph is
    /// the one the scan is about, and the SBOM's own relationship stays reachable through its edges.
    /// </summary>
    [Test]
    [Arguments("nuget-project.assets.json", "mixed-nuget.cdx.json", "pkg:nuget/Native.Package@4.0.0", "transitive")]
    [Arguments("nuget-project.assets.json", "mixed-nuget-inverted.cdx.json", "pkg:nuget/Direct.Package@1.0.0", "direct")]
    public async Task Scan_WithBothInputsStatingARelationship_KeepsTheResolverRelationship(
        string packageManagerFixture,
        string sbomFixture,
        string purl,
        string expected)
    {
        var report = await ScanMixedAsync(packageManagerFixture, sbomFixture);

        var rows = FindComponents(report, purl);
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0].GetProperty("dependency").GetString()).IsEqualTo(expected);
        await Assert.That(SuppliedBy(rows[0])).IsEquivalentTo(new[] { "sbom", "package-manager" });
    }

    /// <summary>
    /// The resolver owning the relationship is not the same claim as the SBOM never being read. A resolved input that
    /// states no relationship determined nothing, and an input that determined nothing does not outrank one that did.
    /// </summary>
    [Test]
    public async Task Scan_WithResolverStatingNoRelationship_TakesTheSbomRelationship()
    {
        var report = await ScanMixedAsync("Package.resolved", "mixed-swift-direct.cdx.json");

        var rows = FindComponents(report, "pkg:swift/github.com/apple/swift-log@1.6.2");
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0].GetProperty("dependency").GetString()).IsEqualTo("direct");
    }

    [Test]
    public async Task Scan_WithMixedInputs_PreservesEachInputGraphWithoutInventingEdges()
    {
        var packageManagerOnly = await ScanAsync("package-lock.json");
        var sbomOnly = await ScanAsync("mixed-npm.cdx.json");
        var mixed = await ScanMixedAsync("package-lock.json", "mixed-npm.cdx.json");

        var expectedEdges = EdgeCount(packageManagerOnly) + EdgeCount(sbomOnly);
        var expectedOccurrences = OccurrenceCount(packageManagerOnly) + OccurrenceCount(sbomOnly);
        await Assert.That(EdgeCount(mixed)).IsEqualTo(expectedEdges);
        await Assert.That(OccurrenceCount(mixed)).IsEqualTo(expectedOccurrences);
    }

    /// <summary>
    /// A package-manager input listing a component is itself the determination that the component is a dependency of
    /// the scanned resolution. An SBOM's root is the root of the SBOM's own graph, so folding it onto that row must
    /// not restate the row as the subject of the scan — <see href="cli.md#contract-policy-checks">policy skips a root</see>,
    /// so the promotion would withdraw the component from the gate on evidence that never described this resolution.
    /// </summary>
    [Test]
    [Arguments("package-lock.json", "mixed-npm-root-direct.cdx.json", "pkg:npm/alpha@1.0.0", "direct")]
    [Arguments("pip-inspect.json", "mixed-pypi-root-transitive.cdx.json", "pkg:pypi/urllib3@2.5.0", "transitive")]
    [Arguments("Package.resolved", "mixed-swift-root-unknown.cdx.json", "pkg:swift/github.com/apple/swift-log@1.6.2", "unknown")]
    public async Task Scan_WithSbomRootFoldedOntoResolvedRow_KeepsTheResolverRelationship(
        string packageManagerFixture,
        string sbomFixture,
        string purl,
        string expected)
    {
        var report = await ScanMixedAsync(packageManagerFixture, sbomFixture);

        var rows = FindComponents(report, purl);
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0].GetProperty("dependency").GetString()).IsEqualTo(expected);
        await Assert.That(SuppliedBy(rows[0])).IsEquivalentTo(new[] { "sbom", "package-manager" });
    }

    /// <summary>
    /// One SBOM can place the same purl at two positions in its own graph, and the relationship it then contributes
    /// to a resolver that determined none must be the one the SBOM alone would report. Filling the row from whichever
    /// component the document happened to list first would make the result depend on document order, which is the
    /// property combining inputs in two passes exists to protect.
    /// </summary>
    [Test]
    [Arguments("mixed-swift-duplicate-transitive-first.cdx.json")]
    [Arguments("mixed-swift-duplicate-direct-first.cdx.json")]
    public async Task Scan_WithSbomPlacingOnePurlTwice_FillsTheRelationshipTheSbomAloneReports(string sbomFixture)
    {
        var sbomOnly = await ScanAsync(sbomFixture);
        var expected = FindComponents(sbomOnly, "pkg:swift/github.com/apple/swift-log@1.6.2")[0].GetProperty("dependency").GetString();

        var report = await ScanMixedAsync("Package.resolved", sbomFixture);

        var rows = FindComponents(report, "pkg:swift/github.com/apple/swift-log@1.6.2");
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0].GetProperty("dependency").GetString()).IsEqualTo(expected);
        await Assert.That(expected).IsEqualTo("direct");
    }

    /// <summary>
    /// A package installed at two paths is two rows the resolver classified differently, and one SBOM component
    /// answers for both. Each row is decided against its own resolver answer rather than against the first row the
    /// identity chain reaches, so the SBOM's single relationship does not level them.
    /// </summary>
    [Test]
    public async Task Scan_WithSbomRelationshipFannedOntoSeveralInstallations_DecidesEachRowSeparately()
    {
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm-installations-direct.cdx.json");

        var relationships = FindComponents(report, "pkg:npm/shared@1.0.0")
            .Select(row => row.GetProperty("dependency").GetString())
            .ToArray();

        await Assert.That(relationships).IsEquivalentTo(new[] { "direct", "transitive" });
    }

    /// <summary>
    /// An SBOM states one root and a package manager tracks the same package at two paths, so the fan-out reaches
    /// rows the resolver classified differently. Each keeps its own relationship rather than every copy collapsing
    /// onto the one value the SBOM happened to state.
    /// </summary>
    [Test]
    public async Task Scan_WithSbomRootFoldedOntoSeveralInstallations_KeepsEachResolverRelationship()
    {
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm-root-installations.cdx.json");

        var relationships = FindComponents(report, "pkg:npm/shared@1.0.0")
            .Select(row => row.GetProperty("dependency").GetString())
            .ToArray();

        await Assert.That(relationships).IsEquivalentTo(new[] { "direct", "transitive" });
    }

    /// <summary>
    /// The rule is about the receiving row, not about the value: an SBOM root no package-manager input answers for
    /// keeps its own row and stays a root, exactly as it does when the SBOM is scanned alone.
    /// </summary>
    [Test]
    public async Task Scan_WithSbomRootMatchingNoResolvedRow_KeepsItAsARootRow()
    {
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm-root-unmatched.cdx.json");

        var rows = FindComponents(report, "pkg:npm/standalone@2.0.0");
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0].GetProperty("dependency").GetString()).IsEqualTo("root");
        await Assert.That(SuppliedBy(rows[0])).IsEquivalentTo(new[] { "sbom" });
    }

    [Test]
    public async Task Scan_WithSbomRootComponentWithoutPurl_KeepsItAsItsOwnRow()
    {
        var report = await ScanMixedAsync("package-lock.json", "mixed-npm.cdx.json");

        var components = report.RootElement.GetProperty("components");
        var found = false;
        foreach (var component in components.EnumerateArray())
        {
            if (component.GetProperty("name").GetString() != "mixed-app") continue;
            found = true;
            await Assert.That(component.GetProperty("purl").GetString()).IsEqualTo(string.Empty);
            await Assert.That(SuppliedBy(component)).IsEquivalentTo(new[] { "sbom" });
        }

        await Assert.That(found).IsTrue();
    }

    [Test]
    public async Task Enrichment_WithFannedOutComponentsSharingPurl_PlansOneLookupPerIdentity()
    {
        // Fanning a declaration out to several rows must not fan the network out with it.
        var index = new SpdxLicenseIndex(["MIT"], []);
        var root = Path.Combine(Path.GetTempPath(), $"ol-mixed-enrich-{Guid.NewGuid():N}");
        try
        {
            var cache = new PackageMetadataCache(root);
            await cache.WriteAsync(new PackageMetadataRecord("pkg:npm/shared@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
            await cache.WriteAsync(new PackageMetadataRecord("pkg:npm/alpha@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0);
            var components = new[]
            {
                CreateComponent(index, "pkg:npm/shared@1.0.0"),
                CreateComponent(index, "pkg:npm/shared@1.0.0"),
                CreateComponent(index, "pkg:npm/alpha@1.0.0"),
            };
            var resolutions = new PackageMetadataResolution?[components.Length];

            var enrichment = await service.EnrichAsync(components, resolutions, concurrency: 1);

            await Assert.That(enrichment.Summary.TargetCount).IsEqualTo(2);
            await Assert.That(enrichment.Summary.SupportedComponentCount).IsEqualTo(3);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ScanComponent CreateComponent(SpdxLicenseIndex index, Utf8Slice purl)
        => new("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, purl, default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), []);

    private static int EdgeCount(JsonDocument report)
        => report.RootElement.GetProperty("inventory").GetProperty("edges").GetArrayLength();

    private static int OccurrenceCount(JsonDocument report)
        => report.RootElement.GetProperty("inventory").GetProperty("occurrences").GetArrayLength();

    private static List<JsonElement> FindComponents(JsonDocument report, string purl)
    {
        var matches = new List<JsonElement>();
        foreach (var component in report.RootElement.GetProperty("components").EnumerateArray())
        {
            if (component.GetProperty("purl").GetString() == purl)
            {
                matches.Add(component);
            }
        }

        return matches;
    }

    private static List<string> SuppliedBy(JsonElement component)
    {
        var values = new List<string>();
        foreach (var value in component.GetProperty("suppliedBy").EnumerateArray())
        {
            values.Add(value.GetString()!);
        }

        return values;
    }

    private static List<string> CandidateSources(JsonElement component)
    {
        var sources = new List<string>();
        foreach (var candidate in component.GetProperty("licenseCandidates").EnumerateArray())
        {
            sources.Add(candidate.GetProperty("source").GetString()!);
        }

        return sources;
    }

    private static async Task<JsonDocument> ScanMixedAsync(string packageManagerFixture, string sbomFixture)
    {
        var root = FindRepositoryRoot();
        var (exitCode, stdout, stderr) = await RunOlAsync(
            root,
            "scan",
            "--input", FixturePath(packageManagerFixture),
            "--input", FixturePath(sbomFixture),
            "--no-external-evidence",
            "--format", "json");

        await Assert.That(stderr).IsEmpty();
        await Assert.That(exitCode).IsEqualTo(0);
        return JsonDocument.Parse(stdout);
    }

    private static async Task<JsonDocument> ScanAsync(string fixture)
    {
        var root = FindRepositoryRoot();
        var (exitCode, stdout, stderr) = await RunOlAsync(root, "scan", "--input", FixturePath(fixture), "--no-external-evidence", "--format", "json");

        await Assert.That(stderr).IsEmpty();
        await Assert.That(exitCode).IsEqualTo(0);
        return JsonDocument.Parse(stdout);
    }

    private static string FixturePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

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
