namespace Ol.Core.PackageManagers;

/// <summary>
/// Recognizes the PyPI license classifiers that name a license family without saying which license in
/// it applies.
/// </summary>
/// <remarks>
/// <para>
/// The list is PEP 639's, quoted rather than derived. Its appendix enumerates the classifiers that
/// "intend to specify a particular license, but do not specify the particular version or variant",
/// and states that tools "MUST NOT attempt to automatically infer a <c>License-Expression</c>" from
/// them. Ol treats them as it treats any value it cannot resolve; recognizing them adds no mapping and
/// resolves nothing. It only lets a report name the reason, because what a reviewer does next follows
/// from knowing the value can never resolve rather than from the status alone.
/// </para>
/// <para>
/// Deliberately only the excluded list. PEP 639 says the remaining classifiers each map to one SPDX
/// identifier, but publishes no machine-readable table for them, and reproducing one would make Ol the
/// author of a license mapping rather than a reader of published data. The "or later" forms of AGPLv3,
/// GPLv2, GPLv3 and LGPLv3 are absent on purpose: the appendix calls those unambiguous, and only
/// LGPLv2 among them ambiguous, because v2 could mean v2.0 or v2.1.
/// </para>
/// <para>
/// The comparison is exact. A classifier is a value from a closed vocabulary that PyPI validates on
/// upload, and the vocabulary is frozen — PEP 639 states new license classifiers must not be added — so
/// a value that differs at all is not one of these classifiers and must not be described as one.
/// </para>
/// </remarks>
public static class PyPiLicenseClassifier
{
    /// <summary>
    /// Reports whether a value is a PyPI license classifier that names a family rather than a license.
    /// </summary>
    /// <param name="value">The declared value, as UTF-8.</param>
    /// <returns><see langword="true"/> when PEP 639 excludes this classifier from inference.</returns>
    public static bool IsNotSpecific(ReadOnlySpan<byte> value)
    {
        // Ordered as the appendix lists them. Every entry was verified to exist verbatim in the PyPI
        // classifier vocabulary; a typo here would silently stop explaining one of them.
        if (!value.StartsWith("License :: OSI Approved :: "u8))
        {
            return false;
        }

        var family = value["License :: OSI Approved :: ".Length..];
        return family.SequenceEqual("Academic Free License (AFL)"u8)
            || family.SequenceEqual("Apache Software License"u8)
            || family.SequenceEqual("Apple Public Source License"u8)
            || family.SequenceEqual("Artistic License"u8)
            || family.SequenceEqual("BSD License"u8)
            || family.SequenceEqual("GNU Affero General Public License v3"u8)
            || family.SequenceEqual("GNU Free Documentation License (FDL)"u8)
            || family.SequenceEqual("GNU General Public License (GPL)"u8)
            || family.SequenceEqual("GNU General Public License v2 (GPLv2)"u8)
            || family.SequenceEqual("GNU General Public License v3 (GPLv3)"u8)
            || family.SequenceEqual("GNU Lesser General Public License v2 (LGPLv2)"u8)
            || family.SequenceEqual("GNU Lesser General Public License v2 or later (LGPLv2+)"u8)
            || family.SequenceEqual("GNU Lesser General Public License v3 (LGPLv3)"u8)
            || family.SequenceEqual("GNU Library or Lesser General Public License (LGPL)"u8);
    }
}
