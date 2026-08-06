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
        var handler = new RateLimitThenSuccessHandler(HttpStatusCode.TooManyRequests, retryAfterSeconds: 0, remaining: null);
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        var record = await GitHubLicenseFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1);

        await Assert.That(record.License!.Value.SpdxId).IsEqualTo("MIT");
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task FetchScheduler_PrimaryRateLimitForbiddenThenSuccess_RetriesAndReturnsLicense()
    {
        var handler = new RateLimitThenSuccessHandler(HttpStatusCode.Forbidden, retryAfterSeconds: null, remaining: 0);
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        var record = await GitHubLicenseFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1);

        await Assert.That(record.License!.Value.SpdxId).IsEqualTo("MIT");
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task FetchScheduler_RetryAfter_DelaysRetry()
    {
        var handler = new RateLimitThenSuccessHandler(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1, remaining: null);
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await GitHubLicenseFetchScheduler.FetchAsync(client, new SourceRepositoryTarget("owner", "repository", "default"), retryCount: 1);

        await Assert.That(stopwatch.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(800));
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Client_Fetch_SecondaryRateLimitWithoutHeaders_ClassifiesTransientWithMinimumDelay()
    {
        var client = new GitHubLicenseApiClient(
            new GitHubResponseHandler(HttpStatusCode.Forbidden, """{ "message": "You have exceeded a secondary rate limit. Please wait before retrying." }"""),
            GitHubAuthentication.Create());
        SourceRepositoryFetchException? failure = null;

        try
        {
            await client.FetchAsync(new SourceRepositoryTarget("owner", "repository", "default"));
        }
        catch (SourceRepositoryFetchException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.IsRateLimited).IsTrue();
        await Assert.That(failure.IsTransient).IsTrue();
        await Assert.That(failure.RetryAfter!.Value).IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task Client_Fetch_RateLimitWithoutHeaders_UsesMinimumDelay()
    {
        var client = new GitHubLicenseApiClient(new SequenceResponseHandler(HttpStatusCode.TooManyRequests), GitHubAuthentication.Create());
        SourceRepositoryFetchException? failure = null;

        try
        {
            await client.FetchAsync(new SourceRepositoryTarget("owner", "repository", "default"));
        }
        catch (SourceRepositoryFetchException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.RetryAfter!.Value).IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task Client_Fetch_RateLimitResetBeyondMaximum_DoesNotRetryEarly()
    {
        var handler = new RateLimitResetHandler((DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30)).ToUnixTimeSeconds());
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());
        SourceRepositoryFetchException? failure = null;

        try
        {
            await client.FetchAsync(new SourceRepositoryTarget("owner", "repository", "default"));
        }
        catch (SourceRepositoryFetchException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.IsRateLimited).IsTrue();
        await Assert.That(failure.IsTransient).IsFalse();
        await Assert.That(failure.RetryAfter!.Value).IsGreaterThan(TimeSpan.FromMinutes(5));
        await Assert.That(handler.CallCount).IsEqualTo(1);
        await Assert.That(async () => await client.FetchAsync(new SourceRepositoryTarget("owner", "other", "default"))).Throws<SourceRepositoryFetchException>();
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Client_Fetch_ForbiddenWithUnreadableBody_KeepsNonTransientForbidden()
    {
        var client = new GitHubLicenseApiClient(new UnreadableForbiddenBodyHandler(), GitHubAuthentication.Create());
        SourceRepositoryFetchException? failure = null;

        try
        {
            await client.FetchAsync(new SourceRepositoryTarget("owner", "repository", "default"));
        }
        catch (SourceRepositoryFetchException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(failure.IsRateLimited).IsFalse();
        await Assert.That(failure.IsTransient).IsFalse();
    }

    [Test]
    public async Task Client_Fetch_LateRateLimitResponse_DoesNotAdmitASecondProbe()
    {
        var handler = new ProbeConcurrencyHandler();
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());

        // Two requests are in flight before any rate-limit state exists.
        var burst = Swallow(client.FetchAsync(new SourceRepositoryTarget("owner", "burst", "default")));
        var late = Swallow(client.FetchAsync(new SourceRepositoryTarget("owner", "late", "default")));
        await handler.LateEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        handler.ReleaseBurst.TrySetResult();
        await burst;

        // Two waiters queue behind the limit; one of them wins the probe slot.
        var first = Swallow(client.FetchAsync(new SourceRepositoryTarget("owner", "first", "default")));
        var second = Swallow(client.FetchAsync(new SourceRepositoryTarget("owner", "second", "default")));
        await handler.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The late non-probe response lands while the probe is still outstanding.
        handler.ReleaseLate.TrySetResult();
        await late;
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        await Assert.That(handler.MaxProbesInFlight).IsEqualTo(1);

        handler.ReleaseProbe.TrySetResult();
        await Task.WhenAll(first, second);

        static async Task Swallow(Task<SourceRepositoryRecord> task)
        {
            try { await task; } catch (SourceRepositoryFetchException) { }
        }
    }

    [Test]
    public async Task Client_Fetch_OutOfRangeRateLimitReset_ReturnsBoundedFailure()
    {
        var client = new GitHubLicenseApiClient(new RateLimitResetHandler(long.MaxValue), GitHubAuthentication.Create());

        await Assert.That(async () => await client.FetchAsync(new SourceRepositoryTarget("owner", "repository", "default"))).Throws<SourceRepositoryFetchException>();
    }

    [Test]
    public async Task Client_Fetch_CancelledProbe_AllowsOnlyOneReplacementProbe()
    {
        var handler = new CancelledProbeHandler();
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        await Assert.That(async () => await client.FetchAsync(target)).Throws<SourceRepositoryFetchException>();
        using var cancellation = new CancellationTokenSource();
        var cancelledProbe = client.FetchAsync(target, cancellation.Token);
        await handler.CancelledProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstWaiter = client.FetchAsync(target);
        var secondWaiter = client.FetchAsync(target);

        cancellation.Cancel();
        await Assert.That(async () => await cancelledProbe).Throws<OperationCanceledException>();
        await handler.ReplacementProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        await Assert.That(handler.CallCount).IsEqualTo(3);
        handler.ReleaseReplacementProbe.TrySetResult();
        await Task.WhenAll(firstWaiter, secondWaiter);
        await Assert.That(handler.CallCount).IsEqualTo(4);
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
    public async Task Target_TryCreate_UrlShapes_NormalizeOrRejectExactly()
    {
        // Pins the normalization of every URL shape the parser must keep answering the same way.
        // An empty expectation means the URL must be rejected.
        (string Url, string Expected)[] cases =
        [
            ("https://github.com/owner/repository", "owner/repository"),
            ("https://github.com/owner/repository/", "owner/repository"),
            ("https://GitHub.com/owner/repository", "owner/repository"),
            ("https://github.com:443/owner/repository", "owner/repository"),
            ("https://user@github.com/owner/repository", "owner/repository"),
            ("https://github.com/owner/repository/tree/main", "owner/repository"),
            ("https://github.com/owner/repository?tab=readme", "owner/repository"),
            ("https://github.com/owner/repository#readme", "owner/repository"),
            ("  https://github.com/owner/repository  ", "owner/repository"),
            ("git+ssh://git@github.com/owner/repository.git", "owner/repository"),
            ("GIT+HTTPS://github.com/owner/repository.git", "owner/repository"),
            ("git@github.com:owner/repository.git", "owner/repository"),
            ("git@github.com:owner/repository", "owner/repository"),
            ("https://github.com/owner/repository.GIT", "owner/repository"),
            ("https://raw.github.com/owner/repository", ""),
            ("https://github.com.example.test/owner/repository", ""),
            ("https://example.test/owner/repository", ""),
            ("https://github.com/owner", ""),
            ("https://github.com/", ""),
            ("https://github.com", ""),
            ("github.com/owner/repository", ""),
            ("git@gitlab.test:owner/repository.git", ""),
            ("   ", ""),

            // Shapes that only a URI parser answers a particular way. They are pathological for a
            // repository URL, but the answer must not drift silently.
            ("HTTPS://GITHUB.COM/Owner/Repository", "Owner/Repository"),
            ("https://github.com./owner/repository", ""),
            ("https://github.com//owner/repository", "owner/repository"),
            ("https://github.com/owner//repository", ""),
            ("https://github.com:notaport/owner/repository", ""),
            ("ftp://github.com/owner/repository", "owner/repository"),
            ("ht!tp://github.com/owner/repository", ""),
            ("https://github.com/owner/repo%73itory", "owner/repository"),
            ("https://github.com/owner/../evil/repository", "evil/repository"),
        ];

        for (var i = 0; i < cases.Length; i++)
        {
            var (url, expected) = cases[i];

            var parsed = SourceRepositoryTarget.TryCreate(url, out var target);

            await Assert.That(parsed).IsEqualTo(expected.Length != 0).Because($"URL: {url}");
            if (expected.Length != 0)
            {
                await Assert.That(target.Repository).IsEqualTo(expected).Because($"URL: {url}");
                await Assert.That(target.CacheKey).IsEqualTo($"github:{expected}@default").Because($"URL: {url}");
            }
        }
    }

    [Test]
    public async Task Enrichment_FromCacheHitAndFromFetch_CarryTheTargetCacheKeyDigestAsEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-digest-{Guid.NewGuid():N}");
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        var expected = SourceRepositoryCache.GetCacheKeySha256(target.CacheKey);
        var index = new SpdxLicenseIndex(["MIT"], []);
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        try
        {
            using var fetchWorkspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty));
            using var httpClient = new HttpClient(new GitHubResponseHandler(HttpStatusCode.OK, ReadGitHubLicenseFixture()));
            var fetchService = new SourceRepositoryService(index, sourceCache, refresh: false, retryCount: 0, httpClient);
            var fetched = await fetchService.EnrichAsync([CreateDigestComponent(index)], fetchWorkspace, concurrency: 1);

            using var cachedWorkspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty));
            var cachedService = new SourceRepositoryService(index, sourceCache, refresh: false, retryCount: 0);
            var cached = await cachedService.EnrichAsync([CreateDigestComponent(index)], cachedWorkspace, concurrency: 1);

            await Assert.That(fetched.Summary.GitHubRequestCount).IsEqualTo(1);
            await Assert.That(cached.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(GetSourceEvidence(fetched.Components[0]).CacheKeySha256).IsEqualTo(expected);
            await Assert.That(GetSourceEvidence(cached.Components[0]).CacheKeySha256).IsEqualTo(expected);
            await Assert.That(GetSourceEvidence(cached.Components[0]).Repository).IsEqualTo("owner/repository");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ScanComponent CreateDigestComponent(SpdxLicenseIndex index)
        => new("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);

    private static SourceRepositoryEvidence GetSourceEvidence(ScanComponent component)
    {
        for (var i = 0; i < component.CandidateCount; i++)
        {
            if (component.GetCandidate(i).Evidence.SourceRepository is { } evidence)
            {
                return evidence;
            }
        }

        throw new InvalidOperationException("The component carries no source repository evidence.");
    }

    [Test]
    public async Task Enrichment_EquivalentRepositoryUrlSpellings_PlanOneSharedTarget()
    {
        var urls = new[] { "https://github.com/owner/repository.git", "git+https://github.com/owner/repository", "git@github.com:owner/repository.git" };

        var (summary, components, callCount) = await EnrichRepositoryUrlsAsync(urls);

        await Assert.That(summary.TargetCount).IsEqualTo(1);
        await Assert.That(summary.GitHubRequestCount).IsEqualTo(1);
        await Assert.That(callCount).IsEqualTo(1);
        for (var i = 0; i < components.Length; i++)
        {
            await Assert.That(components[i].License.ToString()).IsEqualTo("MIT");
        }
    }

    [Test]
    public async Task Enrichment_SameRepositoryWithDifferentRefs_PlansOneTargetEach()
    {
        var urls = new[] { "https://github.com/owner/repository", "https://github.com/owner/repository" };

        var (summary, _, callCount) = await EnrichRepositoryUrlsAsync(urls, ["v1", "v2"]);

        await Assert.That(summary.TargetCount).IsEqualTo(2);
        await Assert.That(callCount).IsEqualTo(2);
    }

    [Test]
    public async Task Enrichment_ManyComponentsSharingTwoRepositories_PlansTwoTargets()
    {
        // Above the linear-planning limit so the dictionary planning path is exercised.
        var urls = new string[12];
        for (var i = 0; i < urls.Length; i++)
        {
            urls[i] = i % 2 == 0 ? "https://github.com/owner/repository" : "https://github.com/owner/other.git";
        }

        var (summary, components, callCount) = await EnrichRepositoryUrlsAsync(urls);

        await Assert.That(summary.TargetCount).IsEqualTo(2);
        await Assert.That(callCount).IsEqualTo(2);
        for (var i = 0; i < components.Length; i++)
        {
            await Assert.That(components[i].License.ToString()).IsEqualTo("MIT");
        }
    }

    [Test]
    public async Task Enrichment_ManyComponentsSharingOneUnsupportedRepository_ReportsEachAsUnsupported()
    {
        // Above the linear-planning limit so a repeated unsupported URL is answered from the planning index.
        var urls = new string[12];
        Array.Fill(urls, "https://gitlab.test/owner/repository");

        var (summary, components, callCount) = await EnrichRepositoryUrlsAsync(urls);

        await Assert.That(summary.TargetCount).IsEqualTo(0);
        await Assert.That(summary.UnknownCount).IsEqualTo(urls.Length);
        await Assert.That(callCount).IsEqualTo(0);
        for (var i = 0; i < components.Length; i++)
        {
            await Assert.That(components[i].Warnings).Contains("unsupported_source_repository");
        }
    }

    private static async Task<(SourceRepositorySummary Summary, ScanComponent[] Components, int CallCount)> EnrichRepositoryUrlsAsync(string[] repositoryUrls, string[]? repositoryRefs = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-plan-{Guid.NewGuid():N}");
        var index = new SpdxLicenseIndex(["MIT"], []);
        var components = new ScanComponent[repositoryUrls.Length];
        var workspace = new PackageMetadataWorkspace(repositoryUrls.Length);
        for (var i = 0; i < repositoryUrls.Length; i++)
        {
            components[i] = new ScanComponent($"example{i}", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, $"pkg:npm/example{i}@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);
            workspace.Records[i] = new PackageMetadataResolution($"pkg:npm/example{i}@1.0.0", repositoryUrls[i], repositoryRefs is null ? string.Empty : repositoryRefs[i]);
        }

        var handler = new SequenceResponseHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new SourceRepositoryService(index, new SourceRepositoryCache(Path.Combine(root, "source")), refresh: false, retryCount: 0, httpClient);
        try
        {
            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);
            return (enrichment.Summary, enrichment.Components, handler.CallCount);
        }
        finally
        {
            workspace.Dispose();
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
    public async Task Enrichment_WithRateLimitFailure_DoesNotCacheTransientError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-rate-limit-{Guid.NewGuid():N}");
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        var index = new SpdxLicenseIndex(["MIT"], []);
        var handler = new RateLimitThenSuccessHandler(HttpStatusCode.TooManyRequests, retryAfterSeconds: 0, remaining: null);
        using var httpClient = new HttpClient(handler);
        var service = new SourceRepositoryService(index, sourceCache, refresh: false, retryCount: 0, httpClient);
        try
        {
            using (var firstWorkspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty)))
            {
                var firstComponent = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);
                var first = await service.EnrichAsync([firstComponent], firstWorkspace, concurrency: 1);

                await Assert.That(first.Summary.FetchErrorCount).IsEqualTo(1);
            }

            await Assert.That((await sourceCache.ReadAsync(target.CacheKey)).Status).IsEqualTo(SourceRepositoryCacheReadStatus.Missing);

            using var secondWorkspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty));
            var secondComponent = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);
            var second = await service.EnrichAsync([secondComponent], secondWorkspace, concurrency: 1);

            await Assert.That(second.Components[0].License.ToString()).IsEqualTo("MIT");
            await Assert.That(handler.CallCount).IsEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrichment_WithLegacyCachedRateLimitError_RefreshesOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-source-rate-limit-cache-{Guid.NewGuid():N}");
        var sourceCache = new SourceRepositoryCache(Path.Combine(root, "source"));
        var target = new SourceRepositoryTarget("owner", "repository", "default");
        var index = new SpdxLicenseIndex(["MIT"], []);
        using var workspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty));
        var component = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);
        var handler = new SequenceResponseHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new SourceRepositoryService(index, sourceCache, refresh: false, retryCount: 0, httpClient);
        try
        {
            Directory.CreateDirectory(sourceCache.Root);
            var legacy = CreateSourceCacheJson()
                .Replace("\"HttpStatus\": 200", "\"HttpStatus\": 429", StringComparison.Ordinal)
                .Replace("\"Errors\": []", "\"Errors\": [\"source_repository_fetch_failed\"]", StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourceCache.GetPath(target.CacheKey), legacy);

            var enrichment = await service.EnrichAsync([component], workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheHitCount).IsEqualTo(0);
            await Assert.That(enrichment.Components[0].License.ToString()).IsEqualTo("MIT");
            await Assert.That(handler.CallCount).IsEqualTo(1);

            using var cachedWorkspace = CreateWorkspace(new PackageMetadataResolution("pkg:npm/example@1.0.0", "https://github.com/owner/repository", string.Empty));
            var cachedComponent = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, "pkg:npm/example@1.0.0", default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);
            var cached = await service.EnrichAsync([cachedComponent], cachedWorkspace, concurrency: 1);

            await Assert.That(cached.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(handler.CallCount).IsEqualTo(1);
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
    public async Task Cache_Read_NegativeResolverVersion_RejectsEntry()
    {
        var json = CreateSourceCacheJson().Replace("\"FetchedAt\":", "\"ResolverVersion\": -1, \"FetchedAt\":", StringComparison.Ordinal);

        await AssertSyncReadMatchesAsync(json, SourceRepositoryCacheReadStatus.Invalid);
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

    private sealed class RateLimitThenSuccessHandler(HttpStatusCode firstStatus, int? retryAfterSeconds, int? remaining) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(CallCount++ == 0 ? firstStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(CallCount == 1 ? string.Empty : ReadGitHubLicenseFixture()),
            };
            if (response.StatusCode != HttpStatusCode.OK)
            {
                if (retryAfterSeconds is { } seconds)
                {
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                }
                if (remaining is { } value)
                {
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (value == 0 && retryAfterSeconds is null)
                    {
                        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }

            return Task.FromResult(response);
        }
    }

    private sealed class UnreadableForbiddenBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new FaultingContent() });

        private sealed class FaultingContent : HttpContent
        {
            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromException<Stream>(new IOException("body unavailable"));
            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.FromException(new IOException("body unavailable"));
            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }

    /// <summary>Counts how many probe requests the client allows while one rate limit is active.</summary>
    private sealed class ProbeConcurrencyHandler : HttpMessageHandler
    {
        private int probesInFlight;
        public int MaxProbesInFlight;
        public TaskCompletionSource LateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBurst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProbeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/burst/", StringComparison.Ordinal))
            {
                await ReleaseBurst.Task.WaitAsync(cancellationToken);
                return RateLimited(TimeSpan.FromSeconds(2));
            }

            if (path.Contains("/late/", StringComparison.Ordinal))
            {
                LateEntered.TrySetResult();
                await ReleaseLate.Task.WaitAsync(cancellationToken);
                return RateLimited(TimeSpan.Zero);
            }

            var inFlight = Interlocked.Increment(ref probesInFlight);
            InterlockedMax(ref MaxProbesInFlight, inFlight);
            try
            {
                ProbeEntered.TrySetResult();
                await ReleaseProbe.Task.WaitAsync(cancellationToken);
                return RateLimited(TimeSpan.FromSeconds(2));
            }
            finally
            {
                Interlocked.Decrement(ref probesInFlight);
            }
        }

        private static HttpResponseMessage RateLimited(TimeSpan retryAfter)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(string.Empty) };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            return response;
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int current;
            while ((current = Volatile.Read(ref location)) < value)
            {
                if (Interlocked.CompareExchange(ref location, value, current) == current) return;
            }
        }
    }

    private sealed class RateLimitResetHandler(long reset) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(string.Empty) };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        }
    }

    private sealed class CancelledProbeHandler : HttpMessageHandler
    {
        public int CallCount;
        public TaskCompletionSource CancelledProbeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReplacementProbeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReplacementProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref CallCount);
            if (call == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(string.Empty) };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            }

            if (call == 2)
            {
                CancelledProbeEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (call == 3)
            {
                ReplacementProbeEntered.TrySetResult();
                await ReleaseReplacementProbe.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReadGitHubLicenseFixture()) };
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
