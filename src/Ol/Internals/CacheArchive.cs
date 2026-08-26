using System.Formats.Tar;
using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ol.Internals;

internal readonly record struct CacheArchiveLimits(
    long MaximumArchiveBytes,
    long MaximumEntryBytes,
    long MaximumExpandedBytes,
    int MaximumEntryCount);

internal static class CacheArchive
{
    private const string ManifestName = "ol-cache-manifest.json";
    private const string ManifestContent = "{\"FormatVersion\":1}";
    private const int MaximumStackCacheKeyBytes = 512;
    private static readonly DateTimeOffset DeterministicTimestamp = DateTimeOffset.UnixEpoch;
    private static readonly CacheArchiveLimits DefaultLimits = new(
        MaximumArchiveBytes: 512L * 1024 * 1024,
        MaximumEntryBytes: 16L * 1024 * 1024,
        MaximumExpandedBytes: 1024L * 1024 * 1024,
        MaximumEntryCount: 250_000);
    private static readonly CacheCategory[] Categories =
    [
        new("package-metadata"),
        new("source-repository"),
        new("github-file"),
    ];

    public static int Pack(string archivePath, CacheDirectories directories, TimeSpan? maximumAge, DateTimeOffset now)
        => Pack(archivePath, directories, maximumAge, now, DefaultLimits);

    internal static int Pack(string archivePath, CacheDirectories directories, TimeSpan? maximumAge, DateTimeOffset now, CacheArchiveLimits limits)
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
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: false))
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
            return entries.Count;
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

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"ol-cache-unpack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
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

    public static int Prune(CacheDirectories directories, TimeSpan maximumAge, DateTimeOffset now)
    {
        ValidateCachePaths(directories);
        var cutoff = GetCutoff(maximumAge, now);
        var count = 0;
        for (var categoryIndex = 0; categoryIndex < Categories.Length; categoryIndex++)
        {
            var root = GetCategoryRoot(Categories[categoryIndex].Name, directories);
            if (!Directory.Exists(root)) continue;

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                ValidateLinkFreePath(path);
                var fileName = Path.GetFileName(path);
                if (!IsCacheFileName(fileName)) continue;
                var fetchedAt = ValidateCacheEntry(path, fileName.AsSpan(0, 64), DefaultLimits.MaximumEntryBytes);
                if (fetchedAt >= cutoff) continue;

                ValidateLinkFreePath(path);
                File.Delete(path);
                count = checked(count + 1);
            }
        }

        return count;
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
                ValidateLinkFreePath(path);
                var fileName = Path.GetFileName(path);
                ValidateCacheFileName(fileName);
                var fetchedAt = ValidateCacheEntry(path, fileName.AsSpan(0, 64), limits.MaximumEntryBytes);
                if (fetchedAt < cutoff) continue;
                if (entries.Count + categoryEntries.Count == limits.MaximumEntryCount) throw new InvalidDataException($"Archive contains more than {limits.MaximumEntryCount} cache entries.");
                categoryEntries.Add(new(path, string.Concat(category.Name, "/", fileName)));
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
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > maximumEntryBytes) throw new InvalidDataException($"Cache entry size is invalid: {Path.GetFileName(path)}.");
        using var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
        var actualHash = GetCacheKeySha256(cacheKey);
        if (!expectedHash.Equals(persistedHash, StringComparison.Ordinal) || !string.Equals(actualHash, persistedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Cache entry identity does not match its file name: {Path.GetFileName(path)}.");
        }

        if (!fetchedAtElement.TryGetDateTimeOffset(out var fetchedAt) || fetchedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"Cache entry FetchedAt must be a UTC timestamp: {Path.GetFileName(path)}.");
        }

        return fetchedAt;
    }

    private static void ValidateCacheFileName(string fileName)
    {
        if (!IsCacheFileName(fileName))
        {
            throw new InvalidDataException($"Unsupported cache entry name: {fileName}.");
        }
    }

    private static bool IsCacheFileName(string fileName)
    {
        if (fileName.Length != 69 || !fileName.EndsWith(".json", StringComparison.Ordinal)) return false;

        for (var i = 0; i < 64; i++)
        {
            var value = fileName[i];
            if (!((uint)(value - '0') <= 9 || (uint)(value - 'a') <= 5)) return false;
        }

        return true;
    }

    private static string GetCacheKeySha256(string cacheKey)
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
            return Convert.ToHexStringLower(hash);
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

    private static void ValidateLinkFreePath(string path, string pathKind = "Cache")
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) throw new InvalidDataException("Cache path must be absolute.");

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
    private readonly record struct ArchiveSourceEntry(string SourcePath, string ArchivePath);
    private readonly record struct StagedEntry(string Category, string FileName, string SourcePath);
}
