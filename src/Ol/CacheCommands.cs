using System.Buffers;
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
            var result = CacheArchive.Summarize(CachePaths.Resolve(cacheDir));
            Console.WriteLine("Cache locations:");
            for (var i = 0; i < result.Categories.Count; i++)
            {
                var category = result.Categories[i];
                Console.WriteLine($"  {category.Category}: {category.Path} ({FormatEntryCount(category.EntryCount)}, {FormatBytes(category.Bytes)})");
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
    /// <param name="format">Output format: text or markdown.</param>
    /// <param name="cacheDir">Root directory containing the managed cache categories when path is omitted.</param>
    /// <param name="verbose">List every managed entry instead of only the entries that failed validation.</param>
    [Command("info")]
    public int Info([Argument] string? path = null, CacheInfoFormat format = CacheInfoFormat.Text, string? cacheDir = null, bool verbose = false)
    {
        if (path is not null && cacheDir is not null)
        {
            Console.Error.WriteLine("Cache info accepts either a path or --cache-dir, not both.");
            return 1;
        }

        try
        {
            var result = path is null
                ? CacheArchive.Inspect(CachePaths.Resolve(cacheDir), cacheDir is null ? null : Path.GetFullPath(cacheDir))
                : CacheArchive.Inspect(path);
            if (format == CacheInfoFormat.Markdown)
            {
                WriteMarkdownInfo(result, verbose);
            }
            else
            {
                WriteTextInfo(result, verbose);
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

    /// <summary>
    /// Reports whether an entry belongs in the entry table. A managed cache grows to thousands of valid
    /// entries whose sizes and counts the category table already carries, so only the entries that failed
    /// validation are worth a row by default; <c>--verbose</c> asks for the complete listing.
    /// </summary>
    private static bool IsReportedEntry(CacheEntryInfo entry, bool verbose) => verbose || entry.Error is not null;

    private static void WriteMarkdownInfo(CacheInspectionResult result, bool verbose)
    {
        Console.WriteLine(result.IsArchive ? "# Cache archive" : "# Cache directory");
        Console.WriteLine();
        Console.WriteLine("| Property | Value |");
        Console.WriteLine("|---|---|");
        if (!string.IsNullOrEmpty(result.Path)) Console.WriteLine($"| Path | {EscapeMarkdown(result.Path)} |");
        if (result.IsArchive) Console.WriteLine($"| Archive size | {FormatBytes(new FileInfo(result.Path).Length)} |");
        Console.WriteLine($"| Entries | {FormatEntryCount(result.EntryCount)} |");
        Console.WriteLine($"| Content size | {FormatBytes(result.TotalBytes)} |");
        Console.WriteLine();
        Console.WriteLine("## Categories");
        Console.WriteLine();
        if (result.IsArchive)
        {
            Console.WriteLine("| Category | Entries | Size | Unmanaged files |");
            Console.WriteLine("|---|---:|---:|---:|");
        }
        else
        {
            Console.WriteLine("| Category | Path | Entries | Size | Unmanaged files |");
            Console.WriteLine("|---|---|---:|---:|---:|");
        }

        for (var i = 0; i < result.Categories.Count; i++)
        {
            var category = result.Categories[i];
            var bytes = FormatBytes(GetCategoryBytes(category));
            var entries = FormatEntryCount(category.Entries.Count);
            if (result.IsArchive)
            {
                Console.WriteLine($"| {EscapeMarkdown(category.Category)} | {entries} | {bytes} | {category.UnmanagedFileCount} |");
            }
            else
            {
                Console.WriteLine($"| {EscapeMarkdown(category.Category)} | {EscapeMarkdown(category.Path)} | {entries} | {bytes} | {category.UnmanagedFileCount} |");
            }
        }

        Console.WriteLine();
        Console.WriteLine(verbose ? "## Entries" : "## Invalid entries");
        Console.WriteLine();
        Console.WriteLine("| Category | Cache key | Fetched at | Size | Status | Details |");
        Console.WriteLine("|---|---|---|---:|---|---|");
        var hasEntries = false;
        for (var i = 0; i < result.Categories.Count; i++)
        {
            var category = result.Categories[i];
            for (var j = 0; j < category.Entries.Count; j++)
            {
                var entry = category.Entries[j];
                if (!IsReportedEntry(entry, verbose)) continue;

                hasEntries = true;
                if (entry.Error is not null)
                {
                    Console.WriteLine($"| {EscapeMarkdown(category.Category)} | - | - | {FormatBytes(entry.Bytes)} | invalid | {EscapeMarkdown($"File: {entry.Name}; {entry.Error}")} |");
                }
                else
                {
                    Console.WriteLine($"| {EscapeMarkdown(category.Category)} | {EscapeMarkdown(entry.CacheKey ?? "-")} | {entry.FetchedAt:O} | {FormatBytes(entry.Bytes)} | valid | - |");
                }
            }
        }

        if (!hasEntries) Console.WriteLine($"| - | - | - | - | - | {EmptyEntryReason(result)} |");
    }

    /// <summary>
    /// Names why an entry table is empty. An empty cache and a cache whose entries all validated are
    /// different answers, and the default report reaches the second one far more often.
    /// </summary>
    private static string EmptyEntryReason(CacheInspectionResult result)
        => result.EntryCount == 0 ? "No managed entries." : "No invalid entries.";

    private static void WriteTextInfo(CacheInspectionResult result, bool verbose)
    {
        using var writer = new PooledStreamBufferWriter(Console.OpenStandardOutput());
        TextTable.WriteLine(writer, result.IsArchive ? "Cache archive"u8 : "Cache directory"u8);

        // Built strings, and one filesystem stat, so measure and write from the same result.
        var archiveSize = result.IsArchive ? FormatBytes(new FileInfo(result.Path).Length) : null;
        var entryCount = FormatEntryCount(result.EntryCount);
        var contentSize = FormatBytes(result.TotalBytes);

        var propertyWidth = "Property"u8.Length;
        if (!string.IsNullOrEmpty(result.Path)) TextTable.Include(ref propertyWidth, "Path"u8);
        if (archiveSize is not null) TextTable.Include(ref propertyWidth, "Archive size"u8);
        TextTable.Include(ref propertyWidth, "Entries"u8);
        TextTable.Include(ref propertyWidth, "Content size"u8);

        var valueWidth = "Value"u8.Length;
        if (!string.IsNullOrEmpty(result.Path)) TextTable.Include(ref valueWidth, result.Path);
        if (archiveSize is not null) TextTable.Include(ref valueWidth, archiveSize);
        TextTable.Include(ref valueWidth, entryCount);
        TextTable.Include(ref valueWidth, contentSize);

        TextTable.WriteCell(writer, "Property"u8, propertyWidth);
        TextTable.WriteCell(writer, "Value"u8, valueWidth, last: true);
        TextTable.WriteNewLine(writer);
        Span<int> overviewWidths = stackalloc int[] { propertyWidth, valueWidth };
        TextTable.WriteSeparator(writer, overviewWidths);
        if (!string.IsNullOrEmpty(result.Path)) WriteProperty(writer, "Path"u8, result.Path, overviewWidths);
        if (archiveSize is not null) WriteProperty(writer, "Archive size"u8, archiveSize, overviewWidths);
        WriteProperty(writer, "Entries"u8, entryCount, overviewWidths);
        WriteProperty(writer, "Content size"u8, contentSize, overviewWidths);

        TextTable.WriteNewLine(writer);
        TextTable.WriteLine(writer, "Categories:"u8);
        WriteTextCategories(writer, result);

        TextTable.WriteNewLine(writer);
        TextTable.WriteLine(writer, verbose ? "Entries:"u8 : "Invalid entries:"u8);
        WriteTextEntries(writer, result, verbose);
    }

    private static void WriteProperty(System.Buffers.IBufferWriter<byte> writer, ReadOnlySpan<byte> property, string value, ReadOnlySpan<int> widths)
    {
        TextTable.WriteCell(writer, property, widths[0]);
        TextTable.WriteCell(writer, value, widths[1], last: true);
        TextTable.WriteNewLine(writer);
    }

    private static void WriteTextCategories(System.Buffers.IBufferWriter<byte> writer, CacheInspectionResult result)
    {
        var columnCount = result.IsArchive ? 4 : 5;
        Span<int> widths = stackalloc int[columnCount];
        widths[0] = "Category"u8.Length;
        var entryColumn = result.IsArchive ? 1 : 2;
        if (!result.IsArchive) widths[1] = "Path"u8.Length;
        widths[entryColumn] = "Entries"u8.Length;
        widths[entryColumn + 1] = "Size"u8.Length;
        widths[entryColumn + 2] = "Unmanaged files"u8.Length;

        // Each counter is a freshly built string, so it is derived once and reused by the write pass.
        var cells = ArrayPool<CategoryCells>.Shared.Rent(result.Categories.Count);
        try
        {
            for (var i = 0; i < result.Categories.Count; i++)
            {
                var category = result.Categories[i];
                var counts = new CategoryCells(FormatEntryCount(category.Entries.Count), FormatBytes(GetCategoryBytes(category)));
                cells[i] = counts;
                TextTable.Include(ref widths[0], category.Category);
                if (!result.IsArchive) TextTable.Include(ref widths[1], category.Path);
                TextTable.Include(ref widths[entryColumn], counts.Entries);
                TextTable.Include(ref widths[entryColumn + 1], counts.Size);
                TextTable.Include(ref widths[entryColumn + 2], category.UnmanagedFileCount);
            }

            TextTable.WriteCell(writer, "Category"u8, widths[0]);
            if (!result.IsArchive) TextTable.WriteCell(writer, "Path"u8, widths[1]);
            TextTable.WriteCell(writer, "Entries"u8, widths[entryColumn]);
            TextTable.WriteCell(writer, "Size"u8, widths[entryColumn + 1]);
            TextTable.WriteCell(writer, "Unmanaged files"u8, widths[entryColumn + 2], last: true);
            TextTable.WriteNewLine(writer);
            TextTable.WriteSeparator(writer, widths);

            for (var i = 0; i < result.Categories.Count; i++)
            {
                var category = result.Categories[i];
                TextTable.WriteCell(writer, category.Category, widths[0]);
                if (!result.IsArchive) TextTable.WriteCell(writer, category.Path, widths[1]);
                TextTable.WriteCell(writer, cells[i].Entries, widths[entryColumn]);
                TextTable.WriteCell(writer, cells[i].Size, widths[entryColumn + 1]);
                TextTable.WriteCell(writer, category.UnmanagedFileCount, widths[entryColumn + 2], last: true);
                TextTable.WriteNewLine(writer);
            }
        }
        finally
        {
            ArrayPool<CategoryCells>.Shared.Return(cells, clearArray: true);
        }
    }

    /// <summary>One category row's derived text, kept between the width pass and the write pass.</summary>
    private readonly record struct CategoryCells(string Entries, string Size);

    /// <summary>One entry row's derived text, kept between the width pass and the write pass.</summary>
    private readonly record struct EntryCells(string CacheKey, string FetchedAt, string Size, string Details);

    private static void WriteTextEntries(System.Buffers.IBufferWriter<byte> writer, CacheInspectionResult result, bool verbose)
    {
        Span<int> widths = stackalloc int[]
        {
            "Category"u8.Length,
            "Cache key"u8.Length,
            "Fetched at"u8.Length,
            "Size"u8.Length,
            "Status"u8.Length,
            "Details"u8.Length,
        };

        // Only reported rows are counted, so the default report neither sizes storage for the thousands
        // of valid entries it will not print nor builds the cell text they would have needed.
        var rowCount = 0;
        for (var i = 0; i < result.Categories.Count; i++)
        {
            var entries = result.Categories[i].Entries;
            for (var j = 0; j < entries.Count; j++)
            {
                if (IsReportedEntry(entries[j], verbose)) rowCount++;
            }
        }

        // Built strings, so the width pass keeps what it derived instead of building each one twice.
        var cells = ArrayPool<EntryCells>.Shared.Rent(Math.Max(1, rowCount));
        try
        {
            var next = 0;
            for (var i = 0; i < result.Categories.Count; i++)
            {
                var category = result.Categories[i];
                for (var j = 0; j < category.Entries.Count; j++)
                {
                    var entry = category.Entries[j];
                    if (!IsReportedEntry(entry, verbose)) continue;

                    var row = new EntryCells(
                        entry.Error is null ? entry.CacheKey ?? "-" : "-",
                        entry.Error is null ? entry.FetchedAt?.ToString("O") ?? "-" : "-",
                        FormatBytes(entry.Bytes),
                        entry.Error is null ? "-" : $"File: {entry.Name}; {entry.Error}");
                    cells[next++] = row;
                    TextTable.Include(ref widths[0], category.Category);
                    TextTable.Include(ref widths[1], row.CacheKey);
                    TextTable.Include(ref widths[2], row.FetchedAt);
                    TextTable.Include(ref widths[3], row.Size);
                    TextTable.Include(ref widths[4], entry.Error is null ? "valid"u8 : "invalid"u8);
                    TextTable.Include(ref widths[5], row.Details);
                }
            }

            var hasEntries = rowCount != 0;
            var emptyReason = hasEntries ? null : EmptyEntryReason(result);
            if (emptyReason is not null) TextTable.Include(ref widths[5], emptyReason);
            TextTable.WriteCell(writer, "Category"u8, widths[0]);
            TextTable.WriteCell(writer, "Cache key"u8, widths[1]);
            TextTable.WriteCell(writer, "Fetched at"u8, widths[2]);
            TextTable.WriteCell(writer, "Size"u8, widths[3]);
            TextTable.WriteCell(writer, "Status"u8, widths[4]);
            TextTable.WriteCell(writer, "Details"u8, widths[5], last: true);
            TextTable.WriteNewLine(writer);
            TextTable.WriteSeparator(writer, widths);

            if (emptyReason is not null)
            {
                for (var i = 0; i < widths.Length - 1; i++) TextTable.WriteCell(writer, "-"u8, widths[i]);
                TextTable.WriteCell(writer, emptyReason, widths[^1], last: true);
                TextTable.WriteNewLine(writer);
                return;
            }

            next = 0;
            for (var i = 0; i < result.Categories.Count; i++)
            {
                var category = result.Categories[i];
                for (var j = 0; j < category.Entries.Count; j++)
                {
                    if (!IsReportedEntry(category.Entries[j], verbose)) continue;

                    var row = cells[next++];
                    TextTable.WriteCell(writer, category.Category, widths[0]);
                    TextTable.WriteCell(writer, row.CacheKey, widths[1]);
                    TextTable.WriteCell(writer, row.FetchedAt, widths[2]);
                    TextTable.WriteCell(writer, row.Size, widths[3]);
                    TextTable.WriteCell(writer, category.Entries[j].Error is null ? "valid"u8 : "invalid"u8, widths[4]);
                    TextTable.WriteCell(writer, row.Details, widths[5], last: true);
                    TextTable.WriteNewLine(writer);
                }
            }
        }
        finally
        {
            ArrayPool<EntryCells>.Shared.Return(cells, clearArray: true);
        }
    }

    private static string EscapeMarkdown(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Replace("!", "\\!", StringComparison.Ordinal)
            .Replace("~", "\\~", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);
}

/// <summary>Selects the cache info output format.</summary>
internal enum CacheInfoFormat
{
    /// <summary>Human-readable ASCII tables.</summary>
    Text,

    /// <summary>Markdown tables.</summary>
    Markdown,
}
