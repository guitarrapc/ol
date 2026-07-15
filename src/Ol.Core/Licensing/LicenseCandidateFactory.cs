namespace Ol.Core;

/// <summary>
/// Classifies raw license evidence into SPDX-aware candidates.
/// </summary>
public static class LicenseCandidateFactory
{
    /// <summary>
    /// Creates one classified license candidate from an unescaped UTF-8 JSON string value.
    /// </summary>
    /// <param name="source">The evidence source.</param>
    /// <param name="kind">The source field or license value kind.</param>
    /// <param name="rawUtf8">The unescaped UTF-8 raw license value.</param>
    /// <param name="spdxLicenseIndex">The active SPDX data index.</param>
    /// <param name="evidence">Typed provenance that substantiates the candidate.</param>
    /// <returns>The classified candidate.</returns>
    public static LicenseCandidate Create(string source, string kind, ReadOnlySpan<byte> rawUtf8, SpdxLicenseIndex spdxLicenseIndex, LicenseEvidence evidence = default)
    {
        var bytes = rawUtf8.ToArray();
        return Create(source, kind, new Utf8Slice(bytes, 0, bytes.Length), spdxLicenseIndex, evidence);
    }

    /// <summary>
    /// Creates one classified license candidate from a UTF-8 slice owned by the scanned input.
    /// </summary>
    /// <param name="source">The evidence source.</param>
    /// <param name="kind">The source field or license value kind.</param>
    /// <param name="raw">The source-backed raw license value.</param>
    /// <param name="spdxLicenseIndex">The active SPDX data index.</param>
    /// <param name="evidence">Typed provenance that substantiates the candidate.</param>
    /// <returns>The classified candidate.</returns>
    public static LicenseCandidate Create(string source, string kind, Utf8Slice raw, SpdxLicenseIndex spdxLicenseIndex, LicenseEvidence evidence = default)
    {
        var status = Classify(raw.Span, spdxLicenseIndex, out var normalized, out var deprecated);
        return new LicenseCandidate(source, kind, raw, normalized, status, deprecated, deprecated ? ["deprecated_spdx_identifier"] : [], evidence);
    }

    /// <summary>
    /// Creates an error candidate for failed external evidence collection.
    /// </summary>
    /// <param name="source">The attempted evidence source.</param>
    /// <param name="kind">The attempted evidence kind.</param>
    /// <param name="warning">The warning retained for the failure.</param>
    /// <param name="evidence">Typed provenance for the failed collection attempt.</param>
    /// <returns>The error candidate.</returns>
    public static LicenseCandidate CreateError(string source, string kind, string warning, LicenseEvidence evidence = default)
        => new(source, kind, default, default, LicenseStatus.Error, false, [warning], evidence);

    private static LicenseStatus Classify(ReadOnlySpan<byte> value, SpdxLicenseIndex spdxLicenseIndex, out Utf8Slice normalized, out bool deprecated)
    {
        normalized = default;
        deprecated = false;
        if (IsUnknown(value))
        {
            return LicenseStatus.Unknown;
        }

        if (spdxLicenseIndex.TryNormalizeLicenseIdUtf8Slice(value, out normalized, out deprecated))
        {
            return LicenseStatus.Matched;
        }

        if (!LooksLikeSpdxExpression(value))
        {
            normalized = Utf8Slice.FromOwnedBytes(value.ToArray());
            return LicenseStatus.Ambiguous;
        }

        if (SpdxExpression.TryNormalize(value, spdxLicenseIndex, out normalized, out deprecated))
        {
            return LicenseStatus.Matched;
        }

        normalized = Utf8Slice.FromOwnedBytes(value.ToArray());
        return LicenseStatus.Invalid;
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

    private static bool LooksLikeSpdxExpression(ReadOnlySpan<byte> value)
        => ContainsAsciiIgnoreCase(value, " and "u8)
        || ContainsAsciiIgnoreCase(value, " or "u8)
        || ContainsAsciiIgnoreCase(value, " with "u8)
        || value.Contains((byte)'(')
        || value.Contains((byte)')');

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expectedLowercase)
    {
        for (var offset = 0; offset <= value.Length - expectedLowercase.Length; offset++)
        {
            if (AsciiEqualsIgnoreCase(value.Slice(offset, expectedLowercase.Length), expectedLowercase))
            {
                return true;
            }
        }

        return false;
    }
}
