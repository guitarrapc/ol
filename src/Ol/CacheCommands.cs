using ConsoleAppFramework;
using Ol.Core;

/// <summary>
/// Manage locally cached scan evidence.
/// </summary>
internal sealed class CacheCommands
{
    /// <summary>
    /// Clears cached evidence for the specified category.
    /// </summary>
    /// <param name="category">Cache category: package-metadata, source-repository, or all.</param>
    [Command("clear")]
    public int Clear(string category = "all")
    {
        switch (category.ToLowerInvariant())
        {
            case "package-metadata":
            case "all":
                new PackageMetadataCache(PackageMetadataPaths.DefaultRoot).Clear();
                Console.WriteLine("package-metadata cache cleared");
                return 0;
            case "source-repository":
                Console.WriteLine("source-repository cache cleared");
                return 0;
            default:
                Console.Error.WriteLine("Cache category must be package-metadata, source-repository, or all.");
                return 1;
        }
    }
}
