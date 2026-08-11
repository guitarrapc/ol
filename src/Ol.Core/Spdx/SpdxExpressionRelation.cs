namespace Ol.Core.Spdx;

/// <summary>
/// Decides whether one normalized SPDX expression states anything another does not already account for.
/// </summary>
/// <remarks>
/// <para>
/// Two sources describing one package are usually not two claims of equal completeness. A publisher
/// states an expression; a detector answers with the one license it identified. Comparing them as text,
/// or as sets that must match, made collecting more evidence produce a worse result than collecting
/// none, so the comparison is a relation rather than an equality.
/// </para>
/// <para>
/// Deliberately shallow: nothing is distributed or simplified beyond what the normalizer established, a
/// <c>WITH</c> exception is one license, and anything undecidable this way stays a disagreement.
/// </para>
/// </remarks>
internal static class SpdxExpressionRelation
{
    private static ReadOnlySpan<byte> Or => " OR "u8;
    private static ReadOnlySpan<byte> And => " AND "u8;

    /// <summary>
    /// Reports whether <paramref name="stated"/> already accounts for everything <paramref name="observed"/> says.
    /// </summary>
    /// <param name="observed">The expression being checked against the other.</param>
    /// <param name="stated">The expression that may already cover it.</param>
    /// <returns><see langword="true"/> when the two do not disagree.</returns>
    /// <remarks>
    /// The two rules of expression agreement in spdx.md: a choice covered by a wider choice, and a single license
    /// named among the licenses another expression requires. Keep the second restricted to one license on one side —
    /// two compound expressions can share a license and still require different terms.
    /// </remarks>
    public static bool IsAccountedFor(ReadOnlySpan<byte> observed, ReadOnlySpan<byte> stated)
        => IsSubsetOf(observed, stated)
        || (IsSingleLicense(observed) && Names(stated, observed));

    /// <summary>
    /// Reports whether every top-level disjunct of <paramref name="subset"/> also occurs in
    /// <paramref name="superset"/>.
    /// </summary>
    /// <remarks>
    /// Both expressions must already be normalized. Operator spelling and spacing are canonical there,
    /// so a separator can be located by scanning bytes without parsing the expression again. A disjunct
    /// is the exact normalized text between top-level <c>OR</c> operators, and nothing inside one is
    /// interpreted: a conjunction, a parenthesized group, and a <c>WITH</c> exception are each compared
    /// as a whole. An expression whose top level is <c>AND</c> therefore has exactly one disjunct,
    /// itself.
    /// </remarks>
    public static bool IsSubsetOf(ReadOnlySpan<byte> subset, ReadOnlySpan<byte> superset)
    {
        var remainder = subset;
        while (!remainder.IsEmpty)
        {
            if (!ContainsDisjunct(superset, TakeDisjunct(ref remainder)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reports whether an expression is one license, with or without a <c>WITH</c> exception.</summary>
    private static bool IsSingleLicense(ReadOnlySpan<byte> value)
        => !value.IsEmpty
        && !value.ContainsAny((byte)'(', (byte)')')
        && value.IndexOf(Or) < 0
        && value.IndexOf(And) < 0;

    /// <summary>Reports whether <paramref name="expression"/> names <paramref name="license"/> among its licenses.</summary>
    private static bool Names(ReadOnlySpan<byte> expression, ReadOnlySpan<byte> license)
    {
        var remainder = expression;
        while (!remainder.IsEmpty)
        {
            if (TakeLicense(ref remainder).SequenceEqual(license))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDisjunct(ReadOnlySpan<byte> expression, ReadOnlySpan<byte> disjunct)
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
            else if (depth == 0 && current == (byte)' ' && remainder[i..].StartsWith(Or))
            {
                var disjunct = remainder[..i];
                remainder = remainder[(i + Or.Length)..];
                return disjunct;
            }
        }

        var last = remainder;
        remainder = default;
        return last;
    }

    /// <summary>
    /// Takes the next license and advances <paramref name="remainder"/> past its operator.
    /// </summary>
    /// <remarks>
    /// Splits on both operators at every depth, because which licenses an expression names does not
    /// depend on how they are grouped. Grouping parentheses are stripped from the result; a license
    /// identifier never contains one, so nothing else can be removed by mistake. <c>WITH</c> is not an
    /// operator here: the exception is part of the license it modifies, so
    /// <c>Apache-2.0 WITH LLVM-exception</c> is one name and is not matched by <c>Apache-2.0</c>.
    /// </remarks>
    private static ReadOnlySpan<byte> TakeLicense(ref ReadOnlySpan<byte> remainder)
    {
        for (var i = 0; i < remainder.Length; i++)
        {
            if (remainder[i] != (byte)' ')
            {
                continue;
            }

            var rest = remainder[i..];
            if (rest.StartsWith(Or))
            {
                var license = remainder[..i];
                remainder = remainder[(i + Or.Length)..];
                return Unwrap(license);
            }

            if (rest.StartsWith(And))
            {
                var license = remainder[..i];
                remainder = remainder[(i + And.Length)..];
                return Unwrap(license);
            }
        }

        var last = remainder;
        remainder = default;
        return Unwrap(last);
    }

    private static ReadOnlySpan<byte> Unwrap(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && (value[start] == (byte)'(' || value[start] == (byte)' '))
        {
            start++;
        }

        while (end > start && (value[end - 1] == (byte)')' || value[end - 1] == (byte)' '))
        {
            end--;
        }

        return value[start..end];
    }
}
