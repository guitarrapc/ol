using System.Text;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Tests;

/// <summary>
/// Guards that a declared license location is retained wherever an ecosystem states one.
/// </summary>
/// <remarks>
/// A publisher that cannot express its license as an SPDX expression usually says where the license
/// is instead: a URL, or a path inside the published artifact. Ol could not resolve those, and it also
/// discarded them, so a report said a component was unresolved without saying that the publisher had
/// pointed somewhere. Retaining the pointer is what lets a reviewer act, and one type keeps it one
/// concept across ecosystems rather than one warning vocabulary per ecosystem.
/// </remarks>
public sealed class DeclaredLicenseReferenceTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT", "Apache-2.0"], []);

    // Equivalence classes for a CycloneDX license entry: a resolvable value with and without a URL, an
    // unresolvable name with and without a URL, and an entry naming no location at all.

    [Test]
    [Arguments("id-with-url", """{ "id": "MIT", "url": "https://example.test/LICENSE" }""", DeclaredLicenseReferenceKind.Location, "https://example.test/LICENSE")]
    [Arguments("id-without-url", """{ "id": "MIT" }""", DeclaredLicenseReferenceKind.None, "")]
    [Arguments("name-with-url", """{ "name": "Unknown - See URL", "url": "https://example.test/LICENSE.txt" }""", DeclaredLicenseReferenceKind.Location, "https://example.test/LICENSE.txt")]
    [Arguments("name-without-url", """{ "name": "Some License" }""", DeclaredLicenseReferenceKind.None, "")]
    public async Task Scan_CycloneDxLicenseUrl_RetainsTheDeclaredLocation(string label, string entry, DeclaredLicenseReferenceKind expectedKind, string expectedValue)
    {
        var component = ScanCycloneDx(entry);
        var reference = component.GetCandidate(0).Evidence.DeclaredReference;

        await Assert.That(reference?.Kind ?? DeclaredLicenseReferenceKind.None).IsEqualTo(expectedKind);
        await Assert.That(reference?.Value.ToString() ?? string.Empty).IsEqualTo(expectedValue);
        await Assert.That(label).IsNotEmpty();
    }

    // A location never becomes a license value. Retaining it must not change what a component resolves
    // to, or Ol would be concluding from a document it has not read.
    [Test]
    [Arguments("""{ "id": "MIT", "url": "https://example.test/LICENSE" }""", LicenseStatus.Matched, "MIT")]
    [Arguments("""{ "name": "Unknown - See URL", "url": "https://example.test/LICENSE" }""", LicenseStatus.Ambiguous, "Unknown - See URL (?)")]
    public async Task Scan_CycloneDxLicenseUrl_DoesNotChangeWhatTheComponentResolvesTo(string entry, LicenseStatus expectedStatus, string expectedLicense)
    {
        var component = ScanCycloneDx(entry);

        await Assert.That(component.Status).IsEqualTo(expectedStatus);
        await Assert.That(component.License.ToString()).IsEqualTo(expectedLicense);
    }

    private static ScanComponent ScanCycloneDx(string licenseEntry)
    {
        var sbom = Encoding.UTF8.GetBytes($$"""
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "components": [
            {
              "type": "library",
              "name": "example",
              "version": "1.0.0",
              "purl": "pkg:nuget/example@1.0.0",
              "licenses": [ { "license": {{licenseEntry}} } ]
            }
          ]
        }
        """);

        return DependencyInputScanner.Scan(sbom, Spdx).Components[0];
    }
}
