using System.Formats.Tar;
using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ol.Internals;

internal readonly record struct CacheArchiveLimits(
    long MaximumArchiveBytes,
    long MaximumEntryBytes,
    long MaximumExpandedBytes,
    int MaximumEntryCount);

internal readonly record struct CachePackResult(
    int EntryCount,
    long ArchiveBytes,
    int PackageMetadataCount,
    int SourceRepositoryCount,
    int GitHubFileCount);

internal readonly record struct CachePruneResult(
    int PrunedCount,
    long BeforeBytes,
    long AfterBytes)
{
    public long ReclaimedBytes => BeforeBytes - AfterBytes;
}

/// <summary>
/// One inspected cache entry. <paramref name="CacheKey"/> and <paramref name="Name"/> are the entry's two
/// possible identities and exactly one of them is present: an entry that validated is named by its logical
/// key, and one that did not has only the physical file name left to report it by.
/// </summary>
internal readonly record struct CacheEntryInfo(
    string Category,
    string? Name,
    string? CacheKey,
    DateTimeOffset? FetchedAt,
    long Bytes,
    string? Error);

internal readonly record struct CacheCategoryInfo(
    string Category,
    string Path,
    IReadOnlyList<CacheEntryInfo> Entries,
    int UnmanagedFileCount);

internal readonly record struct CacheCategorySummary(
    string Category,
    string Path,
    int EntryCount,
    long Bytes);

/// <summary>
/// What a cache listing reports: where each managed category is, how many entries it holds, and how many
/// bytes they occupy. None of those answers depends on an entry's content, so a summary never reads one.
/// </summary>
internal readonly record struct CacheSummary(
    IReadOnlyList<CacheCategorySummary> Categories)
{
    public int EntryCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < Categories.Count; i++) count += Categories[i].EntryCount;
            return count;
        }
    }

    public long TotalBytes
    {
        get
        {
            long bytes = 0;
            for (var i = 0; i < Categories.Count; i++) bytes = checked(bytes + Categories[i].Bytes);
            return bytes;
        }
    }
}

internal readonly record struct CacheInspectionResult(
    bool IsArchive,
    string Path,
    IReadOnlyList<CacheCategoryInfo> Categories)
{
    public int EntryCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < Categories.Count; i++) count += Categories[i].Entries.Count;
            return count;
        }
    }

    public long TotalBytes
    {
        get
        {
            long bytes = 0;
            for (var i = 0; i < Categories.Count; i++)
            {
                var entries = Categories[i].Entries;
                for (var j = 0; j < entries.Count; j++) bytes = checked(bytes + entries[j].Bytes);
            }

            return bytes;
        }
    }
}

internal static class CacheArchive
{
    private const string ManifestName = "ol-cache-manifest.json";
    private const string ManifestContent = "{\"FormatVersion\":1}";
    private const int MaximumStackCacheKeyBytes = 512;
    private const int InitialManagedFileCapacity = 256;
    private const int CacheKeyHashLength = 64;
    private const int CacheFileNameLength = CacheKeyHashLength + 5;

    // Reproduces what Directory.EnumerateFiles(root, "*", TopDirectoryOnly) enumerated: an entry is skipped
    // for nothing, hidden and system included, and an inaccessible one surfaces rather than disappearing.
    private static readonly EnumerationOptions ManagedEntryEnumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
    };
    private static ReadOnlySpan<byte> Utf8Preamble => [0xEF, 0xBB, 0xBF];
    private static readonly DateTimeOffset DeterministicTimestamp = DateTimeOffset.UnixEpoch;
    internal static readonly long RecommendedArchiveBytes = 1L * 1024 * 1024;
    internal static readonly CacheArchiveLimits DefaultLimits = new(
        MaximumArchiveBytes: 8L * 1024 * 1024,
        MaximumEntryBytes: 2L * 1024 * 1024,
        MaximumExpandedBytes: 64L * 1024 * 1024,
        MaximumEntryCount: 10_000);
    private static readonly CacheCategory[] Categories =
    [
        new("package-metadata"),
        new("source-repository"),
        new("github-file"),
    ];

    public static CachePackResult Pack(string archivePath, CacheDirectories directories, TimeSpan? maximumAge, DateTimeOffset now)
        => Pack(archivePath, directories, maximumAge, now, DefaultLimits);

    internal static CachePackResult Pack(string archivePath, CacheDirectories directories, TimeSpan? maximumAge, DateTimeOffset now, CacheArchiveLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ValidateCachePaths(directories);
        var outputPath = Path.GetFullPath(archivePath);
        ValidateLinkFreePath(outputPath, "Archive");
        ValidateArchiveOutsideCache(outputPath, directories);
        var entries = CollectEntries(directories, maximumAge, now, limits);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        ValidateArchiveOutputPaths(outputPath, temporaryPath);
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var bounded = new MaximumLengthWriteStream(output, limits.MaximumArchiveBytes, leaveOpen: true))
            using (var gzip = new GZipStream(bounded, CompressionLevel.SmallestSize, leaveOpen: false))
            using (var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: false))
            {
                using var manifest = new MemoryStream(Encoding.UTF8.GetBytes(ManifestContent), writable: false);
                long expandedBytes = manifest.Length;
                if (expandedBytes > limits.MaximumExpandedBytes)
                {
                    throw new InvalidDataException($"Expanded archive exceeds {limits.MaximumExpandedBytes} bytes.");
                }

                WriteEntry(writer, ManifestName, manifest);
                for (var i = 0; i < entries.Count; i++)
                {
                    ValidateLinkFreePath(entries[i].SourcePath);
                    using var content = new FileStream(entries[i].SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    expandedBytes = checked(expandedBytes + content.Length);
                    if (expandedBytes > limits.MaximumExpandedBytes)
                    {
                        throw new InvalidDataException($"Expanded archive exceeds {limits.MaximumExpandedBytes} bytes.");
                    }

                    WriteEntry(writer, entries[i].ArchivePath, content);
                }
            }

            var archiveLength = new FileInfo(temporaryPath).Length;
            if (archiveLength <= 0 || archiveLength > limits.MaximumArchiveBytes)
            {
                throw new InvalidDataException($"Archive size must be between 1 and {limits.MaximumArchiveBytes} bytes.");
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
            var packageMetadataCount = 0;
            var sourceRepositoryCount = 0;
            var githubFileCount = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                switch (entries[i].CategoryIndex)
                {
                    case 0: packageMetadataCount++; break;
                    case 1: sourceRepositoryCount++; break;
                    case 2: githubFileCount++; break;
                }
            }

            return new(entries.Count, archiveLength, packageMetadataCount, sourceRepositoryCount, githubFileCount);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static int Unpack(string archivePath, CacheDirectories directories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ValidateCachePaths(directories);
        var inputPath = Path.GetFullPath(archivePath);
        ValidateLinkFreePath(inputPath, "Archive");
        ValidateArchiveOutsideCache(inputPath, directories);
        var archiveLength = new FileInfo(inputPath).Length;
        if (archiveLength <= 0 || archiveLength > DefaultLimits.MaximumArchiveBytes)
        {
            throw new InvalidDataException($"Archive size must be between 1 and {DefaultLimits.MaximumArchiveBytes} bytes.");
        }

        var stagingRoot = CreatePrivateStagingDirectory();
        try
        {
            var stagedEntries = ReadArchive(inputPath, stagingRoot, DefaultLimits);
            for (var i = 0; i < stagedEntries.Count; i++)
            {
                var destinationRoot = GetCategoryRoot(stagedEntries[i].Category, directories);
                Directory.CreateDirectory(destinationRoot);
                ValidateLinkFreePath(destinationRoot);
                var destinationPath = Path.Combine(destinationRoot, stagedEntries[i].FileName);
                ValidateLinkFreePath(destinationPath);
                var temporaryPath = Path.Combine(destinationRoot, $".{stagedEntries[i].FileName}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.Copy(stagedEntries[i].SourcePath, temporaryPath, overwrite: false);
                    ValidateLinkFreePath(destinationRoot);
                    ValidateLinkFreePath(destinationPath);
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }

            return stagedEntries.Count;
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    /// <summary>
    /// Counts and measures the managed entries in each category without reading any of them. An entry's
    /// count and size come from the directory itself, so validating thousands of entries to print three
    /// numbers that would be identical either way is work a listing does not owe. Use <see cref="Inspect"/>
    /// when the caller needs an entry's logical key, timestamp, or validity.
    /// </summary>
    public static CacheSummary Summarize(CacheDirectories directories)
    {
        ValidateCachePaths(directories);
        var categories = new CacheCategorySummary[Categories.Length];
        for (var i = 0; i < Categories.Length; i++)
        {
            var category = Categories[i].Name;
            categories[i] = SummarizeCategory(category, GetCategoryRoot(category, directories));
        }

        return new(categories);
    }

    private static CacheCategorySummary SummarizeCategory(string category, string root)
    {
        if (File.Exists(root)) throw new InvalidDataException($"Cache category path must be a directory: {root}.");
        if (!Directory.Exists(root)) return new(category, root, 0, 0);

        // Every entry shares the category root, so its components are checked once here instead of
        // once per file; each entry then only has to answer for itself.
        ValidateLinkFreePath(root);

        // A listing keeps nothing but a size, so the enumeration is asked for nothing else and no path or
        // name string is built for any of the entries it counts.
        var enumerable = new FileSystemEnumerable<long>(
            root,
            static (ref FileSystemEntry entry) => entry.Length,
            ManagedEntryEnumeration)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => IsManagedCacheEntry(ref entry),
        };

        var entryCount = 0;
        long bytes = 0;
        foreach (var length in enumerable)
        {
            entryCount++;
            bytes = checked(bytes + length);
        }

        return new(category, root, entryCount, bytes);
    }

    /// <summary>
    /// Reports whether a located file is one of the managed cache entries, and rejects one that is a link
    /// rather than a file. Both answers come from data the directory enumeration already carried, so
    /// neither costs a stat call — and the name is judged as a span, so a file that is not an entry never
    /// gets a string built for it.
    /// </summary>
    private static bool IsManagedCacheEntry(ref FileSystemEntry entry)
    {
        if (entry.IsDirectory || !IsCacheFileName(entry.FileName)) return false;
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Cache path must not contain symbolic links or reparse points.");
        }

        return true;
    }

    /// <summary>
    /// Slices the digest an entry's file name states out of the full path that located it. A managed entry's
    /// name is a fixed 69 characters of which the first 64 are the digest, so the path already carries the
    /// value and no separate name string has to exist to hold it.
    /// </summary>
    private static ReadOnlySpan<char> GetExpectedCacheKeyHash(string path)
        => path.AsSpan(path.Length - CacheFileNameLength, CacheKeyHashLength);

    public static CacheInspectionResult Inspect(CacheDirectories directories, string? displayPath = null)
    {
        ValidateCachePaths(directories);
        var categories = new List<CacheCategoryInfo>(Categories.Length);
        for (var i = 0; i < Categories.Length; i++)
        {
            var category = Categories[i].Name;
            categories.Add(InspectCategory(category, GetCategoryRoot(category, directories)));
        }

        return new(false, displayPath ?? string.Empty, categories);
    }

    public static CacheInspectionResult Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        ValidateLinkFreePath(fullPath, "Cache target");
        if (Directory.Exists(fullPath))
        {
            var categoryComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var categoryIndex = -1;
            for (var i = 0; i < Categories.Length; i++)
            {
                if (string.Equals(Path.GetFileName(fullPath), Categories[i].Name, categoryComparison))
                {
                    categoryIndex = i;
                    break;
                }
            }

            if (categoryIndex >= 0)
            {
                var hasNestedCategory = false;
                for (var i = 0; i < Categories.Length; i++)
                {
                    if (!Directory.Exists(Path.Combine(fullPath, Categories[i].Name))) continue;
                    hasNestedCategory = true;
                    break;
                }

                if (!hasNestedCategory)
                {
                    return new(false, fullPath, [InspectCategory(Categories[categoryIndex].Name, fullPath)]);
                }
            }

            var directories = new CacheDirectories(
                Path.Combine(fullPath, Categories[0].Name),
                Path.Combine(fullPath, Categories[1].Name),
                Path.Combine(fullPath, Categories[2].Name));
            return Inspect(directories, fullPath);
        }

        if (File.Exists(fullPath)) return InspectArchive(fullPath);
        throw new FileNotFoundException("Cache directory or archive was not found.", fullPath);
    }

    private static CacheInspectionResult InspectArchive(string archivePath)
    {
        ValidateLinkFreePath(archivePath, "Archive");
        var archiveLength = new FileInfo(archivePath).Length;
        if (archiveLength <= 0 || archiveLength > DefaultLimits.MaximumArchiveBytes)
        {
            throw new InvalidDataException($"Archive size must be between 1 and {DefaultLimits.MaximumArchiveBytes} bytes.");
        }

        var stagingRoot = CreatePrivateStagingDirectory();
        try
        {
            var stagedEntries = ReadArchive(archivePath, stagingRoot, DefaultLimits);
            var categories = new List<CacheCategoryInfo>(Categories.Length);
            for (var categoryIndex = 0; categoryIndex < Categories.Length; categoryIndex++)
            {
                var category = Categories[categoryIndex].Name;
                var entries = new List<CacheEntryInfo>();
                for (var i = 0; i < stagedEntries.Count; i++)
                {
                    if (!string.Equals(stagedEntries[i].Category, category, StringComparison.Ordinal)) continue;
                    var staged = stagedEntries[i];
                    var metadata = ReadCacheEntryMetadata(staged.SourcePath, staged.FileName.AsSpan(0, CacheKeyHashLength), DefaultLimits.MaximumEntryBytes);
                    entries.Add(new(category, null, metadata.CacheKey, metadata.FetchedAt, new FileInfo(staged.SourcePath).Length, null));
                }

                entries.Sort(CompareEntries);
                categories.Add(new(category, category, entries, 0));
            }

            return new(true, archivePath, categories);
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private static CacheCategoryInfo InspectCategory(string category, string root)
    {
        var unmanagedFileCount = 0;
        if (File.Exists(root)) throw new InvalidDataException($"Cache category path must be a directory: {root}.");
        if (!Directory.Exists(root)) return new(category, root, [], unmanagedFileCount);

        // Every entry shares the category root, so its components are checked once here instead of
        // once per file; each entry then only has to answer for itself.
        ValidateLinkFreePath(root);

        // The located files are discarded once their entries exist, and a managed cache holds thousands
        // of them, so the list they are gathered into is borrowed from the pool rather than grown.
        var managed = ArrayPool<ManagedCacheFile>.Shared.Rent(InitialManagedFileCapacity);
        var managedCount = 0;
        try
        {
            // The predicate is where an entry's name is in scope without a string behind it, so it both
            // applies the managed-entry rule and tallies the files that are not entries; the transform then
            // runs for managed entries alone and keeps only what reading one needs.
            var enumerable = new FileSystemEnumerable<ManagedCacheFile>(
                root,
                static (ref FileSystemEntry entry) => new ManagedCacheFile(entry.ToFullPath(), entry.Length),
                ManagedEntryEnumeration)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (IsManagedCacheEntry(ref entry)) return true;
                    if (!entry.IsDirectory) unmanagedFileCount++;
                    return false;
                },
            };

            foreach (var file in enumerable)
            {
                if (managedCount == managed.Length) Grow(ref managed, managedCount);
                managed[managedCount++] = file;
            }

            // Each entry costs an open and a parse, and the reads are independent of each other, so they
            // run concurrently; the index keeps them apart and the sort restores the reported order.
            var located = managed;
            var entries = new CacheEntryInfo[managedCount];
            Parallel.For(0, managedCount, index =>
            {
                var file = located[index];
                try
                {
                    var metadata = ReadCacheEntryMetadata(file.Path, GetExpectedCacheKeyHash(file.Path), file.Bytes, DefaultLimits.MaximumEntryBytes);
                    entries[index] = new(category, null, metadata.CacheKey, metadata.FetchedAt, file.Bytes, null);
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    // The physical name is the only identity an entry that failed validation has, so it is
                    // built here and nowhere else.
                    entries[index] = new(category, Path.GetFileName(file.Path), null, null, file.Bytes, exception.Message);
                }
            });

            Array.Sort(entries, CompareEntries);
            return new(category, root, entries, unmanagedFileCount);
        }
        finally
        {
            ArrayPool<ManagedCacheFile>.Shared.Return(managed, clearArray: true);
        }
    }

    /// <summary>
    /// Orders entries by the logical cache key a reader is looking for rather than by the opaque hash the
    /// file is named after, and settles ties on that unique name. An entry too invalid to have a key sorts
    /// first, which is also the only kind the default report shows.
    /// </summary>
    private static int CompareEntries(CacheEntryInfo left, CacheEntryInfo right)
    {
        var byCacheKey = StringComparer.Ordinal.Compare(left.CacheKey, right.CacheKey);
        return byCacheKey != 0 ? byCacheKey : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }

    /// <summary>Replaces a full rental with a larger one, returning the old buffer once its content moved.</summary>
    private static void Grow(ref ManagedCacheFile[] buffer, int count)
    {
        var grown = ArrayPool<ManagedCacheFile>.Shared.Rent(buffer.Length * 2);
        buffer.AsSpan(0, count).CopyTo(grown);
        var replaced = buffer;
        buffer = grown;
        ArrayPool<ManagedCacheFile>.Shared.Return(replaced, clearArray: true);
    }

    public static bool TryParseMaxAge(string? value, out TimeSpan? maximumAge)
    {
        maximumAge = null;
        if (value is null) return true;
        if (value.Length < 2) return false;

        var multiplier = value[^1] switch
        {
            'd' => TimeSpan.TicksPerDay,
            'h' => TimeSpan.TicksPerHour,
            'm' => TimeSpan.TicksPerMinute,
            _ => 0,
        };
        if (multiplier == 0
            || !long.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0
            || amount > TimeSpan.MaxValue.Ticks / multiplier)
        {
            return false;
        }

        maximumAge = TimeSpan.FromTicks(amount * multiplier);
        return true;
    }

    internal static string CreatePrivateStagingDirectory()
        => Directory.CreateTempSubdirectory("ol-cache-unpack-").FullName;

    public static int Prune(CacheDirectories directories, TimeSpan maximumAge, DateTimeOffset now)
        => Prune(directories, maximumAge, now, dryRun: false).PrunedCount;

    public static CachePruneResult Prune(CacheDirectories directories, TimeSpan maximumAge, DateTimeOffset now, bool dryRun)
    {
        ValidateCachePaths(directories);
        var cutoff = GetCutoff(maximumAge, now);
        var count = 0;
        long beforeBytes = 0;
        long reclaimedBytes = 0;
        for (var categoryIndex = 0; categoryIndex < Categories.Length; categoryIndex++)
        {
            var root = GetCategoryRoot(Categories[categoryIndex].Name, directories);
            if (!Directory.Exists(root)) continue;

            var enumerable = new FileSystemEnumerable<ManagedCacheFile>(
                root,
                static (ref FileSystemEntry entry) => new ManagedCacheFile(entry.ToFullPath(), entry.Length),
                ManagedEntryEnumeration)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => IsManagedCacheEntry(ref entry),
            };

            foreach (var file in enumerable)
            {
                var path = file.Path;
                var bytes = file.Bytes;
                var fetchedAt = ValidateCacheEntry(path, GetExpectedCacheKeyHash(path), bytes, DefaultLimits.MaximumEntryBytes);
                beforeBytes = checked(beforeBytes + bytes);
                if (fetchedAt >= cutoff) continue;

                reclaimedBytes = checked(reclaimedBytes + bytes);
                // Deletion is the one step that must not act on stale attributes, so the entry is rechecked
                // against the filesystem here rather than against what the enumeration reported.
                ValidateLinkFreePath(path);
                if (!dryRun) File.Delete(path);
                count = checked(count + 1);
            }
        }

        return new(count, beforeBytes, beforeBytes - reclaimedBytes);
    }

    internal static void ValidateArchiveOutputPaths(string outputPath, string temporaryPath)
    {
        ValidateLinkFreePath(outputPath, "Archive");
        ValidateLinkFreePath(temporaryPath, "Archive temporary");
    }

    public static bool IsExpectedFailure(Exception exception)
        => exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException
            or JsonException;

    private static List<ArchiveSourceEntry> CollectEntries(CacheDirectories directories, TimeSpan? maximumAge, DateTimeOffset now, CacheArchiveLimits limits)
    {
        var entries = new List<ArchiveSourceEntry>();
        var cutoff = maximumAge.HasValue
            ? GetCutoff(maximumAge.Value, now)
            : DateTimeOffset.MinValue;
        for (var categoryIndex = 0; categoryIndex < Categories.Length; categoryIndex++)
        {
            var category = Categories[categoryIndex];
            var root = GetCategoryRoot(category.Name, directories);
            if (!Directory.Exists(root)) continue;

            var categoryEntries = new List<ArchiveSourceEntry>();
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                if (!IsCacheFileName(fileName)) continue;
                ValidateLinkFreePath(path);
                var fetchedAt = ValidateCacheEntry(path, fileName.AsSpan(0, 64), limits.MaximumEntryBytes);
                if (fetchedAt < cutoff) continue;
                if (entries.Count + categoryEntries.Count == limits.MaximumEntryCount) throw new InvalidDataException($"Archive contains more than {limits.MaximumEntryCount} cache entries.");
                categoryEntries.Add(new(path, string.Concat(category.Name, "/", fileName), categoryIndex));
            }

            categoryEntries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.ArchivePath, right.ArchivePath));
            entries.AddRange(categoryEntries);
        }

        return entries;
    }

    private static List<StagedEntry> ReadArchive(string inputPath, string stagingRoot, CacheArchiveLimits limits)
    {
        var entries = new List<StagedEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var manifestSeen = false;
        long expandedBytes = 0;
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new TarReader(gzip, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            if (entry.Format != TarEntryFormat.Ustar) throw new InvalidDataException($"Unsupported archive format: {entry.Format}.");
            if (entry.EntryType != TarEntryType.RegularFile) throw new InvalidDataException($"Unsupported archive entry type: {entry.EntryType}.");
            if (!manifestSeen && entry.Name != ManifestName) throw new InvalidDataException("Archive manifest must be the first entry.");
            if (!names.Add(entry.Name)) throw new InvalidDataException($"Duplicate archive entry: {entry.Name}.");
            if (entry.Length < 0 || entry.Length > limits.MaximumEntryBytes) throw new InvalidDataException($"Archive entry is too large: {entry.Name}.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > limits.MaximumExpandedBytes) throw new InvalidDataException($"Expanded archive exceeds {limits.MaximumExpandedBytes} bytes.");
            if (entry.DataStream is null) throw new InvalidDataException($"Archive entry has no content: {entry.Name}.");

            if (entry.Name == ManifestName)
            {
                ValidateManifest(entry.DataStream, entry.Length);
                manifestSeen = true;
                continue;
            }

            if (entries.Count == limits.MaximumEntryCount) throw new InvalidDataException($"Archive contains more than {limits.MaximumEntryCount} cache entries.");
            var separator = entry.Name.IndexOf('/');
            if (separator <= 0 || entry.Name.IndexOf('/', separator + 1) >= 0) throw new InvalidDataException($"Unsupported archive entry path: {entry.Name}.");
            var category = entry.Name[..separator];
            if (!IsCategory(category)) throw new InvalidDataException($"Unsupported cache category: {category}.");
            var fileName = entry.Name[(separator + 1)..];
            ValidateCacheFileName(fileName);
            var stagedCategory = Path.Combine(stagingRoot, category);
            Directory.CreateDirectory(stagedCategory);
            var stagedPath = Path.Combine(stagedCategory, fileName);
            using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                entry.DataStream.CopyTo(output);
            }

            ValidateCacheEntry(stagedPath, fileName.AsSpan(0, 64), limits.MaximumEntryBytes);
            entries.Add(new(category, fileName, stagedPath));
        }

        if (!manifestSeen) throw new InvalidDataException("Archive manifest is missing.");
        return entries;
    }

    private static void WriteEntry(TarWriter writer, string name, Stream content)
    {
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = content,
            ModificationTime = DeterministicTimestamp,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            Uid = 0,
            Gid = 0,
            UserName = string.Empty,
            GroupName = string.Empty,
        };
        writer.WriteEntry(entry);
    }

    private static void ValidateManifest(Stream content, long length)
    {
        if (length > 4096) throw new InvalidDataException("Archive manifest is too large.");
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("FormatVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var value)
            || value != 1)
        {
            throw new InvalidDataException("Unsupported cache archive format version.");
        }
    }

    private static DateTimeOffset ValidateCacheEntry(string path, ReadOnlySpan<char> expectedHash, long maximumEntryBytes)
        => ReadCacheEntryMetadata(path, expectedHash, maximumEntryBytes).FetchedAt;

    private static DateTimeOffset ValidateCacheEntry(string path, ReadOnlySpan<char> expectedHash, long length, long maximumEntryBytes)
        => ReadCacheEntryMetadata(path, expectedHash, length, maximumEntryBytes).FetchedAt;

    private static CacheEntryMetadata ReadCacheEntryMetadata(string path, ReadOnlySpan<char> expectedHash, long maximumEntryBytes)
        => ReadCacheEntryMetadata(path, expectedHash, new FileInfo(path).Length, maximumEntryBytes);

    private static CacheEntryMetadata ReadCacheEntryMetadata(string path, ReadOnlySpan<char> expectedHash, long length, long maximumEntryBytes)
    {
        if (length <= 0 || length > maximumEntryBytes) throw new InvalidDataException($"Cache entry size is invalid: {Path.GetFileName(path)}.");
        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            return ReadCacheEntryMetadata(path, expectedHash, buffer, (int)length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static CacheEntryMetadata ReadCacheEntryMetadata(string path, ReadOnlySpan<char> expectedHash, byte[] buffer, int length)
    {
        // Opening the entry is the step that would follow a link out of the cache, so the check that it is
        // not one sits here, against the filesystem, rather than resting on what the enumeration saw.
        ValidateLinkFreeEntry(path);
        var read = 0;
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            while (read < length)
            {
                var chunk = RandomAccess.Read(handle, buffer.AsSpan(read, length - read), read);
                if (chunk == 0) break;
                read += chunk;
            }
        }

        // Reading the bytes ourselves means the UTF-8 preamble a stream parse would have skipped is still here.
        var content = buffer.AsMemory(0, read);
        if (content.Span.StartsWith(Utf8Preamble)) content = content[Utf8Preamble.Length..];

        // The document reads straight out of the rented buffer, so it must not outlive this scope.
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("SchemaVersion", out var schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number
            || !schemaVersion.TryGetInt32(out var schema)
            || schema != 1
            || !root.TryGetProperty("CacheKey", out var cacheKeyElement)
            || cacheKeyElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("CacheKeySha256", out var hashElement)
            || hashElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("FetchedAt", out var fetchedAtElement)
            || fetchedAtElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Cache entry is missing required common fields: {Path.GetFileName(path)}.");
        }

        var cacheKey = cacheKeyElement.GetString()!;
        var persistedHash = hashElement.GetString()!;

        // The digest is only ever compared, so it is written into a stack buffer instead of into a string
        // built once per entry and dropped on the next line.
        Span<char> actualHash = stackalloc char[CacheKeyHashLength];
        WriteCacheKeySha256(cacheKey, actualHash);
        if (!expectedHash.Equals(persistedHash, StringComparison.Ordinal) || !actualHash.SequenceEqual(persistedHash))
        {
            throw new InvalidDataException($"Cache entry identity does not match its file name: {Path.GetFileName(path)}.");
        }

        if (!fetchedAtElement.TryGetDateTimeOffset(out var fetchedAt) || fetchedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"Cache entry FetchedAt must be a UTC timestamp: {Path.GetFileName(path)}.");
        }

        return new(cacheKey, fetchedAt);
    }

    private static void ValidateCacheFileName(string fileName)
    {
        if (!IsCacheFileName(fileName))
        {
            throw new InvalidDataException($"Unsupported cache entry name: {fileName}.");
        }
    }

    private static bool IsCacheFileName(ReadOnlySpan<char> fileName)
    {
        if (fileName.Length != CacheFileNameLength || !fileName.EndsWith(".json", StringComparison.Ordinal)) return false;

        for (var i = 0; i < CacheKeyHashLength; i++)
        {
            var value = fileName[i];
            if (!((uint)(value - '0') <= 9 || (uint)(value - 'a') <= 5)) return false;
        }

        return true;
    }

    /// <summary>Writes a cache key's lowercase hex SHA-256 into <paramref name="destination"/>, which must hold 64 characters.</summary>
    private static void WriteCacheKeySha256(string cacheKey, Span<char> destination)
    {
        var maximumByteCount = Encoding.UTF8.GetMaxByteCount(cacheKey.Length);
        byte[]? rented = null;
        var utf8 = maximumByteCount <= MaximumStackCacheKeyBytes
            ? stackalloc byte[MaximumStackCacheKeyBytes]
            : (rented = ArrayPool<byte>.Shared.Rent(maximumByteCount));
        try
        {
            var length = Encoding.UTF8.GetBytes(cacheKey, utf8);
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(utf8[..length], hash);
            Convert.TryToHexStringLower(hash, destination, out _);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsCategory(string value)
        => value is "package-metadata" or "source-repository" or "github-file";

    private static DateTimeOffset GetCutoff(TimeSpan maximumAge, DateTimeOffset now)
        => maximumAge >= now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - maximumAge;

    private static void ValidateCachePaths(CacheDirectories directories)
    {
        ValidateLinkFreePath(directories.PackageMetadata);
        ValidateLinkFreePath(directories.SourceRepository);
        ValidateLinkFreePath(directories.GitHubFile);
    }

    private static void ValidateArchiveOutsideCache(string archivePath, CacheDirectories directories)
    {
        if (IsWithinDirectory(archivePath, directories.PackageMetadata)
            || IsWithinDirectory(archivePath, directories.SourceRepository)
            || IsWithinDirectory(archivePath, directories.GitHubFile))
        {
            throw new InvalidDataException("Archive path must be outside the managed cache directories.");
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), path);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Rechecks one entry against the filesystem immediately before it is opened. The attributes the
    /// enumeration reported were read before the rest of the directory was, so by the time this entry is
    /// reached they are too old to decide whether the path still leads outside the cache. Only the entry
    /// itself is rechecked: its parent directories were validated in the same pass that located it, and
    /// re-walking them per entry is what made inspection cost a stat call for every path component.
    ///
    /// A failure to read the attributes is raised rather than swallowed. <see cref="ValidateLinkFreePath"/>
    /// tolerates a missing component because it walks a path whose parents may legitimately not exist yet;
    /// here the only path is a file the caller is about to open, and treating "it vanished" as "it is fine"
    /// would let the open proceed against whatever took its place without any check having been made.
    /// Every way this can fail is an expected failure, so it is reported exactly as a failed open would be.
    /// </summary>
    private static void ValidateLinkFreeEntry(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Cache path must not contain symbolic links or reparse points.");
        }
    }

    private static void ValidateLinkFreePath(string path, string pathKind = "Cache")
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) throw new InvalidDataException($"{pathKind} path must be absolute.");

        var current = root;
        var components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < components.Length; i++)
        {
            current = Path.Combine(current, components[i]);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"{pathKind} path must not contain symbolic links or reparse points.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static string GetCategoryRoot(string category, CacheDirectories directories)
        => category switch
        {
            "package-metadata" => directories.PackageMetadata,
            "source-repository" => directories.SourceRepository,
            "github-file" => directories.GitHubFile,
            _ => throw new InvalidDataException($"Unsupported cache category: {category}."),
        };

    private readonly record struct CacheCategory(string Name);
    private readonly record struct ArchiveSourceEntry(string SourcePath, string ArchivePath, int CategoryIndex);
    private readonly record struct StagedEntry(string Category, string FileName, string SourcePath);
    private readonly record struct CacheEntryMetadata(string CacheKey, DateTimeOffset FetchedAt);

    /// <summary>One hash-named cache file located by inspection, with the size the enumeration already knew.</summary>
    private readonly record struct ManagedCacheFile(string Path, long Bytes);
}
