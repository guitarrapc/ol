using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;

namespace Ol.Internals;

/// <summary>Identifies the bounded unresolved-mechanism vocabulary without allocating its display name.</summary>
internal enum UnresolvedMechanismKind : byte
{
    None,
    ExternalEvidenceNotCollected,
    PackageMetadataNotFound,
    DeclaredLicenseFileNotCollected,
    DeclaredLicenseTextNotCollected,
    LicenseNotRecognized,
    LicenseNotDetected,
    DeclaredLicenseLocationNotCollected,
    LicenseClassifierNotSpecific,
    PackageMetadataNoPurl,
    PackageMetadataUnversionedPurl,
    UnsupportedPackageMetadata,
    UnsupportedSourceRepository,
    SourceRepositorySubdirectory,
    SourceRepositoryUnavailable,
    SourceRepositoryFetchFailed,
    PackageMetadataFetchFailed,
}

/// <summary>
/// Names the one mechanism that left a component's license unresolved, and the place evidence pointed at.
/// </summary>
/// <remarks>
/// A status alone does not tell a reviewer what to do next; the mechanism does: wait for Ol to gain a
/// capability, open a document, or ask the publisher. Both the scan report's unresolved section and the
/// <c>check</c> violation table answer that question about the same components, so one vocabulary and one
/// ranking serve both. A fact carried by only one projection of a run is a fact the reader of the other
/// never gets, and <c>check</c> is the projection a CI job actually reads.
/// </remarks>
internal static class UnresolvedMechanism
{
    /// <summary>Selects the one mechanism that best explains an unresolved component.</summary>
    /// <remarks>
    /// A component can carry several warnings at once, and listing all of them restates plumbing instead of naming
    /// the next action. A component with no mechanism at all yields an empty value: the scan report omits its row
    /// rather than repeating its status, while <c>check</c> still prints the row its violation requires.
    /// <see cref="SelectReason"/> holds the order, which is a reported contract rather than an implementation detail.
    /// </remarks>
    internal static bool TryGetReason(in ScanComponent component, out UnresolvedMechanismKind reason)
    {
        reason = SelectReason(CollectEvidence(component));
        return reason != UnresolvedMechanismKind.None;
    }

    /// <summary>Returns the stable UTF-8 identifier for one mechanism.</summary>
    internal static ReadOnlySpan<byte> GetNameUtf8(UnresolvedMechanismKind reason) => reason switch
    {
        UnresolvedMechanismKind.ExternalEvidenceNotCollected => "external_evidence_not_collected"u8,
        UnresolvedMechanismKind.PackageMetadataNotFound => "package_metadata_not_found"u8,
        UnresolvedMechanismKind.DeclaredLicenseFileNotCollected => "declared_license_file_not_collected"u8,
        UnresolvedMechanismKind.DeclaredLicenseTextNotCollected => "declared_license_text_not_collected"u8,
        UnresolvedMechanismKind.LicenseNotRecognized => "license_not_recognized"u8,
        UnresolvedMechanismKind.LicenseNotDetected => "license_not_detected"u8,
        UnresolvedMechanismKind.DeclaredLicenseLocationNotCollected => "declared_license_location_not_collected"u8,
        UnresolvedMechanismKind.LicenseClassifierNotSpecific => "license_classifier_not_specific"u8,
        UnresolvedMechanismKind.PackageMetadataNoPurl => "package_metadata_no_purl"u8,
        UnresolvedMechanismKind.PackageMetadataUnversionedPurl => "package_metadata_unversioned_purl"u8,
        UnresolvedMechanismKind.UnsupportedPackageMetadata => "unsupported_package_metadata"u8,
        UnresolvedMechanismKind.UnsupportedSourceRepository => "unsupported_source_repository"u8,
        UnresolvedMechanismKind.SourceRepositorySubdirectory => "source_repository_subdirectory"u8,
        UnresolvedMechanismKind.SourceRepositoryUnavailable => "source_repository_unavailable"u8,
        UnresolvedMechanismKind.SourceRepositoryFetchFailed => "source_repository_fetch_failed"u8,
        UnresolvedMechanismKind.PackageMetadataFetchFailed => "package_metadata_fetch_failed"u8,
        _ => "no mechanism reported"u8,
    };

    /// <summary>
    /// Reduces a component's candidates to the facts the ranking asks about.
    /// </summary>
    /// <remarks>
    /// Declared references and the family classifier are derived here rather than recorded by each provider, because
    /// what a reviewer does next follows from the kind of place a publisher named and from nothing else. Several
    /// sources can each declare a different kind for one component, so the flags accumulate and the ranking picks the
    /// strongest present rather than whichever source spoke first.
    /// </remarks>
    private static Evidence CollectEvidence(in ScanComponent component)
    {
        // Derived rather than recorded, because the report already carries it in typed form: a warning
        // restating a field would be the same mistake the three retired NuGet license warnings were. It
        // also makes the mechanism independent of whether the run collected anything, which the two
        // failures it replaces were not — collection invented a repository outcome for it, and
        // --no-external-evidence left it with nothing said at all.
        var evidence = new Evidence { NoPurl = component.Purl.IsEmpty };
        for (var i = 0; i < component.CandidateCount; i++)
        {
            var candidate = component.GetCandidate(i);
            evidence.Warnings |= candidate.Warnings;
            switch (candidate.Evidence.DeclaredReference?.Kind)
            {
                case DeclaredLicenseReferenceKind.ArtifactPath: evidence.DeclaredFile = true; break;
                case DeclaredLicenseReferenceKind.InlineText: evidence.DeclaredText = true; break;
                case DeclaredLicenseReferenceKind.Location: evidence.DeclaredLocation = true; break;
            }

            evidence.FamilyClassifier |= candidate.Status == LicenseStatus.Ambiguous && PyPiLicenseClassifier.IsNotSpecific(candidate.Raw.Span);
        }

        return evidence;
    }

    /// <summary>Ranks the mechanisms from the most specific and actionable to the most general.</summary>
    private static UnresolvedMechanismKind SelectReason(Evidence evidence)
    {
        // Collection that never ran, or a registry that answered "no such package", settles the component: no later
        // mechanism can explain more than the fact that there was nothing to explain.
        if (evidence.Has(LicenseCandidateWarnings.ExternalEvidenceNotCollected)) return UnresolvedMechanismKind.ExternalEvidenceNotCollected;
        if (evidence.Has(LicenseCandidateWarnings.PackageMetadataNotFound)) return UnresolvedMechanismKind.PackageMetadataNotFound;

        // A document that certainly answers the question outranks any outcome about where Ol looked.
        if (evidence.DeclaredFile) return UnresolvedMechanismKind.DeclaredLicenseFileNotCollected;
        if (evidence.DeclaredText) return UnresolvedMechanismKind.DeclaredLicenseTextNotCollected;

        // A document Ol did read but could not classify still points at something to open.
        if (evidence.Has(LicenseCandidateWarnings.SourceLicenseNotRecognized)) return UnresolvedMechanismKind.LicenseNotRecognized;
        if (evidence.Has(LicenseCandidateWarnings.SourceLicenseNotDetected)) return UnresolvedMechanismKind.LicenseNotDetected;

        // A URL may lead anywhere, so it ranks below a named document; a family classifier names no place at all.
        if (evidence.DeclaredLocation) return UnresolvedMechanismKind.DeclaredLicenseLocationNotCollected;
        if (evidence.FamilyClassifier) return UnresolvedMechanismKind.LicenseClassifierNotSpecific;

        // A component no registry could be asked about also has no repository, because nothing produced one. Naming
        // the repository would report the consequence and send the reader hunting for something never sought.
        // No identity at all comes first in this family: the other two have a purl that could not be used, while
        // this one has nothing to use, and no evidence any source could add would change that.
        if (evidence.NoPurl) return UnresolvedMechanismKind.PackageMetadataNoPurl;
        if (evidence.Has(LicenseCandidateWarnings.PackageMetadataUnversionedPurl)) return UnresolvedMechanismKind.PackageMetadataUnversionedPurl;
        if (evidence.Has(LicenseCandidateWarnings.UnsupportedPackageMetadata)) return UnresolvedMechanismKind.UnsupportedPackageMetadata;

        // Last come the outcomes that describe only where Ol looked, ending with the two that a later run may change.
        if (evidence.Has(LicenseCandidateWarnings.UnsupportedSourceRepository)) return UnresolvedMechanismKind.UnsupportedSourceRepository;
        if (evidence.Has(LicenseCandidateWarnings.SourceRepositorySubdirectory)) return UnresolvedMechanismKind.SourceRepositorySubdirectory;
        if (evidence.Has(LicenseCandidateWarnings.SourceRepositoryUnavailable)) return UnresolvedMechanismKind.SourceRepositoryUnavailable;
        if (evidence.Has(LicenseCandidateWarnings.SourceRepositoryFetchFailed)) return UnresolvedMechanismKind.SourceRepositoryFetchFailed;
        if (evidence.Has(LicenseCandidateWarnings.PackageMetadataFetchFailed)) return UnresolvedMechanismKind.PackageMetadataFetchFailed;

        return UnresolvedMechanismKind.None;
    }

    /// <summary>The facts about one component that decide which unresolved mechanism is reported.</summary>
    private struct Evidence
    {
        public LicenseCandidateWarnings Warnings;
        public bool DeclaredFile;
        public bool DeclaredText;
        public bool DeclaredLocation;
        public bool FamilyClassifier;
        public bool NoPurl;

        public readonly bool Has(LicenseCandidateWarnings warning) => (Warnings & warning) != 0;
    }

    /// <summary>Returns the location Ol observed for this reason, or an empty value.</summary>
    /// <remarks>
    /// Only the two mechanisms whose whole point is an unread document supply one: a repository license
    /// file GitHub could not identify, and a repository URL Ol cannot collect from. It is tied to the
    /// selected reason rather than to any candidate, because a homepage printed beside an unread license
    /// file would read as the place that file can be found. Ol never constructs a URL evidence did not
    /// supply, so a package whose license text is inside its own artifact shows no reference.
    /// </remarks>
    internal static string GetReference(in ScanComponent component, UnresolvedMechanismKind reason)
    {
        // A location the publisher declared outranks anything Ol inferred, because it is the place the
        // publisher said the license is rather than a place Ol happened to look. Embedded text names no
        // place at all and is retained with an empty value by design, so it is skipped rather than
        // returned: reporting it would print a blank reference and hide the one a later source states.
        for (var i = 0; i < component.CandidateCount; i++)
        {
            if (component.GetCandidate(i).Evidence.DeclaredReference is { Value.IsEmpty: false } declared)
            {
                return declared.Value.ToString();
            }
        }

        var recognized = reason == UnresolvedMechanismKind.LicenseNotRecognized;
        if (!recognized && reason != UnresolvedMechanismKind.UnsupportedSourceRepository)
        {
            return string.Empty;
        }

        for (var i = 0; i < component.CandidateCount; i++)
        {
            var candidate = component.GetCandidate(i);
            if (recognized)
            {
                if ((candidate.Warnings & LicenseCandidateWarnings.SourceLicenseNotRecognized) != 0
                    && candidate.Evidence.SourceRepository is { LicenseUrl.Length: > 0 } evidence)
                {
                    return evidence.LicenseUrl;
                }
            }
            else if ((candidate.Warnings & LicenseCandidateWarnings.UnsupportedSourceRepository) != 0 && !candidate.Raw.IsEmpty)
            {
                return candidate.Raw.ToString();
            }
        }

        return string.Empty;
    }
}
