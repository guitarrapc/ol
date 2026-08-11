using System.Text;
using Ol.Core.PackageManagers;

namespace Ol.Tests;

/// <summary>
/// Guards the list of PyPI license classifiers that name a license family without saying which
/// license in it applies.
/// </summary>
/// <remarks>
/// The list is PEP 639's, not Ol's. That appendix enumerates the classifiers a tool "MUST NOT attempt
/// to automatically infer a License-Expression" from, because they intend a particular license but
/// state neither its version nor its variant. Recognizing them adds no mapping and resolves nothing;
/// it only lets a report say why a value cannot be resolved instead of repeating its status.
/// </remarks>
public sealed class PyPiLicenseClassifierTests
{
    [Test]
    [Arguments("License :: OSI Approved :: Academic Free License (AFL)")]
    [Arguments("License :: OSI Approved :: Apache Software License")]
    [Arguments("License :: OSI Approved :: Apple Public Source License")]
    [Arguments("License :: OSI Approved :: Artistic License")]
    [Arguments("License :: OSI Approved :: BSD License")]
    [Arguments("License :: OSI Approved :: GNU Affero General Public License v3")]
    [Arguments("License :: OSI Approved :: GNU Free Documentation License (FDL)")]
    [Arguments("License :: OSI Approved :: GNU General Public License (GPL)")]
    [Arguments("License :: OSI Approved :: GNU General Public License v2 (GPLv2)")]
    [Arguments("License :: OSI Approved :: GNU General Public License v3 (GPLv3)")]
    [Arguments("License :: OSI Approved :: GNU Lesser General Public License v2 (LGPLv2)")]
    [Arguments("License :: OSI Approved :: GNU Lesser General Public License v2 or later (LGPLv2+)")]
    [Arguments("License :: OSI Approved :: GNU Lesser General Public License v3 (LGPLv3)")]
    [Arguments("License :: OSI Approved :: GNU Library or Lesser General Public License (LGPL)")]
    public async Task IsNotSpecific_ClassifierPep639Excludes_IsRecognized(string classifier)
    {
        await Assert.That(PyPiLicenseClassifier.IsNotSpecific(Encoding.UTF8.GetBytes(classifier))).IsTrue();
    }

    // PEP 639 lists the "or later" forms of AGPLv3, GPLv2, GPLv3 and LGPLv3 as unambiguous, and only
    // LGPLv2 among them as ambiguous, because v2 could mean v2.0 or v2.1. Claiming the others are not
    // specific would be as wrong as resolving the ones that are not.
    [Test]
    [Arguments("License :: OSI Approved :: GNU Affero General Public License v3 or later (AGPLv3+)")]
    [Arguments("License :: OSI Approved :: GNU General Public License v2 or later (GPLv2+)")]
    [Arguments("License :: OSI Approved :: GNU General Public License v3 or later (GPLv3+)")]
    [Arguments("License :: OSI Approved :: GNU Lesser General Public License v3 or later (LGPLv3+)")]
    [Arguments("License :: OSI Approved :: MIT License")]
    [Arguments("License :: OSI Approved :: Mozilla Public License 2.0 (MPL 2.0)")]
    public async Task IsNotSpecific_ClassifierNamingOneLicense_IsNotRecognized(string classifier)
    {
        await Assert.That(PyPiLicenseClassifier.IsNotSpecific(Encoding.UTF8.GetBytes(classifier))).IsFalse();
    }

    // The check is over a closed machine-generated vocabulary, so it matches the classifier exactly.
    // A value that merely contains or resembles one is not a classifier PyPI would have accepted.
    [Test]
    [Arguments("")]
    [Arguments("BSD License")]
    [Arguments("Apache 2.0")]
    [Arguments("Modified BSD License")]
    [Arguments("Dual License")]
    [Arguments("license :: osi approved :: bsd license")]
    [Arguments("License :: OSI Approved :: BSD License extra")]
    [Arguments("License :: OSI Approved")]
    [Arguments("License :: Other/Proprietary License")]
    public async Task IsNotSpecific_ValueThatIsNotOneOfThoseClassifiers_IsNotRecognized(string value)
    {
        await Assert.That(PyPiLicenseClassifier.IsNotSpecific(Encoding.UTF8.GetBytes(value))).IsFalse();
    }
}
