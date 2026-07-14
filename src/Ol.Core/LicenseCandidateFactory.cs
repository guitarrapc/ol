namespace Ol.Core;

/// <summary>
/// Classifies raw license evidence into SPDX-aware candidates.
/// </summary>
public static class LicenseCandidateFactory
{
    /// <summary>
    /// Creates one classified license candidate.
    /// </summary>
    /// <param name="source">The evidence source.</param>
    /// <param name="kind">The source field or license value kind.</param>
    /// <param name="raw">The raw license value.</param>
    /// <param name="spdxLicenseIndex">The active SPDX data index.</param>
    /// <returns>The classified candidate.</returns>
    public static LicenseCandidate Create(string source, string kind, string raw, SpdxLicenseIndex spdxLicenseIndex)
    {
        var status = Classify(raw, spdxLicenseIndex, out var normalized, out var deprecated);
        return new LicenseCandidate(source, kind, Utf8Slice.FromString(raw), normalized, status, deprecated, deprecated ? ["deprecated_spdx_identifier"] : []);
    }

    /// <summary>
    /// Creates one classified license candidate from an unescaped UTF-8 JSON string value.
    /// </summary>
    /// <param name="source">The evidence source.</param>
    /// <param name="kind">The source field or license value kind.</param>
    /// <param name="rawUtf8">The unescaped UTF-8 raw license value.</param>
    /// <param name="spdxLicenseIndex">The active SPDX data index.</param>
    /// <returns>The classified candidate.</returns>
    public static LicenseCandidate Create(string source, string kind, ReadOnlySpan<byte> rawUtf8, SpdxLicenseIndex spdxLicenseIndex)
    {
        var bytes = rawUtf8.ToArray();
        return Create(source, kind, new Utf8Slice(bytes, 0, bytes.Length), spdxLicenseIndex);
    }

    /// <summary>
    /// Creates one classified license candidate from a UTF-8 slice owned by the scanned input.
    /// </summary>
    public static LicenseCandidate Create(string source, string kind, Utf8Slice raw, SpdxLicenseIndex spdxLicenseIndex)
    {
        if (IsUnknown(raw.Span))
        {
            return new LicenseCandidate(source, kind, raw, string.Empty, LicenseStatus.Unknown, false, []);
        }

        if (spdxLicenseIndex.TryNormalizeLicenseIdUtf8(raw.Span, out var normalized))
        {
            var deprecated = spdxLicenseIndex.IsDeprecatedLicenseId(normalized);
            return new LicenseCandidate(source, kind, raw, normalized, LicenseStatus.Matched, deprecated, deprecated ? ["deprecated_spdx_identifier"] : []);
        }

        var rawText = raw.ToString();
        var status = Classify(rawText, spdxLicenseIndex, out var normalizedExpression, out var deprecatedExpression);
        return new LicenseCandidate(source, kind, raw, normalizedExpression, status, deprecatedExpression, deprecatedExpression ? ["deprecated_spdx_identifier"] : []);
    }

    /// <summary>
    /// Creates an error candidate for failed external evidence collection.
    /// </summary>
    /// <param name="source">The attempted evidence source.</param>
    /// <param name="kind">The attempted evidence kind.</param>
    /// <param name="warning">The warning retained for the failure.</param>
    /// <returns>The error candidate.</returns>
    public static LicenseCandidate CreateError(string source, string kind, string warning)
        => new(source, kind, default, string.Empty, LicenseStatus.Error, false, [warning]);

    private static LicenseStatus Classify(string value, SpdxLicenseIndex spdxLicenseIndex, out string normalized, out bool deprecated)
    {
        normalized = string.Empty;
        deprecated = false;
        if (value.Length == 0
            || string.Equals(value, "NOASSERTION", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            return LicenseStatus.Unknown;
        }

        if (spdxLicenseIndex.TryNormalizeLicenseId(value, out normalized))
        {
            deprecated = spdxLicenseIndex.IsDeprecatedLicenseId(normalized);
            return LicenseStatus.Matched;
        }

        if (SpdxExpression.TryNormalize(value, spdxLicenseIndex, out normalized, out deprecated))
        {
            return LicenseStatus.Matched;
        }

        normalized = value;
        return value.Contains(" AND ", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" OR ", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" WITH ", StringComparison.OrdinalIgnoreCase)
            || value.Contains('(')
            || value.Contains(')')
            ? LicenseStatus.Invalid
            : LicenseStatus.Ambiguous;
    }

    private static bool IsUnknown(ReadOnlySpan<byte> value)
        => value.IsEmpty
        || AsciiEqualsIgnoreCase(value, "noassertion"u8)
        || AsciiEqualsIgnoreCase(value, "none"u8)
        || AsciiEqualsIgnoreCase(value, "unknown"u8);

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expectedLowercase)
    {
        if (value.Length != expectedLowercase.Length)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current is >= (byte)'A' and <= (byte)'Z')
            {
                current = (byte)(current | 0x20);
            }

            if (current != expectedLowercase[i])
            {
                return false;
            }
        }

        return true;
    }
}
