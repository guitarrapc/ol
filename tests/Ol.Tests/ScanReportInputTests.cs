using System.Text;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Reporting;

namespace Ol.Tests;

public sealed class ScanReportInputTests
{
    private static string Component(
        string name = "example",
        string version = "1.0.0",
        string license = "MIT",
        string status = "matched",
        string raw = "MIT",
        string normalized = "MIT")
        => $$"""
        {
          "name": "{{name}}", "version": "{{version}}", "license": "{{license}}",
          "ecosystem": "npm", "dependency": "direct", "status": "{{status}}",
          "purl": "pkg:npm/{{name}}@{{version}}", "sourceId": "{{name}}",
          "licenseCandidates": [
            { "source": "sbom", "kind": "expression", "raw": "{{raw}}", "normalized": "{{normalized}}", "status": "matched", "deprecated": false }
          ],
          "warnings": []
        }
        """;

    private static string Report(params string[] components)
        => $$"""
        {
          "schemaVersion": 1,
          "metadata": {
            "tool": {
              "name": "ol",
              "version": "0.1.0-dev",
              "informationUri": "https://github.com/guitarrapc/ol"
            },
            "input": { "sourceReference": "sbom.json", "kind": "sbom", "format": "cyclonedx-json" },
            "spdx": { "source": "generated", "licenseListVersion": "5e59516" }
          },
          "components": [ {{string.Join(",", components)}} ]
        }
        """;

    [Test]
    public async Task TryRead_WithCanonicalReport_RestoresComponentsAndMetadata()
    {
        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(Report(Component())), out var report, out var error);

        await Assert.That(parsed).IsTrue();
        await Assert.That(error).IsEmpty();
        await Assert.That(report.LicenseListVersion).IsEqualTo("5e59516");
        await Assert.That(report.SourceReference).IsEqualTo("sbom.json");
        await Assert.That(report.Components).Count().IsEqualTo(1);
        await Assert.That(report.ExcludedInputPaths).IsEmpty();

        var component = report.Components[0];
        await Assert.That(component.Name.ToString()).IsEqualTo("example");
        await Assert.That(component.Version.ToString()).IsEqualTo("1.0.0");
        await Assert.That(component.Ecosystem).IsEqualTo("npm");
        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(component.License.ToString()).IsEqualTo("MIT");
        await Assert.That(component.Purl.ToString()).IsEqualTo("pkg:npm/example@1.0.0");
        await Assert.That(component.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(component.CandidateCount).IsEqualTo(1);
        await Assert.That(component.GetCandidate(0).Source).IsEqualTo(LicenseCandidateSource.Sbom);
        await Assert.That(component.GetCandidate(0).Normalized.ToString()).IsEqualTo("MIT");
    }

    [Test]
    public async Task TryRead_WithInputScope_RestoresExcludedInputPaths()
    {
        var json = Report(Component()).Replace(
            "\"spdx\":",
            "\"inputScope\": { \"excludedPathCount\": 2, \"excludedPaths\": [\"product-a/docs\", \"product-b/docs\"] }, \"spdx\":",
            StringComparison.Ordinal);

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out var report, out var error);

        await Assert.That(parsed).IsTrue().Because(error);
        await Assert.That(report.ExcludedInputPaths).IsEquivalentTo(["product-a/docs", "product-b/docs"]);
    }

    [Test]
    public async Task TryRead_WithMalformedInputScope_Fails()
    {
        var json = Report(Component()).Replace(
            "\"spdx\":",
            "\"inputScope\": { \"excludedPaths\": {} }, \"spdx\":",
            StringComparison.Ordinal);

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out _, out var error);

        await Assert.That(parsed).IsFalse();
        await Assert.That(error).Contains("metadata.inputScope");
    }

    [Test]
    public async Task TryRead_WithFilteredView_RestoresTheFilterAndTheExcludedCounts()
    {
        var json = Report(Component()).Replace(
            "\"spdx\":",
            "\"view\": { \"dependencyFilter\": \"direct\", \"excludedCount\": 3, \"excludedUnknownCount\": 1 }, \"spdx\":",
            StringComparison.Ordinal);

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out var report, out var error);

        await Assert.That(parsed).IsTrue().Because(error);
        await Assert.That(report.View.IsFiltered).IsTrue();
        await Assert.That(report.View.DependencyFilter).IsEqualTo("direct");
        await Assert.That(report.View.ExcludedCount).IsEqualTo(3);
        await Assert.That(report.View.ExcludedUnknownCount).IsEqualTo(1);
    }

    [Test]
    public async Task TryRead_WithUnfilteredView_RestoresNoFilter()
    {
        var json = Report(Component()).Replace(
            "\"spdx\":",
            "\"view\": { \"dependencyFilter\": null, \"excludedCount\": 0, \"excludedUnknownCount\": 0 }, \"spdx\":",
            StringComparison.Ordinal);

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out var report, out var error);

        await Assert.That(parsed).IsTrue().Because(error);
        await Assert.That(report.View.IsFiltered).IsFalse();
    }

    /// <summary>
    /// A view that states no filter at all is not the same document as one stating there was none, and a view whose
    /// counts contradict its filter is neither. Accepting either as unfiltered would let a narrowed report be gated
    /// as a complete one, which is what reading the view prevents.
    /// </summary>
    [Test]
    [Arguments("\"view\": null")]
    [Arguments("\"view\": {}")]
    [Arguments("\"view\": { \"excludedCount\": 0, \"excludedUnknownCount\": 0 }")]
    [Arguments("\"view\": { \"dependencyFilter\": 6, \"excludedCount\": 0, \"excludedUnknownCount\": 0 }")]
    [Arguments("\"view\": { \"dependencyFilter\": \"direct\", \"excludedUnknownCount\": 0 }")]
    [Arguments("\"view\": { \"dependencyFilter\": \"direct\", \"excludedCount\": 0 }")]
    [Arguments("\"view\": { \"dependencyFilter\": \"direct\", \"excludedCount\": 1.5, \"excludedUnknownCount\": 0 }")]
    [Arguments("\"view\": { \"dependencyFilter\": \"direct\", \"excludedCount\": -1, \"excludedUnknownCount\": 0 }")]
    [Arguments("\"view\": { \"dependencyFilter\": \"direct\", \"excludedCount\": 3, \"excludedUnknownCount\": -1 }")]
    [Arguments("\"view\": { \"dependencyFilter\": \"direct\", \"excludedCount\": 3, \"excludedUnknownCount\": 5 }")]
    [Arguments("\"view\": { \"dependencyFilter\": null, \"excludedCount\": 6, \"excludedUnknownCount\": 0 }")]
    [Arguments("\"view\": { \"dependencyFilter\": \"\", \"excludedCount\": 0, \"excludedUnknownCount\": 3 }")]
    public async Task TryRead_WithUnreadableView_Fails(string view)
    {
        var json = Report(Component()).Replace("\"spdx\":", $"{view}, \"spdx\":", StringComparison.Ordinal);

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out _, out var error);

        await Assert.That(parsed).IsFalse();
        await Assert.That(error).Contains("metadata.view");
    }

    /// <summary>
    /// The rejected states are narrow, so the nearby valid ones have to stay valid: a filter that excluded nothing,
    /// and one whose exclusions were all relationships no input determined.
    /// </summary>
    [Test]
    [Arguments("\"dependencyFilter\": \"direct\", \"excludedCount\": 0, \"excludedUnknownCount\": 0", true)]
    [Arguments("\"dependencyFilter\": \"direct\", \"excludedCount\": 3, \"excludedUnknownCount\": 3", true)]
    [Arguments("\"dependencyFilter\": null, \"excludedCount\": 0, \"excludedUnknownCount\": 0", false)]
    [Arguments("\"dependencyFilter\": \"\", \"excludedCount\": 0, \"excludedUnknownCount\": 0", false)]
    public async Task TryRead_WithConsistentView_Succeeds(string view, bool filtered)
    {
        var json = Report(Component()).Replace("\"spdx\":", $"\"view\": {{ {view} }}, \"spdx\":", StringComparison.Ordinal);

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out var report, out var error);

        await Assert.That(parsed).IsTrue().Because(error);
        await Assert.That(report.View.IsFiltered).IsEqualTo(filtered);
    }

    /// <summary>
    /// One message for every way a view can be unreadable points a reader at the wrong contract. A report is rejected
    /// once and the operator has only the message to work from, so it has to name the field that failed.
    /// </summary>
    [Test]
    [Arguments("\"view\": null", "must be an object")]
    [Arguments("\"view\": { \"excludedCount\": 0, \"excludedUnknownCount\": 0 }", "dependencyFilter")]
    [Arguments("\"view\": { \"dependencyFilter\": \"direct\", \"excludedCount\": -1, \"excludedUnknownCount\": 0 }", "excludedCount")]
    [Arguments("\"view\": { \"dependencyFilter\": \"direct\", \"excludedCount\": 3, \"excludedUnknownCount\": 5 }", "excludedUnknownCount")]
    [Arguments("\"view\": { \"dependencyFilter\": null, \"excludedCount\": 6, \"excludedUnknownCount\": 0 }", "states no dependency filter")]
    public async Task TryRead_WithUnreadableView_NamesWhatFailed(string view, string expected)
    {
        var json = Report(Component()).Replace("\"spdx\":", $"{view}, \"spdx\":", StringComparison.Ordinal);

        ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out _, out var error);

        await Assert.That(error).Contains("metadata.view");
        await Assert.That(error).Contains(expected);
    }

    /// <summary>
    /// A default-constructed scope is the one a caller gets when it states no view, and it must read as unfiltered
    /// rather than as an unusable value, because that is the only value the renderers' optional parameter can take.
    /// </summary>
    [Test]
    public async Task ViewScope_WhenDefaultConstructed_StatesNoFilter()
    {
        await Assert.That(default(ScanReportViewScope).IsFiltered).IsFalse();
        await Assert.That(new ScanReportViewScope(string.Empty, 0, 0).IsFiltered).IsFalse();
    }

    [Test]
    public async Task TryRead_WithoutView_RestoresNoFilter()
    {
        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(Report(Component())), out var report, out var error);

        await Assert.That(parsed).IsTrue().Because(error);
        await Assert.That(report.View.IsFiltered).IsFalse();
    }

    [Test]
    public async Task TryRead_RestoresPerComponentDevelopmentUsage()
    {
        const string components = """
            { "name": "dev-pkg", "version": "1.0.0", "license": "MIT", "ecosystem": "npm", "dependency": "direct", "status": "matched", "purl": "pkg:npm/dev-pkg@1.0.0", "sourceId": "dev-pkg", "usage": "development", "warnings": [] },
            { "name": "run-pkg", "version": "1.0.0", "license": "MIT", "ecosystem": "npm", "dependency": "direct", "status": "matched", "purl": "pkg:npm/run-pkg@1.0.0", "sourceId": "run-pkg", "usage": "runtime", "warnings": [] },
            { "name": "plain-pkg", "version": "1.0.0", "license": "MIT", "ecosystem": "npm", "dependency": "direct", "status": "matched", "purl": "pkg:npm/plain-pkg@1.0.0", "sourceId": "plain-pkg", "warnings": [] }
            """;

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(Report(components)), out var report, out var error);

        await Assert.That(parsed).IsTrue().Because(error);
        await Assert.That(report.ComponentUsages).Count().IsEqualTo(3);
        await Assert.That(report.ComponentUsages[0]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(report.ComponentUsages[1]).IsEqualTo(DependencyUsage.Runtime);
        await Assert.That(report.ComponentUsages[2]).IsEqualTo(DependencyUsage.Unknown);
    }

    [Test]
    public async Task TryRead_PreservesCandidatesNeededForPolicyAndBaseline()
    {
        var json = Report(Component(status: "conflict", license: "MIT, GPL-3.0-only (?)", raw: "MIT"));

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out var report, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(report.Components[0].Status).IsEqualTo(LicenseStatus.Conflict);
        await Assert.That(report.Components[0].GetCandidate(0).Raw.ToString()).IsEqualTo("MIT");
    }

    [Test]
    public async Task TryRead_WithAbsentLicensePlaceholder_RestoresEmptyLicense()
    {
        var json = Report(Component(license: "-", status: "unknown", raw: string.Empty, normalized: string.Empty));

        ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out var report, out _);

        await Assert.That(report.Components[0].License.IsEmpty).IsTrue();
    }

    [Test]
    [Arguments("{ malformed")]
    [Arguments("[]")]
    [Arguments("{ \"schemaVersion\": 99, \"components\": [] }")]
    [Arguments("{ \"components\": [] }")]
    public async Task TryRead_WithUnusableDocument_Fails(string json)
    {
        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out _, out var error);

        await Assert.That(parsed).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    public async Task TryRead_WithUnknownComponentStatus_Fails()
    {
        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(Report(Component(status: "weird"))), out _, out var error);

        await Assert.That(parsed).IsFalse();
        await Assert.That(error).Contains("status");
    }

    [Test]
    public async Task TryRead_WithNonObjectInputMetadata_FailsWithActionableError()
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "metadata": {
            "input": [ { "kind": "sbom" } ],
            "spdx": { "licenseListVersion": "5e59516" }
          },
          "components": [ {{Component()}} ]
        }
        """;

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out _, out var error);

        await Assert.That(parsed).IsFalse();
        await Assert.That(error).Contains("metadata.input");
    }

    [Test]
    public async Task TryRead_WithGroupedReport_FailsWithActionableError()
    {
        var json = """{ "schemaVersion": 1, "metadata": { "tool": { "name": "ol", "version": "0.1.0-dev", "informationUri": "https://github.com/guitarrapc/ol" } }, "groups": [ { "key": "MIT", "count": 2 } ] }""";

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out _, out var error);

        await Assert.That(parsed).IsFalse();
        await Assert.That(error).Contains("--group-by");
    }

    [Test]
    public async Task TryRead_WithInvalidInventoryIndex_Fails()
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "metadata": {
            "input": { "kind": "sbom", "format": "cyclonedx", "sourceRef": "sbom.json", "parser": "cyclonedx-json" },
            "spdx": { "licenseListVersion": "5e59516" }
          },
          "inventory": {
            "contexts": [],
            "components": [
              { "name": "example", "version": "1.0.0", "ecosystem": "npm", "dependency": "direct", "purl": "pkg:npm/example@1.0.0", "sourceId": "example" }
            ],
            "occurrences": [ { "contextIndex": -1, "componentIndex": 1 } ],
            "edges": []
          },
          "components": [ {{Component()}} ]
        }
        """;

        var parsed = ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out _, out var error);

        await Assert.That(parsed).IsFalse();
        await Assert.That(error).Contains("componentIndex");
    }

    // Diff.

    [Test]
    public async Task Compare_WithIdenticalReports_ReportsNoChanges()
        => await Assert.That(ScanReportDiff.Compare(Read(Report(Component())), Read(Report(Component())))).IsEmpty();

    [Test]
    public async Task Compare_DetectsAddedAndRemovedComponents()
    {
        var changes = ScanReportDiff.Compare(Read(Report(Component())), Read(Report(Component(name: "replacement"))));

        await Assert.That(changes).Count().IsEqualTo(2);
        await Assert.That(changes[0].Name).IsEqualTo("example");
        await Assert.That(changes[0].Kind).IsEqualTo(ScanReportChangeKind.Removed);
        await Assert.That(changes[1].Name).IsEqualTo("replacement");
        await Assert.That(changes[1].Kind).IsEqualTo(ScanReportChangeKind.Added);
    }

    [Test]
    public async Task Compare_DetectsVersionChange()
    {
        var changes = ScanReportDiff.Compare(Read(Report(Component())), Read(Report(Component(version: "2.0.0"))));

        await Assert.That(changes).Count().IsEqualTo(1);
        await Assert.That(changes[0].Kind).IsEqualTo(ScanReportChangeKind.VersionChanged);
        await Assert.That(changes[0].PreviousVersion).IsEqualTo("1.0.0");
        await Assert.That(changes[0].CurrentVersion).IsEqualTo("2.0.0");
    }

    [Test]
    public async Task Compare_WhenVersionAndLicenseChange_ReportsBothChanges()
    {
        var changes = ScanReportDiff.Compare(
            Read(Report(Component())),
            Read(Report(Component(version: "2.0.0", license: "AGPL-3.0-only", raw: "AGPL-3.0-only", normalized: "AGPL-3.0-only"))));

        await Assert.That(changes).Count().IsEqualTo(2);
        await Assert.That(changes[0].Kind).IsEqualTo(ScanReportChangeKind.VersionChanged);
        await Assert.That(changes[1].Kind).IsEqualTo(ScanReportChangeKind.LicenseChanged);
        await Assert.That(changes[1].PreviousVersion).IsEqualTo("1.0.0");
        await Assert.That(changes[1].CurrentVersion).IsEqualTo("2.0.0");
        await Assert.That(changes[1].PreviousLicense).IsEqualTo("MIT");
        await Assert.That(changes[1].CurrentLicense).IsEqualTo("AGPL-3.0-only");
    }

    [Test]
    public async Task Compare_WhenVersionSetsAndLicensesChange_ReportsBothChanges()
    {
        var previous = Read(Report(
            Component(),
            Component(version: "2.0.0", license: "Apache-2.0", raw: "Apache-2.0", normalized: "Apache-2.0")));
        var current = Read(Report(
            Component(version: "2.0.0", license: "Apache-2.0", raw: "Apache-2.0", normalized: "Apache-2.0"),
            Component(version: "3.0.0", license: "AGPL-3.0-only", raw: "AGPL-3.0-only", normalized: "AGPL-3.0-only")));

        var changes = ScanReportDiff.Compare(previous, current);

        await Assert.That(changes).Count().IsEqualTo(2);
        await Assert.That(changes[0].Kind).IsEqualTo(ScanReportChangeKind.VersionChanged);
        await Assert.That(changes[1].Kind).IsEqualTo(ScanReportChangeKind.LicenseChanged);
        await Assert.That(changes[1].PreviousVersion).IsEqualTo("1.0.0");
        await Assert.That(changes[1].CurrentVersion).IsEqualTo("3.0.0");
        await Assert.That(changes[1].PreviousLicense).IsEqualTo("MIT");
        await Assert.That(changes[1].CurrentLicense).IsEqualTo("AGPL-3.0-only");
    }

    [Test]
    public async Task Compare_WhenSameLicenseVersionIsAdded_ReportsOnlyVersionChange()
    {
        var previous = Read(Report(Component()));
        var current = Read(Report(Component(), Component(version: "2.0.0")));

        var changes = ScanReportDiff.Compare(previous, current);

        await Assert.That(changes).Count().IsEqualTo(1);
        await Assert.That(changes[0].Kind).IsEqualTo(ScanReportChangeKind.VersionChanged);
    }

    [Test]
    public async Task Compare_DetectsLicenseChangeAtSameVersion()
    {
        var changes = ScanReportDiff.Compare(
            Read(Report(Component())),
            Read(Report(Component(license: "Apache-2.0", raw: "Apache-2.0", normalized: "Apache-2.0"))));

        await Assert.That(changes).Count().IsEqualTo(1);
        await Assert.That(changes[0].Kind).IsEqualTo(ScanReportChangeKind.LicenseChanged);
        await Assert.That(changes[0].PreviousLicense).IsEqualTo("MIT");
        await Assert.That(changes[0].CurrentLicense).IsEqualTo("Apache-2.0");
    }

    [Test]
    public async Task Compare_DetectsStatusChangeAtSameVersion()
    {
        var changes = ScanReportDiff.Compare(Read(Report(Component())), Read(Report(Component(status: "unknown"))));

        await Assert.That(changes).Count().IsEqualTo(1);
        await Assert.That(changes[0].Kind).IsEqualTo(ScanReportChangeKind.StatusChanged);
        await Assert.That(changes[0].PreviousStatus).IsEqualTo("matched");
        await Assert.That(changes[0].CurrentStatus).IsEqualTo("unknown");
    }

    [Test]
    public async Task Compare_WhenStatusAndLicenseChange_ReportsBothChanges()
    {
        var changes = ScanReportDiff.Compare(
            Read(Report(Component())),
            Read(Report(Component(status: "unknown", license: "", raw: "", normalized: ""))));

        await Assert.That(changes).Count().IsEqualTo(2);
        await Assert.That(changes[0].Kind).IsEqualTo(ScanReportChangeKind.StatusChanged);
        await Assert.That(changes[1].Kind).IsEqualTo(ScanReportChangeKind.LicenseChanged);
    }

    [Test]
    public async Task Compare_DetectsEvidenceChangeWhenConclusionIsUnchanged()
    {
        var changes = ScanReportDiff.Compare(Read(Report(Component())), Read(Report(Component(raw: "MIT License"))));

        await Assert.That(changes).Count().IsEqualTo(1);
        await Assert.That(changes[0].Kind).IsEqualTo(ScanReportChangeKind.EvidenceChanged);
    }

    [Test]
    public async Task Compare_OrdersChangesByComponentName()
    {
        var current = Read(Report(Component(name: "zzz"), Component(name: "aaa"), Component()));

        var changes = ScanReportDiff.Compare(Read(Report(Component())), current);

        await Assert.That(changes).Count().IsEqualTo(2);
        await Assert.That(changes[0].Name).IsEqualTo("aaa");
        await Assert.That(changes[1].Name).IsEqualTo("zzz");
    }

    private static ScanComponent[] Read(string json)
    {
        if (!ScanReportReader.TryRead(Encoding.UTF8.GetBytes(json), out var report, out var error)) throw new InvalidOperationException(error);
        return report.Components;
    }
}
