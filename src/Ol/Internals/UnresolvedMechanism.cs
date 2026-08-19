using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;

namespace Ol.Internals;

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
    /// <summary>The tally label for a violated component whose evidence names no mechanism at all.</summary>
    internal const string NoneLabel = "no mechanism reported";

    /// <summary>Selects the one mechanism that best explains an unresolved component.</summary>
    /// <remarks>
    /// A component can carry several warnings at once, and listing all of them restates plumbing instead of naming
    /// the next action. A component with no mechanism at all yields an empty value: the scan report omits its row
    /// rather than repeating its status, while <c>check</c> still prints the row its violation requires.
    /// <see cref="SelectReason"/> holds the order, which is a reported contract rather than an implementation detail.
    /// </remarks>
    internal static bool TryGetReason(in ScanComponent component, out ReadOnlySpan<byte> reason)
    {
        reason = SelectReason(CollectEvidence(component));
        return !reason.IsEmpty;
    }

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
        var evidence = default(Evidence);
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
    private static ReadOnlySpan<byte> SelectReason(Evidence evidence)
    {
        // Collection that never ran, or a registry that answered "no such package", settles the component: no later
        // mechanism can explain more than the fact that there was nothing to explain.
        if (evidence.Has(LicenseCandidateWarnings.ExternalEvidenceNotCollected)) return "external_evidence_not_collected"u8;
        if (evidence.Has(LicenseCandidateWarnings.PackageMetadataNotFound)) return "package_metadata_not_found"u8;

        // A document that certainly answers the question outranks any outcome about where Ol looked.
        if (evidence.DeclaredFile) return "declared_license_file_not_collected"u8;
        if (evidence.DeclaredText) return "declared_license_text_not_collected"u8;

        // A document Ol did read but could not classify still points at something to open.
        if (evidence.Has(LicenseCandidateWarnings.SourceLicenseNotRecognized)) return "license_not_recognized"u8;
        if (evidence.Has(LicenseCandidateWarnings.SourceLicenseNotDetected)) return "license_not_detected"u8;

        // A URL may lead anywhere, so it ranks below a named document; a family classifier names no place at all.
        if (evidence.DeclaredLocation) return "declared_license_location_not_collected"u8;
        if (evidence.FamilyClassifier) return "license_classifier_not_specific"u8;

        // A component no registry could be asked about also has no repository, because nothing produced one. Naming
        // the repository would report the consequence and send the reader hunting for something never sought.
        if (evidence.Has(LicenseCandidateWarnings.PackageMetadataUnversionedPurl)) return "package_metadata_unversioned_purl"u8;
        if (evidence.Has(LicenseCandidateWarnings.UnsupportedPackageMetadata)) return "unsupported_package_metadata"u8;

        // Last come the outcomes that describe only where Ol looked, ending with the two that a later run may change.
        if (evidence.Has(LicenseCandidateWarnings.UnsupportedSourceRepository)) return "unsupported_source_repository"u8;
        if (evidence.Has(LicenseCandidateWarnings.SourceRepositorySubdirectory)) return "source_repository_subdirectory"u8;
        if (evidence.Has(LicenseCandidateWarnings.SourceRepositoryUnavailable)) return "source_repository_unavailable"u8;
        if (evidence.Has(LicenseCandidateWarnings.SourceRepositoryFetchFailed)) return "source_repository_fetch_failed"u8;
        if (evidence.Has(LicenseCandidateWarnings.PackageMetadataFetchFailed)) return "package_metadata_fetch_failed"u8;

        return default;
    }

    /// <summary>The facts about one component that decide which unresolved mechanism is reported.</summary>
    private struct Evidence
    {
        public LicenseCandidateWarnings Warnings;
        public bool DeclaredFile;
        public bool DeclaredText;
        public bool DeclaredLocation;
        public bool FamilyClassifier;

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
    internal static string GetReference(in ScanComponent component, ReadOnlySpan<byte> reason)
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

        var recognized = reason.SequenceEqual("license_not_recognized"u8);
        if (!recognized && !reason.SequenceEqual("unsupported_source_repository"u8))
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
