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
    private readonly SpdxLicenseIndex spdxLicenseIndex;

    private LicenseAllowPolicy(FrozenSet<string> allowedLicenses, SpdxLicenseIndex spdxLicenseIndex)
    {
        this.allowedLicenses = allowedLicenses;
        this.spdxLicenseIndex = spdxLicenseIndex;
    }

    /// <summary>Creates an immutable allow-list from SPDX License Identifiers.</summary>
    public static bool TryCreate(ReadOnlySpan<string> licenseIds, SpdxLicenseIndex spdxLicenseIndex, out LicenseAllowPolicy policy, out string error)
    {
        if (licenseIds.IsEmpty)
        {
            policy = null!;
            error = "The allow-list must contain at least one SPDX License Identifier.";
            return false;
        }

        var normalized = new HashSet<string>(licenseIds.Length, StringComparer.Ordinal);
        for (var i = 0; i < licenseIds.Length; i++)
        {
            var value = TrimAsciiWhitespace(licenseIds[i].AsSpan());
            if (value.IsEmpty)
            {
                policy = null!;
                error = "Allow-list entries must not be empty.";
                return false;
            }

            if (!spdxLicenseIndex.TryNormalizeLicenseId(value, out var identifier))
            {
                policy = null!;
                error = $"Unknown SPDX License Identifier: {Display(value)}";
                return false;
            }

            normalized.Add(identifier);
        }

        policy = new LicenseAllowPolicy(normalized.ToFrozenSet(StringComparer.Ordinal), spdxLicenseIndex);
        error = string.Empty;
        return true;
    }

    /// <summary>Evaluates every completed component and returns all violations in component order.</summary>
    public LicensePolicyViolation[] Evaluate(ReadOnlySpan<ScanComponent> components)
    {
        if (components.IsEmpty) return [];

        var violations = ArrayPool<LicensePolicyViolation>.Shared.Rent(components.Length);
        var violationCount = 0;
        try
        {
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                LicensePolicyViolationKind kind;
                if (component.Status == LicenseStatus.Matched)
                {
                    if (SpdxExpression.TryEvaluatePolicy(component.License.Span, spdxLicenseIndex, allowedLicenses, out var allowed) && allowed)
                    {
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
