namespace Ol.Core.PackageManagers;

/// <summary>
/// Rewrites Cargo's pre-SPDX license spelling into the SPDX expression it stands for.
/// </summary>
/// <remarks>
/// Cargo documents <c>/</c> as the deprecated form of <c>OR</c>, so <c>MIT/Apache-2.0</c> denotes an expression
/// rather than an unknown license to guess at. Only the separator is rewritten, and only when the value carries one.
/// The rewrite is a classification input, not a replacement: the candidate keeps the spelling the crate published.
/// </remarks>
public static class CargoLicenseExpression
{
    private static ReadOnlySpan<byte> Separator => " OR "u8;

    /// <summary>
    /// Rewrites every <c>/</c> separator into an SPDX <c>OR</c> operator.
    /// </summary>
    /// <param name="value">The license value exactly as Cargo published it.</param>
    /// <param name="rewritten">The SPDX expression the legacy spelling denotes.</param>
    /// <returns><see langword="true"/> when the value used the legacy separator.</returns>
    public static bool TryRewriteLegacyChoice(ReadOnlySpan<byte> value, out byte[] rewritten)
    {
        var separators = value.Count((byte)'/');
        if (separators == 0)
        {
            rewritten = [];
            return false;
        }

        rewritten = new byte[value.Length + (separators * (Separator.Length - 1))];
        var written = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != (byte)'/')
            {
                rewritten[written++] = value[i];
                continue;
            }

            // A legacy value spells the choice as `MIT/Apache-2.0`, without the spaces the operator needs,
            // but `MIT / Apache-2.0` occurs too. Trimming around the separator keeps both forms canonical.
            while (written > 0 && rewritten[written - 1] == (byte)' ')
            {
                written--;
            }

            Separator.CopyTo(rewritten.AsSpan(written));
            written += Separator.Length;
            while (i + 1 < value.Length && value[i + 1] == (byte)' ')
            {
                i++;
            }
        }

        if (written != rewritten.Length)
        {
            Array.Resize(ref rewritten, written);
        }

        return true;
    }
}
