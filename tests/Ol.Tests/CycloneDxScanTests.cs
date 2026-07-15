using System.Text;
using System.Text.Json;
using Ol.Core;

namespace Ol.Tests;

public sealed class CycloneDxScanTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "GPL-2.0-only", "MIT"], ["Classpath-exception-2.0"]);

    [Test]
    public async Task Scan_RegisteredFormat_UsesItsOwnMarkerAndParser()
    {
        var format = new SbomFormat("test-json");
        var registry = new SbomFormatRegistry([
          new SbomFormatHandler(format, "testFormat"u8.ToArray(), "test"u8.ToArray(), static (source, _, _) => new ScanReport(new SbomFormat("test-json"), default, [])),
    ]);

        var report = SbomScanner.Scan(Encoding.UTF8.GetBytes("""{ "testFormat": "test" }"""), Spdx, registry);

        await Assert.That(report.Format).IsEqualTo(format);
    }

    [Test]
    public async Task Registry_WithDuplicatePublicInputFormat_RejectsRegistration()
    {
        var first = new SbomFormatHandler(new SbomFormat("first"), "first"u8.ToArray(), "one"u8.ToArray(), static (_, _, _) => default)
        {
            InputFormat = new ScanInputFormat("shared", "first-json", "First"),
        };
        var second = new SbomFormatHandler(new SbomFormat("second"), "second"u8.ToArray(), "two"u8.ToArray(), static (_, _, _) => default)
        {
            InputFormat = new ScanInputFormat("SHARED", "second-json", "Second"),
        };

        await Assert.That(() => new SbomFormatRegistry([first, second])).Throws<ArgumentException>();
    }

    [Test]
    public async Task TryNormalizeLicenseIdUtf8_KnownIdentifier_NormalizesWithoutInputString()
    {
        var normalized = Spdx.TryNormalizeLicenseIdUtf8("mit"u8, out var identifier);

        await Assert.That(normalized).IsTrue();
        await Assert.That(identifier).IsEqualTo("MIT");
    }

    [Test]
    public async Task ScanCycloneDxComponentWithSpdxLicenseIdReturnsMatchedComponent()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.5",
              "components": [
                {
                  "bom-ref": "pkg:npm/left-pad@1.3.0",
                  "type": "library",
                  "name": "left-pad",
                  "version": "1.3.0",
                  "purl": "pkg:npm/left-pad@1.3.0",
                  "licenses": [
                    {
                      "license": {
                        "id": "MIT"
                      }
                    }
                  ]
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Format).IsEqualTo(SbomFormat.CycloneDxJson);
        await Assert.That(report.Components.Length).IsEqualTo(1);

        var component = report.Components[0];
        await Assert.That(component.Name.ToString()).IsEqualTo("left-pad");
        await Assert.That(component.Version.ToString()).IsEqualTo("1.3.0");
        await Assert.That(component.Purl.ToString()).IsEqualTo("pkg:npm/left-pad@1.3.0");
        await Assert.That(component.Ecosystem).IsEqualTo("npm");
        await Assert.That(component.DependencyType).IsEqualTo(DependencyType.Unknown);
        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(component.License.ToString()).IsEqualTo("MIT");
        await Assert.That(component.CandidateCount).IsEqualTo(1);
        await Assert.That(component.AdditionalCandidates.Length).IsEqualTo(0);
        await Assert.That(component.GetCandidate(0).Normalized.ToString()).IsEqualTo("MIT");
    }

    [Test]
    public async Task Scan_UnescapedComponentText_RetainsUtf8SlicesUntilRendered()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                {
                  "bom-ref": "pkg:npm/example@1.0.0",
                  "name": "example",
                  "version": "1.0.0",
                  "purl": "pkg:npm/example@1.0.0",
                  "licenses": [ { "license": { "id": "MIT" } } ]
                }
              ]
            }
            """);

        var component = SbomScanner.Scan(sbom, Spdx).Components[0];

        await Assert.That(component.Name.Span.SequenceEqual("example"u8)).IsTrue();
        await Assert.That(component.Purl.Span.SequenceEqual("pkg:npm/example@1.0.0"u8)).IsTrue();
        await Assert.That(component.GetCandidate(0).Raw.Span.SequenceEqual("MIT"u8)).IsTrue();
    }

    [Test]
    public async Task Scan_PurlWithMixedCaseType_DetectsEcosystemWithoutDecoding()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                { "name": "example", "purl": "pkg:NPM/example@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] }
              ]
            }
            """);

        var component = SbomScanner.Scan(sbom, Spdx).Components[0];

        await Assert.That(component.Ecosystem).IsEqualTo("npm");
    }

    [Test]
    public async Task ScanCycloneDxComponentWithMultipleLicenseIdsReturnsAmbiguousComponent()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                {
                  "name": "dual",
                  "licenses": [
                    { "license": { "id": "MIT" } },
                    { "license": { "id": "Apache-2.0" } }
                  ]
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Components[0].Status).IsEqualTo(LicenseStatus.Ambiguous);
        await Assert.That(report.Components[0].License.ToString()).IsEqualTo("MIT, Apache-2.0 (?)");
        await Assert.That(report.Components[0].CandidateCount).IsEqualTo(2);
        await Assert.That(report.Components[0].GetCandidate(0).Raw.ToString()).IsEqualTo("MIT");
        await Assert.That(report.Components[0].GetCandidate(1).Raw.ToString()).IsEqualTo("Apache-2.0");
    }

    [Test]
    public async Task ScanCycloneDxComponentWithNoAssertionReturnsUnknownComponent()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                {
                  "name": "mystery",
                  "licenses": [
                    { "license": { "id": "NOASSERTION" } }
                  ]
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Components[0].Status).IsEqualTo(LicenseStatus.Unknown);
        await Assert.That(report.Components[0].License.ToString()).IsEqualTo("-");
    }

    [Test]
    public async Task Scan_WithUnknownLikeLicense_RetainsRawCandidateAndEvidence()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                {
                  "name": "mystery",
                  "licenses": [ { "license": { "id": "NOASSERTION" } } ]
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        var component = report.Components[0];
        await Assert.That(component.CandidateCount).IsEqualTo(1);
        await Assert.That(component.GetCandidate(0).Raw.ToString()).IsEqualTo("NOASSERTION");
        await Assert.That(component.GetCandidate(0).Status).IsEqualTo(LicenseStatus.Unknown);
    }

    [Test]
    public async Task Scan_WithDeprecatedIdentifierInsideExpression_AddsCandidateWarning()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                {
                  "name": "deprecated-expression",
                  "licenses": [ { "license": { "expression": "gpl-2.0-only OR MIT" } } ]
                }
              ]
            }
            """);
        var deprecatedSpdx = new SpdxLicenseIndex(["GPL-2.0-only", "MIT"], [], ["GPL-2.0-only"]);

        var report = SbomScanner.Scan(sbom, deprecatedSpdx);

        await Assert.That(report.Components[0].GetCandidate(0).Deprecated).IsTrue();
        await Assert.That(report.Components[0].Warnings[0]).IsEqualTo("deprecated_spdx_identifier");
    }

    [Test]
    public async Task Scan_WithCycloneDxExpression_NormalizesSpdxExpression()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                {
                  "name": "licensed",
                  "licenses": [
                    { "license": { "expression": "mit OR (apache-2.0 WITH classpath-exception-2.0)" } }
                  ]
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Components[0].Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(report.Components[0].License.ToString()).IsEqualTo("MIT OR (Apache-2.0 WITH Classpath-exception-2.0)");
    }

    [Test]
    public async Task Scan_WithUnknownIdentifierInCycloneDxExpression_ReturnsInvalidComponent()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                {
                  "name": "invalid",
                  "licenses": [
                    { "license": { "expression": "MIT OR Not-A-License" } }
                  ]
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Components[0].Status).IsEqualTo(LicenseStatus.Invalid);
        await Assert.That(report.Components[0].License.ToString()).IsEqualTo("MIT OR Not-A-License (?)");
    }

    [Test]
    public async Task Scan_WithNaturalLanguageCycloneDxLicenseName_ReturnsAmbiguousComponent()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "components": [
                {
                  "name": "ambiguous",
                  "licenses": [
                    { "license": { "name": "Apache License" } }
                  ]
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Components[0].Status).IsEqualTo(LicenseStatus.Ambiguous);
        await Assert.That(report.Components[0].License.ToString()).IsEqualTo("Apache License (?)");
    }

    [Test]
    public async Task Scan_WithCycloneDxDependencyGraph_ClassifiesRootDirectAndTransitiveComponents()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "metadata": {
                "component": {
                  "bom-ref": "pkg:npm/app@1.0.0",
                  "name": "app",
                  "version": "1.0.0",
                  "purl": "pkg:npm/app@1.0.0",
                  "licenses": [ { "license": { "id": "MIT" } } ]
                }
              },
              "components": [
                {
                  "bom-ref": "pkg:npm/direct@1.0.0",
                  "name": "direct",
                  "version": "1.0.0",
                  "purl": "pkg:npm/direct@1.0.0",
                  "licenses": [ { "license": { "id": "MIT" } } ]
                },
                {
                  "bom-ref": "pkg:npm/transitive@1.0.0",
                  "name": "transitive",
                  "version": "1.0.0",
                  "purl": "pkg:npm/transitive@1.0.0",
                  "licenses": [ { "license": { "id": "MIT" } } ]
                }
              ],
              "dependencies": [
                { "ref": "pkg:npm/app@1.0.0", "dependsOn": [ "pkg:npm/direct@1.0.0" ] },
                { "ref": "pkg:npm/direct@1.0.0", "dependsOn": [ "pkg:npm/transitive@1.0.0" ] }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Components.Length).IsEqualTo(3);
        await Assert.That(report.Components[0].Name.ToString()).IsEqualTo("app");
        await Assert.That(report.Components[0].DependencyType).IsEqualTo(DependencyType.Root);
        await Assert.That(report.Components[1].Name.ToString()).IsEqualTo("direct");
        await Assert.That(report.Components[1].DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(report.Components[2].Name.ToString()).IsEqualTo("transitive");
        await Assert.That(report.Components[2].DependencyType).IsEqualTo(DependencyType.Transitive);
    }

    [Test]
    public async Task Scan_WithAmbiguousFormatMarkers_ThrowsUnsupportedFormatError()
    {
        var sbom = Encoding.UTF8.GetBytes(
          """
          {
            "bomFormat": "CycloneDX",
            "spdxVersion": "SPDX-2.3",
            "components": [],
            "packages": []
          }
          """);

        await Assert.That(() => SbomScanner.Scan(sbom, Spdx)).Throws<JsonException>();
    }
}

public sealed class SpdxScanTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "GPL-2.0-only", "MIT"], ["Classpath-exception-2.0"]);

    [Test]
    public async Task ScanSpdxPackageWithMatchingDeclaredAndConcludedReturnsMatchedComponent()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                {
                  "SPDXID": "SPDXRef-Package-left-pad",
                  "name": "left-pad",
                  "versionInfo": "1.3.0",
                  "licenseDeclared": "mit",
                  "licenseConcluded": "MIT",
                  "externalRefs": [
                    {
                      "referenceCategory": "PACKAGE-MANAGER",
                      "referenceType": "purl",
                      "referenceLocator": "pkg:npm/left-pad@1.3.0"
                    }
                  ]
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Format).IsEqualTo(SbomFormat.SpdxJson);
        await Assert.That(report.Components.Length).IsEqualTo(1);

        var component = report.Components[0];
        await Assert.That(component.Name.ToString()).IsEqualTo("left-pad");
        await Assert.That(component.Version.ToString()).IsEqualTo("1.3.0");
        await Assert.That(component.Purl.ToString()).IsEqualTo("pkg:npm/left-pad@1.3.0");
        await Assert.That(component.Ecosystem).IsEqualTo("npm");
        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(component.License.ToString()).IsEqualTo("MIT");
    }

    [Test]
    public async Task ScanSpdxPackageWithDifferentDeclaredAndConcludedReturnsConflictComponent()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                {
                  "name": "conflicted",
                  "licenseDeclared": "MIT",
                  "licenseConcluded": "Apache-2.0"
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Components[0].Status).IsEqualTo(LicenseStatus.Conflict);
        await Assert.That(report.Components[0].License.ToString()).IsEqualTo("MIT, Apache-2.0 (?)");
    }

    [Test]
    public async Task Scan_WithInvalidSpdxExpressionAndNoValidEvidence_ReturnsInvalidComponent()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                {
                  "SPDXID": "SPDXRef-Package-invalid",
                  "name": "invalid",
                  "licenseDeclared": "MIT OR Not-A-License",
                  "licenseConcluded": "NOASSERTION"
                }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Components[0].Status).IsEqualTo(LicenseStatus.Invalid);
        await Assert.That(report.Components[0].License.ToString()).IsEqualTo("MIT OR Not-A-License (?)");
    }

    [Test]
    public async Task Scan_WithSpdxRelationships_ClassifiesRootDirectAndTransitivePackages()
    {
        var sbom = Encoding.UTF8.GetBytes(
            """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-App", "name": "app", "licenseDeclared": "MIT" },
                { "SPDXID": "SPDXRef-Direct", "name": "direct", "licenseDeclared": "MIT" },
                { "SPDXID": "SPDXRef-Transitive", "name": "transitive", "licenseDeclared": "MIT" }
              ],
              "relationships": [
                { "spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES", "relatedSpdxElement": "SPDXRef-App" },
                { "spdxElementId": "SPDXRef-App", "relationshipType": "DEPENDS_ON", "relatedSpdxElement": "SPDXRef-Direct" },
                { "spdxElementId": "SPDXRef-Direct", "relationshipType": "DEPENDS_ON", "relatedSpdxElement": "SPDXRef-Transitive" }
              ]
            }
            """);

        var report = SbomScanner.Scan(sbom, Spdx);

        await Assert.That(report.Components[0].DependencyType).IsEqualTo(DependencyType.Root);
        await Assert.That(report.Components[1].DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(report.Components[2].DependencyType).IsEqualTo(DependencyType.Transitive);
    }
}
