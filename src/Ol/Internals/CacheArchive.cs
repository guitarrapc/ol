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

/// <summary>Exactly one of <paramref name="CacheKey"/> and <paramref name="Name"/> is present: a validated entry is named by its key, an invalid one only by its file name.</summary>
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

/// <summary>What a listing reports. None of it depends on an entry's content, so a summary never reads one.</summary>
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

    // Unlike the EnumerationOptions defaults, which skip hidden and system entries and ignore inaccessible ones.
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

            var destinationRoots = new string?[Categories.Length];
            for (var i = 0; i < stagedEntries.Count; i++)
            {
                var destinationRoot = PrepareDestinationRoot(stagedEntries[i].Category, directories, destinationRoots);
                var destinationPath = Path.Combine(destinationRoot, stagedEntries[i].FileName);
                ValidateLinkFreeDestination(destinationRoot);
                ValidateLinkFreeDestination(destinationPath);
                var temporaryPath = Path.Combine(destinationRoot, $".{stagedEntries[i].FileName}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.Copy(stagedEntries[i].StagedPath!, temporaryPath, overwrite: false);

                    // Rechecked because replacement is the step a link would redirect.
                    ValidateLinkFreeDestination(destinationRoot);
                    ValidateLinkFreeDestination(destinationPath);
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
    /// Counts and measures managed entries without reading any of them; both answers come from the directory.
    /// Use <see cref="Inspect"/> when the caller needs an entry's logical key, timestamp, or validity.
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

        ValidateLinkFreePath(root);
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

    /// <summary>Reports whether a located file is a managed entry, and throws for one that is a link. Reads only what the enumeration already carried.</summary>
    private static bool IsManagedCacheEntry(ref FileSystemEntry entry)
    {
        if (entry.IsDirectory || !IsCacheFileName(entry.FileName)) return false;
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Cache path must not contain symbolic links or reparse points.");
        }

        return true;
    }

    /// <summary>Slices the digest out of the full path, so no separate name string has to exist to hold it.</summary>
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

        var archiveEntries = ReadArchive(archivePath, stagingRoot: null, DefaultLimits);
        var categories = new List<CacheCategoryInfo>(Categories.Length);
        for (var categoryIndex = 0; categoryIndex < Categories.Length; categoryIndex++)
        {
            var category = Categories[categoryIndex].Name;
            var entries = new List<CacheEntryInfo>();
            for (var i = 0; i < archiveEntries.Count; i++)
            {
                var archiveEntry = archiveEntries[i];
                if (!string.Equals(archiveEntry.Category, category, StringComparison.Ordinal)) continue;

                entries.Add(new(category, null, archiveEntry.CacheKey, archiveEntry.FetchedAt, archiveEntry.Bytes, null));
            }

            entries.Sort(CompareEntries);
            categories.Add(new(category, category, entries, 0));
        }

        return new(true, archivePath, categories);
    }

    private static CacheCategoryInfo InspectCategory(string category, string root)
    {
        var unmanagedFileCount = 0;
        if (File.Exists(root)) throw new InvalidDataException($"Cache category path must be a directory: {root}.");
        if (!Directory.Exists(root)) return new(category, root, [], unmanagedFileCount);

        ValidateLinkFreePath(root);
        var managed = ArrayPool<ManagedCacheFile>.Shared.Rent(InitialManagedFileCapacity);
        var managedCount = 0;
        try
        {
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

    /// <summary>Orders by the logical cache key rather than the opaque hash the file is named after. An entry with no key sorts first.</summary>
    private static int CompareEntries(CacheEntryInfo left, CacheEntryInfo right)
    {
        var byCacheKey = StringComparer.Ordinal.Compare(left.CacheKey, right.CacheKey);
        return byCacheKey != 0 ? byCacheKey : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }

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
                // Deletion must not act on the enumeration's stale attributes.
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

    /// <summary>
    /// Reads an archive once and returns what validating it established. <paramref name="stagingRoot"/> is
    /// passed only by a caller that must hold the entries before acting on them; inspection passes null.
    /// </summary>
    private static List<ArchiveEntry> ReadArchive(string inputPath, string? stagingRoot, CacheArchiveLimits limits)
    {
        var entries = new List<ArchiveEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var stagedCategories = new HashSet<string>(StringComparer.Ordinal);
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
            if (entry.Length == 0) throw new InvalidDataException($"Cache entry size is invalid: {fileName}.");

            var length = (int)entry.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            string? stagedPath = null;
            CacheEntryMetadata metadata;
            try
            {
                entry.DataStream.ReadExactly(buffer.AsSpan(0, length));
                if (stagingRoot is not null)
                {
                    var stagedCategory = Path.Combine(stagingRoot, category);
                    if (stagedCategories.Add(category)) Directory.CreateDirectory(stagedCategory);
                    stagedPath = Path.Combine(stagedCategory, fileName);
                    using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(buffer.AsSpan(0, length));
                }

                metadata = ReadCacheEntryContent(fileName.AsSpan(0, CacheKeyHashLength), fileName, buffer, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            entries.Add(new(category, fileName, stagedPath, metadata.CacheKey, metadata.FetchedAt, length));
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
        if (length <= 0 || length > maximumEntryBytes) throw new InvalidDataException($"Cache entry size is invalid: {Path.GetFileName(path.AsSpan())}.");
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

        return ReadCacheEntryContent(expectedHash, Path.GetFileName(path.AsSpan()), buffer, read);
    }

    /// <summary>Validates an entry's common transport fields where its content already is, so an archived entry and a stored one are judged by the same rules.</summary>
    private static CacheEntryMetadata ReadCacheEntryContent(ReadOnlySpan<char> expectedHash, ReadOnlySpan<char> name, byte[] buffer, int length)
    {
        // A stream parse would have skipped the UTF-8 preamble; reading the bytes ourselves does not.
        var content = buffer.AsMemory(0, length);
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
            throw new InvalidDataException($"Cache entry is missing required common fields: {name}.");
        }

        var cacheKey = cacheKeyElement.GetString()!;
        var persistedHash = hashElement.GetString()!;

        Span<char> actualHash = stackalloc char[CacheKeyHashLength];
        WriteCacheKeySha256(cacheKey, actualHash);
        if (!expectedHash.Equals(persistedHash, StringComparison.Ordinal) || !actualHash.SequenceEqual(persistedHash))
        {
            throw new InvalidDataException($"Cache entry identity does not match its file name: {name}.");
        }

        if (!fetchedAtElement.TryGetDateTimeOffset(out var fetchedAt) || fetchedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"Cache entry FetchedAt must be a UTC timestamp: {name}.");
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
    /// Rechecks the entry itself immediately before it is opened; the enumeration's attributes are too old
    /// by then. Its parents were walked by the pass that located it. A failure to read the attributes is
    /// raised, not swallowed: the open must never proceed with no check having been made.
    /// </summary>
    private static void ValidateLinkFreeEntry(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Cache path must not contain symbolic links or reparse points.");
        }
    }

    /// <summary>
    /// Rechecks a path about to be written. Unlike <see cref="ValidateLinkFreeEntry"/>, a destination that
    /// does not exist yet is the ordinary case for a restore; only finding a link there is an answer.
    /// </summary>
    private static void ValidateLinkFreeDestination(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Cache path must not contain symbolic links or reparse points.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
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

    /// <summary>
    /// Creates a category's directory and walks its whole path once, the first time the category is reached.
    /// A category with no entry in the archive is never prepared, so a partial archive leaves it untouched.
    /// </summary>
    private static string PrepareDestinationRoot(string category, CacheDirectories directories, string?[] prepared)
    {
        var index = GetCategoryIndex(category);
        if (prepared[index] is { } existing) return existing;

        var root = GetCategoryRoot(category, directories);
        Directory.CreateDirectory(root);
        ValidateLinkFreePath(root);
        prepared[index] = root;
        return root;
    }

    private static int GetCategoryIndex(string category)
    {
        for (var i = 0; i < Categories.Length; i++)
        {
            if (string.Equals(Categories[i].Name, category, StringComparison.Ordinal)) return i;
        }

        throw new InvalidDataException($"Unsupported cache category: {category}.");
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
    /// <summary>One validated archive entry; <c>StagedPath</c> is set only for a caller that asked for staging.</summary>
    private readonly record struct ArchiveEntry(
        string Category,
        string FileName,
        string? StagedPath,
        string CacheKey,
        DateTimeOffset FetchedAt,
        long Bytes);
    private readonly record struct CacheEntryMetadata(string CacheKey, DateTimeOffset FetchedAt);

    private readonly record struct ManagedCacheFile(string Path, long Bytes);
}
