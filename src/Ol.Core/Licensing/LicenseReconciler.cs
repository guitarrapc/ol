using System.Buffers;

namespace Ol.Core;

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
        if (component.PrimaryCandidate.Source is null)
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
        var warningCapacity = 0;
        for (var i = 0; i < component.CandidateCount; i++)
        {
            var candidate = component.GetCandidate(i);
            warningCapacity += candidate.Warnings.Length;
            switch (candidate.Status)
            {
                case LicenseStatus.Matched:
                    var duplicate = false;
                    for (var matchedIndex = 0; matchedIndex < matchedCount; matchedIndex++)
                    {
                        if (matched[matchedIndex].Equals(candidate.Normalized))
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
                > 1 => (LicenseText.Conflict(matched[0], matched[1]), LicenseStatus.Conflict),
                _ when invalid is { } value => (LicenseText.WithUncertainty(value.Raw), LicenseStatus.Invalid),
                _ when ambiguous is { } value => (LicenseText.WithUncertainty(value.Raw), LicenseStatus.Ambiguous),
                _ when hasError => (default(Utf8Slice), LicenseStatus.Error),
                _ => (default(Utf8Slice), LicenseStatus.Unknown),
            };

            var warningCount = 0;
            for (var i = 0; i < component.CandidateCount; i++)
            {
                var candidate = component.GetCandidate(i);
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
            return component with { License = license, Status = status, Warnings = warnings };
        }
        finally
        {
            Array.Clear(matched, 0, matchedCount);
            ArrayPool<Utf8Slice>.Shared.Return(matched);
            if (warningValues is not null)
            {
                Array.Clear(warningValues, 0, warningCapacity);
                ArrayPool<string>.Shared.Return(warningValues);
            }
        }
    }
}
