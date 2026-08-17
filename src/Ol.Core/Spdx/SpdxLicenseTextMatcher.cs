using System.Text;
using System.Text.RegularExpressions;

using System.Buffers;
using System.Collections.Frozen;

namespace Ol.Core.Spdx;

/// <summary>Pairs one SPDX identifier with its standard license template.</summary>
/// <param name="LicenseId">The canonical SPDX license identifier.</param>
/// <param name="Template">The SPDX standard license template.</param>
public readonly record struct SpdxLicenseTextTemplate(string LicenseId, string Template);

/// <summary>Matches bounded UTF-8 license documents against a versioned SPDX template corpus.</summary>
/// <remarks>
/// Construction validates template rules and indexes required literal anchors. A candidate's regex is
/// parsed once on first use, after the anchor index selects it. Runtime matching bounds both document
/// bytes and regex execution time, and more than one matching identifier is deliberately unresolved.
/// </remarks>
/// <summary>Names which observable evidence in a license document produced the identifier.</summary>
public enum SpdxLicenseTextMatchKind : byte
{
    /// <summary>The document resolved nothing.</summary>
    None = 0,

    /// <summary>The document reproduces one SPDX license text.</summary>
    Template = 1,

    /// <summary>The document declares one license by a URL the SPDX license list publishes for it.</summary>
    DeclaredUrl = 2,
}

/// <summary>Provides the stable matcher identifiers persisted as evidence provenance.</summary>
public static class SpdxLicenseTextMatchKinds
{
    /// <summary>Gets the stable matcher identifier a report and a baseline record.</summary>
    public static string ToMatcherId(this SpdxLicenseTextMatchKind value) => value switch
    {
        SpdxLicenseTextMatchKind.Template => "spdx-template",
        SpdxLicenseTextMatchKind.DeclaredUrl => "spdx-license-url",
        _ => "",
    };
}

public sealed class SpdxLicenseTextMatcher
{
    /// <summary>The default maximum document size accepted by the matcher.</summary>
    public const int DefaultMaximumTextBytes = 1024 * 1024;

    private const int MaximumDeclaredUrlChars = 512;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private readonly TemplatePattern[] patterns;
    private readonly FrozenDictionary<string, int[]> anchoredPatterns;
    private readonly int[] unanchoredPatternIndexes;
    private readonly int maximumTextBytes;
    private readonly SpdxLicenseIndex? licenseIndex;

    /// <summary>Initializes an immutable matcher for one versioned SPDX template corpus.</summary>
    /// <remarks>
    /// Supplying <paramref name="licenseIndex"/> lets the matcher also read a license URL the SPDX
    /// license list itself publishes as one license's <c>seeAlso</c>. A document that states
    /// <c>Licensed under the Apache License, Version 2.0</c> and links the canonical Apache page
    /// declares its license without reproducing the text, and the same reading makes a document that
    /// quotes one license in full while linking another resolve to neither.
    /// </remarks>
    public SpdxLicenseTextMatcher(
        string corpusVersion,
        SpdxLicenseTextTemplate[] templates,
        int maximumTextBytes = DefaultMaximumTextBytes,
        SpdxLicenseIndex? licenseIndex = null)
    {
        this.licenseIndex = licenseIndex;
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusVersion);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTextBytes);

        CorpusVersion = corpusVersion;
        this.maximumTextBytes = maximumTextBytes;
        patterns = new TemplatePattern[templates.Length];
        var anchorGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var licenseIds = new HashSet<string>(templates.Length, StringComparer.Ordinal);
        var unanchored = new List<int>();
        for (var i = 0; i < templates.Length; i++)
        {
            var template = templates[i];
            if (string.IsNullOrWhiteSpace(template.LicenseId) || string.IsNullOrWhiteSpace(template.Template))
            {
                throw new ArgumentException("SPDX license text templates require an identifier and template text.", nameof(templates));
            }
            if (!licenseIds.Add(template.LicenseId))
            {
                throw new ArgumentException($"SPDX license text templates contain duplicate identifier: {template.LicenseId}", nameof(templates));
            }

            try
            {
                var anchors = FindRequiredAnchors(template.Template);
                patterns[i] = new TemplatePattern(template.LicenseId, template.Template, anchors.Primary, anchors.Secondary);
                if (patterns[i].Anchor.Length == 0)
                {
                    unanchored.Add(i);
                }
                else
                {
                    if (!anchorGroups.TryGetValue(patterns[i].Anchor, out var indexes))
                    {
                        indexes = [];
                        anchorGroups.Add(patterns[i].Anchor, indexes);
                    }

                    indexes.Add(i);
                }
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"SPDX license template is invalid: {template.LicenseId}", nameof(templates), exception);
            }
        }

        var frozenInput = new Dictionary<string, int[]>(anchorGroups.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in anchorGroups) frozenInput.Add(pair.Key, [.. pair.Value]);
        anchoredPatterns = frozenInput.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        unanchoredPatternIndexes = [.. unanchored];
    }

    /// <summary>Gets the SPDX corpus version whose templates this matcher uses.</summary>
    public string CorpusVersion { get; }

    /// <summary>Gets the maximum document size this matcher accepts.</summary>
    public int MaximumTextBytes => maximumTextBytes;

    internal int UnanchoredTemplateCount => unanchoredPatternIndexes.Length;

    /// <summary>Attempts to identify exactly one SPDX license from a UTF-8 document.</summary>
    public bool TryMatch(ReadOnlySpan<byte> licenseTextUtf8, out string licenseId)
        => TryMatch(licenseTextUtf8, out licenseId, out _);

    /// <summary>Attempts to identify exactly one SPDX license, reporting which evidence named it.</summary>
    /// <param name="licenseTextUtf8">The UTF-8 license document.</param>
    /// <param name="licenseId">The resolved SPDX license identifier.</param>
    /// <param name="kind">The evidence that produced <paramref name="licenseId"/>, for provenance.</param>
    /// <returns><see langword="true" /> when the document names exactly one license.</returns>
    public bool TryMatch(ReadOnlySpan<byte> licenseTextUtf8, out string licenseId, out SpdxLicenseTextMatchKind kind)
    {
        licenseId = string.Empty;
        kind = SpdxLicenseTextMatchKind.None;
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
            return TryMatchCharacters(characters, out licenseId, out kind);
        }

        var rented = System.Buffers.ArrayPool<char>.Shared.Rent(characterCount);
        try
        {
            var written = StrictUtf8.GetChars(licenseTextUtf8, rented);
            return TryMatchCharacters(rented.AsSpan(0, written), out licenseId, out kind);
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    private bool TryMatchCharacters(ReadOnlySpan<char> text, out string licenseId, out SpdxLicenseTextMatchKind kind)
    {
        licenseId = string.Empty;
        kind = SpdxLicenseTextMatchKind.None;
        if (!TryMatchTemplates(text, ref licenseId)) return false;
        if (licenseId.Length != 0) kind = SpdxLicenseTextMatchKind.Template;
        if (licenseIndex is not null && !TryMatchDeclaredUrls(text, ref licenseId))
        {
            kind = SpdxLicenseTextMatchKind.None;
            return false;
        }

        if (licenseId.Length == 0) return false;
        if (kind == SpdxLicenseTextMatchKind.None) kind = SpdxLicenseTextMatchKind.DeclaredUrl;
        return true;
    }

    private bool TryMatchTemplates(ReadOnlySpan<char> text, ref string licenseId)
    {
        if (patterns.Length == 0) return true;
        var visited = ArrayPool<bool>.Shared.Rent(patterns.Length);
        visited.AsSpan(0, patterns.Length).Clear();
        try
        {
            var lookup = anchoredPatterns.GetAlternateLookup<ReadOnlySpan<char>>();
            var wordStart = 0;
            for (var index = 0; index <= text.Length; index++)
            {
                if (index < text.Length && char.IsLetterOrDigit(text[index])) continue;
                var word = text[wordStart..index];
                if (word.Length >= 4 && lookup.TryGetValue(word, out var candidateIndexes))
                {
                    for (var candidateIndex = 0; candidateIndex < candidateIndexes.Length; candidateIndex++)
                    {
                        var patternIndex = candidateIndexes[candidateIndex];
                        if (visited[patternIndex]) continue;
                        visited[patternIndex] = true;
                        if (!TryMatchPattern(patterns[patternIndex], text, ref licenseId)) return false;
                    }
                }

                wordStart = index + 1;
            }

            for (var index = 0; index < unanchoredPatternIndexes.Length; index++)
            {
                if (!TryMatchPattern(patterns[unanchoredPatternIndexes[index]], text, ref licenseId)) return false;
            }

            return true;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(visited);
        }
    }

    /// <summary>Reads every SPDX-published license URL the document declares under the single-answer rule.</summary>
    /// <remarks>
    /// Only a URL the SPDX license list publishes for exactly one license resolves anything, so a
    /// redirector, a project's own page, or a page SPDX shares between licenses contributes nothing.
    /// A URL naming a license the templates did not name leaves the document unresolved, because
    /// nothing observable says which of the two governs the work.
    /// </remarks>
    private bool TryMatchDeclaredUrls(ReadOnlySpan<char> text, ref string licenseId)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            var start = IndexOfUrlStart(text[offset..]);
            if (start < 0) return true;
            start += offset;
            var url = TrimUrlBoundary(text[start..(start + UrlLength(text[start..]))]);
            offset = start + Math.Max(url.Length, 1);
            if (url.Length is 0 or > MaximumDeclaredUrlChars || !licenseIndex!.TryResolveLicenseUrl(url, out var resolved)) continue;
            if (licenseId.Length != 0 && !string.Equals(licenseId, resolved, StringComparison.Ordinal))
            {
                licenseId = string.Empty;
                return false;
            }

            licenseId = resolved;
        }

        return true;
    }

    /// <summary>Finds the next <c>http</c> scheme that begins a URL rather than continuing a word.</summary>
    private static int IndexOfUrlStart(ReadOnlySpan<char> text)
    {
        var offset = 0;
        while (true)
        {
            var index = text[offset..].IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return -1;
            index += offset;
            var scheme = text[index..];
            var isScheme = scheme.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || scheme.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (isScheme && (index == 0 || !char.IsLetterOrDigit(text[index - 1]))) return index;
            offset = index + 4;
            if (offset >= text.Length) return -1;
        }
    }

    /// <summary>Measures the URL run, stopping at the characters prose uses to delimit a URL.</summary>
    private static int UrlLength(ReadOnlySpan<char> text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current) || current is '"' or '\'' or '<' or '>' or '(' or ')' or '[' or ']' or '{' or '}' or ',' or ';' or '|' or '\\' or '`') return index;
        }

        return text.Length;
    }

    /// <summary>Removes the sentence punctuation a URL can pick up when prose ends on it.</summary>
    private static ReadOnlySpan<char> TrimUrlBoundary(ReadOnlySpan<char> url) => url.TrimEnd('.').TrimEnd(':');

    private static bool TryMatchPattern(TemplatePattern pattern, ReadOnlySpan<char> text, ref string licenseId)
    {
        bool matched;
        try
        {
            matched = pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            licenseId = string.Empty;
            return false;
        }

        if (!matched) return true;
        if (licenseId.Length != 0 && !string.Equals(licenseId, pattern.LicenseId, StringComparison.Ordinal))
        {
            licenseId = string.Empty;
            return false;
        }

        licenseId = pattern.LicenseId;
        return true;
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
            if (ruleStart + 2 < template.Length && template[ruleStart + 2] == '<')
            {
                AppendLiteral(pattern, "<".AsSpan());
                position = ruleStart + 1;
                continue;
            }

            var ruleEnd = template.IndexOf(">>", ruleStart + 2, StringComparison.Ordinal);
            if (ruleEnd < 0)
            {
                throw new ArgumentException("SPDX license template contains an unterminated rule.", nameof(template));
            }

            var rule = template.AsSpan(ruleStart + 2, ruleEnd - ruleStart - 2).Trim();
            if (rule.SequenceEqual("beginOptional".AsSpan()))
            {
                pattern.Append("(?:");
                optionalDepth++;
            }
            else if (rule.SequenceEqual("endOptional".AsSpan()))
            {
                if (optionalDepth == 0) throw new ArgumentException("SPDX license template closes an optional rule that was not opened.", nameof(template));
                pattern.Append(")?");
                optionalDepth--;
            }
            else if (rule.StartsWith("var;".AsSpan(), StringComparison.Ordinal))
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

    private static RequiredAnchors FindRequiredAnchors(string template)
    {
        var position = 0;
        var optionalDepth = 0;
        var bestStart = 0;
        var bestLength = 0;
        var secondStart = 0;
        var secondLength = 0;
        while (position < template.Length)
        {
            var ruleStart = template.IndexOf("<<", position, StringComparison.Ordinal);
            if (ruleStart < 0)
            {
                if (optionalDepth == 0) ConsiderAnchors(template, position, template.Length - position, ref bestStart, ref bestLength, ref secondStart, ref secondLength);
                break;
            }

            if (optionalDepth == 0) ConsiderAnchors(template, position, ruleStart - position, ref bestStart, ref bestLength, ref secondStart, ref secondLength);
            if (ruleStart + 2 < template.Length && template[ruleStart + 2] == '<')
            {
                position = ruleStart + 1;
                continue;
            }

            var ruleEnd = template.IndexOf(">>", ruleStart + 2, StringComparison.Ordinal);
            if (ruleEnd < 0) throw new ArgumentException("SPDX license template contains an unterminated rule.", nameof(template));
            var rule = template.AsSpan(ruleStart + 2, ruleEnd - ruleStart - 2).Trim();
            if (rule.SequenceEqual("beginOptional".AsSpan()))
            {
                optionalDepth++;
            }
            else if (rule.SequenceEqual("endOptional".AsSpan()))
            {
                if (optionalDepth == 0) throw new ArgumentException("SPDX license template closes an optional rule that was not opened.", nameof(template));
                optionalDepth--;
            }
            else if (rule.StartsWith("var;".AsSpan(), StringComparison.Ordinal))
            {
                _ = ReadVariableMatch(rule);
            }
            else
            {
                throw new ArgumentException("SPDX license template contains an unsupported rule.", nameof(template));
            }

            position = ruleEnd + 2;
        }

        if (optionalDepth != 0) throw new ArgumentException("SPDX license template contains an unclosed optional rule.", nameof(template));
        return new RequiredAnchors(
            bestLength >= 4 ? template.Substring(bestStart, bestLength) : string.Empty,
            secondLength >= 4 ? template.Substring(secondStart, secondLength) : string.Empty);
    }

    private static void ConsiderAnchors(
        string template,
        int start,
        int length,
        ref int bestStart,
        ref int bestLength,
        ref int secondStart,
        ref int secondLength)
    {
        var end = start + length;
        var wordStart = start;
        for (var index = start; index <= end; index++)
        {
            if (index < end && char.IsLetterOrDigit(template[index])) continue;
            var wordLength = index - wordStart;
            var hasStableLeftBoundary = wordStart > start || start == 0;
            var hasStableRightBoundary = index < end || end == template.Length;
            if (!hasStableLeftBoundary || !hasStableRightBoundary)
            {
                wordStart = index + 1;
                continue;
            }

            if (wordLength > bestLength)
            {
                secondStart = bestStart;
                secondLength = bestLength;
                bestStart = wordStart;
                bestLength = wordLength;
            }
            else if (wordLength > secondLength
                && !template.AsSpan(wordStart, wordLength).Equals(template.AsSpan(bestStart, bestLength), StringComparison.OrdinalIgnoreCase))
            {
                secondStart = wordStart;
                secondLength = wordLength;
            }

            wordStart = index + 1;
        }

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
        if (start < 0) throw new ArgumentException("SPDX variable rule has no match expression.", nameof(rule));
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

    private readonly record struct RequiredAnchors(string Primary, string Secondary);

    private sealed class TemplatePattern(string licenseId, string template, string anchor, string secondaryAnchor)
    {
        private Regex? regex;

        public string LicenseId { get; } = licenseId;
        public string Anchor { get; } = anchor;

        public bool IsMatch(ReadOnlySpan<char> text)
        {
            if (secondaryAnchor.Length != 0 && !text.Contains(secondaryAnchor, StringComparison.OrdinalIgnoreCase)) return false;
            var current = Volatile.Read(ref regex);
            if (current is null)
            {
                var created = CreateRegex(template);
                current = Interlocked.CompareExchange(ref regex, created, null) ?? created;
            }

            return current.IsMatch(text);
        }
    }
}
