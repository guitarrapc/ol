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
        var matched = new List<string>();
        LicenseCandidate? invalid = null;
        LicenseCandidate? ambiguous = null;
        var hasError = false;
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            switch (candidate.Status)
            {
                case LicenseStatus.Matched:
                    if (!matched.Contains(candidate.Normalized, StringComparer.Ordinal))
                    {
                        matched.Add(candidate.Normalized);
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

        var (license, status) = matched.Count switch
        {
            1 => (matched[0], LicenseStatus.Matched),
            > 1 => (string.Concat(string.Join(", ", matched), " (?)"), LicenseStatus.Conflict),
            _ when invalid is { } value => (string.Concat(value.Raw, " (?)"), LicenseStatus.Invalid),
            _ when ambiguous is { } value => (string.Concat(value.Raw, " (?)"), LicenseStatus.Ambiguous),
            _ when hasError => ("-", LicenseStatus.Error),
            _ => ("-", LicenseStatus.Unknown),
        };

        var evidence = new LicenseEvidence[candidates.Length];
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            evidence[i] = new LicenseEvidence(candidate.Source, candidate.Kind, candidate.Raw, candidate.Normalized, candidate.Status, candidate.Warnings);
            for (var warningIndex = 0; warningIndex < candidate.Warnings.Length; warningIndex++)
            {
                warnings.Add(candidate.Warnings[warningIndex]);
            }
        }

        return component with { License = license, Status = status, LicenseCandidates = candidates, Evidence = evidence, Warnings = [.. warnings.Order(StringComparer.Ordinal)] };
    }
}
