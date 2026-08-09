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
    /// Combines two valid expressions when neither withdraws what the other states.
    /// </summary>
    /// <param name="kept">The expression that drops no option and no obligation either source stated.</param>
    /// <returns><see langword="true"/> when the two do not disagree.</returns>
    /// <remarks>
    /// The relation is defined by <see cref="SpdxExpressionRelation.IsAccountedFor"/>; this decides only
    /// which of the two survives. The one that accounts for the other is kept, because narrowing to the
    /// observation would drop a choice nothing withdrew, and widening past the statement would drop a
    /// term the publisher required. Equal expressions keep the one already recorded, so the reported
    /// spelling depends on candidate order alone, which is deterministic.
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

        if (SpdxExpressionRelation.IsAccountedFor(candidateSpan, existingSpan))
        {
            kept = existing;
            return true;
        }

        if (SpdxExpressionRelation.IsAccountedFor(existingSpan, candidateSpan))
        {
            kept = candidate;
            return true;
        }

        kept = default;
        return false;
    }
}
