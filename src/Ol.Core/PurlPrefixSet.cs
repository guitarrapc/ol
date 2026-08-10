using System.Text;

namespace Ol.Core;

/// <summary>
/// An immutable, order-preserving set of package URL prefixes used to select components by identity.
/// </summary>
/// <remarks>
/// Matching is ordinal, case-sensitive, and anchored at purl separators. A prefix that ends at a separator states its own
/// boundary, so <c>pkg:nuget/MyCompany.</c> covers a package family. A prefix that ends inside a name must reach a separator
/// in the purl, so <c>pkg:nuget/MyCompany</c> never matches <c>pkg:nuget/MyCompanyEvil</c>. A casing mismatch and a component
/// without a purl both fail to match, which keeps a mistyped prefix from selecting more than it names.
/// </remarks>
public sealed class PurlPrefixSet
{
    private readonly byte[][] prefixes;
    private readonly string[] texts;

    private PurlPrefixSet(byte[][] prefixes, string[] texts)
    {
        this.prefixes = prefixes;
        this.texts = texts;
    }

    /// <summary>Gets the normalized prefixes in the order they were supplied.</summary>
    public ReadOnlySpan<string> Prefixes => texts;

    /// <summary>
    /// Creates a prefix set, or reports why an entry cannot be used. An empty input produces no set rather than an
    /// empty one, so a caller can tell "not supplied" from "supplied and matched nothing".
    /// </summary>
    public static bool TryCreate(ReadOnlySpan<string> values, out PurlPrefixSet? set, out string error)
    {
        set = null;
        error = string.Empty;
        if (values.IsEmpty) return true;

        var unique = new HashSet<string>(values.Length, StringComparer.Ordinal);
        var ordered = new List<string>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var value = TrimAsciiWhitespace(values[i].AsSpan());
            if (value.IsEmpty)
            {
                error = "Package URL prefix entries must not be empty.";
                return false;
            }

            if (!IsPackageUrlPrefix(value))
            {
                error = $"Package URL prefix entries must identify at least one package or namespace, such as pkg:nuget/MyCompany.: {Display(value)}";
                return false;
            }

            var text = CanonicalizeScope(value);
            if (unique.Add(text)) ordered.Add(text);
        }

        var texts = ordered.ToArray();
        var encoded = new byte[texts.Length][];
        for (var i = 0; i < texts.Length; i++) encoded[i] = Encoding.UTF8.GetBytes(texts[i]);
        set = new PurlPrefixSet(encoded, texts);
        return true;
    }

    /// <summary>Determines whether a component purl matches any prefix.</summary>
    public bool Contains(Utf8Slice purl) => Match(purl) >= 0;

    /// <summary>
    /// Counts how many components each prefix matched, attributing every matched component to the first prefix that
    /// matches it so the counts sum to the number of selected components.
    /// </summary>
    public void CountMatches(ReadOnlySpan<ScanComponent> components, Span<int> matchCounts)
    {
        matchCounts.Clear();
        for (var i = 0; i < components.Length; i++)
        {
            var match = Match(components[i].Purl);
            if ((uint)match < (uint)matchCounts.Length) matchCounts[match]++;
        }
    }

    /// <summary>Returns the index of the first matching prefix, or <c>-1</c> when none matches.</summary>
    public int Match(Utf8Slice purl)
    {
        if (purl.IsEmpty) return -1;

        var value = purl.Span;
        for (var i = 0; i < prefixes.Length; i++)
        {
            var prefix = prefixes[i].AsSpan();
            if (value.Length < prefix.Length || !value[..prefix.Length].SequenceEqual(prefix)) continue;
            if (value.Length == prefix.Length || IsSeparator((char)prefix[^1])) return i;
            if (value[prefix.Length] is (byte)'/' or (byte)'@') return i;
        }

        return -1;
    }

    /// <summary>
    /// Rewrites a namespace <c>@</c> into its canonical percent-encoded form so a user can write the prefix the way the
    /// ecosystem spells it.
    /// </summary>
    /// <remarks>
    /// A purl encodes an npm scope as <c>%40acme</c> while people write <c>@acme</c>, and requiring the encoded form
    /// made a correct-looking prefix match nothing. Only an <c>@</c> that starts a segment is a namespace marker; the
    /// one that separates a version is left alone, so <c>pkg:npm/left-pad@1.3.0</c> still addresses one component.
    /// </remarks>
    private static string CanonicalizeScope(ReadOnlySpan<char> value)
    {
        var typeSeparator = value[4..].IndexOf('/') + 5;
        var scopeStart = -1;
        for (var i = typeSeparator; i < value.Length; i++)
        {
            if (value[i] != '@') continue;
            if (i == typeSeparator || value[i - 1] == '/')
            {
                scopeStart = i;
                break;
            }
        }

        if (scopeStart < 0) return value.ToString();

        return string.Concat(value[..scopeStart], "%40", value[(scopeStart + 1)..]);
    }

    /// <summary>
    /// Requires a prefix to name an ecosystem. It may stop there, selecting that whole ecosystem.
    /// </summary>
    /// <remarks>
    /// Selecting a whole ecosystem was once rejected as too broad, but a generator can catalogue an ecosystem the
    /// project never depended on, and enumerating every namespace it emits is not a stable answer. Breadth is
    /// answered by visibility instead: the selected count is always reported, per prefix under verbose. A prefix
    /// naming no ecosystem stays rejected because it selects everything.
    /// </remarks>
    private static bool IsPackageUrlPrefix(ReadOnlySpan<char> value)
    {
        if (!value.StartsWith("pkg:", StringComparison.Ordinal)) return false;

        var remainder = value[4..];
        var separator = remainder.IndexOf('/');
        var ecosystem = separator < 0 ? remainder : remainder[..separator];
        if (ecosystem.IsEmpty) return false;

        // A trailing separator states the prefix's own boundary, so an ecosystem alone is complete either way. What
        // must not pass is a tail made only of separators, which names a namespace the writer never finished.
        if (separator < 0) return true;

        var tail = remainder[(separator + 1)..];
        if (tail.IsEmpty) return true;
        for (var i = 0; i < tail.Length; i++)
        {
            if (!IsSeparator(tail[i])) return true;
        }

        return false;
    }

    private static bool IsSeparator(char value) => value is '/' or '.' or '@';

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
