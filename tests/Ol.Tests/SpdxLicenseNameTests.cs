using System.Text;
using Ol.Core;
using Ol.Core.Generated;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Tests;

/// <summary>
/// Guards resolving a declared value against the SPDX license list's own <c>name</c> field.
/// </summary>
/// <remarks>
/// A publisher that writes <c>MIT License</c> has written the name SPDX gives <c>MIT</c>, in the same
/// document that defines the identifier. Resolving it reads published data rather than guessing at a
/// spelling, which is what separates it from <c>Apache 2.0</c> or a PyPI Trove classifier: those
/// resemble a license name without being one, and a family without a version cannot be resolved at all.
/// </remarks>
public sealed class SpdxLicenseNameTests
{
    private static readonly SpdxLicenseIndex Spdx = new(
        ["MIT", "Apache-2.0", "BSD-3-Clause", "GPL-2.0", "GPL-2.0-only", "Unicode-3.0"],
        ["LLVM-exception"],
        ["GPL-2.0"],
        ["MIT License", "Apache License 2.0", "BSD 3-Clause \"New\" or \"Revised\" License", "GNU General Public License v2.0 only", "GNU General Public License v2.0 only", "Unicode License v3"]);

    [Test]
    [Arguments("MIT License", "MIT")]
    [Arguments("mit license", "MIT")]
    [Arguments("Apache License 2.0", "Apache-2.0")]
    [Arguments("Unicode License v3", "Unicode-3.0")]
    public async Task Classify_SpdxLicenseName_ResolvesToItsIdentifier(string raw, string expected)
    {
        var candidate = Classify(raw);

        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(candidate.Normalized.ToString()).IsEqualTo(expected);
        await Assert.That(candidate.Raw.ToString()).IsEqualTo(raw);
        await Assert.That(candidate.Deprecated).IsFalse();
    }

    // A name is not an expression even when it reads like one. Checking the name first is what keeps
    // `BSD 3-Clause "New" or "Revised" License` from being parsed as a disjunction and rejected.
    [Test]
    public async Task Classify_SpdxNameContainingAnOperatorWord_ResolvesInsteadOfFailingAsAnExpression()
    {
        var candidate = Classify("BSD 3-Clause \"New\" or \"Revised\" License");

        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(candidate.Normalized.ToString()).IsEqualTo("BSD-3-Clause");
    }

    // Every name SPDX shares between two identifiers is a deprecated identifier and its replacement,
    // which are the same license. The replacement is the answer; the deprecated spelling is not.
    [Test]
    public async Task Classify_NameSharedByADeprecatedIdentifier_ResolvesToTheReplacement()
    {
        var candidate = Classify("GNU General Public License v2.0 only");

        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(candidate.Normalized.ToString()).IsEqualTo("GPL-2.0-only");
        await Assert.That(candidate.Deprecated).IsFalse();
    }

    // Values that resemble a license name without being one. None of them names a version, so none can
    // be resolved without inventing the part the publisher left out.
    [Test]
    [Arguments("Apache 2.0")]
    [Arguments("Modified BSD License")]
    [Arguments("MIT license text")]
    [Arguments("Dual License")]
    [Arguments("License :: OSI Approved :: BSD License")]
    [Arguments("License :: OSI Approved :: MIT License")]
    public async Task Classify_ValueThatIsNotAnSpdxName_StaysAmbiguous(string raw)
    {
        var candidate = Classify(raw);

        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Ambiguous);
    }

    // Names are resolved for a whole declared value only. Inside an expression an operand is an
    // identifier, and admitting names there would make `MIT License OR Apache-2.0` depend on whether a
    // name happens to contain an operator word.
    [Test]
    [Arguments("MIT License OR Apache-2.0")]
    [Arguments("Apache License 2.0 AND MIT")]
    public async Task Classify_SpdxNameUsedAsAnExpressionOperand_IsNotResolved(string raw)
    {
        var candidate = Classify(raw);

        await Assert.That(candidate.Status).IsNotEqualTo(LicenseStatus.Matched);
    }

    [Test]
    public async Task Index_WithoutNames_ResolvesIdentifiersOnly()
    {
        var index = new SpdxLicenseIndex(["MIT"], []);

        await Assert.That(index.TryNormalizeLicenseIdUtf8Slice("MIT"u8, out _)).IsTrue();
        await Assert.That(index.TryNormalizeLicenseNameUtf8Slice("MIT License"u8, out _, out _)).IsFalse();
    }

    // The bundled data is what a default run uses, so the arrays it ships have to stay aligned and the
    // names it ships have to actually resolve.
    [Test]
    public async Task BundledData_LicenseNames_AlignWithIdentifiersAndResolve()
    {
        await Assert.That(SpdxGeneratedLicenseData.LicenseNames.Length).IsEqualTo(SpdxGeneratedLicenseData.LicenseIds.Length);

        var index = new SpdxLicenseIndex(
            SpdxGeneratedLicenseData.LicenseIds,
            SpdxGeneratedLicenseData.ExceptionIds,
            SpdxGeneratedLicenseData.DeprecatedLicenseIds,
            SpdxGeneratedLicenseData.LicenseNames);

        foreach (var (name, expected) in new[] { ("MIT License", "MIT"), ("Apache License 2.0", "Apache-2.0"), ("ISC License", "ISC"), ("The Unlicense", "Unlicense") })
        {
            var resolved = index.TryNormalizeLicenseNameUtf8Slice(Encoding.UTF8.GetBytes(name), out var normalized, out _);

            await Assert.That(resolved).IsTrue();
            await Assert.That(normalized.ToString()).IsEqualTo(expected);
        }
    }

    private static LicenseCandidate Classify(string raw)
        => LicenseCandidateFactory.Create(
            LicenseCandidateSource.PackageRegistry,
            LicenseCandidateKind.License,
            Encoding.UTF8.GetBytes(raw),
            Spdx);
}
