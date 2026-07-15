using System.Buffers;

namespace Ol.Core.Spdx;

internal ref struct SpdxExpression
{
    private readonly ReadOnlySpan<byte> value;
    private readonly SpdxLicenseIndex spdxLicenseIndex;
    private Span<char> output;
    private int position;
    private int outputCount;
    private bool hasDeprecatedLicense;

    private SpdxExpression(ReadOnlySpan<byte> value, SpdxLicenseIndex spdxLicenseIndex, Span<char> output)
    {
        this.value = value;
        this.spdxLicenseIndex = spdxLicenseIndex;
        this.output = output;
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
            if (!parser.TryParseExpression() || !parser.IsAtEnd())
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

    private bool TryParseExpression()
    {
        return TryParseOrExpression();
    }

    private bool TryParseOrExpression()
    {
        if (!TryParseAndExpression())
        {
            return false;
        }

        while (TryConsumeOperator("or"u8))
        {
            Append(" OR ");
            if (!TryParseAndExpression())
            {
                return false;
            }
        }

        return true;
    }

    private bool TryParseAndExpression()
    {
        if (!TryParseWithExpression())
        {
            return false;
        }

        while (TryConsumeOperator("and"u8))
        {
            Append(" AND ");
            if (!TryParseWithExpression())
            {
                return false;
            }
        }

        return true;
    }

    private bool TryParseWithExpression()
    {
        if (!TryParsePrimary())
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

    private bool TryParsePrimary()
    {
        SkipWhitespace();
        if (position >= value.Length)
        {
            return false;
        }

        if (value[position] == (byte)'(')
        {
            position++;
            Append('(');
            if (!TryParseExpression())
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
            return false;
        }

        hasDeprecatedLicense |= spdxLicenseIndex.IsDeprecatedLicenseId(normalizedLicense);
        Append(normalizedLicense);
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
        output[outputCount] = value;
        outputCount++;
    }

    private void Append(string value)
    {
        value.CopyTo(output[outputCount..]);
        outputCount += value.Length;
    }
}
