namespace Ol.Core.Spdx;

/// <summary>
/// Compares two normalized SPDX expressions by their top-level disjunct sets.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shallow. A disjunct is the exact normalized text between top-level <c>OR</c>
/// operators, and nothing inside one is interpreted: a conjunction, a parenthesized group, and a
/// <c>WITH</c> exception are each compared as a whole. The comparison therefore rests only on what
/// the normalizer already established, and every relation Ol cannot decide this way stays a
/// disagreement rather than becoming a guess.
/// </para>
/// <para>
/// A consequence worth stating: an expression whose top level is <c>AND</c> has exactly one disjunct,
/// itself. <c>(MIT OR Apache-2.0) AND Unicode-3.0</c> is not satisfied by <c>Apache-2.0</c>, because
/// distributing the conjunction would drop the other required term.
/// </para>
/// </remarks>
internal static class SpdxDisjunctSet
{
    private static ReadOnlySpan<byte> Separator => " OR "u8;

    /// <summary>
    /// Reports whether every top-level disjunct of <paramref name="subset"/> also occurs in
    /// <paramref name="superset"/>.
    /// </summary>
    /// <remarks>
    /// Both expressions must already be normalized. Operator spelling and spacing are canonical there,
    /// so a separator can be located by scanning bytes without parsing the expression again.
    /// </remarks>
    public static bool IsSubsetOf(ReadOnlySpan<byte> subset, ReadOnlySpan<byte> superset)
    {
        var remainder = subset;
        while (!remainder.IsEmpty)
        {
            if (!Contains(superset, TakeDisjunct(ref remainder)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(ReadOnlySpan<byte> expression, ReadOnlySpan<byte> disjunct)
    {
        var remainder = expression;
        while (!remainder.IsEmpty)
        {
            if (TakeDisjunct(ref remainder).SequenceEqual(disjunct))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Takes the next top-level disjunct and advances <paramref name="remainder"/> past its separator.</summary>
    private static ReadOnlySpan<byte> TakeDisjunct(ref ReadOnlySpan<byte> remainder)
    {
        var depth = 0;
        for (var i = 0; i < remainder.Length; i++)
        {
            var current = remainder[i];
            if (current == (byte)'(')
            {
                depth++;
            }
            else if (current == (byte)')')
            {
                depth--;
            }
            else if (depth == 0 && current == (byte)' ' && remainder[i..].StartsWith(Separator))
            {
                var disjunct = remainder[..i];
                remainder = remainder[(i + Separator.Length)..];
                return disjunct;
            }
        }

        var last = remainder;
        remainder = default;
        return last;
    }
}
