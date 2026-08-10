using System.Text;
using Ol.Core;
using Ol.Core.Generated;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Tests;

/// <summary>
/// Guards resolving a declared license location against the SPDX license list's own <c>seeAlso</c> field.
/// </summary>
/// <remarks>
/// A publisher that writes <c>https://www.apache.org/licenses/LICENSE-2.0</c> has written the URL SPDX
/// publishes for <c>Apache-2.0</c>, in the same document that defines the identifier, so recognizing it
/// reads published data rather than reading the page. That is what separates it from a redirector, a
/// vendor's terms page, or a repository blob: those name a place whose contents Ol has not seen.
/// </remarks>
public sealed class SpdxLicenseUrlTests
{
    private static readonly SpdxLicenseIndex Spdx = new(
        ["MIT", "Apache-2.0", "LGPL-2.1", "LGPL-2.1-or-later", "GPL-2.0"],
        [],
        ["GPL-2.0"],
        [],
        [
            "https://opensource.org/license/MIT",
            "https://www.apache.org/licenses/LICENSE-2.0",
            "https://opensource.org/license/LGPL-2.1",
            "https://opensource.org/license/LGPL-2.1",
            "https://www.gnu.org/licenses/old-licenses/gpl-2.0-standalone.html",
        ],
        ["MIT", "Apache-2.0", "LGPL-2.1", "LGPL-2.1-or-later", "GPL-2.0"]);

    [Test]
    [Arguments("https://opensource.org/license/MIT", "MIT")]
    [Arguments("http://opensource.org/license/MIT", "MIT")]
    [Arguments("https://opensource.org/license/mit", "MIT")]
    [Arguments("https://opensource.org/license/MIT/", "MIT")]
    [Arguments("https://www.apache.org/licenses/LICENSE-2.0", "Apache-2.0")]
    [Arguments("http://apache.org/licenses/LICENSE-2.0", "Apache-2.0")]
    public async Task Resolve_PublishedUrl_ReadsTheSpellingsThatNameTheSameDocument(string url, string expected)
    {
        var resolved = Spdx.TryResolveLicenseUrl(Encoding.UTF8.GetBytes(url), out var normalized, out _);

        await Assert.That(resolved).IsTrue();
        await Assert.That(normalized.ToString()).IsEqualTo(expected);
    }

    // Everything a rewrite would have to invent, rather than read, stays unresolved.
    [Test]
    [Arguments("https://opensource.org/licenses/MIT")]
    [Arguments("http://go.microsoft.com/fwlink/?LinkId=329770")]
    [Arguments("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT")]
    [Arguments("https://servicestack.net/terms")]
    [Arguments("https://opensource.org/license")]
    [Arguments("")]
    public async Task Resolve_UnpublishedUrl_ResolvesNothing(string url)
        => await Assert.That(Spdx.TryResolveLicenseUrl(Encoding.UTF8.GetBytes(url), out _, out _)).IsFalse();

    // LGPL-2.1 and LGPL-2.1-or-later are different licenses, so the page they share names neither.
    [Test]
    public async Task Resolve_UrlSharedByTwoLicenses_ResolvesNothing()
        => await Assert.That(Spdx.TryResolveLicenseUrl("https://opensource.org/license/LGPL-2.1"u8, out _, out _)).IsFalse();

    [Test]
    public async Task Resolve_DeprecatedLicense_ReportsTheDeprecation()
    {
        var resolved = Spdx.TryResolveLicenseUrl("https://www.gnu.org/licenses/old-licenses/gpl-2.0-standalone.html"u8, out var normalized, out var deprecated);

        await Assert.That(resolved).IsTrue();
        await Assert.That(normalized.ToString()).IsEqualTo("GPL-2.0");
        await Assert.That(deprecated).IsTrue();
    }

    [Test]
    public async Task Resolve_WithoutSeeAlsoData_ResolvesNothing()
        => await Assert.That(new SpdxLicenseIndex(["MIT"], []).TryResolveLicenseUrl("https://opensource.org/license/MIT"u8, out _, out _)).IsFalse();

    [Test]
    public async Task Candidate_StatingNoLicenseAndDeclaringAPublishedUrl_ResolvesAndKeepsTheDeclaration()
    {
        var candidate = LicenseCandidateFactory.ResolveDeclaredLocation(Declaring(string.Empty, "https://www.apache.org/licenses/LICENSE-2.0"), Spdx);

        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(candidate.Normalized.ToString()).IsEqualTo("Apache-2.0");
        await Assert.That(candidate.Kind).IsEqualTo(LicenseCandidateKind.Location);
        await Assert.That(candidate.Raw.IsEmpty).IsTrue();
        await Assert.That(candidate.Evidence.DeclaredReference!.Value.ToString()).IsEqualTo("https://www.apache.org/licenses/LICENSE-2.0");
    }

    // A declaration says where the license is, so it never overrides a license the publisher stated.
    [Test]
    public async Task Candidate_StatingALicense_IsNotReplacedByItsDeclaration()
    {
        var candidate = LicenseCandidateFactory.ResolveDeclaredLocation(Declaring("MIT", "https://www.apache.org/licenses/LICENSE-2.0"), Spdx);

        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(candidate.Normalized.ToString()).IsEqualTo("MIT");
        await Assert.That(candidate.Kind).IsEqualTo(LicenseCandidateKind.License);
    }

    [Test]
    public async Task Candidate_DeclaringAnArtifactPath_IsLeftUnresolved()
    {
        var candidate = LicenseCandidateFactory.ResolveDeclaredLocation(
            Declaring(string.Empty, "LICENSE-2.0.txt", DeclaredLicenseReferenceKind.ArtifactPath),
            Spdx);

        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Unknown);
        await Assert.That(candidate.Kind).IsEqualTo(LicenseCandidateKind.License);
    }

    [Test]
    public async Task Candidate_DeclaringAnUnpublishedUrl_IsLeftUnresolved()
    {
        var candidate = LicenseCandidateFactory.ResolveDeclaredLocation(Declaring(string.Empty, "http://go.microsoft.com/fwlink/?LinkId=329770"), Spdx);

        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Unknown);
        await Assert.That(candidate.Normalized.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Kind_Location_RoundTripsThroughItsPersistedToken()
    {
        await Assert.That(Encoding.UTF8.GetString(LicenseCandidateKind.Location.ToUtf8())).IsEqualTo("location");
        await Assert.That(LicenseCandidateIdentifiers.ParseKind("location"u8)).IsEqualTo(LicenseCandidateKind.Location);
    }

    // The bundled data is what a default run uses, so the arrays it ships have to stay aligned and the
    // URLs it ships have to actually resolve.
    [Test]
    public async Task BundledData_SeeAlsoUrls_AlignWithLicensesAndResolve()
    {
        await Assert.That(SpdxGeneratedLicenseData.SeeAlsoUrls.Length).IsEqualTo(SpdxGeneratedLicenseData.SeeAlsoLicenseIds.Length);
        await Assert.That(SpdxGeneratedLicenseData.SeeAlsoUrls.Length).IsGreaterThan(0);

        var index = new SpdxLicenseIndex(
            SpdxGeneratedLicenseData.LicenseIds,
            SpdxGeneratedLicenseData.ExceptionIds,
            SpdxGeneratedLicenseData.DeprecatedLicenseIds,
            SpdxGeneratedLicenseData.LicenseNames,
            SpdxGeneratedLicenseData.SeeAlsoUrls,
            SpdxGeneratedLicenseData.SeeAlsoLicenseIds);

        foreach (var (url, expected) in new[]
        {
            ("https://www.apache.org/licenses/LICENSE-2.0", "Apache-2.0"),
            ("http://www.apache.org/licenses/LICENSE-2.0", "Apache-2.0"),
            ("https://opensource.org/license/MIT", "MIT"),
        })
        {
            var resolved = index.TryResolveLicenseUrl(Encoding.UTF8.GetBytes(url), out var normalized, out _);

            await Assert.That(resolved).IsTrue().Because(url);
            await Assert.That(normalized.ToString()).IsEqualTo(expected);
        }

        // SPDX gives this page to four LGPL identifiers, so the bundled data must not resolve it either.
        await Assert.That(index.TryResolveLicenseUrl("https://www.gnu.org/licenses/old-licenses/lgpl-2.1-standalone.html"u8, out _, out _)).IsFalse();
    }

    private static LicenseCandidate Declaring(string raw, string reference, DeclaredLicenseReferenceKind kind = DeclaredLicenseReferenceKind.Location)
    {
        var candidate = LicenseCandidateFactory.Create(LicenseCandidateSource.NuGetRegistry, LicenseCandidateKind.License, Encoding.UTF8.GetBytes(raw), Spdx);
        return candidate with
        {
            Evidence = new LicenseEvidence(LicenseEvidenceKind.PackageRegistry, DeclaredReference: new(kind, Utf8Slice.FromString(reference))),
        };
    }
}
