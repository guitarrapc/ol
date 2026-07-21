using System.Buffers;
using System.Collections.Frozen;

namespace Ol.Core.Spdx;

internal ref struct SpdxExpression
{
    private readonly ReadOnlySpan<byte> value;
    private readonly SpdxLicenseIndex spdxLicenseIndex;
    private readonly FrozenSet<string>? allowedLicenses;
    private Span<char> output;
    private int position;
    private int outputCount;
    private bool hasDeprecatedLicense;

    private SpdxExpression(ReadOnlySpan<byte> value, SpdxLicenseIndex spdxLicenseIndex, Span<char> output, FrozenSet<string>? allowedLicenses = null)
    {
        this.value = value;
        this.spdxLicenseIndex = spdxLicenseIndex;
        this.output = output;
        this.allowedLicenses = allowedLicenses;
        position = 0;
        outputCount = 0;
    }

    public static bool TryNormalize(ReadOnlySpan<byte> value, SpdxLicenseIndex spdxLicenseIndex, out Utf8Slice normalized, out bool hasDeprecatedLicense)
    {
        const int MaxStackChars = 128;
        char[]? rented = null;
        Span<char> output = value.Length <= MaxStackChars
            ? stackalloc char[MaxStackChars]
            : (rented = ArrayPool<char>.Shared.Rent(value.Length));
        try
        {
            var parser = new SpdxExpression(value, spdxLicenseIndex, output);
            if (!parser.TryParseExpression(out _) || !parser.IsAtEnd())
            {
                normalized = default;
                hasDeprecatedLicense = false;
                return false;
            }

            var byteCount = System.Text.Encoding.UTF8.GetByteCount(output[..parser.outputCount]);
            var bytes = new byte[byteCount];
            System.Text.Encoding.UTF8.GetBytes(output[..parser.outputCount], bytes);
            normalized = Utf8Slice.FromOwnedBytes(bytes);
            hasDeprecatedLicense = parser.hasDeprecatedLicense;
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    public static bool TryEvaluatePolicy(ReadOnlySpan<byte> value, SpdxLicenseIndex spdxLicenseIndex, FrozenSet<string> allowedLicenses, out bool allowed)
    {
        var parser = new SpdxExpression(value, spdxLicenseIndex, [], allowedLicenses);
        return parser.TryParseExpression(out allowed) && parser.IsAtEnd();
    }

    private bool TryParseExpression(out bool allowed)
    {
        return TryParseOrExpression(out allowed);
    }

    private bool TryParseOrExpression(out bool allowed)
    {
        if (!TryParseAndExpression(out allowed))
        {
            return false;
        }

        while (TryConsumeOperator("or"u8))
        {
            Append(" OR ");
            if (!TryParseAndExpression(out var right))
            {
                return false;
            }

            allowed |= right;
        }

        return true;
    }

    private bool TryParseAndExpression(out bool allowed)
    {
        if (!TryParseWithExpression(out allowed))
        {
            return false;
        }

        while (TryConsumeOperator("and"u8))
        {
            Append(" AND ");
            if (!TryParseWithExpression(out var right))
            {
                return false;
            }

            allowed &= right;
        }

        return true;
    }

    private bool TryParseWithExpression(out bool allowed)
    {
        if (!TryParsePrimary(out allowed))
        {
            return false;
        }

        if (!TryConsumeOperator("with"u8))
        {
            return true;
        }

        Append(" WITH ");
        if (!TryReadIdentifier(out var exceptionId) || !spdxLicenseIndex.TryNormalizeExceptionIdUtf8(exceptionId, out var normalizedException))
        {
            return false;
        }

        Append(normalizedException);
        return true;
    }

    private bool TryParsePrimary(out bool allowed)
    {
        SkipWhitespace();
        if (position >= value.Length)
        {
            allowed = false;
            return false;
        }

        if (value[position] == (byte)'(')
        {
            position++;
            Append('(');
            if (!TryParseExpression(out allowed))
            {
                return false;
            }

            SkipWhitespace();
            if (position >= value.Length || value[position] != (byte)')')
            {
                return false;
            }

            position++;
            Append(')');
            return true;
        }

        if (!TryReadIdentifier(out var licenseId) || !spdxLicenseIndex.TryNormalizeLicenseIdUtf8(licenseId, out var normalizedLicense))
        {
            allowed = false;
            return false;
        }

        hasDeprecatedLicense |= spdxLicenseIndex.IsDeprecatedLicenseId(normalizedLicense);
        Append(normalizedLicense);
        allowed = allowedLicenses?.Contains(normalizedLicense) ?? false;
        return true;
    }

    private bool TryConsumeOperator(ReadOnlySpan<byte> operatorText)
    {
        SkipWhitespace();
        var start = position;
        if (!TryReadIdentifier(out var token) || !AsciiEqualsIgnoreCase(token, operatorText))
        {
            position = start;
            return false;
        }

        return true;
    }

    private bool TryReadIdentifier(out ReadOnlySpan<byte> identifier)
    {
        SkipWhitespace();
        var start = position;
        while (position < value.Length && IsIdentifierByte(value[position]))
        {
            position++;
        }

        if (position == start)
        {
            identifier = [];
            return false;
        }

        identifier = value[start..position];
        return true;
    }

    private bool IsAtEnd()
    {
        SkipWhitespace();
        return position == value.Length;
    }

    private void SkipWhitespace()
    {
        while (position < value.Length && value[position] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            position++;
        }
    }

    private static bool IsIdentifierByte(byte value)
        => value is >= (byte)'a' and <= (byte)'z'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is (byte)'-' or (byte)'.' or (byte)'+';

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

    private void Append(char value)
    {
        if (output.IsEmpty) return;
        output[outputCount] = value;
        outputCount++;
    }

    private void Append(string value)
    {
        if (output.IsEmpty) return;
        value.CopyTo(output[outputCount..]);
        outputCount += value.Length;
    }
}
