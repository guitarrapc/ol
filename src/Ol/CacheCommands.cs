using ConsoleAppFramework;
using Ol.Core.GitHub;
using Ol.Core.PackageMetadata;
using Ol.Core.SourceRepository;
using Ol.Internals;

/// <summary>
/// Manage locally cached scan evidence.
/// </summary>
internal sealed class CacheCommands
{
    /// <summary>
    /// Packs managed cache entries into one deterministic gzip-compressed archive.
    /// </summary>
    /// <param name="archive">Output .olcache archive path.</param>
    /// <param name="cacheDir">Root directory containing the managed cache categories.</param>
    /// <param name="maxAge">Include only entries no older than this duration, such as 30d, 12h, or 90m.</param>
    [Command("pack")]
    public int Pack([Argument] string archive, string? cacheDir = null, string? maxAge = null)
    {
        if (!CacheArchive.TryParseMaxAge(maxAge, out var maximumAge))
        {
            Console.Error.WriteLine("Max age must be a positive integer followed by d, h, or m.");
            return 1;
        }

        try
        {
            var result = CacheArchive.Pack(archive, CachePaths.Resolve(cacheDir), maximumAge, DateTimeOffset.UtcNow);
            Console.WriteLine($"Packed {result.EntryCount} cache {(result.EntryCount == 1 ? "entry" : "entries")} ({FormatBytes(result.ArchiveBytes)})");
            if (result.ArchiveBytes > CacheArchive.RecommendedArchiveBytes)
            {
                Console.Error.WriteLine("Warning: cache archive exceeds the recommended Git seed size of 1 MiB.");
                Console.Error.WriteLine($"  package-metadata: {result.PackageMetadataCount}");
                Console.Error.WriteLine($"  source-repository: {result.SourceRepositoryCount}");
                Console.Error.WriteLine($"  github-file: {result.GitHubFileCount}");
            }
            return 0;
        }
        catch (Exception exception) when (CacheArchive.IsExpectedFailure(exception))
        {
            Console.Error.WriteLine($"Cache pack failed: {exception.Message}");
            return 1;
        }
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024
            ? FormattableString.Invariant($"{bytes / (1024d * 1024):F1} MiB")
            : bytes >= 1024
                ? FormattableString.Invariant($"{bytes / 1024d:F1} KiB")
                : $"{bytes} bytes";

    private static string FormatEntryCount(int count)
        => FormattableString.Invariant($"{count} {(count == 1 ? "entry" : "entries")}");

    private static long GetCategoryBytes(CacheCategoryInfo category)
    {
        long bytes = 0;
        for (var i = 0; i < category.Entries.Count; i++) bytes = checked(bytes + category.Entries[i].Bytes);
        return bytes;
    }

    /// <summary>
    /// Unpacks one Ol cache archive into the managed cache directories.
    /// </summary>
    /// <param name="archive">Input .olcache archive path.</param>
    /// <param name="cacheDir">Root directory containing the managed cache categories.</param>
    [Command("unpack")]
    public int Unpack([Argument] string archive, string? cacheDir = null)
    {
        try
        {
            var count = CacheArchive.Unpack(archive, CachePaths.Resolve(cacheDir));
            Console.WriteLine($"Unpacked {count} cache {(count == 1 ? "entry" : "entries")}");
            return 0;
        }
        catch (Exception exception) when (CacheArchive.IsExpectedFailure(exception))
        {
            Console.Error.WriteLine($"Cache unpack failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Lists managed cache locations and their sizes.
    /// </summary>
    /// <param name="cacheDir">Root directory containing the managed cache categories.</param>
    [Command("list")]
    public int List(string? cacheDir = null)
    {
        try
        {
            var result = CacheArchive.Inspect(CachePaths.Resolve(cacheDir));
            Console.WriteLine("Cache locations:");
            for (var i = 0; i < result.Categories.Count; i++)
            {
                var category = result.Categories[i];
                Console.WriteLine($"  {category.Category}: {category.Path} ({FormatEntryCount(category.Entries.Count)}, {FormatBytes(GetCategoryBytes(category))})");
            }

            Console.WriteLine($"Total: {FormatEntryCount(result.EntryCount)} ({FormatBytes(result.TotalBytes)})");
            return 0;
        }
        catch (Exception exception) when (CacheArchive.IsExpectedFailure(exception))
        {
            Console.Error.WriteLine($"Cache list failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Shows the contents of a cache directory or archive.
    /// </summary>
    /// <param name="path">Cache directory or .olcache archive path. Defaults to the resolved cache location.</param>
    /// <param name="cacheDir">Root directory containing the managed cache categories when path is omitted.</param>
    [Command("info")]
    public int Info([Argument] string? path = null, string? cacheDir = null)
    {
        try
        {
            var result = path is null
                ? CacheArchive.Inspect(CachePaths.Resolve(cacheDir), cacheDir is null ? null : Path.GetFullPath(cacheDir))
                : CacheArchive.Inspect(path);
            Console.WriteLine($"Type: {(result.IsArchive ? "cache archive" : "cache directory")}");
            if (!string.IsNullOrEmpty(result.Path)) Console.WriteLine($"Path: {result.Path}");
            Console.WriteLine($"Entries: {FormatEntryCount(result.EntryCount)} ({FormatBytes(result.TotalBytes)})");
            for (var i = 0; i < result.Categories.Count; i++)
            {
                var category = result.Categories[i];
                Console.WriteLine($"{category.Category}: {category.Path} ({FormatEntryCount(category.Entries.Count)})");
                for (var j = 0; j < category.Entries.Count; j++)
                {
                    var entry = category.Entries[j];
                    if (entry.Error is not null)
                    {
                        Console.WriteLine($"  {entry.Name}: invalid ({FormatBytes(entry.Bytes)}) - {entry.Error}");
                        continue;
                    }

                    Console.WriteLine($"  {entry.CacheKey}  fetched-at={entry.FetchedAt:O}  size={FormatBytes(entry.Bytes)}");
                }

                if (category.UnmanagedFileCount > 0)
                {
                    Console.WriteLine($"  unmanaged files: {category.UnmanagedFileCount}");
                }
            }

            return 0;
        }
        catch (Exception exception) when (CacheArchive.IsExpectedFailure(exception))
        {
            Console.Error.WriteLine($"Cache info failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Removes managed cache entries older than the specified age.
    /// </summary>
    /// <param name="maxAge">Remove entries older than this duration, such as 30d, 12h, or 90m.</param>
    /// <param name="dryRun">Report the entries and bytes that would be removed without changing the cache.</param>
    /// <param name="cacheDir">Root directory containing the managed cache categories.</param>
    [Command("prune")]
    public int Prune(string maxAge, bool dryRun = false, string? cacheDir = null)
    {
        if (!CacheArchive.TryParseMaxAge(maxAge, out var maximumAge))
        {
            Console.Error.WriteLine("Max age must be a positive integer followed by d, h, or m.");
            return 1;
        }

        try
        {
            var result = CacheArchive.Prune(CachePaths.Resolve(cacheDir), maximumAge!.Value, DateTimeOffset.UtcNow, dryRun);
            var verb = dryRun ? "Would prune" : "Pruned";
            var reclaimed = dryRun ? "free" : "freed";
            Console.WriteLine($"{verb} {result.PrunedCount} cache {(result.PrunedCount == 1 ? "entry" : "entries")} ({reclaimed} {FormatBytes(result.ReclaimedBytes)}; managed cache {FormatBytes(result.BeforeBytes)} -> {FormatBytes(result.AfterBytes)})");
            return 0;
        }
        catch (Exception exception) when (CacheArchive.IsExpectedFailure(exception))
        {
            Console.Error.WriteLine($"Cache prune failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Clears cached evidence for the specified category.
    /// </summary>
    /// <param name="category">Cache category: package-metadata, source-repository, github-file, or all. [Default: all]</param>
    /// <param name="cacheDir">Root directory containing the managed cache categories.</param>
    [Command("clear")]
    public int Clear([Argument] string category = "all", string? cacheDir = null)
    {
        CacheDirectories directories;
        try
        {
            directories = CachePaths.Resolve(cacheDir);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine($"Invalid cache directory: {exception.Message}");
            return 1;
        }

        switch (category.ToLowerInvariant())
        {
            case "package-metadata":
                new PackageMetadataCache(directories.PackageMetadata).Clear();
                Console.WriteLine("package-metadata cache cleared");
                return 0;
            case "source-repository":
                new SourceRepositoryCache(directories.SourceRepository).Clear();
                Console.WriteLine("source-repository cache cleared");
                return 0;
            case "github-file":
                new DeclaredGitHubFileCache(directories.GitHubFile).Clear();
                Console.WriteLine("github-file cache cleared");
                return 0;
            case "all":
                new PackageMetadataCache(directories.PackageMetadata).Clear();
                new SourceRepositoryCache(directories.SourceRepository).Clear();
                new DeclaredGitHubFileCache(directories.GitHubFile).Clear();
                Console.WriteLine("package-metadata cache cleared");
                Console.WriteLine("source-repository cache cleared");
                Console.WriteLine("github-file cache cleared");
                return 0;
            default:
                Console.Error.WriteLine("Cache category must be package-metadata, source-repository, github-file, or all.");
                return 1;
        }
    }
}
