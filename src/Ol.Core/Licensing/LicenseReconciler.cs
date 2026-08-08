using System.Buffers;
using Ol.Core.Spdx;

namespace Ol.Core.Licensing;

/// <summary>
/// Reconciles license candidates from all available evidence sources.
/// </summary>
public static class LicenseReconciler
{
    /// <summary>
    /// Adds a candidate and recalculates the component's display license, status, and warnings.
    /// </summary>
    /// <param name="component">The component to enrich.</param>
    /// <param name="candidate">The additional candidate.</param>
    /// <returns>The reconciled component.</returns>
    public static ScanComponent AddCandidate(ScanComponent component, LicenseCandidate candidate)
    {
        if (component.PrimaryCandidate.Source == LicenseCandidateSource.None)
        {
            return Reconcile(component with { PrimaryCandidate = candidate });
        }

        var additional = new LicenseCandidate[component.AdditionalCandidates.Length + 1];
        component.AdditionalCandidates.CopyTo(additional, 0);
        additional[^1] = candidate;
        return Reconcile(component with { AdditionalCandidates = additional });
    }

    /// <summary>
    /// Reconciles all candidates for a component.
    /// </summary>
    /// <param name="component">The component to reconcile.</param>
    /// <returns>The reconciled component.</returns>
    public static ScanComponent Reconcile(ScanComponent component)
    {
        var matched = ArrayPool<Utf8Slice>.Shared.Rent(component.CandidateCount);
        LicenseCandidate? invalid = null;
        LicenseCandidate? ambiguous = null;
        var hasError = false;
        var matchedCount = 0;
        var candidateWarnings = LicenseCandidateWarnings.None;
        try
        {
            for (var i = 0; i < component.CandidateCount; i++)
            {
                var candidate = component.GetCandidate(i);
                candidateWarnings |= candidate.Warnings;
                switch (candidate.Status)
                {
                    case LicenseStatus.Matched:
                        var combined = false;
                        for (var matchedIndex = 0; matchedIndex < matchedCount; matchedIndex++)
                        {
                            if (!TryCombine(matched[matchedIndex], candidate.Normalized, out var kept))
                            {
                                continue;
                            }

                            matched[matchedIndex] = kept;
                            combined = true;
                            break;
                        }

                        if (!combined)
                        {
                            matched[matchedCount] = candidate.Normalized;
                            matchedCount++;
                        }

                        break;
                    case LicenseStatus.Invalid:
                        invalid ??= candidate;
                        break;
                    case LicenseStatus.Ambiguous:
                        ambiguous ??= candidate;
                        break;
                    case LicenseStatus.Error:
                        hasError = true;
                        break;
                }
            }

            var (license, status) = matchedCount switch
            {
                1 => (matched[0], LicenseStatus.Matched),
                > 1 => (LicenseText.Conflict(matched[0], matched[1]), LicenseStatus.Conflict),
                _ when invalid is { } value => (LicenseText.WithUncertainty(value.Raw), LicenseStatus.Invalid),
                _ when ambiguous is { } value => (LicenseText.WithUncertainty(value.Raw), LicenseStatus.Ambiguous),
                _ when hasError => (default(Utf8Slice), LicenseStatus.Error),
                _ => (default(Utf8Slice), LicenseStatus.Unknown),
            };

            return component with { License = license, Status = status, Warnings = candidateWarnings.ToStrings() };
        }
        finally
        {
            Array.Clear(matched, 0, matchedCount);
            ArrayPool<Utf8Slice>.Shared.Return(matched);
        }
    }

    /// <summary>
    /// Combines two valid expressions when one states a choice that the other satisfies.
    /// </summary>
    /// <param name="kept">The expression that keeps every option both sources leave available.</param>
    /// <returns><see langword="true"/> when the two do not disagree.</returns>
    /// <remarks>
    /// <para>
    /// A disjunction is an offer, not a claim that every option applies at once. Repository license
    /// detection answers with the one file it found at the repository root, so it names a single option
    /// out of several by construction. Reading that as disagreement would make every dual-licensed
    /// package a conflict, which is the ordinary case in some ecosystems, and would leave a scan that
    /// collected more evidence worse off than one that collected none.
    /// </para>
    /// <para>
    /// The result keeps the wider offer rather than the narrower observation. Nothing withdrew the
    /// other options, and an allow-list that permits only one of them still passes, whereas narrowing
    /// to the observed option would reject it.
    /// </para>
    /// </remarks>
    private static bool TryCombine(Utf8Slice existing, Utf8Slice candidate, out Utf8Slice kept)
    {
        var existingSpan = existing.Span;
        var candidateSpan = candidate.Span;
        if (existingSpan.SequenceEqual(candidateSpan))
        {
            kept = existing;
            return true;
        }

        // Equal sets spelled in a different order satisfy each other, and keeping the one already
        // recorded makes the reported spelling depend on candidate order alone, which is deterministic.
        if (SpdxDisjunctSet.IsSubsetOf(candidateSpan, existingSpan))
        {
            kept = existing;
            return true;
        }

        if (SpdxDisjunctSet.IsSubsetOf(existingSpan, candidateSpan))
        {
            kept = candidate;
            return true;
        }

        kept = default;
        return false;
    }
}
