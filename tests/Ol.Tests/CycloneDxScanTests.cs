using System.Text;
using Ol.Core;
using TUnit.Assertions;
using TUnit.Core;

namespace Ol.Tests;

public sealed class CycloneDxScanTests
{
  private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "MIT"], ["Classpath-exception-2.0"]);

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
        await Assert.That(component.Name).IsEqualTo("left-pad");
        await Assert.That(component.Version).IsEqualTo("1.3.0");
        await Assert.That(component.Purl).IsEqualTo("pkg:npm/left-pad@1.3.0");
        await Assert.That(component.Ecosystem).IsEqualTo("npm");
        await Assert.That(component.DependencyType).IsEqualTo(DependencyType.Unknown);
        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(component.License).IsEqualTo("MIT");
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
        await Assert.That(report.Components[0].License).IsEqualTo("MIT, Apache-2.0 (?)");
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
        await Assert.That(report.Components[0].License).IsEqualTo("-");
    }
}

public sealed class SpdxScanTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "MIT"], []);

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
        await Assert.That(component.Name).IsEqualTo("left-pad");
        await Assert.That(component.Version).IsEqualTo("1.3.0");
        await Assert.That(component.Purl).IsEqualTo("pkg:npm/left-pad@1.3.0");
        await Assert.That(component.Ecosystem).IsEqualTo("npm");
        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(component.License).IsEqualTo("MIT");
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
        await Assert.That(report.Components[0].License).IsEqualTo("MIT, Apache-2.0 (?)");
    }
}
