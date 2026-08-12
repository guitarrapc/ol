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
