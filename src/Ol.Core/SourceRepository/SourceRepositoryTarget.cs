namespace Ol.Core.SourceRepository;

/// <summary>Identifies a normalized GitHub repository license lookup.</summary>
/// <remarks>
/// <see cref="Repository"/> and <see cref="CacheKey"/> are derived once at construction. They were
/// calculated properties, and enrichment reads them once per component for cache lookup, dedup, and
/// evidence, so each read concatenated the same strings again. A <see langword="default"/> instance
/// never reaches those reads: every consumer obtains a target from <see cref="TryCreate(string, string?, out SourceRepositoryTarget)"/>.
/// </remarks>
public readonly record struct SourceRepositoryTarget(string Owner, string Name, string Ref)
{
    /// <summary>Gets the logical owner/repository reference.</summary>
    public string Repository { get; } = string.Concat(Owner, "/", Name);

    /// <summary>Gets the opaque-cache logical key.</summary>
    public string CacheKey { get; } = string.Concat("github:", Owner, "/", Name, "@", Ref);

    /// <summary>Normalizes common GitHub repository URL forms.</summary>
    public static bool TryCreate(string repositoryUrl, out SourceRepositoryTarget target)
        => TryCreate(repositoryUrl, string.Empty, out target);

    /// <summary>Normalizes a GitHub repository URL and optional package-version ref.</summary>
    public static bool TryCreate(string repositoryUrl, string? repositoryRef, out SourceRepositoryTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return false;
        }

        ReadOnlySpan<char> value = repositoryUrl.AsSpan().Trim();
        if (value.StartsWith("git@github.com:"))
        {
            value = value["git@github.com:".Length..];
        }
        else
        {
            var prefix = value.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ? "git+".Length : 0;
            var candidate = value[prefix..];
            if (TryGetGitHubPath(candidate, out var path))
            {
                value = path.Trim('/');
            }
            else
            {
                if (!Uri.TryCreate(candidate.ToString(), UriKind.Absolute, out var uri)
                    || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                value = uri.AbsolutePath.AsSpan().Trim('/');
            }
        }

        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        var owner = value[..separator];
        var name = value[(separator + 1)..];
        var trailing = name.IndexOf('/');
        if (trailing >= 0)
        {
            name = name[..trailing];
        }

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (owner.IsEmpty || name.IsEmpty)
        {
            return false;
        }

        if (!IsValidRef(repositoryRef))
        {
            return false;
        }

        target = new SourceRepositoryTarget(owner.ToString(), name.ToString(), repositoryRef!.Length == 0 ? "default" : repositoryRef);
        return true;
    }

    /// <summary>Extracts the path of a plain <c>github.com</c> URL without constructing a <see cref="Uri"/>.</summary>
    /// <param name="value">The repository URL, with any <c>git+</c> prefix already removed.</param>
    /// <param name="path">The path component, when this URL is a shape the scan reproduces exactly.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> was extracted.</returns>
    /// <remarks>
    /// Deliberately narrow. <see cref="Uri"/> rewrites a URL in ways a scan does not — it unescapes
    /// unreserved percent escapes, collapses <c>.</c> and <c>..</c> segments, rewrites backslashes, and
    /// validates the scheme and port — so anything carrying those constructs returns
    /// <see langword="false"/> and leaves the answer to <see cref="Uri"/>. Package metadata supplies a
    /// plain repository URL almost every time, and that case allocates nothing here.
    /// </remarks>
    private static bool TryGetGitHubPath(ReadOnlySpan<char> value, out ReadOnlySpan<char> path)
    {
        path = default;
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || !IsKnownScheme(value[..schemeEnd]))
        {
            return false;
        }

        var afterScheme = value[(schemeEnd + 3)..];
        var authorityEnd = afterScheme.IndexOfAny('/', '?', '#');
        var authority = authorityEnd < 0 ? afterScheme : afterScheme[..authorityEnd];
        var userInfoEnd = authority.LastIndexOf('@');
        if (userInfoEnd >= 0)
        {
            authority = authority[(userInfoEnd + 1)..];
        }

        var portStart = authority.IndexOf(':');
        if (portStart >= 0)
        {
            if (!IsDigits(authority[(portStart + 1)..]))
            {
                return false;
            }

            authority = authority[..portStart];
        }

        if (!authority.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = authorityEnd < 0 ? default : afterScheme[authorityEnd..];
        var queryStart = remainder.IndexOfAny('?', '#');
        if (queryStart >= 0)
        {
            remainder = remainder[..queryStart];
        }

        if (remainder.ContainsAny('%', '\\') || remainder.Contains("/.", StringComparison.Ordinal))
        {
            return false;
        }

        path = remainder;
        return true;
    }

    private static bool IsKnownScheme(ReadOnlySpan<char> scheme)
        => scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
        || scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
        || scheme.Equals("git", StringComparison.OrdinalIgnoreCase)
        || scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase);

    private static bool IsDigits(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidRef(string? value)
    {
        if (value is null || value.Length > 256)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Contains the GitHub authentication mode without exposing credentials.</summary>
public readonly record struct GitHubAuthentication(string Mode, string Token)
{
    /// <summary>Creates authentication from the dedicated Ol token only.</summary>
    public static GitHubAuthentication Create(string? olGitHubToken = null, string? githubToken = null)
        => string.IsNullOrEmpty(olGitHubToken) ? new("none", string.Empty) : new("ol_github_token", olGitHubToken);

    /// <summary>Reads only the dedicated Ol environment variable.</summary>
    public static GitHubAuthentication FromEnvironment()
        => Create(Environment.GetEnvironmentVariable("OL_GITHUB_TOKEN"));
}
