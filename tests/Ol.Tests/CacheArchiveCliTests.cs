using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Ol.Core.GitHub;
using Ol.Internals;

namespace Ol.Tests;

public sealed class CacheArchiveCliTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

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
