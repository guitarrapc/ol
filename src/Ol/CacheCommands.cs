using ConsoleAppFramework;
using Ol.Core;
using Ol.Core.PackageMetadata;
using Ol.Core.SourceRepository;

/// <summary>
/// Manage locally cached scan evidence.
/// </summary>
internal sealed class CacheCommands
{
    /// <summary>
    /// Clears cached evidence for the specified category.
    /// </summary>
    /// <param name="category">Cache category: package-metadata, source-repository, or all.</param>
    /// <param name="cacheDir">Root directory containing the managed cache categories.</param>
    [Command("clear")]
    public int Clear(string category = "all", string? cacheDir = null)
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
            case "all":
                new PackageMetadataCache(directories.PackageMetadata).Clear();
                new SourceRepositoryCache(directories.SourceRepository).Clear();
                Console.WriteLine("package-metadata cache cleared");
                Console.WriteLine("source-repository cache cleared");
                return 0;
            default:
                Console.Error.WriteLine("Cache category must be package-metadata, source-repository, or all.");
                return 1;
        }
    }
}
