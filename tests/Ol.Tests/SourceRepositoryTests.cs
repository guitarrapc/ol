using System.Net;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.Spdx;
using Ol.Internals;

namespace Ol.Tests;

public sealed class SourceRepositoryTests
{
    [Test]
    public async Task Target_TryCreate_CommonGitHubUrls_NormalizesOwnerRepositoryAndRef()
    {
        var urls = new[]
        {
            "https://github.com/owner/repository.git",
            "git+https://github.com/owner/repository.git",
            "git://github.com/owner/repository.git",
            "ssh://git@github.com/owner/repository.git",
            "git@github.com:owner/repository.git",
        };

        for (var i = 0; i < urls.Length; i++)
        {
            var parsed = SourceRepositoryTarget.TryCreate(urls[i], out var target);

            await Assert.That(parsed).IsTrue();
            await Assert.That(target.Repository).IsEqualTo("owner/repository");
            await Assert.That(target.Ref).IsEqualTo("default");
            await Assert.That(target.CacheKey).IsEqualTo("github:owner/repository@default");
        }
    }

    [Test]
    public async Task Target_TryCreate_NonGitHubOrMissingUrl_RejectsTarget()
    {
        await Assert.That(SourceRepositoryTarget.TryCreate("https://example.test/owner/repository", out _)).IsFalse();
        await Assert.That(SourceRepositoryTarget.TryCreate(string.Empty, out _)).IsFalse();
        await Assert.That(SourceRepositoryTarget.TryCreate("https://github.com/owner/repository", null!, out _)).IsFalse();
    }

    [Test]
    public async Task Target_TryCreate_WithPackageMetadataRef_UsesExplicitRefInCacheIdentity()
    {
        var parsed = SourceRepositoryTarget.TryCreate("https://github.com/owner/repository.git", "0123456789abcdef", out var target);

        await Assert.That(parsed).IsTrue();
        await Assert.That(target.Ref).IsEqualTo("0123456789abcdef");
        await Assert.That(target.CacheKey).IsEqualTo("github:owner/repository@0123456789abcdef");
    }

    [Test]
    public async Task NpmProvider_ParseResponse_WithGitHead_ProjectsRepositoryRef()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{ "license": "MIT", "repository": { "url": "https://github.com/owner/repository.git" }, "gitHead": "0123456789abcdef" }""");

        var response = new NpmPackageMetadataProvider().ParseResponse(document.RootElement, default);

        await Assert.That(response.RepositoryUrl).IsEqualTo("https://github.com/owner/repository.git");
        await Assert.That(response.RepositoryRef).IsEqualTo("0123456789abcdef");
    }

    [Test]
    public async Task Authentication_GitHubTokenOnly_UsesNoAuthentication()
    {
        var authentication = GitHubAuthentication.Create(olGitHubToken: null, githubToken: "must-not-be-used");

        await Assert.That(authentication.Mode).IsEqualTo("none");
        await Assert.That(authentication.Token).IsEmpty();
    }

    [Test]
    public async Task Cache_WriteThenRead_UsesHashNamedEntryAndRetainsLogicalTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-cache-{Guid.NewGuid():N}");
        var target = new SourceRepositoryTarget("owner", "private-repository", "main");
        var record = new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, HttpStatusCode.OK, new GitHubLicenseResult("MIT", "mit", "MIT License", "LICENSE", "sha", "https://github.com/owner/private-repository/blob/main/LICENSE"), [], []);
        try
        {
            var cache = new SourceRepositoryCache(root);
            await cache.WriteAsync(record);
            var read = await cache.TryReadAsync(target.CacheKey);

            await Assert.That(read.HasValue).IsTrue();
            await Assert.That(read!.Value.License!.Value.SpdxId).IsEqualTo("MIT");
            await Assert.That(Path.GetFileName(cache.GetPath(target.CacheKey))).DoesNotContain("private-repository");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Client_Fetch_ValidSpdxId_CreatesLicenseRecordAndSendsOnlyOlToken()
    {
        var handler = new GitHubResponseHandler(HttpStatusCode.OK, ReadGitHubLicenseFixture());
        var target = new SourceRepositoryTarget("owner", "repository", "main");
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create("secret-token", "must-not-be-used"));

        var record = await client.FetchAsync(target);

        await Assert.That(record.License!.Value.SpdxId).IsEqualTo("MIT");
        await Assert.That(record.AuthMode).IsEqualTo("ol_github_token");
        await Assert.That(handler.Authorization).IsEqualTo("Bearer secret-token");
        await Assert.That(handler.RequestUri).IsEqualTo("https://api.github.com/repos/owner/repository/license?ref=main");
    }

    [Test]
    public async Task Client_Fetch_NoAssertionAndNotFound_ProducesUnknownRecords()
    {
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        var noAssertion = await new GitHubLicenseApiClient(new GitHubResponseHandler(HttpStatusCode.OK, """{ "license": { "spdx_id": "NOASSERTION" } }"""), GitHubAuthentication.Create(null, null)).FetchAsync(target);
        var notFound = await new GitHubLicenseApiClient(new GitHubResponseHandler(HttpStatusCode.NotFound, string.Empty), GitHubAuthentication.Create(null, null)).FetchAsync(target);

        await Assert.That(noAssertion.License!.Value.SpdxId).IsEqualTo("NOASSERTION");
        await Assert.That(notFound.License.HasValue).IsFalse();
        await Assert.That(notFound.Warnings[0]).IsEqualTo("license_not_detected");
    }

    [Test]
    public async Task Cache_Read_CorruptEntry_DistinguishesInvalidFromMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-cache-{Guid.NewGuid():N}");
        var cache = new SourceRepositoryCache(root);
        const string cacheKey = "github:owner/repository@default";
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(cache.GetPath(cacheKey), "{ invalid json");

            var invalid = await cache.ReadAsync(cacheKey);
            var missing = await cache.ReadAsync("github:owner/missing@default");

            await Assert.That(invalid.Status).IsEqualTo(SourceRepositoryCacheReadStatus.Invalid);
            await Assert.That(invalid.Record.HasValue).IsFalse();
            await Assert.That(missing.Status).IsEqualTo(SourceRepositoryCacheReadStatus.Missing);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Client_Fetch_NullLicense_ProducesUnknownRecord()
    {
        var record = await new GitHubLicenseApiClient(
            new GitHubResponseHandler(HttpStatusCode.OK, """{ "license": null }"""),
            GitHubAuthentication.Create()).FetchAsync(new SourceRepositoryTarget("owner", "repository", "default"));

        await Assert.That(record.License.HasValue).IsTrue();
        await Assert.That(record.License!.Value.SpdxId).IsNull();
    }

    [Test]
    public async Task FetchScheduler_RateLimitThenSuccess_RetriesAndReturnsLicense()
    {
        var handler = new SequenceResponseHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        var record = await GitHubLicenseFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1);

        await Assert.That(record.License!.Value.SpdxId).IsEqualTo("MIT");
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task FetchScheduler_ExhaustedServerFailure_ThrowsAfterConfiguredAttempts()
    {
        var handler = new SequenceResponseHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        await Assert.That(async () => await GitHubLicenseFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1)).Throws<SourceRepositoryFetchException>();
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task FetchScheduler_Forbidden_DoesNotRetry()
    {
        var handler = new SequenceResponseHandler(HttpStatusCode.Forbidden, HttpStatusCode.OK);
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        await Assert.That(async () => await GitHubLicenseFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1)).Throws<SourceRepositoryFetchException>();
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task FetchScheduler_TimeoutExhausted_RetriesConfiguredAttempts()
    {
        var handler = new TimeoutResponseHandler();
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        await Assert.That(async () => await GitHubLicenseFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1)).Throws<TaskCanceledException>();
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Enrichment_WithSuppliedPackageMetadata_UsesRepositoryWithoutCacheRead()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-metadata-{Guid.NewGuid():N}");
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await sourceCache.WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, HttpStatusCode.OK, new GitHubLicenseResult("MIT", "mit", "MIT License", "LICENSE", "sha", string.Empty), [], []));
        var index = new SpdxLicenseIndex(["MIT"], []);
        var component = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);
        using var workspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty));
        var service = new SourceRepositoryService(index, sourceCache, refresh: false, retryCount: 0);

        try
        {
            var enrichment = await service.EnrichAsync([component], workspace, concurrency: 1);

            await Assert.That(enrichment.Components[0].License.ToString()).IsEqualTo("MIT");
            await Assert.That(enrichment.Summary.CacheHitCount).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrichment_WithRefresh_RefetchesAndOverwritesSourceCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-refresh-{Guid.NewGuid():N}");
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        using var workspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty));
        await sourceCache.WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, HttpStatusCode.OK, new GitHubLicenseResult("Apache-2.0", "apache-2.0", "Apache", "LICENSE", "old", string.Empty), [], []));
        var index = new SpdxLicenseIndex(["Apache-2.0", "MIT"], []);
        var component = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);
        using var httpClient = new HttpClient(new GitHubResponseHandler(HttpStatusCode.OK, ReadGitHubLicenseFixture()));
        var service = new SourceRepositoryService(index, sourceCache, refresh: true, retryCount: 0, httpClient);

        try
        {
            var enrichment = await service.EnrichAsync([component], workspace, concurrency: 1);
            var cached = await sourceCache.TryReadAsync(target.CacheKey);

            await Assert.That(enrichment.Components[0].License.ToString()).IsEqualTo("MIT");
            await Assert.That(enrichment.Summary.GitHubRequestCount).IsEqualTo(1);
            await Assert.That(enrichment.Summary.CacheHitCount).IsEqualTo(0);
            await Assert.That(cached!.Value.License!.Value.SpdxId).IsEqualTo("MIT");
            await Assert.That(cached.Value.License!.Value.Sha).IsNotEqualTo("old");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrichment_WithCorruptCacheAndFetchFailure_PreservesAuditWarningsAndValidSbom()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-invalid-{Guid.NewGuid():N}");
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        using var workspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty));
        Directory.CreateDirectory(sourceCache.Root);
        await File.WriteAllTextAsync(sourceCache.GetPath(target.CacheKey), "{ invalid json");
        var index = new SpdxLicenseIndex(["MIT"], []);
        var component = new ScanComponent("example", "1.0.0", "MIT", "npm", DependencyType.Unknown, LicenseStatus.Matched, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "MIT"u8, index), [], []);
        using var httpClient = new HttpClient(new SequenceResponseHandler(HttpStatusCode.Forbidden));
        var service = new SourceRepositoryService(index, sourceCache, refresh: false, retryCount: 1, httpClient);

        try
        {
            var enrichment = await service.EnrichAsync([component], workspace, concurrency: 1);
            var warnings = enrichment.Components[0].Warnings;
            var cached = await sourceCache.TryReadAsync(target.CacheKey);

            await Assert.That(enrichment.Components[0].Status).IsEqualTo(LicenseStatus.Matched);
            await Assert.That(warnings).Contains("source_repository_cache_invalid");
            await Assert.That(warnings).Contains("source_repository_fetch_failed");
            await Assert.That(enrichment.Summary.FetchErrorCount).IsEqualTo(1);
            await Assert.That(enrichment.Summary.UnknownCount).IsEqualTo(0);
            await Assert.That(cached!.Value.Errors).Contains("source_repository_fetch_failed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrichment_WithCacheWriteFailure_KeepsFetchedLicenseAsComponentEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-write-failure-{Guid.NewGuid():N}");
        var invalidSourceRoot = Path.Combine(root, "source-is-a-file");
        using var workspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(invalidSourceRoot, "not a directory");
        var index = new SpdxLicenseIndex(["MIT"], []);
        var component = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);
        using var httpClient = new HttpClient(new GitHubResponseHandler(HttpStatusCode.OK, ReadGitHubLicenseFixture()));
        var service = new SourceRepositoryService(index, new SourceRepositoryCache(invalidSourceRoot), refresh: true, retryCount: 0, httpClient);

        try
        {
            var enrichment = await service.EnrichAsync([component], workspace, concurrency: 1);

            await Assert.That(enrichment.Components[0].Status).IsEqualTo(LicenseStatus.Matched);
            await Assert.That(enrichment.Components[0].License.ToString()).IsEqualTo("MIT");
            await Assert.That(enrichment.Components[0].Warnings).Contains("source_repository_cache_write_failed");
            await Assert.That(enrichment.Summary.FetchErrorCount).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Client_Fetch_CustomApiBaseUri_SendsRequestToConfiguredMockHost()
    {
        var handler = new GitHubResponseHandler(HttpStatusCode.OK, """{ "license": { "spdx_id": "MIT" } }""");
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create("secret-token"), new Uri("http://127.0.0.1:19080/"));

        await client.FetchAsync(new SourceRepositoryTarget("owner", "repository", "main"));

        await Assert.That(handler.RequestUri).IsEqualTo("http://127.0.0.1:19080/repos/owner/repository/license?ref=main");
        await Assert.That(handler.Authorization).IsEmpty();
    }

    [Test]
    public async Task Cache_Read_ValidEntry_MatchesAsyncHit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-cache-{Guid.NewGuid():N}");
        var target = new SourceRepositoryTarget("owner", "repository", "main");
        try
        {
            var cache = new SourceRepositoryCache(root);
            await cache.WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, HttpStatusCode.OK, new GitHubLicenseResult("MIT", "mit", "MIT License", "LICENSE", "sha", string.Empty), [], []));

            var read = cache.Read(target.CacheKey);
            var readAsync = await cache.ReadAsync(target.CacheKey);

            await Assert.That(read.Status).IsEqualTo(SourceRepositoryCacheReadStatus.Hit);
            await Assert.That(read.Status).IsEqualTo(readAsync.Status);
            await Assert.That(read.Record!.Value.CacheKey).IsEqualTo(readAsync.Record!.Value.CacheKey);
            await Assert.That(read.Record.Value.License!.Value.SpdxId).IsEqualTo("MIT");
            await Assert.That(read.Record.Value.FetchedAt).IsEqualTo(readAsync.Record.Value.FetchedAt);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Cache_Read_MissingCorruptAndIncompatibleEntries_MatchAsyncStatus()
    {
        await AssertSyncReadMatchesAsync(null, SourceRepositoryCacheReadStatus.Missing);
        await AssertSyncReadMatchesAsync("{ invalid json", SourceRepositoryCacheReadStatus.Invalid);
        await AssertSyncReadMatchesAsync(CreateSourceCacheJson(source: "other-source"), SourceRepositoryCacheReadStatus.Invalid);
        await AssertSyncReadMatchesAsync(CreateSourceCacheJson(cacheKey: "github:owner/other@default"), SourceRepositoryCacheReadStatus.Invalid);
    }

    [Test]
    public async Task Cache_GetPath_RootSeparatorVariants_MatchesCombinedHashName()
    {
        const string cacheKey = "github:owner/repository@default";
        var fileName = string.Concat(SourceRepositoryCache.GetCacheKeySha256(cacheKey), ".json");
        var directory = Path.Combine(Path.GetTempPath(), "ol-source-cache-path");

        await Assert.That(new SourceRepositoryCache(directory).GetPath(cacheKey)).IsEqualTo(Path.Combine(directory, fileName));
        await Assert.That(new SourceRepositoryCache(directory + Path.DirectorySeparatorChar).GetPath(cacheKey)).IsEqualTo(Path.Combine(directory + Path.DirectorySeparatorChar, fileName));
        await Assert.That(new SourceRepositoryCache(string.Empty).GetPath(cacheKey)).IsEqualTo(fileName);
    }

    [Test]
    public async Task Cache_Read_EmptyEntryFile_ReportsInvalid()
    {
        await AssertSyncReadMatchesAsync(string.Empty, SourceRepositoryCacheReadStatus.Invalid);
    }

    [Test]
    public async Task Cache_Read_MissingRequiredProperties_ReportsInvalid()
    {
        // Renaming a property removes it without depending on the fixture's line endings.
        foreach (var required in new[] { "SchemaVersion", "CacheKeySha256", "AuthMode", "Repository", "Ref", "HttpStatus", "License", "Warnings", "Errors", "FetchedAt" })
        {
            var json = CreateSourceCacheJson().Replace($"\"{required}\":", $"\"Absent{required}\":", StringComparison.Ordinal);

            await AssertSyncReadMatchesAsync(json, SourceRepositoryCacheReadStatus.Invalid);
        }
    }

    [Test]
    public async Task Cache_Read_UnknownSchemaVersionOrMismatchedKeyHash_ReportsInvalid()
    {
        await AssertSyncReadMatchesAsync(CreateSourceCacheJson().Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2", StringComparison.Ordinal), SourceRepositoryCacheReadStatus.Invalid);
        await AssertSyncReadMatchesAsync(CreateSourceCacheJson().Replace(SourceRepositoryCache.GetCacheKeySha256("github:owner/repository@default"), new string('0', 64), StringComparison.Ordinal), SourceRepositoryCacheReadStatus.Invalid);
    }

    [Test]
    public async Task Cache_Read_UnsupportedAuthModeOrEmptyTarget_ReportsInvalid()
    {
        await AssertSyncReadMatchesAsync(CreateSourceCacheJson().Replace("\"AuthMode\": \"none\"", "\"AuthMode\": \"github_token\"", StringComparison.Ordinal), SourceRepositoryCacheReadStatus.Invalid);
        await AssertSyncReadMatchesAsync(CreateSourceCacheJson().Replace("\"Repository\": \"owner/repository\"", "\"Repository\": \"\"", StringComparison.Ordinal), SourceRepositoryCacheReadStatus.Invalid);
        await AssertSyncReadMatchesAsync(CreateSourceCacheJson().Replace("\"Ref\": \"default\"", "\"Ref\": \"\"", StringComparison.Ordinal), SourceRepositoryCacheReadStatus.Invalid);
        await AssertSyncReadMatchesAsync(CreateSourceCacheJson().Replace("\"FetchedAt\": \"2026-07-08T00:00:00+00:00\"", "\"FetchedAt\": \"2026-07-08T00:00:00+09:00\"", StringComparison.Ordinal), SourceRepositoryCacheReadStatus.Invalid);
    }

    [Test]
    public async Task Cache_Read_LicenseAndWarningsContent_IsRetained()
    {
        const string cacheKey = "github:owner/repository@default";
        var json = CreateSourceCacheJson()
            .Replace("\"License\": null", "\"License\": { \"SpdxId\": \"MIT\", \"Key\": \"mit\", \"Name\": \"MIT License\", \"Path\": \"LICENSE\", \"Sha\": \"abc\", \"HtmlUrl\": \"https://example.test/LICENSE\" }", StringComparison.Ordinal)
            .Replace("\"Warnings\": []", "\"Warnings\": [\"source_repository_cache_invalid\"]", StringComparison.Ordinal);
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new SourceRepositoryCache(root);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(cache.GetPath(cacheKey), json);

            var read = cache.Read(cacheKey);

            await Assert.That(read.Status).IsEqualTo(SourceRepositoryCacheReadStatus.Hit);
            await Assert.That(read.Record!.Value.Repository).IsEqualTo("owner/repository");
            await Assert.That(read.Record.Value.Ref).IsEqualTo("default");
            await Assert.That(read.Record.Value.AuthMode).IsEqualTo("none");
            await Assert.That(read.Record.Value.HttpStatus).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(read.Record.Value.License!.Value.SpdxId).IsEqualTo("MIT");
            await Assert.That(read.Record.Value.License.Value.Key).IsEqualTo("mit");
            await Assert.That(read.Record.Value.License.Value.Name).IsEqualTo("MIT License");
            await Assert.That(read.Record.Value.License.Value.Path).IsEqualTo("LICENSE");
            await Assert.That(read.Record.Value.License.Value.Sha).IsEqualTo("abc");
            await Assert.That(read.Record.Value.License.Value.HtmlUrl).IsEqualTo("https://example.test/LICENSE");
            await Assert.That(read.Record.Value.Warnings).IsEquivalentTo(new[] { "source_repository_cache_invalid" });
            await Assert.That(read.Record.Value.Errors).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Cache_Read_UnknownProperty_RemainsCompatibleHit()
    {
        var json = CreateSourceCacheJson().Replace("\"Warnings\": []", "\"Unknown\": { \"nested\": [1, 2] },\n  \"Warnings\": []", StringComparison.Ordinal);

        await AssertSyncReadMatchesAsync(json, SourceRepositoryCacheReadStatus.Hit);
    }

    [Test]
    public async Task Cache_Read_MissingCacheRoot_ReportsMissingWithoutThrowing()
    {
        var cache = new SourceRepositoryCache(Path.Combine(Path.GetTempPath(), $"ol-source-cache-{Guid.NewGuid():N}"));

        await Assert.That(cache.Read("github:owner/repository@default").Status).IsEqualTo(SourceRepositoryCacheReadStatus.Missing);
    }

    private static PackageMetadataWorkspace CreateWorkspace(PackageMetadataResolution? resolution)
    {
        var workspace = new PackageMetadataWorkspace(1);
        workspace.Records[0] = resolution;
        return workspace;
    }

    private static async Task AssertSyncReadMatchesAsync(string? json, SourceRepositoryCacheReadStatus expected)
    {
        const string cacheKey = "github:owner/repository@default";
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new SourceRepositoryCache(root);
            Directory.CreateDirectory(root);
            if (json is not null) await File.WriteAllTextAsync(cache.GetPath(cacheKey), json);

            var read = cache.Read(cacheKey);
            var readAsync = await cache.ReadAsync(cacheKey);

            await Assert.That(read.Status).IsEqualTo(expected);
            await Assert.That(read.Status).IsEqualTo(readAsync.Status);
            await Assert.That(read.Record.HasValue).IsEqualTo(expected == SourceRepositoryCacheReadStatus.Hit);
            await Assert.That(readAsync.Record.HasValue).IsEqualTo(expected == SourceRepositoryCacheReadStatus.Hit);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateSourceCacheJson(string cacheKey = "github:owner/repository@default", string source = "github-license-api")
        => $$"""
            {
              "SchemaVersion": 1,
              "CacheKey": "{{cacheKey}}",
              "CacheKeySha256": "{{SourceRepositoryCache.GetCacheKeySha256("github:owner/repository@default")}}",
              "Source": "{{source}}",
              "AuthMode": "none",
              "Repository": "owner/repository",
              "Ref": "default",
              "HttpStatus": 200,
              "License": null,
              "Warnings": [],
              "Errors": [],
              "FetchedAt": "2026-07-08T00:00:00+00:00"
            }
            """;

    private sealed class GitHubResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public string Authorization { get; private set; } = string.Empty;
        public string RequestUri { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            RequestUri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
        }
    }

    private sealed class SequenceResponseHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = statuses[Math.Min(CallCount, statuses.Length - 1)];
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? ReadGitHubLicenseFixture() : string.Empty),
            });
        }
    }

    private sealed class TimeoutResponseHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout"));
        }
    }

    private static string ReadGitHubLicenseFixture()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-license-api-license.json"));
}
