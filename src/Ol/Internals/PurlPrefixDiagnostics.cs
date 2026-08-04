using System.Buffers;
using Ol.Core;

namespace Ol.Internals;

/// <summary>
/// Writes how many components each supplied package URL prefix matched.
/// </summary>
/// <remarks>
/// An aggregate total cannot show that one entry matched nothing, which is what a typo or a prefix left behind after a
/// dependency was removed looks like. Diagnostics are verbose-only, so this counts separately instead of adding work to
/// the paths that apply the prefixes.
/// </remarks>
internal static class PurlPrefixDiagnostics
{
    public static void WriteMatches(string label, PurlPrefixSet prefixes, ReadOnlySpan<ScanComponent> components)
    {
        var counts = ArrayPool<int>.Shared.Rent(prefixes.Prefixes.Length);
        try
        {
            prefixes.CountMatches(components, counts.AsSpan(0, prefixes.Prefixes.Length));
            WriteMatches(label, prefixes.Prefixes, counts.AsSpan(0, prefixes.Prefixes.Length));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    public static void WriteMatches(string label, ReadOnlySpan<string> prefixes, ReadOnlySpan<int> counts)
    {
        for (var i = 0; i < prefixes.Length; i++)
        {
            var count = i < counts.Length ? counts[i] : 0;
            Console.Error.WriteLine($"{label} prefix {prefixes[i]} matched {count} component{(count == 1 ? string.Empty : "s")}.");
        }
    }
}
