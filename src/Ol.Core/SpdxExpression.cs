using System.Text;

namespace Ol.Core;

internal ref struct SpdxExpression
{
    private readonly string value;
    private readonly SpdxLicenseIndex spdxLicenseIndex;
    private readonly StringBuilder builder;
    private int position;
    private bool hasDeprecatedLicense;

    private SpdxExpression(string value, SpdxLicenseIndex spdxLicenseIndex, StringBuilder builder)
    {
        this.value = value;
        this.spdxLicenseIndex = spdxLicenseIndex;
        this.builder = builder;
        position = 0;
    }

    public static bool TryNormalize(string value, SpdxLicenseIndex spdxLicenseIndex, out string normalized)
        => TryNormalize(value, spdxLicenseIndex, out normalized, out _);

    public static bool TryNormalize(string value, SpdxLicenseIndex spdxLicenseIndex, out string normalized, out bool hasDeprecatedLicense)
    {
        var builder = new StringBuilder(value.Length);
        var parser = new SpdxExpression(value, spdxLicenseIndex, builder);
        if (!parser.TryParseExpression() || !parser.IsAtEnd())
        {
            normalized = string.Empty;
            hasDeprecatedLicense = false;
            return false;
        }

        normalized = builder.ToString();
        hasDeprecatedLicense = parser.hasDeprecatedLicense;
        return true;
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

        while (TryConsumeOperator("OR"))
        {
            builder.Append(" OR ");
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

        while (TryConsumeOperator("AND"))
        {
            builder.Append(" AND ");
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

        if (!TryConsumeOperator("WITH"))
        {
            return true;
        }

        builder.Append(" WITH ");
        if (!TryReadIdentifier(out var exceptionId) || !spdxLicenseIndex.TryNormalizeExceptionId(exceptionId, out var normalizedException))
        {
            return false;
        }

        builder.Append(normalizedException);
        return true;
    }

    private bool TryParsePrimary()
    {
        SkipWhitespace();
        if (position >= value.Length)
        {
            return false;
        }

        if (value[position] == '(')
        {
            position++;
            builder.Append('(');
            if (!TryParseExpression())
            {
                return false;
            }

            SkipWhitespace();
            if (position >= value.Length || value[position] != ')')
            {
                return false;
            }

            position++;
            builder.Append(')');
            return true;
        }

        if (!TryReadIdentifier(out var licenseId) || !spdxLicenseIndex.TryNormalizeLicenseId(licenseId, out var normalizedLicense))
        {
            return false;
        }

        hasDeprecatedLicense |= spdxLicenseIndex.IsDeprecatedLicenseId(normalizedLicense);
        builder.Append(normalizedLicense);
        return true;
    }

    private bool TryConsumeOperator(string operatorText)
    {
        SkipWhitespace();
        var start = position;
        if (!TryReadIdentifier(out var token) || !string.Equals(token, operatorText, StringComparison.OrdinalIgnoreCase))
        {
            position = start;
            return false;
        }

        return true;
    }

    private bool TryReadIdentifier(out string identifier)
    {
        SkipWhitespace();
        var start = position;
        while (position < value.Length)
        {
            var ch = value[position];
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' || ch == '+')
            {
                position++;
                continue;
            }

            break;
        }

        if (position == start)
        {
            identifier = string.Empty;
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
        while (position < value.Length && char.IsWhiteSpace(value[position]))
        {
            position++;
        }
    }
}
