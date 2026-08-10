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
    private readonly PurlPrefixSet? excludedPackages;
    private readonly SpdxLicenseIndex spdxLicenseIndex;

    private LicenseAllowPolicy(
        FrozenSet<string> allowedLicenses,
        FrozenSet<string>? developmentUnionLicenses,
        PurlPrefixSet? excludedPackages,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        this.allowedLicenses = allowedLicenses;
        this.developmentUnionLicenses = developmentUnionLicenses;
        this.excludedPackages = excludedPackages;
        this.spdxLicenseIndex = spdxLicenseIndex;
    }

    /// <summary>Gets the normalized exclusion prefixes in the order they were supplied.</summary>
    public ReadOnlySpan<string> ExclusionPrefixes => excludedPackages is null ? [] : excludedPackages.Prefixes;

    /// <summary>Creates an immutable allow-list from SPDX License Identifiers.</summary>
    public static bool TryCreate(ReadOnlySpan<string> licenseIds, SpdxLicenseIndex spdxLicenseIndex, out LicenseAllowPolicy policy, out string error)
        => TryCreate(licenseIds, [], [], spdxLicenseIndex, out policy, out error);

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
        => TryCreate(licenseIds, developmentLicenseIds, [], spdxLicenseIndex, out policy, out error);

    /// <summary>
    /// Creates an immutable allow-list, an optional development allow-list, and an optional set of package URL prefixes
    /// whose components are not evaluated at all. Exclusion states which components the caller takes outside this policy's
    /// scope; it is not a license decision, so an excluded component is neither evaluated nor acknowledgeable.
    /// </summary>
    public static bool TryCreate(
        ReadOnlySpan<string> licenseIds,
        ReadOnlySpan<string> developmentLicenseIds,
        ReadOnlySpan<string> excludedPackagePrefixes,
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

        if (!PurlPrefixSet.TryCreate(excludedPackagePrefixes, out var excluded, out error))
        {
            return false;
        }

        policy = new LicenseAllowPolicy(normalized.ToFrozenSet(StringComparer.Ordinal), developmentUnion, excluded, spdxLicenseIndex);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Counts how many non-root components each exclusion prefix removed, attributing every excluded component to the
    /// first prefix that matches it so the counts sum to the evaluated exclusion total. This exists for diagnostics such
    /// as verbose output, which is why it iterates separately instead of adding work to <see cref="Evaluate"/>.
    /// </summary>
    public void CountExclusionMatches(ReadOnlySpan<ScanComponent> components, Span<int> matchCounts)
    {
        matchCounts.Clear();
        if (excludedPackages is not { } prefixes) return;

        for (var i = 0; i < components.Length; i++)
        {
            if (components[i].DependencyType == DependencyType.Root) continue;

            var match = prefixes.Match(components[i].Purl);
            if ((uint)match < (uint)matchCounts.Length) matchCounts[match]++;
        }
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

        // An excluded component is outside policy scope, so a baseline snapshot must not record it either.
        if (excludedPackages is { } prefixes && prefixes.Contains(component.Purl))
        {
            return false;
        }

        if (component.Status is not (LicenseStatus.Unknown or LicenseStatus.Ambiguous or LicenseStatus.Conflict or LicenseStatus.Invalid))
        {
            return false;
        }

        // A component the allow-list already admits on every reading is not a violation, so a baseline has
        // nothing to acknowledge and a snapshot must not carry an entry that reviews a decided question.
        if (component.Status == LicenseStatus.Ambiguous && IsAllowedOnEveryReading(component))
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
        => Evaluate(components, default, null, out _, out _, out _, out _);

    /// <summary>
    /// Evaluates every non-root completed component, removing violations for unresolved components the baseline
    /// acknowledges. Acknowledgement removes a violation only; component status and evidence are unchanged.
    /// </summary>
    public LicensePolicyViolation[] Evaluate(ReadOnlySpan<ScanComponent> components, LicenseBaseline? baseline, out int acknowledgedCount)
        => Evaluate(components, default, baseline, out acknowledgedCount, out _, out _, out _);

    /// <summary>
    /// Evaluates every non-root completed component and returns both acknowledged and evaluated component counts.
    /// </summary>
    public LicensePolicyViolation[] Evaluate(
        ReadOnlySpan<ScanComponent> components,
        LicenseBaseline? baseline,
        out int acknowledgedCount,
        out int evaluatedCount)
        => Evaluate(components, default, baseline, out acknowledgedCount, out evaluatedCount, out _, out _);

    /// <summary>
    /// Evaluates every non-root completed component with development usage, without reporting the excluded count.
    /// </summary>
    public LicensePolicyViolation[] Evaluate(
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<DependencyUsage> componentUsages,
        LicenseBaseline? baseline,
        out int acknowledgedCount,
        out int evaluatedCount,
        out int[] developmentAllowedComponents)
        => Evaluate(components, componentUsages, baseline, out acknowledgedCount, out evaluatedCount, out developmentAllowedComponents, out _);

    /// <summary>
    /// Evaluates every non-root completed component, allowing a development-only component whose license satisfies the
    /// development allow-list even when the primary allow-list rejects it. <paramref name="componentUsages"/> is indexed
    /// by component; entries beyond its length, and every component when no development allow-list was supplied, follow
    /// the primary allow-list unchanged. <paramref name="excludedCount"/> reports how many components the exclusion
    /// prefixes removed from evaluation, so a caller can make the reduced scope visible.
    /// </summary>
    public LicensePolicyViolation[] Evaluate(
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<DependencyUsage> componentUsages,
        LicenseBaseline? baseline,
        out int acknowledgedCount,
        out int evaluatedCount,
        out int[] developmentAllowedComponents,
        out int excludedCount)
        => Evaluate(components, componentUsages, baseline, out acknowledgedCount, out evaluatedCount, out developmentAllowedComponents, out excludedCount, out _);

    /// <summary>
    /// Evaluates every non-root completed component, additionally reporting through
    /// <paramref name="ambiguityAllowedCount"/> how many ambiguous components the allow-list admits on every reading
    /// of their evidence. See <see cref="IsAllowedOnEveryReading"/> for what that decides and what it leaves alone.
    /// </summary>
    public LicensePolicyViolation[] Evaluate(
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<DependencyUsage> componentUsages,
        LicenseBaseline? baseline,
        out int acknowledgedCount,
        out int evaluatedCount,
        out int[] developmentAllowedComponents,
        out int excludedCount,
        out int ambiguityAllowedCount)
    {
        ambiguityAllowedCount = 0;
        acknowledgedCount = 0;
        evaluatedCount = 0;
        excludedCount = 0;
        developmentAllowedComponents = [];
        if (components.IsEmpty) return [];

        var violations = ArrayPool<LicensePolicyViolation>.Shared.Rent(components.Length);
        var violationCount = 0;
        // Hoisted so a run without exclusions performs neither a field load nor a call per component.
        var exclusions = excludedPackages;
        // Development allowances are collected only when a development allow-list exists, so a run without the option
        // rents nothing extra. The indices identify components the caller reports separately from violations.
        var developmentAllowed = developmentUnionLicenses is null ? null : ArrayPool<int>.Shared.Rent(components.Length);
        var developmentAllowedCount = 0;
        try
        {
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component.DependencyType == DependencyType.Root)
                {
                    continue;
                }

                // Exclusion is a scope statement by the caller, so it is applied before any license question is asked.
                if (exclusions is not null && exclusions.Contains(component.Purl))
                {
                    excludedCount++;
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

                    if (developmentAllowed is not null
                        && (uint)i < (uint)componentUsages.Length
                        && componentUsages[i] == DependencyUsage.Development
                        && SpdxExpression.TryEvaluatePolicy(component.License.Span, spdxLicenseIndex, developmentUnionLicenses!, out var developmentSatisfied)
                        && developmentSatisfied)
                    {
                        developmentAllowed[developmentAllowedCount++] = i;
                        continue;
                    }

                    kind = LicensePolicyViolationKind.NotAllowed;
                }
                else
                {
                    if (component.Status == LicenseStatus.Ambiguous && IsAllowedOnEveryReading(component))
                    {
                        ambiguityAllowedCount++;
                        continue;
                    }

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

            if (developmentAllowedCount != 0)
            {
                developmentAllowedComponents = developmentAllowed!.AsSpan(0, developmentAllowedCount).ToArray();
            }

            return violationCount == 0 ? [] : violations.AsSpan(0, violationCount).ToArray();
        }
        finally
        {
            ArrayPool<LicensePolicyViolation>.Shared.Return(violations);
            if (developmentAllowed is not null) ArrayPool<int>.Shared.Return(developmentAllowed);
        }
    }

    /// <summary>
    /// Determines whether an ambiguous component is allowed whichever way its evidence is read.
    /// </summary>
    /// <remarks>
    /// A registry that lists the licenses it found without stating how they relate leaves exactly one thing
    /// unknown: the operator between them. Ol keeps that visible by joining the values with <c>;</c> instead of
    /// an SPDX operator — see <c>DepsDevMetadata.ReadLicenses</c> — which is why the value does not normalize and
    /// the component stays ambiguous. The missing operator is also the only thing the policy question needs: a
    /// listing whose every element the allow-list admits is admitted as a conjunction and as a disjunction alike,
    /// so no reading of that evidence violates the policy and there is nothing left to decide. Anything that is
    /// not such a listing — a license name, a URL, a classifier — names possibilities Ol cannot enumerate, and
    /// stays a violation.
    /// Every candidate that carries a value must clear the same bar, so a second source naming a forbidden license
    /// still fails the component. This answers the policy question only: status, license text, and evidence are
    /// left as collected, exactly as a baseline acknowledgement leaves them.
    /// </remarks>
    private bool IsAllowedOnEveryReading(in ScanComponent component)
    {
        var stated = false;
        var candidateCount = component.CandidateCount;
        for (var i = 0; i < candidateCount; i++)
        {
            var normalized = component.GetCandidate(i).Normalized;
            if (normalized.IsEmpty) continue;

            if (!IsAllowedListing(normalized.Span)) return false;

            stated = true;
        }

        return stated;
    }

    /// <summary>Reports whether every <c>;</c>-separated element is an SPDX expression the allow-list admits.</summary>
    /// <remarks>
    /// <c>;</c> is not an SPDX operator, so a value carrying one is never a single expression and the split is
    /// unambiguous. A value without one is evaluated whole, which an ambiguous candidate always fails — had it
    /// parsed, the candidate would have been matched instead.
    /// </remarks>
    private bool IsAllowedListing(ReadOnlySpan<byte> value)
    {
        while (true)
        {
            var separator = value.IndexOf((byte)';');
            var element = separator < 0 ? value : value[..separator];
            if (!SpdxExpression.TryEvaluatePolicy(element, spdxLicenseIndex, allowedLicenses, out var allowed) || !allowed)
            {
                return false;
            }

            if (separator < 0) return true;
            value = value[(separator + 1)..];
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
