using System.Buffers;
using System.Collections.Frozen;
using Ol.Core.Spdx;

namespace Ol.Core.Licensing;

/// <summary>Identifies why one component violates an allow-list policy.</summary>
public enum LicensePolicyViolationKind : byte
{
    NotAllowed,
    Conflict,
    Unknown,
    Ambiguous,
    Invalid,
    Error,
}

/// <summary>Locates one policy violation in the completed component array.</summary>
/// <param name="ComponentIndex">The index of the violating component.</param>
/// <param name="Kind">The violation reason.</param>
public readonly record struct LicensePolicyViolation(int ComponentIndex, LicensePolicyViolationKind Kind);

/// <summary>Evaluates completed scan components against normalized SPDX license identifiers.</summary>
public sealed class LicenseAllowPolicy
{
    private readonly FrozenSet<string> allowedLicenses;
    private readonly FrozenSet<string>? developmentUnionLicenses;
    private readonly SpdxLicenseIndex spdxLicenseIndex;

    private LicenseAllowPolicy(FrozenSet<string> allowedLicenses, FrozenSet<string>? developmentUnionLicenses, SpdxLicenseIndex spdxLicenseIndex)
    {
        this.allowedLicenses = allowedLicenses;
        this.developmentUnionLicenses = developmentUnionLicenses;
        this.spdxLicenseIndex = spdxLicenseIndex;
    }

    /// <summary>Creates an immutable allow-list from SPDX License Identifiers.</summary>
    public static bool TryCreate(ReadOnlySpan<string> licenseIds, SpdxLicenseIndex spdxLicenseIndex, out LicenseAllowPolicy policy, out string error)
        => TryCreate(licenseIds, [], spdxLicenseIndex, out policy, out error);

    /// <summary>
    /// Creates an immutable allow-list plus an optional development allow-list. The development identifiers are held as
    /// their union with the primary allow-list, built once here, so a development-only component is re-evaluated against
    /// <c>primary ∪ development</c> without allocating per component. An empty development list carries no development policy.
    /// </summary>
    public static bool TryCreate(
        ReadOnlySpan<string> licenseIds,
        ReadOnlySpan<string> developmentLicenseIds,
        SpdxLicenseIndex spdxLicenseIndex,
        out LicenseAllowPolicy policy,
        out string error)
    {
        policy = null!;
        if (licenseIds.IsEmpty)
        {
            error = "The allow-list must contain at least one SPDX License Identifier.";
            return false;
        }

        if (!TryNormalize(licenseIds, spdxLicenseIndex, "Allow-list entries must not be empty.", out var normalized, out error))
        {
            return false;
        }

        FrozenSet<string>? developmentUnion = null;
        if (!developmentLicenseIds.IsEmpty)
        {
            var union = new HashSet<string>(normalized, StringComparer.Ordinal);
            for (var i = 0; i < developmentLicenseIds.Length; i++)
            {
                var value = TrimAsciiWhitespace(developmentLicenseIds[i].AsSpan());
                if (value.IsEmpty)
                {
                    error = "Development allow-list entries must not be empty.";
                    return false;
                }

                if (!spdxLicenseIndex.TryNormalizeLicenseId(value, out var identifier))
                {
                    error = $"Unknown SPDX License Identifier: {Display(value)}";
                    return false;
                }

                union.Add(identifier);
            }

            developmentUnion = union.ToFrozenSet(StringComparer.Ordinal);
        }

        policy = new LicenseAllowPolicy(normalized.ToFrozenSet(StringComparer.Ordinal), developmentUnion, spdxLicenseIndex);
        error = string.Empty;
        return true;
    }

    private static bool TryNormalize(ReadOnlySpan<string> licenseIds, SpdxLicenseIndex spdxLicenseIndex, string emptyEntryError, out HashSet<string> normalized, out string error)
    {
        normalized = new HashSet<string>(licenseIds.Length, StringComparer.Ordinal);
        for (var i = 0; i < licenseIds.Length; i++)
        {
            var value = TrimAsciiWhitespace(licenseIds[i].AsSpan());
            if (value.IsEmpty)
            {
                error = emptyEntryError;
                return false;
            }

            if (!spdxLicenseIndex.TryNormalizeLicenseId(value, out var identifier))
            {
                error = $"Unknown SPDX License Identifier: {Display(value)}";
                return false;
            }

            normalized.Add(identifier);
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Determines whether an unresolved component may be acknowledged by a baseline.
    /// </summary>
    /// <remarks>
    /// Status <c>error</c> is excluded because a collection failure is a condition to repair rather than a
    /// policy question, and status <c>matched</c> because a resolved license belongs in the allow-list.
    /// A component is also excluded when any candidate normalizes to a rejected expression, which is what
    /// keeps a forbidden license from being deferred through a conflict. This is evaluated whenever a
    /// baseline is applied, not only when one is written, so tightening the allow-list invalidates entries
    /// a more permissive list had accepted.
    /// </remarks>
    public bool CanAcknowledge(in ScanComponent component)
    {
        if (component.DependencyType == DependencyType.Root)
        {
            return false;
        }

        if (component.Status is not (LicenseStatus.Unknown or LicenseStatus.Ambiguous or LicenseStatus.Conflict or LicenseStatus.Invalid))
        {
            return false;
        }

        var candidateCount = component.CandidateCount;
        for (var i = 0; i < candidateCount; i++)
        {
            var normalized = component.GetCandidate(i).Normalized;
            if (normalized.IsEmpty) continue;
            if (SpdxExpression.TryEvaluatePolicy(normalized.Span, spdxLicenseIndex, allowedLicenses, out var allowed) && !allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Evaluates every non-root completed component and returns all violations in component order.</summary>
    public LicensePolicyViolation[] Evaluate(ReadOnlySpan<ScanComponent> components)
        => Evaluate(components, default, null, out _, out _, out _);

    /// <summary>
    /// Evaluates every non-root completed component, removing violations for unresolved components the baseline
    /// acknowledges. Acknowledgement removes a violation only; component status and evidence are unchanged.
    /// </summary>
    public LicensePolicyViolation[] Evaluate(ReadOnlySpan<ScanComponent> components, LicenseBaseline? baseline, out int acknowledgedCount)
        => Evaluate(components, default, baseline, out acknowledgedCount, out _, out _);

    /// <summary>
    /// Evaluates every non-root completed component and returns both acknowledged and evaluated component counts.
    /// </summary>
    public LicensePolicyViolation[] Evaluate(
        ReadOnlySpan<ScanComponent> components,
        LicenseBaseline? baseline,
        out int acknowledgedCount,
        out int evaluatedCount)
        => Evaluate(components, default, baseline, out acknowledgedCount, out evaluatedCount, out _);

    /// <summary>
    /// Evaluates every non-root completed component, allowing a development-only component whose license satisfies the
    /// development allow-list even when the primary allow-list rejects it. <paramref name="componentUsages"/> is indexed
    /// by component; entries beyond its length, and every component when no development allow-list was supplied, follow
    /// the primary allow-list unchanged.
    /// </summary>
    public LicensePolicyViolation[] Evaluate(
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<DependencyUsage> componentUsages,
        LicenseBaseline? baseline,
        out int acknowledgedCount,
        out int evaluatedCount,
        out int developmentAllowedCount)
    {
        acknowledgedCount = 0;
        evaluatedCount = 0;
        developmentAllowedCount = 0;
        if (components.IsEmpty) return [];

        var violations = ArrayPool<LicensePolicyViolation>.Shared.Rent(components.Length);
        var violationCount = 0;
        try
        {
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component.DependencyType == DependencyType.Root)
                {
                    continue;
                }

                evaluatedCount++;
                LicensePolicyViolationKind kind;
                if (component.Status == LicenseStatus.Matched)
                {
                    if (SpdxExpression.TryEvaluatePolicy(component.License.Span, spdxLicenseIndex, allowedLicenses, out var allowed) && allowed)
                    {
                        continue;
                    }

                    if (developmentUnionLicenses is not null
                        && (uint)i < (uint)componentUsages.Length
                        && componentUsages[i] == DependencyUsage.Development
                        && SpdxExpression.TryEvaluatePolicy(component.License.Span, spdxLicenseIndex, developmentUnionLicenses, out var developmentAllowed)
                        && developmentAllowed)
                    {
                        developmentAllowedCount++;
                        continue;
                    }

                    kind = LicensePolicyViolationKind.NotAllowed;
                }
                else
                {
                    kind = component.Status switch
                    {
                        LicenseStatus.Conflict => LicensePolicyViolationKind.Conflict,
                        LicenseStatus.Unknown => LicensePolicyViolationKind.Unknown,
                        LicenseStatus.Ambiguous => LicensePolicyViolationKind.Ambiguous,
                        LicenseStatus.Invalid => LicensePolicyViolationKind.Invalid,
                        LicenseStatus.Error => LicensePolicyViolationKind.Error,
                        _ => LicensePolicyViolationKind.Error,
                    };
                }

                if (baseline is not null && CanAcknowledge(component) && baseline.IsAcknowledged(component))
                {
                    acknowledgedCount++;
                    continue;
                }

                violations[violationCount++] = new LicensePolicyViolation(i, kind);
            }

            return violationCount == 0 ? [] : violations.AsSpan(0, violationCount).ToArray();
        }
        finally
        {
            ArrayPool<LicensePolicyViolation>.Shared.Return(violations);
        }
    }

    private static ReadOnlySpan<char> TrimAsciiWhitespace(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && value[start] is ' ' or '\t' or '\r' or '\n') start++;
        var end = value.Length;
        while (end > start && value[end - 1] is ' ' or '\t' or '\r' or '\n') end--;
        return value[start..end];
    }

    private static string Display(ReadOnlySpan<char> value)
        => value.Length <= 128 ? value.ToString() : string.Concat(value[..128], "...");
}
