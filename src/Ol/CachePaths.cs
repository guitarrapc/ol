internal readonly record struct CacheDirectories(string PackageMetadata, string SourceRepository);

internal static class CachePaths
{
    public static CacheDirectories Resolve(string? cacheDirectory)
    {
        if (cacheDirectory is not null)
        {
            return ResolveUnifiedRoot(cacheDirectory);
        }

        var environmentRoot = Environment.GetEnvironmentVariable("OL_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return ResolveUnifiedRoot(environmentRoot);
        }

        return new CacheDirectories(PackageMetadataPaths.DefaultRoot, SourceRepositoryPaths.DefaultRoot);
    }

    private static CacheDirectories ResolveUnifiedRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Cache directory must not be empty.", nameof(root));
        }

        var fullPath = Path.GetFullPath(root);
        if (File.Exists(fullPath))
        {
            throw new ArgumentException("Cache directory must not be an existing file.", nameof(root));
        }

        return new CacheDirectories(
            Path.Combine(fullPath, "package-metadata"),
            Path.Combine(fullPath, "source-repository"));
    }
}
