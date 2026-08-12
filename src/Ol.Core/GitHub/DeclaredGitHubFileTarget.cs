namespace Ol.Core.GitHub;

/// <summary>Identifies one exact file named by a trusted public GitHub URL.</summary>
public readonly record struct DeclaredGitHubFileTarget(string Owner, string Name, string Ref, string Path)
{
    /// <summary>Gets the operation-local deduplication identity.</summary>
    public string CacheKey { get; } = CreateCacheKey(Owner, Name, Ref, Path);

    /// <summary>Parses GitHub blob and raw-content URLs whose ref occupies one unambiguous path segment.</summary>
    public static bool TryCreate(string value, out DeclaredGitHubFileTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 4096
            || ContainsUnsafeCharacter(value)
            || ContainsDotSegment(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            return false;
        }

        var path = uri.AbsolutePath.AsSpan().Trim('/');
        var offset = 0;
        if (!TryTakeSegment(path, ref offset, out var ownerRange)
            || !TryTakeSegment(path, ref offset, out var repositoryRange)) return false;

        Range referenceRange;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryTakeSegment(path, ref offset, out var markerRange)
                || !path[markerRange].Equals("blob", StringComparison.Ordinal)
                || !TryTakeSegment(path, ref offset, out referenceRange)) return false;
        }
        else if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("raw.github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryTakeSegment(path, ref offset, out referenceRange)) return false;
        }
        else
        {
            return false;
        }

        var reference = path[referenceRange];
        if (offset >= path.Length || reference.Length > 256) return false;
        var file = path[offset..];
        if (file.Length > 2048 || HasEmptyOrDotSegment(file)) return false;

        target = new DeclaredGitHubFileTarget(path[ownerRange].ToString(), path[repositoryRange].ToString(), reference.ToString(), file.ToString());
        return true;
    }

    private static bool TryTakeSegment(ReadOnlySpan<char> path, ref int offset, out Range range)
    {
        range = default;
        if ((uint)offset >= (uint)path.Length) return false;
        var start = offset;
        var remaining = path[offset..];
        var separator = remaining.IndexOf('/');
        if (separator < 0)
        {
            range = start..path.Length;
            offset = path.Length;
        }
        else
        {
            range = start..(start + separator);
            offset += separator + 1;
        }

        var segment = path[range];
        return !segment.IsEmpty && !segment.SequenceEqual(".".AsSpan()) && !segment.SequenceEqual("..".AsSpan());
    }

    private static bool ContainsUnsafeCharacter(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]) || value[index] is '%' or '\\') return true;
        }

        return false;
    }

    private static bool ContainsDotSegment(string value)
    {
        var schemeEnd = value.AsSpan().IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;
        var pathStart = value.AsSpan(schemeEnd + 3).IndexOf('/');
        return pathStart >= 0 && HasEmptyOrDotSegment(value.AsSpan(schemeEnd + 3 + pathStart + 1), rejectEmpty: false);
    }

    private static bool HasEmptyOrDotSegment(ReadOnlySpan<char> path, bool rejectEmpty = true)
    {
        while (true)
        {
            var separator = path.IndexOf('/');
            var segment = separator < 0 ? path : path[..separator];
            if ((rejectEmpty && segment.IsEmpty) || segment.SequenceEqual(".".AsSpan()) || segment.SequenceEqual("..".AsSpan())) return true;
            if (separator < 0) return false;
            path = path[(separator + 1)..];
        }
    }

    private static string CreateCacheKey(string owner, string name, string reference, string path)
    {
        const string prefix = "github-file:";
        return string.Create(
            prefix.Length + owner.Length + name.Length + reference.Length + path.Length + 3,
            (owner, name, reference, path),
            static (destination, state) =>
            {
                prefix.CopyTo(destination);
                var offset = prefix.Length;
                CopyLowerInvariant(state.owner, destination[offset..]);
                offset += state.owner.Length;
                destination[offset++] = '/';
                CopyLowerInvariant(state.name, destination[offset..]);
                offset += state.name.Length;
                destination[offset++] = '@';
                state.reference.CopyTo(destination[offset..]);
                offset += state.reference.Length;
                destination[offset++] = '/';
                state.path.CopyTo(destination[offset..]);
            });
    }

    private static void CopyLowerInvariant(ReadOnlySpan<char> value, Span<char> destination)
    {
        for (var index = 0; index < value.Length; index++)
        {
            destination[index] = char.ToLowerInvariant(value[index]);
        }
    }
}
