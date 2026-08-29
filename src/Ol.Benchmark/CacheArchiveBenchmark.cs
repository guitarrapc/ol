using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Internals;

/// <summary>
/// Measures the cache archive and cache directory operations: packing, restoring, inspecting, listing, and
/// pruning.
///
/// Two shapes are measured rather than one because every regression these operations have had scaled with the
/// entry count while the content stayed constant, so a single shape of a few large entries reports a bounded
/// cost for a design whose cost is not bounded. <see cref="ManyEntries"/> is what catches a per-entry
/// allocation; <see cref="LargeEntries"/> is what catches one that scales with content.
/// </summary>
[MemoryDiagnoser]
public class CacheArchiveBenchmark : IDisposable
{
    private const int ManyEntries = 512;
    private const int LargeEntries = 4;
    private const int LargeEntryBytes = 256 * 1024;

    private readonly string root;
    private readonly string manyArchive;
    private readonly string largeArchive;
    private readonly CacheDirectories manySource;
    private readonly CacheDirectories largeSource;
    private readonly string restoreRoot;

    public CacheArchiveBenchmark()
    {
        root = Path.Combine(Path.GetTempPath(), $"ol-cache-archive-benchmark-{Guid.NewGuid():N}");
        restoreRoot = Path.Combine(root, "restore");
        manySource = WriteCache(Path.Combine(root, "many"), ManyEntries, contentBytes: 0);
        largeSource = WriteCache(Path.Combine(root, "large"), LargeEntries, LargeEntryBytes);
        manyArchive = Path.Combine(root, "many.olcache");
        largeArchive = Path.Combine(root, "large.olcache");
        CacheArchive.Pack(manyArchive, manySource, maximumAge: null, DateTimeOffset.UnixEpoch);
        CacheArchive.Pack(largeArchive, largeSource, maximumAge: null, DateTimeOffset.UnixEpoch);
    }

    [Benchmark]
    public int PackManyEntries()
        => CacheArchive.Pack(Path.Combine(root, "pack-many.olcache"), manySource, maximumAge: null, DateTimeOffset.UnixEpoch).EntryCount;

    /// <summary>Restores into a directory of its own so a run never measures replacing what a prior run left.</summary>
    [Benchmark]
    public int UnpackManyEntries() => Unpack(manyArchive);

    [Benchmark]
    public int UnpackLargeEntries() => Unpack(largeArchive);

    [Benchmark]
    public int InspectManyEntryArchive() => CacheArchive.Inspect(manyArchive).EntryCount;

    [Benchmark]
    public int InspectLargeEntryArchive() => CacheArchive.Inspect(largeArchive).EntryCount;

    [Benchmark]
    public int InspectDirectory() => CacheArchive.Inspect(manySource).EntryCount;

    [Benchmark]
    public int SummarizeDirectory() => CacheArchive.Summarize(manySource).EntryCount;

    /// <summary>A cutoff no entry meets, so every entry is read and measured and none is deleted.</summary>
    [Benchmark]
    public long PruneDirectoryDryRun()
        => CacheArchive.Prune(manySource, TimeSpan.FromDays(36500), DateTimeOffset.UtcNow, dryRun: true).BeforeBytes;

    private int Unpack(string archivePath)
    {
        var destination = Path.Combine(restoreRoot, Guid.NewGuid().ToString("N"));
        try
        {
            return CacheArchive.Unpack(archivePath, new CacheDirectories(
                Path.Combine(destination, "package-metadata"),
                Path.Combine(destination, "source-repository"),
                Path.Combine(destination, "github-file")));
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    private static CacheDirectories WriteCache(string cacheRoot, int entryCount, int contentBytes)
    {
        var directories = new CacheDirectories(
            Path.Combine(cacheRoot, "package-metadata"),
            Path.Combine(cacheRoot, "source-repository"),
            Path.Combine(cacheRoot, "github-file"));
        var packages = new PackageMetadataCache(directories.PackageMetadata);
        var sources = new SourceRepositoryCache(directories.SourceRepository);
        for (var i = 0; i < entryCount; i++)
        {
            var filler = contentBytes == 0 ? [] : new[] { new string('a', contentBytes) };
            packages
                .WriteAsync(new PackageMetadataRecord(
                    FormattableString.Invariant($"pkg:npm/benchmark-{i}@1.0.0"),
                    "npm-registry",
                    "MIT",
                    string.Empty,
                    filler,
                    [],
                    DateTimeOffset.UnixEpoch))
                .GetAwaiter()
                .GetResult();
            var target = new SourceRepositoryTarget("owner", FormattableString.Invariant($"repository-{i}"), "default");
            sources
                .WriteAsync(new SourceRepositoryRecord(
                    target.CacheKey,
                    "github-license-api",
                    "none",
                    target.Repository,
                    target.Ref,
                    System.Net.HttpStatusCode.OK,
                    new GitHubLicenseResult("MIT", "mit", "MIT License", "LICENSE", "sha", string.Empty),
                    [],
                    [],
                    DateTimeOffset.UnixEpoch))
                .GetAwaiter()
                .GetResult();
        }

        return directories;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
