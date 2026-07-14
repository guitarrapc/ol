using System.Net;
using Ol.Core;

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

        var response = new NpmPackageMetadataProvider().ParseResponse(document.RootElement);

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

        var record = await SourceRepositoryFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1);

        await Assert.That(record.License!.Value.SpdxId).IsEqualTo("MIT");
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task FetchScheduler_ExhaustedServerFailure_ThrowsAfterConfiguredAttempts()
    {
        var handler = new SequenceResponseHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        await Assert.That(async () => await SourceRepositoryFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1)).Throws<SourceRepositoryFetchException>();
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task FetchScheduler_Forbidden_DoesNotRetry()
    {
        var handler = new SequenceResponseHandler(HttpStatusCode.Forbidden, HttpStatusCode.OK);
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        await Assert.That(async () => await SourceRepositoryFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1)).Throws<SourceRepositoryFetchException>();
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task FetchScheduler_TimeoutExhausted_RetriesConfiguredAttempts()
    {
        var handler = new TimeoutResponseHandler();
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        await Assert.That(async () => await SourceRepositoryFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1)).Throws<TaskCanceledException>();
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Enrichment_WithRefresh_RefetchesAndOverwritesSourceCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-refresh-{Guid.NewGuid():N}");
        var metadataCache = new PackageMetadataCache(Path.Combine(root, "package"));
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await metadataCache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        await sourceCache.WriteAsync(new SourceRepositoryRecord(target.CacheKey, "github-license-api", "none", target.Repository, target.Ref, HttpStatusCode.OK, new GitHubLicenseResult("Apache-2.0", "apache-2.0", "Apache", "LICENSE", "old", string.Empty), [], []));
        var index = new SpdxLicenseIndex(["Apache-2.0", "MIT"], []);
        var component = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create("sbom", "id", "NOASSERTION"u8, index), [], []);
        using var httpClient = new HttpClient(new GitHubResponseHandler(HttpStatusCode.OK, ReadGitHubLicenseFixture()));
        var service = new SourceRepositoryService(index, metadataCache, sourceCache, refresh: true, retryCount: 0, httpClient);

        try
        {
            var enrichment = await service.EnrichAsync([component], concurrency: 1);
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
        var metadataCache = new PackageMetadataCache(Path.Combine(root, "package"));
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await metadataCache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        Directory.CreateDirectory(sourceCache.Root);
        await File.WriteAllTextAsync(sourceCache.GetPath(target.CacheKey), "{ invalid json");
        var index = new SpdxLicenseIndex(["MIT"], []);
        var component = new ScanComponent("example", "1.0.0", "MIT", "npm", DependencyType.Unknown, LicenseStatus.Matched, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create("sbom", "id", "MIT"u8, index), [], []);
        using var httpClient = new HttpClient(new SequenceResponseHandler(HttpStatusCode.Forbidden));
        var service = new SourceRepositoryService(index, metadataCache, sourceCache, refresh: false, retryCount: 1, httpClient);

        try
        {
            var enrichment = await service.EnrichAsync([component], concurrency: 1);
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
        var metadataCache = new PackageMetadataCache(Path.Combine(root, "package"));
        var invalidSourceRoot = Path.Combine(root, "source-is-a-file");
        await metadataCache.WriteAsync(new PackageMetadataRecord("pkg:npm/example@1.0.0", "npm-registry", string.Empty, "https://github.com/owner/repository", [], []));
        await File.WriteAllTextAsync(invalidSourceRoot, "not a directory");
        var index = new SpdxLicenseIndex(["MIT"], []);
        var component = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create("sbom", "id", "NOASSERTION"u8, index), [], []);
        using var httpClient = new HttpClient(new GitHubResponseHandler(HttpStatusCode.OK, ReadGitHubLicenseFixture()));
        var service = new SourceRepositoryService(index, metadataCache, new SourceRepositoryCache(invalidSourceRoot), refresh: true, retryCount: 0, httpClient);

        try
        {
            var enrichment = await service.EnrichAsync([component], concurrency: 1);

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
