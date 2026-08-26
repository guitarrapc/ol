using Ol.Core.GitHub;
using Ol.Core.Spdx;

namespace Ol.Tests;

public sealed class DeclaredGitHubFileCacheTests
{
    [Test]
    public async Task Read_OversizedEntry_IsInvalidBeforeContentParsing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-github-file-cache-{Guid.NewGuid():N}");
        DeclaredGitHubFileTarget.TryCreate("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT", out var target);
        var cache = new DeclaredGitHubFileCache(root);
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(cache.GetPath(target.CacheKey), new byte[2 * 1024 * 1024]);

        try
        {
            var result = cache.Read(target, new SpdxLicenseTextMatcher("test", [new("MIT", "MIT License")]));

            await Assert.That(result.Status).IsEqualTo(DeclaredGitHubFileCacheReadStatus.Invalid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Read_EntryCopiedFromDifferentTarget_IsInvalid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-github-file-cache-{Guid.NewGuid():N}");
        DeclaredGitHubFileTarget.TryCreate("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT", out var first);
        DeclaredGitHubFileTarget.TryCreate("https://github.com/dotnet/runtime/blob/main/LICENSE.TXT", out var second);
        var cache = new DeclaredGitHubFileCache(root);
        cache.Write(first, System.Net.HttpStatusCode.OK, "MIT License"u8);
        File.Copy(cache.GetPath(first.CacheKey), cache.GetPath(second.CacheKey));

        try
        {
            var result = cache.Read(second, new SpdxLicenseTextMatcher("test", [new("MIT", "MIT License")]));

            await Assert.That(result.Status).IsEqualTo(DeclaredGitHubFileCacheReadStatus.Invalid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Read_PreviousNotFoundEntry_IsInvalid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-github-file-cache-{Guid.NewGuid():N}");
        DeclaredGitHubFileTarget.TryCreate("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT", out var target);
        var cache = new DeclaredGitHubFileCache(root);
        Directory.CreateDirectory(root);
        var keyHash = Ol.Core.SourceRepository.SourceRepositoryCache.GetCacheKeySha256(target.CacheKey);
        await File.WriteAllTextAsync(cache.GetPath(target.CacheKey), $$"""
            {
              "SchemaVersion": 1,
              "CacheKey": "{{target.CacheKey}}",
              "CacheKeySha256": "{{keyHash}}",
              "Source": "github-contents-api",
              "HttpStatus": 404,
              "ContentSha256": "",
              "Content": "",
              "FetchedAt": "2026-08-12T00:00:00+00:00"
            }
            """);

        try
        {
            var result = cache.Read(target, new SpdxLicenseTextMatcher("test", [new("MIT", "MIT License")]));

            await Assert.That(result.Status).IsEqualTo(DeclaredGitHubFileCacheReadStatus.Invalid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
