using System.Text;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Tests;

/// <summary>
/// Guards how a CycloneDX <c>component.evidence.licenses</c> collection contributes to a component.
/// </summary>
/// <remarks>
/// A generator writes there when it detected a license rather than being told one, which is a weaker
/// claim than <c>component.licenses</c> and the reason the field exists separately. Ignoring it throws
/// away the only license fact some generators produce: a Go SBOM measured for this work carried an
/// SPDX identifier for every module there and nothing under <c>licenses</c>. Treating it as a
/// declaration instead would let a detection silently overrule what a publisher stated.
/// </remarks>
public sealed class CycloneDxEvidenceLicenseTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT", "Apache-2.0", "GPL-2.0-only", "GPL-3.0-only", "Unicode-3.0"], []);

    // Equivalence classes for the pair (declared, observed). The declared side can be absent, resolved,
    // unresolvable, or invalid; the observed side can be absent, a single identifier, several
    // identifiers whose relationship is unstated, or an identifier the SPDX data rejects.

    [Test]
    [Arguments("absent-absent", null, null, LicenseStatus.Unknown)]
    [Arguments("absent-single", null, "MIT", LicenseStatus.Matched)]
    [Arguments("absent-multiple", null, "MIT|Apache-2.0", LicenseStatus.Ambiguous)]
    [Arguments("absent-unrecognizable", null, "Not-A-License", LicenseStatus.Ambiguous)]
    [Arguments("absent-invalid", null, "MIT AND Not-A-License", LicenseStatus.Invalid)]
    [Arguments("declared-absent", "MIT", null, LicenseStatus.Matched)]
    [Arguments("declared-agrees", "MIT", "MIT", LicenseStatus.Matched)]
    [Arguments("declared-satisfied", "MIT OR Apache-2.0", "Apache-2.0", LicenseStatus.Matched)]
    [Arguments("declared-disagrees", "MIT", "Apache-2.0", LicenseStatus.Conflict)]
    [Arguments("declared-conjunction-term-observed", "(MIT OR Apache-2.0) AND Unicode-3.0", "Apache-2.0", LicenseStatus.Matched)]
    [Arguments("declared-conjunction-term-absent", "(MIT OR Apache-2.0) AND Unicode-3.0", "GPL-3.0-only", LicenseStatus.Conflict)]
    [Arguments("declared-multiple-observed", "MIT", "MIT|Apache-2.0", LicenseStatus.Matched)]
    [Arguments("declared-invalid-observed", "MIT", "Not-A-License", LicenseStatus.Matched)]
    public async Task Scan_CycloneDxEvidenceLicenses_ReconcilesWithTheDeclaredLicense(string label, string? declared, string? observed, LicenseStatus expected)
    {
        var component = ScanComponentOf(declared, observed);

        await Assert.That(component.Status).IsEqualTo(expected);
        await Assert.That(label).IsNotEmpty();
    }

    // A detection never replaces a value the publisher stated, so the reported license stays the
    // declared one wherever both exist. It supplies a value only where nothing was declared.

    [Test]
    [Arguments(null, "MIT", "MIT")]
    [Arguments("MIT", "MIT", "MIT")]
    [Arguments("MIT OR Apache-2.0", "Apache-2.0", "MIT OR Apache-2.0")]
    [Arguments("MIT", "MIT|Apache-2.0", "MIT")]
    public async Task Scan_CycloneDxEvidenceLicenses_NeverReplacesADeclaredLicense(string? declared, string observed, string expected)
    {
        var component = ScanComponentOf(declared, observed);

        await Assert.That(component.License.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task Scan_CycloneDxEvidenceLicensesWithoutDeclaration_RetainsDetectionProvenance()
    {
        var component = ScanComponentOf(null, "MIT");

        await Assert.That(component.CandidateCount).IsEqualTo(1);
        await Assert.That(component.GetCandidate(0).Evidence.SbomField).IsEqualTo(SbomLicenseField.CycloneDxEvidenceLicenses);
    }

    [Test]
    public async Task Scan_CycloneDxEvidenceLicensesAlongsideDeclaration_RetainsBothClaims()
    {
        var component = ScanComponentOf("MIT", "Apache-2.0");
        var fields = new SbomLicenseField[component.CandidateCount];
        for (var i = 0; i < component.CandidateCount; i++)
        {
            fields[i] = component.GetCandidate(i).Evidence.SbomField;
        }

        await Assert.That(fields).Contains(SbomLicenseField.CycloneDxLicenses);
        await Assert.That(fields).Contains(SbomLicenseField.CycloneDxEvidenceLicenses);
    }

    // An unstated relationship stays one unresolved observation. If each identifier stayed an
    // independent resolved claim, a later evidence source would find two of them disagreeing and the
    // component would become a conflict that no source actually reported.
    [Test]
    [Arguments("evidence")]
    [Arguments("declared")]
    public async Task Scan_CycloneDxSeveralIdentifiers_DoesNotManufactureAConflictForLaterEvidence(string placement)
    {
        var component = placement == "evidence" ? ScanComponentOf(null, "MIT|Apache-2.0") : ScanComponentOf("MIT|Apache-2.0", null);
        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Ambiguous);

        var enriched = LicenseReconciler.AddCandidate(
            component,
            LicenseCandidateFactory.Create(LicenseCandidateSource.PackageRegistry, LicenseCandidateKind.License, "MIT"u8, Spdx));

        await Assert.That(enriched.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(enriched.License.ToString()).IsEqualTo("MIT");
    }

    [Test]
    public async Task Scan_CycloneDxEvidenceWithoutLicenses_IgnoresOtherEvidenceFields()
    {
        var sbom = Encoding.UTF8.GetBytes("""
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "example",
              "version": "1.0.0",
              "purl": "pkg:golang/example@1.0.0",
              "licenses": [ { "license": { "id": "MIT" } } ],
              "evidence": {
                "identity": [ { "field": "purl", "confidence": 1 } ],
                "occurrences": [ { "location": "src/main.go" } ]
              }
            }
          ]
        }
        """);

        var component = DependencyInputScanner.Scan(sbom, Spdx).Components[0];

        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(component.License.ToString()).IsEqualTo("MIT");
    }

    private static ScanComponent ScanComponentOf(string? declared, string? observed)
    {
        var declaredJson = declared is null ? string.Empty : $""" "licenses": [ {LicenseEntries(declared)} ],""";
        var evidenceJson = observed is null ? string.Empty : $$""" "evidence": { "licenses": [ {{LicenseEntries(observed)}} ] },""";
        var sbom = Encoding.UTF8.GetBytes($$"""
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "example",
              "version": "1.0.0",{{declaredJson}}{{evidenceJson}}
              "purl": "pkg:golang/example@1.0.0"
            }
          ]
        }
        """);

        return DependencyInputScanner.Scan(sbom, Spdx).Components[0];
    }

    private static string LicenseEntries(string value)
    {
        var parts = value.Split('|');
        var entries = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            entries[i] = parts[i].Contains(' ', StringComparison.Ordinal)
                ? $$"""{ "license": { "expression": "{{parts[i]}}" } }"""
                : $$"""{ "license": { "id": "{{parts[i]}}" } }""";
        }

        return string.Join(", ", entries);
    }
}
