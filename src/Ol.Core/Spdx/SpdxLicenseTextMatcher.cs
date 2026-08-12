using System.Text;
using System.Text.RegularExpressions;

namespace Ol.Core.Spdx;

/// <summary>Pairs one SPDX identifier with its standard license template.</summary>
/// <param name="LicenseId">The canonical SPDX license identifier.</param>
/// <param name="Template">The SPDX standard license template.</param>
public readonly record struct SpdxLicenseTextTemplate(string LicenseId, string Template);

/// <summary>Matches bounded UTF-8 license documents against a versioned SPDX template corpus.</summary>
/// <remarks>
/// Templates are parsed once when the immutable matcher is constructed. Runtime matching bounds both
/// document bytes and regex execution time so package-controlled input cannot cause unbounded work. A
/// document matching more than one identifier is deliberately unresolved.
/// </remarks>
public sealed class SpdxLicenseTextMatcher
{
    /// <summary>The default maximum document size accepted by the matcher.</summary>
    public const int DefaultMaximumTextBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private readonly TemplatePattern[] patterns;
    private readonly int maximumTextBytes;

    /// <summary>Initializes an immutable matcher for one versioned SPDX template corpus.</summary>
    public SpdxLicenseTextMatcher(string corpusVersion, SpdxLicenseTextTemplate[] templates, int maximumTextBytes = DefaultMaximumTextBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusVersion);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTextBytes);

        CorpusVersion = corpusVersion;
        this.maximumTextBytes = maximumTextBytes;
        patterns = new TemplatePattern[templates.Length];
        for (var i = 0; i < templates.Length; i++)
        {
            var template = templates[i];
            if (string.IsNullOrWhiteSpace(template.LicenseId) || string.IsNullOrWhiteSpace(template.Template))
            {
                throw new ArgumentException("SPDX license text templates require an identifier and template text.", nameof(templates));
            }

            patterns[i] = new TemplatePattern(template.LicenseId, CreateRegex(template.Template));
        }
    }

    /// <summary>Gets the SPDX corpus version whose templates this matcher uses.</summary>
    public string CorpusVersion { get; }

    /// <summary>Attempts to identify exactly one SPDX license from a UTF-8 document.</summary>
    public bool TryMatch(ReadOnlySpan<byte> licenseTextUtf8, out string licenseId)
    {
        licenseId = string.Empty;
        if (licenseTextUtf8.IsEmpty || licenseTextUtf8.Length > maximumTextBytes)
        {
            return false;
        }

        const int maximumStackCharacters = 512;
        int characterCount;
        try
        {
            characterCount = StrictUtf8.GetCharCount(licenseTextUtf8);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (characterCount <= maximumStackCharacters)
        {
            Span<char> characters = stackalloc char[characterCount];
            StrictUtf8.GetChars(licenseTextUtf8, characters);
            return TryMatchCharacters(characters, out licenseId);
        }

        var rented = System.Buffers.ArrayPool<char>.Shared.Rent(characterCount);
        try
        {
            var written = StrictUtf8.GetChars(licenseTextUtf8, rented);
            return TryMatchCharacters(rented.AsSpan(0, written), out licenseId);
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    private bool TryMatchCharacters(ReadOnlySpan<char> text, out string licenseId)
    {
        licenseId = string.Empty;
        for (var i = 0; i < patterns.Length; i++)
        {
            ref readonly var pattern = ref patterns[i];
            bool matched;
            try
            {
                matched = pattern.Regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                licenseId = string.Empty;
                return false;
            }

            if (!matched) continue;
            if (licenseId.Length != 0 && !string.Equals(licenseId, pattern.LicenseId, StringComparison.Ordinal))
            {
                licenseId = string.Empty;
                return false;
            }

            licenseId = pattern.LicenseId;
        }

        return licenseId.Length != 0;
    }

    private static Regex CreateRegex(string template)
    {
        var pattern = new StringBuilder(template.Length * 2);
        pattern.Append(@"\A\s*");
        var position = 0;
        var optionalDepth = 0;
        while (position < template.Length)
        {
            var ruleStart = template.IndexOf("<<", position, StringComparison.Ordinal);
            if (ruleStart < 0)
            {
                AppendLiteral(pattern, template.AsSpan(position));
                break;
            }

            AppendLiteral(pattern, template.AsSpan(position, ruleStart - position));
            var ruleEnd = template.IndexOf(">>", ruleStart + 2, StringComparison.Ordinal);
            if (ruleEnd < 0)
            {
                throw new ArgumentException("SPDX license template contains an unterminated rule.", nameof(template));
            }

            var rule = template.AsSpan(ruleStart + 2, ruleEnd - ruleStart - 2).Trim();
            if (rule.StartsWith("beginOptional", StringComparison.Ordinal))
            {
                pattern.Append("(?:");
                optionalDepth++;
            }
            else if (rule.StartsWith("endOptional", StringComparison.Ordinal))
            {
                if (optionalDepth == 0) throw new ArgumentException("SPDX license template closes an optional rule that was not opened.", nameof(template));
                pattern.Append(")?");
                optionalDepth--;
            }
            else if (rule.StartsWith("var", StringComparison.Ordinal))
            {
                pattern.Append("(?:").Append(ReadVariableMatch(rule)).Append(')');
            }
            else
            {
                throw new ArgumentException("SPDX license template contains an unsupported rule.", nameof(template));
            }

            position = ruleEnd + 2;
        }

        if (optionalDepth != 0) throw new ArgumentException("SPDX license template contains an unclosed optional rule.", nameof(template));
        pattern.Append(@"\s*\z");
        return new Regex(
            pattern.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            MatchTimeout);
    }

    /// <summary>Writes literal template text with SPDX's whitespace-insensitive comparison.</summary>
    private static void AppendLiteral(StringBuilder pattern, ReadOnlySpan<char> literal)
    {
        for (var i = 0; i < literal.Length; i++)
        {
            var value = literal[i];
            if (char.IsWhiteSpace(value))
            {
                var end = i + 1;
                while (end < literal.Length && char.IsWhiteSpace(literal[end])) end++;
                var separatesWords = i > 0
                    && end < literal.Length
                    && char.IsLetterOrDigit(literal[i - 1])
                    && char.IsLetterOrDigit(literal[end]);
                pattern.Append(separatesWords ? @"\s+" : @"\s*");
                i = end - 1;
                continue;
            }

            if (value is '\\' or '.' or '$' or '^' or '{' or '[' or '(' or '|' or ')' or '*' or '+' or '?' or '#')
            {
                pattern.Append('\\');
            }

            pattern.Append(value);
        }
    }

    private static ReadOnlySpan<char> ReadVariableMatch(ReadOnlySpan<char> rule)
    {
        const string marker = "match=\"";
        var start = rule.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return @".{0,5000}";
        start += marker.Length;
        for (var i = start; i < rule.Length; i++)
        {
            if (rule[i] != '"') continue;
            var backslashes = 0;
            for (var j = i - 1; j >= start && rule[j] == '\\'; j--) backslashes++;
            if ((backslashes & 1) == 0) return rule[start..i];
        }

        throw new ArgumentException("SPDX variable rule contains an unterminated match expression.", nameof(rule));
    }

    private readonly record struct TemplatePattern(string LicenseId, Regex Regex);
}
