namespace Ol.Core.PackageManagers;

/// <summary>
/// Recognizes the PyPI license classifiers that name a license family without saying which license in
/// it applies.
/// </summary>
/// <remarks>
/// The license family classifier contract in spdx.md. The set is PEP 639's appendix quoted rather than derived, so
/// nothing here maps a classifier to an identifier; the "or later" forms of AGPLv3, GPLv2, GPLv3 and LGPLv3 are
/// absent because the appendix calls those unambiguous. Matching is exact: the vocabulary is closed and frozen.
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
