using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ol.Core.GitHub;
using Ol.Internals;

namespace Ol.Tests;

public sealed class CacheArchiveCliTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task GitSeedLimits_Defaults_AreBoundedForCommittedArchive()
    {
        await Assert.That(CacheArchive.RecommendedArchiveBytes).IsEqualTo(1L * 1024 * 1024);
        await Assert.That(CacheArchive.DefaultLimits.MaximumArchiveBytes).IsEqualTo(8L * 1024 * 1024);
        await Assert.That(CacheArchive.DefaultLimits.MaximumEntryBytes).IsEqualTo(2L * 1024 * 1024);
        await Assert.That(CacheArchive.DefaultLimits.MaximumExpandedBytes).IsEqualTo(64L * 1024 * 1024);
        await Assert.That(CacheArchive.DefaultLimits.MaximumEntryCount).IsEqualTo(10_000);
    }

    [Test]
    public async Task MaximumLengthWriteStream_WhenWriteExceedsLimit_RejectsBeforeWriting()
    {
        using var destination = new MemoryStream();
        using var bounded = new MaximumLengthWriteStream(destination, maximumLength: 4, leaveOpen: true);
        bounded.Write([1, 2, 3]);

        await Assert.That(() => bounded.Write([4, 5])).Throws<InvalidDataException>();
        await Assert.That(destination.Length).IsEqualTo(3);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MaximumLengthWriteStream_DisposeAsync_HonorsLeaveOpen(bool leaveOpen)
    {
        using var destination = new MemoryStream();
        var bounded = new MaximumLengthWriteStream(destination, maximumLength: 4, leaveOpen);

        await bounded.DisposeAsync();

        await Assert.That(destination.CanWrite).IsEqualTo(leaveOpen);
    }

    [Test]
    public async Task CreatePrivateStagingDirectory_OnUnix_UsesOwnerOnlyPermissions()
    {
        var stagingRoot = CacheArchive.CreatePrivateStagingDirectory();
        try
        {
            await Assert.That(Directory.Exists(stagingRoot)).IsTrue();
            if (OperatingSystem.IsWindows()) return;

            const UnixFileMode accessPermissions = UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            var mode = File.GetUnixFileMode(stagingRoot) & accessPermissions;
            await Assert.That(mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    [Test]
    public async Task ValidateArchiveOutputPaths_WhenCreatedDirectoryWasReplacedByLink_Rejects()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-output-link-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(root, "output");
        var outside = Path.Combine(root, "outside");
        var outputPath = Path.Combine(outputDirectory, "cache.olcache");
        var temporaryPath = Path.Combine(outputDirectory, ".cache.olcache.tmp");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(outside);
        Directory.Delete(outputDirectory);
        Directory.CreateSymbolicLink(outputDirectory, outside);

        try
        {
            await Assert.That(() => CacheArchive.ValidateArchiveOutputPaths(outputPath, temporaryPath)).Throws<InvalidDataException>();
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ValidateArchiveOutputPaths_WhenTemporaryPathIsLink_Rejects()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-temporary-link-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(root, "cache.olcache");
        var temporaryPath = Path.Combine(root, ".cache.olcache.tmp");
        var outside = Path.Combine(root, "outside.tmp");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(outside, "keep", Encoding.UTF8);
        File.CreateSymbolicLink(temporaryPath, outside);

        try
        {
            await Assert.That(() => CacheArchive.ValidateArchiveOutputPaths(outputPath, temporaryPath)).Throws<InvalidDataException>();
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task PackAndUnpack_ValidCache_RoundTripsDeterministically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-archive-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var restored = Path.Combine(root, "restored");
        var firstArchive = Path.Combine(root, "first.olcache");
        var secondArchive = Path.Combine(root, "second.olcache");
        var cacheKey = "pkg:npm/example@1.0.0";
        Directory.CreateDirectory(root);
        await new PackageMetadataCache(Path.Combine(source, "package-metadata")).WriteAsync(
            new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "https://github.com/example/example", [], [], FetchedAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));
        var sourceTarget = new SourceRepositoryTarget("example", "example", "main");
        await new SourceRepositoryCache(Path.Combine(source, "source-repository")).WriteAsync(
            new SourceRepositoryRecord(sourceTarget.CacheKey, "github-license-api", "none", sourceTarget.Repository, sourceTarget.Ref, System.Net.HttpStatusCode.NotFound, null, [], [], new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));
        DeclaredGitHubFileTarget.TryCreate("https://github.com/example/example/blob/main/LICENSE", out var fileTarget);
        new DeclaredGitHubFileCache(Path.Combine(source, "github-file")).Write(fileTarget, System.Net.HttpStatusCode.OK, "MIT License"u8);

        try
        {
            var first = await RunOlAsync("cache", "pack", firstArchive, "--cache-dir", source);
            var second = await RunOlAsync("cache", "pack", secondArchive, "--cache-dir", source);

            await Assert.That(first.ExitCode).IsEqualTo(0).Because(first.Stderr);
            await Assert.That(second.ExitCode).IsEqualTo(0).Because(second.Stderr);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(first.Stderr)).IsEmpty();
            await Assert.That(CliTestAssembly.DiagnosticsOnly(second.Stderr)).IsEmpty();
            var firstBytes = await File.ReadAllBytesAsync(firstArchive);
            var secondBytes = await File.ReadAllBytesAsync(secondArchive);
            await Assert.That(firstBytes.AsSpan().SequenceEqual(secondBytes)).IsTrue();

            var unpack = await RunOlAsync("cache", "unpack", firstArchive, "--cache-dir", restored);

            await Assert.That(unpack.ExitCode).IsEqualTo(0);
            await Assert.That(CliTestAssembly.DiagnosticsOnly(unpack.Stderr)).IsEmpty();
            var entry = await new PackageMetadataCache(Path.Combine(restored, "package-metadata")).TryReadAsync(cacheKey);
            await Assert.That(entry.IsHit).IsTrue();
            await Assert.That(entry.RawLicense.ToString()).IsEqualTo("MIT");
            await Assert.That((await new SourceRepositoryCache(Path.Combine(restored, "source-repository")).ReadAsync(sourceTarget.CacheKey)).Status).IsEqualTo(SourceRepositoryCacheReadStatus.Hit);
            await Assert.That(File.Exists(new DeclaredGitHubFileCache(Path.Combine(restored, "github-file")).GetPath(fileTarget.CacheKey))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheList_WithCacheDirectory_ShowsLocationsAndManagedEntrySizes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-list-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var cache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        await cache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var result = await RunOlAsync("cache", "list", "--cache-dir", source);

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains($"package-metadata: {Path.Combine(source, "package-metadata")}");
            await Assert.That(result.Stdout).Contains("1 entry");
            await Assert.That(result.Stdout).Contains("Total: 1 entry");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheInfo_WithDirectoryAndArchive_ShowsLogicalEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-info-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var archive = Path.Combine(root, "cache.olcache");
        const string cacheKey = "pkg:npm/example@1.0.0";
        var cache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var directory = await RunOlAsync("cache", "info", source);
            var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", source);
            var packed = await RunOlAsync("cache", "info", archive);
            var packedMarkdown = await RunOlAsync("cache", "info", archive, "--format", "markdown");

            await Assert.That(directory.ExitCode).IsEqualTo(0).Because(directory.Stderr);
            await Assert.That(directory.Stdout).Contains("Cache directory");
            await Assert.That(directory.Stdout).Contains("Categories:");
            await Assert.That(directory.Stdout).Contains("Entries:");
            await Assert.That(directory.Stdout).Contains("Category");
            await Assert.That(directory.Stdout).Contains("Cache key");
            await Assert.That(directory.Stdout).Contains(cacheKey);
            await Assert.That(pack.ExitCode).IsEqualTo(0).Because(pack.Stderr);
            await Assert.That(packed.ExitCode).IsEqualTo(0).Because(packed.Stderr);
            await Assert.That(packed.Stdout).Contains("Cache archive");
            await Assert.That(packed.Stdout).Contains("Archive size  ");
            await Assert.That(packed.Stdout).Contains("Categories:");
            await Assert.That(packed.Stdout).Contains("package-metadata");
            await Assert.That(packed.Stdout).Contains(cacheKey);
            await Assert.That(packedMarkdown.ExitCode).IsEqualTo(0).Because(packedMarkdown.Stderr);
            await Assert.That(packedMarkdown.Stdout.ReplaceLineEndings("\n")).StartsWith("# Cache archive\n\n| Property | Value |\n|---|---|\n");
            await Assert.That(packedMarkdown.Stdout).Contains("| Archive size | ");
            await Assert.That(packedMarkdown.Stdout).Contains(cacheKey);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheInfo_WithMarkdownFormat_RendersSummaryAndEntriesAsTables()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-info-markdown-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        const string cacheKey = "pkg:npm/example@1.0.0";
        var cache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var result = await RunOlAsync("cache", "info", source, "--format", "markdown");
            var output = result.Stdout.ReplaceLineEndings("\n");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(output).StartsWith("# Cache directory\n\n| Property | Value |\n|---|---|\n");
            await Assert.That(output).Contains("## Categories\n\n| Category | Path | Entries | Size | Unmanaged files |");
            await Assert.That(output).Contains("## Entries\n\n| Category | Cache key | Fetched at | Size | Status | Details |");
            await Assert.That(output).Contains(cacheKey);
            await Assert.That(output).DoesNotContain("fetched-at=");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheInfo_WithTextFormat_RendersSummaryAndEntriesAsAsciiTables()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-info-text-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        const string cacheKey = "pkg:npm/example@1.0.0";
        var cache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var result = await RunOlAsync("cache", "info", source, "--format", "text");
            var output = result.Stdout.ReplaceLineEndings("\n");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(output).Contains("Property      Value\n------------  ");
            await Assert.That(output).Contains("Categories:\nCategory");
            await Assert.That(output).Contains("Entries:\nCategory");
            await Assert.That(output).Contains("Cache key");
            await Assert.That(output).Contains(cacheKey);
            await Assert.That(output).DoesNotContain("    Cache key:");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheInfo_WithTrailingSeparatorOnCategoryPath_InspectsCategory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-info-category-{Guid.NewGuid():N}");
        var category = Path.Combine(root, "package-metadata");
        const string cacheKey = "pkg:npm/example@1.0.0";
        var cache = new PackageMetadataCache(category);
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var result = await RunOlAsync("cache", "info", string.Concat(category, Path.DirectorySeparatorChar));

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Entries       1 entry");
            await Assert.That(result.Stdout).Contains(cacheKey);
            await Assert.That(result.Stdout).DoesNotContain("source-repository");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheInfo_WithCaseVariantCategoryPath_OnWindows_InspectsCategory()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-info-category-case-{Guid.NewGuid():N}");
        var category = Path.Combine(root, "package-metadata");
        const string cacheKey = "pkg:npm/example@1.0.0";
        var cache = new PackageMetadataCache(category);
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var result = await RunOlAsync("cache", "info", Path.Combine(root, "PACKAGE-METADATA"));

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Entries       1 entry");
            await Assert.That(result.Stdout).Contains(cacheKey);
            await Assert.That(result.Stdout).DoesNotContain("source-repository");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheInfo_WithUnifiedRootNamedAsCategory_InspectsNestedCategories()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"ol-cache-info-root-name-{Guid.NewGuid():N}");
        var source = Path.Combine(parent, "package-metadata");
        const string cacheKey = "pkg:npm/example@1.0.0";
        var cache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var result = await RunOlAsync("cache", "info", source);

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Entries       1 entry");
            await Assert.That(result.Stdout).Contains(cacheKey);
            await Assert.That(result.Stdout).Contains("source-repository");
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Test]
    public async Task Inspect_WithCategoryRootAsFile_RejectsInvalidLocation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-info-root-file-{Guid.NewGuid():N}");
        var file = Path.Combine(root, "package-metadata");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(file, "not a directory");

        try
        {
            var directories = new CacheDirectories(file, Path.Combine(root, "source-repository"), Path.Combine(root, "github-file"));

            await Assert.That(() => CacheArchive.Inspect(directories)).Throws<InvalidDataException>();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheInfo_WithMarkdownFormat_EscapesMarkupInCacheKey()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-info-markdown-escape-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        const string cacheKey = "<details>**cache** [link](https://example.com) `code` &copy;|x</details>";
        var cache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));

        try
        {
            var result = await RunOlAsync("cache", "info", source, "--format", "markdown");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("&lt;details&gt;\\*\\*cache\\*\\* \\[link\\]\\(https://example.com\\) \\`code\\` &amp;copy;\\|x&lt;/details&gt;");
            await Assert.That(result.Stdout).DoesNotContain("<details>");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheInfo_WithMarkdownFormat_LabelsInvalidEntryAsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-info-markdown-invalid-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var category = Path.Combine(source, "package-metadata");
        const string cacheKey = "pkg:npm/invalid@1.0.0";
        var fileName = $"{PackageMetadataCache.GetCacheKeySha256(cacheKey)}.json";
        Directory.CreateDirectory(category);
        await File.WriteAllTextAsync(Path.Combine(category, fileName), "{}");

        try
        {
            var result = await RunOlAsync("cache", "info", source, "--format", "markdown");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("| package-metadata | - | - |");
            await Assert.That(result.Stdout).Contains($"File: {fileName};");
            await Assert.That(result.Stdout).DoesNotContain($"| package-metadata | {fileName} | - |");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CacheInfo_WithPathAndCacheDirectory_RejectsAmbiguousSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-info-ambiguous-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);

        try
        {
            var result = await RunOlAsync("cache", "info", source, "--cache-dir", Path.Combine(root, "other"));

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr.Trim()).IsEqualTo("Cache info accepts either a path or --cache-dir, not both.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Prune_WithDryRun_ReportsReclaimedBytesWithoutDeleting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-prune-dry-run-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var category = Path.Combine(source, "package-metadata");
        const string cacheKey = "pkg:npm/old@1.0.0";
        var cache = new PackageMetadataCache(category);
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: DateTimeOffset.UtcNow.AddDays(-31)));
        var entryPath = cache.GetPath(cacheKey);

        try
        {
            var result = await RunOlAsync("cache", "prune", "--cache-dir", source, "--max-age", "30d", "--dry-run");

            await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);
            await Assert.That(result.Stdout).Contains("Would prune 1 cache entry");
            await Assert.That(result.Stdout).Contains("free");
            await Assert.That(result.Stdout).Contains("->");
            await Assert.That(File.Exists(entryPath)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WithMaxAge_IncludesOnlyRecentEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-max-age-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var restored = Path.Combine(root, "restored");
        var archive = Path.Combine(root, "cache.olcache");
        var oldKey = "pkg:npm/old@1.0.0";
        var recentKey = "pkg:npm/recent@1.0.0";
        var cache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        Directory.CreateDirectory(root);
        await cache.WriteAsync(new PackageMetadataRecord(oldKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: DateTimeOffset.UtcNow.AddDays(-31)));
        await cache.WriteAsync(new PackageMetadataRecord(recentKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: DateTimeOffset.UtcNow.AddDays(-1)));

        try
        {
            var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", source, "--max-age", "30d");
            var unpack = await RunOlAsync("cache", "unpack", archive, "--cache-dir", restored);
            var restoredCache = new PackageMetadataCache(Path.Combine(restored, "package-metadata"));

            await Assert.That(pack.ExitCode).IsEqualTo(0).Because(pack.Stderr);
            await Assert.That(pack.Stdout).Contains("Packed 1 cache entry");
            await Assert.That(unpack.ExitCode).IsEqualTo(0).Because(unpack.Stderr);
            await Assert.That((await restoredCache.TryReadAsync(oldKey)).IsHit).IsFalse();
            await Assert.That((await restoredCache.TryReadAsync(recentKey)).IsHit).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WithUnmanagedFile_SkipsItAndPacksManagedEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-unmanaged-file-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var category = Path.Combine(source, "package-metadata");
        var restored = Path.Combine(root, "restored");
        var archive = Path.Combine(root, "cache.olcache");
        var cacheKey = "pkg:npm/example@1.0.0";
        Directory.CreateDirectory(root);
        await new PackageMetadataCache(category).WriteAsync(
            new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));
        await File.WriteAllTextAsync(Path.Combine(category, "keep.txt"), "keep", Encoding.UTF8);

        try
        {
            var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", source);
            var unpack = await RunOlAsync("cache", "unpack", archive, "--cache-dir", restored);

            await Assert.That(pack.ExitCode).IsEqualTo(0).Because(pack.Stderr);
            await Assert.That(pack.Stdout).Contains("Packed 1 cache entry");
            await Assert.That(unpack.ExitCode).IsEqualTo(0).Because(unpack.Stderr);
            await Assert.That((await new PackageMetadataCache(Path.Combine(restored, "package-metadata")).TryReadAsync(cacheKey)).IsHit).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WithUnmanagedFileLink_SkipsItAndPacksManagedEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-unmanaged-link-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var category = Path.Combine(source, "package-metadata");
        var outside = Path.Combine(root, "outside.txt");
        var link = Path.Combine(category, "keep.txt");
        var archive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(root);
        await new PackageMetadataCache(category).WriteAsync(
            new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
        await File.WriteAllTextAsync(outside, "keep", Encoding.UTF8);
        File.CreateSymbolicLink(link, outside);

        try
        {
            var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", source);

            await Assert.That(pack.ExitCode).IsEqualTo(0).Because(pack.Stderr);
            await Assert.That(pack.Stdout).Contains("Packed 1 cache entry");
            await Assert.That(await File.ReadAllTextAsync(outside)).IsEqualTo("keep");
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WhenArchiveExceedsRecommendedGitSeedSize_WarnsWithCategoryCounts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-recommended-size-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var archive = Path.Combine(root, "cache.olcache");
        var randomPayload = Convert.ToBase64String(RandomNumberGenerator.GetBytes(1200 * 1024));
        Directory.CreateDirectory(root);
        await new PackageMetadataCache(Path.Combine(source, "package-metadata")).WriteAsync(
            new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], [randomPayload]));

        try
        {
            var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", source);

            await Assert.That(pack.ExitCode).IsEqualTo(0).Because(pack.Stderr);
            await Assert.That(pack.Stdout).Contains("Packed 1 cache entry (");
            await Assert.That(pack.Stderr).Contains("Warning: cache archive exceeds the recommended Git seed size of 1 MiB.");
            await Assert.That(pack.Stderr).Contains("package-metadata: 1");
            await Assert.That(pack.Stderr).Contains("source-repository: 0");
            await Assert.That(pack.Stderr).Contains("github-file: 0");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WithMaxAge_AppliesEntryLimitAfterFiltering()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-filtered-limit-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var restored = Path.Combine(root, "restored");
        var archive = Path.Combine(root, "cache.olcache");
        var oldKey = "pkg:npm/old@1.0.0";
        var recentKey = "pkg:npm/recent@1.0.0";
        var now = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var cache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        Directory.CreateDirectory(root);
        await cache.WriteAsync(new PackageMetadataRecord(oldKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: now.AddDays(-31)));
        await cache.WriteAsync(new PackageMetadataRecord(recentKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: now.AddDays(-1)));

        try
        {
            var limits = new CacheArchiveLimits(
                MaximumArchiveBytes: 1024 * 1024,
                MaximumEntryBytes: 1024 * 1024,
                MaximumExpandedBytes: 1024 * 1024,
                MaximumEntryCount: 1);

            var result = CacheArchive.Pack(archive, CachePaths.Resolve(source), TimeSpan.FromDays(30), now, limits);
            var unpack = await RunOlAsync("cache", "unpack", archive, "--cache-dir", restored);
            var restoredCache = new PackageMetadataCache(Path.Combine(restored, "package-metadata"));

            await Assert.That(result.EntryCount).IsEqualTo(1);
            await Assert.That(unpack.ExitCode).IsEqualTo(0).Because(unpack.Stderr);
            await Assert.That((await restoredCache.TryReadAsync(oldKey)).IsHit).IsFalse();
            await Assert.That((await restoredCache.TryReadAsync(recentKey)).IsHit).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Prune_WithMaxAge_RemovesOnlyOldManagedEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-prune-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var category = Path.Combine(source, "package-metadata");
        var oldKey = "pkg:npm/old@1.0.0";
        var recentKey = "pkg:npm/recent@1.0.0";
        var cache = new PackageMetadataCache(category);
        Directory.CreateDirectory(root);
        await cache.WriteAsync(new PackageMetadataRecord(oldKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: DateTimeOffset.UtcNow.AddDays(-31)));
        await cache.WriteAsync(new PackageMetadataRecord(recentKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var unrelated = Path.Combine(category, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep", Encoding.UTF8);

        try
        {
            var prune = await RunOlAsync("cache", "prune", "--cache-dir", source, "--max-age", "30d");

            await Assert.That(prune.ExitCode).IsEqualTo(0).Because(prune.Stderr);
            await Assert.That(prune.Stdout).Contains("Pruned 1 cache entry");
            await Assert.That((await cache.TryReadAsync(oldKey)).IsHit).IsFalse();
            await Assert.That((await cache.TryReadAsync(recentKey)).IsHit).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(unrelated)).IsEqualTo("keep");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Prune_WithUnmanagedFileLink_SkipsItAndRemovesOldManagedEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-prune-unmanaged-link-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var category = Path.Combine(source, "package-metadata");
        var outside = Path.Combine(root, "outside.txt");
        var link = Path.Combine(category, "keep.txt");
        var cacheKey = "pkg:npm/old@1.0.0";
        var cache = new PackageMetadataCache(category);
        Directory.CreateDirectory(root);
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: DateTimeOffset.UtcNow.AddDays(-31)));
        await File.WriteAllTextAsync(outside, "keep", Encoding.UTF8);
        File.CreateSymbolicLink(link, outside);

        try
        {
            var prune = await RunOlAsync("cache", "prune", "--cache-dir", source, "--max-age", "30d");

            await Assert.That(prune.ExitCode).IsEqualTo(0).Because(prune.Stderr);
            await Assert.That(prune.Stdout).Contains("Pruned 1 cache entry");
            await Assert.That((await cache.TryReadAsync(cacheKey)).IsHit).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(outside)).IsEqualTo("keep");
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Prune_WithLinkedCategory_RejectsWithoutDeletingOutsideCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-linked-prune-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var outside = Path.Combine(root, "outside");
        var link = Path.Combine(source, "package-metadata");
        var outsideCache = new PackageMetadataCache(outside);
        var cacheKey = "pkg:npm/example@1.0.0";
        Directory.CreateDirectory(source);
        await outsideCache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: DateTimeOffset.UtcNow.AddDays(-31)));
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            var prune = await RunOlAsync("cache", "prune", "--cache-dir", source, "--max-age", "30d");

            await Assert.That(prune.ExitCode).IsEqualTo(1);
            await Assert.That(prune.Stderr).Contains("Cache path must not contain symbolic links or reparse points");
            await Assert.That(File.Exists(outsideCache.GetPath(cacheKey))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Prune_WithLinkedManagedEntry_RejectsWithoutDeletingOutsideCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-linked-entry-prune-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source", "package-metadata");
        var outside = Path.Combine(root, "outside");
        var outsideCache = new PackageMetadataCache(outside);
        var cacheKey = "pkg:npm/example@1.0.0";
        Directory.CreateDirectory(source);
        await outsideCache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], [], FetchedAt: DateTimeOffset.UtcNow.AddDays(-31)));
        var link = Path.Combine(source, Path.GetFileName(outsideCache.GetPath(cacheKey)));
        File.CreateSymbolicLink(link, outsideCache.GetPath(cacheKey));

        try
        {
            var prune = await RunOlAsync("cache", "prune", "--cache-dir", Path.Combine(root, "source"), "--max-age", "30d");

            await Assert.That(prune.ExitCode).IsEqualTo(1);
            await Assert.That(prune.Stderr).Contains("Cache path must not contain symbolic links or reparse points");
            await Assert.That(File.Exists(outsideCache.GetPath(cacheKey))).IsTrue();
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WhenExpandedContentExceedsUnpackLimit_RejectsWithoutReplacingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-expanded-limit-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var archive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(root);
        await new PackageMetadataCache(Path.Combine(source, "package-metadata")).WriteAsync(
            new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
        await File.WriteAllTextAsync(archive, "keep", Encoding.UTF8);

        try
        {
            var limits = new CacheArchiveLimits(
                MaximumArchiveBytes: long.MaxValue,
                MaximumEntryBytes: 16L * 1024 * 1024,
                MaximumExpandedBytes: 64,
                MaximumEntryCount: 10);

            await Assert.That(() => CacheArchive.Pack(archive, CachePaths.Resolve(source), maximumAge: null, DateTimeOffset.UtcNow, limits)).Throws<InvalidDataException>();
            await Assert.That(await File.ReadAllTextAsync(archive)).IsEqualTo("keep");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WhenCompressedArchiveExceedsUnpackLimit_RejectsWithoutReplacingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-compressed-limit-{Guid.NewGuid():N}");
        var archive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(archive, "keep", Encoding.UTF8);

        try
        {
            var limits = new CacheArchiveLimits(
                MaximumArchiveBytes: 1,
                MaximumEntryBytes: 16L * 1024 * 1024,
                MaximumExpandedBytes: 1024,
                MaximumEntryCount: 10);

            await Assert.That(() => CacheArchive.Pack(archive, CachePaths.Resolve(Path.Combine(root, "source")), maximumAge: null, DateTimeOffset.UtcNow, limits)).Throws<InvalidDataException>();
            await Assert.That(await File.ReadAllTextAsync(archive)).IsEqualTo("keep");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments("0d")]
    [Arguments("-1d")]
    [Arguments("30")]
    [Arguments("30w")]
    [Arguments("999999999999999999999d")]
    public async Task Pack_WithInvalidMaxAge_RejectsWithoutCreatingArchive(string maxAge)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-invalid-age-{Guid.NewGuid():N}");
        var archive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(root);
        try
        {
            var result = await RunOlAsync("cache", "pack", archive, "--cache-dir", Path.Combine(root, "cache"), "--max-age", maxAge);

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stderr).Contains("Max age must be a positive integer followed by d, h, or m.");
            await Assert.That(File.Exists(archive)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WithMismatchedCacheIdentity_RejectsWithoutReplacingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-invalid-entry-{Guid.NewGuid():N}");
        var cacheRoot = Path.Combine(root, "cache", "package-metadata");
        var archive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(cacheRoot);
        await File.WriteAllTextAsync(Path.Combine(cacheRoot, new string('0', 64) + ".json"), """{"SchemaVersion":1,"CacheKey":"pkg:npm/example@1.0.0","CacheKeySha256":"0000000000000000000000000000000000000000000000000000000000000000","FetchedAt":"2026-08-01T00:00:00+00:00"}""", Encoding.UTF8);
        await File.WriteAllTextAsync(archive, "keep", Encoding.UTF8);

        try
        {
            var result = await RunOlAsync("cache", "pack", archive, "--cache-dir", Path.Combine(root, "cache"));

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stderr).Contains("Cache entry identity does not match its file name");
            await Assert.That(await File.ReadAllTextAsync(archive)).IsEqualTo("keep");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Unpack_WithUnsupportedFormatVersion_RejectsWithoutChangingCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-version-{Guid.NewGuid():N}");
        var cacheRoot = Path.Combine(root, "cache");
        var sentinel = Path.Combine(cacheRoot, "package-metadata", "keep.txt");
        var archive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        await File.WriteAllTextAsync(sentinel, "keep", Encoding.UTF8);
        WriteTestArchive(archive, [(TarEntryType.RegularFile, "ol-cache-manifest.json", "{\"FormatVersion\":2}")]);

        try
        {
            var result = await RunOlAsync("cache", "unpack", archive, "--cache-dir", cacheRoot);

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stderr).Contains("Unsupported cache archive format version");
            await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments(TarEntryType.RegularFile, "package-metadata/../outside.json")]
    [Arguments(TarEntryType.SymbolicLink, "package-metadata/0000000000000000000000000000000000000000000000000000000000000000.json")]
    public async Task Unpack_WithUnsafeEntry_RejectsWithoutWritingOutsideCache(TarEntryType entryType, string entryName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-unsafe-{Guid.NewGuid():N}");
        var cacheRoot = Path.Combine(root, "cache");
        var archive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(root);
        WriteTestArchive(archive,
        [
            (TarEntryType.RegularFile, "ol-cache-manifest.json", "{\"FormatVersion\":1}"),
            (entryType, entryName, "{}"),
        ]);

        try
        {
            var result = await RunOlAsync("cache", "unpack", archive, "--cache-dir", cacheRoot);

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(Directory.Exists(cacheRoot)).IsFalse();
            await Assert.That(File.Exists(Path.Combine(root, "outside.json"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments("root")]
    [Arguments("category")]
    public async Task Unpack_WithLinkedCachePath_RejectsWithoutWritingOutsideCache(string linkedPath)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-linked-unpack-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        var outside = Path.Combine(root, "outside");
        var archive = Path.Combine(root, "cache.olcache");
        var link = linkedPath == "root" ? destination : Path.Combine(destination, "package-metadata");
        Directory.CreateDirectory(root);
        await new PackageMetadataCache(Path.Combine(source, "package-metadata")).WriteAsync(
            new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
        var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", source);
        await Assert.That(pack.ExitCode).IsEqualTo(0).Because(pack.Stderr);
        Directory.CreateDirectory(outside);
        if (linkedPath == "category") Directory.CreateDirectory(destination);
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            var unpack = await RunOlAsync("cache", "unpack", archive, "--cache-dir", destination);

            await Assert.That(unpack.ExitCode).IsEqualTo(1);
            await Assert.That(unpack.Stderr).Contains("Cache path must not contain symbolic links or reparse points");
            await Assert.That(Directory.EnumerateFileSystemEntries(outside)).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WithLinkedCategory_RejectsWithoutReplacingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-linked-category-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var outside = Path.Combine(root, "outside");
        var link = Path.Combine(source, "package-metadata");
        var archive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(source);
        await new PackageMetadataCache(outside).WriteAsync(
            new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", "MIT", string.Empty, [], []));
        Directory.CreateSymbolicLink(link, outside);
        await File.WriteAllTextAsync(archive, "keep", Encoding.UTF8);

        try
        {
            var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", source);

            await Assert.That(pack.ExitCode).IsEqualTo(1);
            await Assert.That(pack.Stderr).Contains("Cache path must not contain symbolic links or reparse points");
            await Assert.That(await File.ReadAllTextAsync(archive)).IsEqualTo("keep");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WithLinkedEntry_RejectsWithoutReplacingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-linked-entry-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source", "package-metadata");
        var outside = Path.Combine(root, "outside");
        var archive = Path.Combine(root, "cache.olcache");
        var outsideCache = new PackageMetadataCache(outside);
        var cacheKey = "pkg:npm/example@1.0.0";
        Directory.CreateDirectory(source);
        await outsideCache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));
        var link = Path.Combine(source, Path.GetFileName(outsideCache.GetPath(cacheKey)));
        File.CreateSymbolicLink(link, outsideCache.GetPath(cacheKey));
        await File.WriteAllTextAsync(archive, "keep", Encoding.UTF8);

        try
        {
            var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", Path.Combine(root, "source"));

            await Assert.That(pack.ExitCode).IsEqualTo(1);
            await Assert.That(pack.Stderr).Contains("Cache path must not contain symbolic links or reparse points");
            await Assert.That(await File.ReadAllTextAsync(archive)).IsEqualTo("keep");
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WithArchiveInsideCacheCategory_RejectsWithoutChangingCacheEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-overlapping-pack-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var cacheKey = "pkg:npm/example@1.0.0";
        var cache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        Directory.CreateDirectory(root);
        await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));
        var archive = cache.GetPath(cacheKey);
        var original = await File.ReadAllBytesAsync(archive);

        try
        {
            var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", source);

            await Assert.That(pack.ExitCode).IsEqualTo(1);
            await Assert.That(pack.Stderr).Contains("Archive path must be outside the managed cache directories");
            await Assert.That((await File.ReadAllBytesAsync(archive)).AsSpan().SequenceEqual(original)).IsTrue();
            await Assert.That((await cache.TryReadAsync(cacheKey)).IsHit).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Unpack_WithArchiveInsideCacheCategory_RejectsWithoutReplacingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-overlapping-unpack-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        var cacheKey = "pkg:npm/example@1.0.0";
        var sourceCache = new PackageMetadataCache(Path.Combine(source, "package-metadata"));
        var destinationCache = new PackageMetadataCache(Path.Combine(destination, "package-metadata"));
        var packedArchive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(root);
        await sourceCache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));
        CacheArchive.Pack(packedArchive, CachePaths.Resolve(source), maximumAge: null, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationCache.GetPath(cacheKey))!);
        var archive = destinationCache.GetPath(cacheKey);
        File.Copy(packedArchive, archive);
        var original = await File.ReadAllBytesAsync(archive);

        try
        {
            var unpack = await RunOlAsync("cache", "unpack", archive, "--cache-dir", destination);

            await Assert.That(unpack.ExitCode).IsEqualTo(1);
            await Assert.That(unpack.Stderr).Contains("Archive path must be outside the managed cache directories");
            await Assert.That((await File.ReadAllBytesAsync(archive)).AsSpan().SequenceEqual(original)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Unpack_WithGnuTarArchive_RejectsWithoutChangingCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-gnu-tar-{Guid.NewGuid():N}");
        var cacheRoot = Path.Combine(root, "cache");
        var archive = Path.Combine(root, "cache.olcache");
        Directory.CreateDirectory(root);
        WriteGnuTestArchive(archive, "ol-cache-manifest.json", "{\"FormatVersion\":1}");

        try
        {
            var unpack = await RunOlAsync("cache", "unpack", archive, "--cache-dir", cacheRoot);

            await Assert.That(unpack.ExitCode).IsEqualTo(1);
            await Assert.That(unpack.Stderr).Contains("Unsupported archive format");
            await Assert.That(Directory.Exists(cacheRoot)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pack_WithLinkedArchiveParentIntoCache_RejectsWithoutWritingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-linked-archive-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var category = Path.Combine(source, "package-metadata");
        var linkedParent = Path.Combine(root, "archive-parent");
        var archive = Path.Combine(linkedParent, "cache.olcache");
        Directory.CreateDirectory(category);
        Directory.CreateSymbolicLink(linkedParent, category);

        try
        {
            var pack = await RunOlAsync("cache", "pack", archive, "--cache-dir", source);

            await Assert.That(pack.ExitCode).IsEqualTo(1);
            await Assert.That(pack.Stderr).Contains("Archive path must not contain symbolic links or reparse points");
            await Assert.That(File.Exists(Path.Combine(category, "cache.olcache"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(linkedParent)) Directory.Delete(linkedParent);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Unpack_WithManifestAfterCacheEntry_RejectsBeforeChangingCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-cache-late-manifest-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source", "package-metadata");
        var cacheRoot = Path.Combine(root, "cache");
        var archive = Path.Combine(root, "cache.olcache");
        var cacheKey = "pkg:npm/example@1.0.0";
        var sourceCache = new PackageMetadataCache(source);
        Directory.CreateDirectory(root);
        await sourceCache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));
        var cachePath = sourceCache.GetPath(cacheKey);
        WriteTestArchive(archive,
        [
            (TarEntryType.RegularFile, $"package-metadata/{Path.GetFileName(cachePath)}", await File.ReadAllTextAsync(cachePath)),
            (TarEntryType.RegularFile, "ol-cache-manifest.json", "{\"FormatVersion\":1}"),
        ]);

        try
        {
            var unpack = await RunOlAsync("cache", "unpack", archive, "--cache-dir", cacheRoot);

            await Assert.That(unpack.ExitCode).IsEqualTo(1);
            await Assert.That(unpack.Stderr).Contains("Archive manifest must be the first entry");
            await Assert.That(Directory.Exists(cacheRoot)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteTestArchive(string path, (TarEntryType Type, string Name, string Content)[] entries)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: false);
        using var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: false);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = new UstarTarEntry(entries[i].Type, entries[i].Name);
            if (entries[i].Type == TarEntryType.RegularFile)
            {
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(entries[i].Content), writable: false);
            }
            else
            {
                entry.LinkName = "target";
            }

            writer.WriteEntry(entry);
            entry.DataStream?.Dispose();
        }
    }

    private static void WriteGnuTestArchive(string path, string name, string content)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: false);
        using var writer = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: false);
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlAsync(params string[] args)
    {
        await CliGate.WaitAsync();
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = FindRepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(CliTestAssembly.ResolveOlDllPath(AppContext.BaseDirectory));
            for (var i = 0; i < args.Length; i++) startInfo.ArgumentList.Add(args[i]);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ol CLI.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            CliGate.Release();
        }
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
