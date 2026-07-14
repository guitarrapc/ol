using System.Buffers;

namespace Ol.Core;

/// <summary>
/// Reconciles license candidates from all available evidence sources.
/// </summary>
public static class LicenseReconciler
{
    /// <summary>
    /// Adds a candidate and recalculates the component's display license, status, evidence, and warnings.
    /// </summary>
    /// <param name="component">The component to enrich.</param>
    /// <param name="candidate">The additional candidate.</param>
    /// <returns>The reconciled component.</returns>
    public static ScanComponent AddCandidate(ScanComponent component, LicenseCandidate candidate)
    {
        var candidates = new LicenseCandidate[component.LicenseCandidates.Length + 1];
        component.LicenseCandidates.CopyTo(candidates, 0);
        candidates[^1] = candidate;
        return Reconcile(component, candidates);
    }

    /// <summary>
    /// Reconciles all candidates for a component.
    /// </summary>
    /// <param name="component">The component to reconcile.</param>
    /// <param name="candidates">The complete candidate set.</param>
    /// <returns>The reconciled component.</returns>
    public static ScanComponent Reconcile(ScanComponent component, LicenseCandidate[] candidates)
    {
        var matched = ArrayPool<string>.Shared.Rent(candidates.Length);
        LicenseCandidate? invalid = null;
        LicenseCandidate? ambiguous = null;
        var hasError = false;
        var matchedCount = 0;
        var warningCapacity = 0;
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            warningCapacity += candidate.Warnings.Length;
            switch (candidate.Status)
            {
                case LicenseStatus.Matched:
                    var duplicate = false;
                    for (var matchedIndex = 0; matchedIndex < matchedCount; matchedIndex++)
                    {
                        if (string.Equals(matched[matchedIndex], candidate.Normalized, StringComparison.Ordinal))
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
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

        var warningValues = warningCapacity == 0 ? null : ArrayPool<string>.Shared.Rent(warningCapacity);
        try
        {
            var (license, status) = matchedCount switch
            {
                1 => (matched[0], LicenseStatus.Matched),
                > 1 => (string.Concat(string.Join(", ", matched, 0, matchedCount), " (?)"), LicenseStatus.Conflict),
                _ when invalid is { } value => (string.Concat(value.Raw, " (?)"), LicenseStatus.Invalid),
                _ when ambiguous is { } value => (string.Concat(value.Raw, " (?)"), LicenseStatus.Ambiguous),
                _ when hasError => ("-", LicenseStatus.Error),
                _ => ("-", LicenseStatus.Unknown),
            };

            var evidence = new LicenseEvidence[candidates.Length];
            var warningCount = 0;
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                evidence[i] = new LicenseEvidence(candidate.Source, candidate.Kind, candidate.Raw, candidate.Normalized, candidate.Status, candidate.Warnings);
                for (var warningIndex = 0; warningIndex < candidate.Warnings.Length; warningIndex++)
                {
                    var warning = candidate.Warnings[warningIndex];
                    var duplicate = false;
                    for (var existingIndex = 0; existingIndex < warningCount; existingIndex++)
                    {
                        if (string.Equals(warningValues![existingIndex], warning, StringComparison.Ordinal))
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                    {
                        warningValues![warningCount] = warning;
                        warningCount++;
                    }
                }
            }

            var warnings = warningCount == 0 ? [] : warningValues!.AsSpan(0, warningCount).ToArray();
            Array.Sort(warnings, StringComparer.Ordinal);
            return component with { License = license, Status = status, LicenseCandidates = candidates, Evidence = evidence, Warnings = warnings };
        }
        finally
        {
            Array.Clear(matched, 0, matchedCount);
            ArrayPool<string>.Shared.Return(matched);
            if (warningValues is not null)
            {
                Array.Clear(warningValues, 0, warningCapacity);
                ArrayPool<string>.Shared.Return(warningValues);
            }
        }
    }
}
