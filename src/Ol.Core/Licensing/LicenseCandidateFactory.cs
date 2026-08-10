using System.Buffers;
using Ol.Core.Spdx;

namespace Ol.Core.Licensing;

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
    public static LicenseCandidate Create(LicenseCandidateSource source, LicenseCandidateKind kind, ReadOnlySpan<byte> rawUtf8, SpdxLicenseIndex spdxLicenseIndex, LicenseEvidence evidence = default)
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
    public static LicenseCandidate Create(LicenseCandidateSource source, LicenseCandidateKind kind, Utf8Slice raw, SpdxLicenseIndex spdxLicenseIndex, LicenseEvidence evidence = default)
    {
        var status = Classify(raw.Span, spdxLicenseIndex, out var normalized, out var deprecated);
        return new LicenseCandidate(source, kind, raw, normalized, status, deprecated, deprecated ? LicenseCandidateWarnings.DeprecatedSpdxIdentifier : LicenseCandidateWarnings.None, evidence);
    }

    /// <summary>
    /// Creates one candidate whose classification reads a rewritten value while the evidence keeps the original.
    /// </summary>
    /// <param name="source">The evidence source.</param>
    /// <param name="kind">The source field or license value kind.</param>
    /// <param name="raw">The license value exactly as the source published it.</param>
    /// <param name="classified">The SPDX expression that spelling denotes.</param>
    /// <param name="spdxLicenseIndex">The active SPDX data index.</param>
    /// <param name="evidence">Typed provenance that substantiates the candidate.</param>
    /// <returns>The classified candidate.</returns>
    /// <remarks>
    /// Used where an ecosystem defines a pre-SPDX spelling for an expression it already states, so the
    /// rewrite resolves a documented notation rather than guessing a license. Keeping <paramref name="raw"/>
    /// unchanged is what makes that safe to audit: a report shows the published value beside the
    /// expression it was read as, instead of quietly presenting Ol's rewrite as the publisher's words.
    /// </remarks>
    public static LicenseCandidate CreateRewritten(LicenseCandidateSource source, LicenseCandidateKind kind, Utf8Slice raw, ReadOnlySpan<byte> classified, SpdxLicenseIndex spdxLicenseIndex, LicenseEvidence evidence = default)
    {
        var status = Classify(classified, spdxLicenseIndex, out var normalized, out var deprecated);
        return new LicenseCandidate(source, kind, raw, normalized, status, deprecated, deprecated ? LicenseCandidateWarnings.DeprecatedSpdxIdentifier : LicenseCandidateWarnings.None, evidence);
    }

    /// <summary>The separator Ol joins an unordered license listing with. Never an SPDX operator.</summary>
    private const byte LicenseSetSeparator = (byte)';';

    /// <summary>
    /// Creates one candidate from a listing of licenses whose relation the source did not state.
    /// </summary>
    /// <param name="source">The evidence source that produced the listing.</param>
    /// <param name="raw">The listing exactly as Ol joined it from the source's values.</param>
    /// <param name="spdxLicenseIndex">The active SPDX data index.</param>
    /// <param name="evidence">Typed provenance that substantiates the candidate.</param>
    /// <returns>The listing candidate, or ordinary classification when the value is not a resolvable listing.</returns>
    /// <remarks>
    /// The kind is what marks a value as a listing, so no later reader has to recognize the separator and
    /// mistake a publisher's free-text semicolon for one. The status stays ambiguous — the members are
    /// known, the relation is not — but resolving them here reports a deprecated member, which classifying
    /// the joined value could not: it parses as neither identifier nor expression, and read as
    /// <c>ambiguous</c> or <c>invalid</c> by whether a member happened to contain an operator word.
    /// A member that resolves nothing, such as deps.dev's <c>non-standard</c>, leaves the whole value to
    /// ordinary classification.
    /// </remarks>
    public static LicenseCandidate CreateLicenseSet(LicenseCandidateSource source, Utf8Slice raw, SpdxLicenseIndex spdxLicenseIndex, LicenseEvidence evidence = default)
    {
        if (raw.Span.IndexOf(LicenseSetSeparator) < 0 || !TryResolveMembers(raw.Span, spdxLicenseIndex, out var members, out var deprecated))
        {
            return Create(source, LicenseCandidateKind.License, raw, spdxLicenseIndex, evidence);
        }

        return new LicenseCandidate(
            source,
            LicenseCandidateKind.LicenseSet,
            raw,
            members,
            LicenseStatus.Ambiguous,
            deprecated,
            deprecated ? LicenseCandidateWarnings.DeprecatedSpdxIdentifier : LicenseCandidateWarnings.None,
            evidence);
    }

    /// <summary>Normalizes every member of a listing, or reports that at least one does not resolve.</summary>
    /// <remarks>
    /// Re-joined into one value rather than kept as an array: a candidate is a flat record on every scanned
    /// component, and listings are rare enough that carrying an array everywhere costs more than splitting
    /// a value Ol itself normalized.
    /// </remarks>
    private static bool TryResolveMembers(ReadOnlySpan<byte> value, SpdxLicenseIndex spdxLicenseIndex, out Utf8Slice members, out bool deprecated)
    {
        members = default;
        deprecated = false;
        var builder = new ArrayBufferWriter<byte>(value.Length);
        var remaining = value;
        while (true)
        {
            var separator = remaining.IndexOf(LicenseSetSeparator);
            var member = separator < 0 ? remaining : remaining[..separator];
            if (!SpdxExpression.TryNormalize(TrimAsciiWhitespace(member), spdxLicenseIndex, out var normalized, out var memberDeprecated))
            {
                return false;
            }

            deprecated |= memberDeprecated;
            if (builder.WrittenCount != 0)
            {
                builder.Write("; "u8);
            }

            builder.Write(normalized.Span);
            if (separator < 0)
            {
                members = Utf8Slice.FromOwnedBytes(builder.WrittenSpan.ToArray());
                return true;
            }

            remaining = remaining[(separator + 1)..];
        }
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        var end = value.Length;
        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') end--;
        return value[start..end];
    }

    /// <summary>
    /// Resolves a candidate that states no license but declares a location SPDX publishes for one license.
    /// </summary>
    /// <param name="candidate">The candidate, with its evidence already attached.</param>
    /// <param name="spdxLicenseIndex">The active SPDX data index.</param>
    /// <returns>The resolved candidate, or <paramref name="candidate"/> unchanged.</returns>
    /// <remarks>
    /// Reads nothing at the URL. It recognizes that the place the publisher named is the one SPDX itself
    /// publishes as that license's <c>seeAlso</c>, which makes the value a URL spelling of an identifier —
    /// the same reading the name lookup gives a value spelled as an SPDX name. Applied only where the
    /// candidate's own value resolved nothing, so a declaration never overrides a stated license.
    /// </remarks>
    public static LicenseCandidate ResolveDeclaredLocation(LicenseCandidate candidate, SpdxLicenseIndex spdxLicenseIndex)
    {
        if (candidate.Status != LicenseStatus.Unknown
            || candidate.Evidence.DeclaredReference is not { Kind: DeclaredLicenseReferenceKind.Location } reference
            || !spdxLicenseIndex.TryResolveLicenseUrl(reference.Value.Span, out var normalized, out var deprecated))
        {
            return candidate;
        }

        return candidate with
        {
            Kind = LicenseCandidateKind.Location,
            Normalized = normalized,
            Status = LicenseStatus.Matched,
            Deprecated = deprecated,
            Warnings = deprecated ? candidate.Warnings | LicenseCandidateWarnings.DeprecatedSpdxIdentifier : candidate.Warnings,
        };
    }

    /// <summary>
    /// Creates an error candidate for failed external evidence collection.
    /// </summary>
    /// <param name="source">The attempted evidence source.</param>
    /// <param name="kind">The attempted evidence kind.</param>
    /// <param name="warning">The warning retained for the failure.</param>
    /// <param name="evidence">Typed provenance for the failed collection attempt.</param>
    /// <returns>The error candidate.</returns>
    public static LicenseCandidate CreateError(LicenseCandidateSource source, LicenseCandidateKind kind, LicenseCandidateWarnings warning, LicenseEvidence evidence = default)
        => new(source, kind, default, default, LicenseStatus.Error, false, warning, evidence);

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

        // Before the value is read as an expression, not after. SPDX names contain the operator words:
        // `BSD 3-Clause "New" or "Revised" License` is the name of BSD-3-Clause, and parsing it as a
        // disjunction rejected a value the SPDX data itself defines. A name is one license, so it is
        // resolved for a whole declared value only and never for an operand inside an expression.
        if (spdxLicenseIndex.TryNormalizeLicenseNameUtf8Slice(value, out normalized, out deprecated))
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
