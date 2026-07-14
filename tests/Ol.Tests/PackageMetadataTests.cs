using System.Net;
using System.Text.Json;
using Ol.Core;

namespace Ol.Tests;

public sealed class PackageMetadataTests
{
    [Test]
    public async Task Fetch_RegisteredProvider_ParsesItsPurlAndOwnResponseWithoutCentralSwitches()
    {
        var provider = new TestPackageMetadataProvider();
        var providers = new PackageMetadataProviders([provider]);
        var client = new PackageMetadataRegistryClient(new StaticResponseHandler("""{ "license": "MIT" }"""), providers);

        var parsed = PackageMetadataRequest.TryCreate("pkg:test/example@1.0.0", providers, out var request);
        var record = await client.FetchAsync(request);

        await Assert.That(parsed).IsTrue();
        await Assert.That(request.Ecosystem).IsEqualTo("test");
        await Assert.That(record.Source).IsEqualTo("test-registry");
        await Assert.That(record.RawLicense).IsEqualTo("MIT");
    }

    [Test]
    public async Task TryParse_ScopedNpmPurl_ProducesNormalizedPackageMetadataRequest()
    {
        var parsed = PackageMetadataRequest.TryCreate("pkg:npm/%40scope/example@1.2.3?download_url=https%3A%2F%2Fexample.test", out var request);

        await Assert.That(parsed).IsTrue();
        await Assert.That(request.Ecosystem).IsEqualTo("npm");
        await Assert.That(request.Namespace).IsEqualTo("@scope");
        await Assert.That(request.Name).IsEqualTo("example");
        await Assert.That(request.Version).IsEqualTo("1.2.3");
        await Assert.That(request.CacheKey).IsEqualTo("pkg:npm/%40scope/example@1.2.3");
    }

    [Test]
    public async Task Cache_WriteThenRead_UsesHashNamedEntryAndRetainsLogicalKey()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
        var request = new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0");
        var record = new PackageMetadataRecord(request.CacheKey, "npm-registry", "MIT", "https://example.test/repository", [], [], DateTimeOffset.UtcNow);

        try
        {
            var cache = new PackageMetadataCache(root);
            await cache.WriteAsync(record);

            var read = await cache.TryReadAsync(request.CacheKey);

            await Assert.That(read.HasValue).IsTrue();
            await Assert.That(read!.Value.CacheKey).IsEqualTo(request.CacheKey);
            await Assert.That(read.Value.RawLicense).IsEqualTo("MIT");
            await Assert.That(Directory.GetFiles(root, "*.json")[0]).DoesNotContain("example");

            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(cache.GetPath(request.CacheKey)));
            await Assert.That(document.RootElement.GetProperty("SchemaVersion").GetInt32()).IsEqualTo(1);
            await Assert.That(document.RootElement.GetProperty("CacheKeySha256").GetString()).IsEqualTo(PackageMetadataCache.GetCacheKeySha256(request.CacheKey));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Cache_UnknownSchemaVersion_TreatsEntryAsMiss()
    {
        var json = CreatePackageCacheJson(schemaVersion: 2);

        await AssertCacheEntryIsMiss(json);
    }

    [Test]
    public async Task Cache_MissingRequiredWarnings_TreatsEntryAsMiss()
    {
        var json = CreatePackageCacheJson().Replace("\n  \"Warnings\": [],", string.Empty, StringComparison.Ordinal);

        await AssertCacheEntryIsMiss(json);
    }

    [Test]
    public async Task Cache_MismatchedKeyHash_TreatsEntryAsMiss()
    {
        var json = CreatePackageCacheJson().Replace(PackageMetadataCache.GetCacheKeySha256("pkg:npm/example@1.0.0"), new string('0', 64), StringComparison.Ordinal);

        await AssertCacheEntryIsMiss(json);
    }

    [Test]
    public async Task Cache_TimestampWithoutExplicitUtcOffset_TreatsEntryAsMiss()
    {
        var json = CreatePackageCacheJson().Replace("2026-07-08T00:00:00+00:00", "2026-07-08T00:00:00", StringComparison.Ordinal);

        await AssertCacheEntryIsMiss(json);
    }

    [Test]
    public async Task Cache_NonStringWarning_TreatsEntryAsMiss()
    {
        var json = CreatePackageCacheJson().Replace("\"Warnings\": []", "\"Warnings\": [null]", StringComparison.Ordinal);

        await AssertCacheEntryIsMiss(json);
    }

    [Test]
    public async Task Cache_DifferentLogicalKey_TreatsEntryAsMiss()
    {
        var json = CreatePackageCacheJson().Replace("pkg:npm/example@1.0.0", "pkg:npm/other@1.0.0", StringComparison.Ordinal);

        await AssertCacheEntryIsMiss(json);
    }

    [Test]
    public async Task Cache_WriterNormalizesTimestampAndRejectsSensitiveRepositoryReferences()
    {
        const string cacheKey = "pkg:npm/example@1.0.0";
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new PackageMetadataCache(root);
            await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [], []));
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(cache.GetPath(cacheKey)));

            await Assert.That(document.RootElement.GetProperty("FetchedAt").GetDateTimeOffset().Offset).IsEqualTo(TimeSpan.Zero);
            await Assert.That(async () => await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "https://token@example.test/repository", [], []))).Throws<ArgumentException>();
            await Assert.That(async () => await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "https://example.test/repository?access_token=secret", [], []))).Throws<ArgumentException>();
            await Assert.That(async () => await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "file:///C:/private/repository", [], []))).Throws<ArgumentException>();
            await Assert.That(async () => await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "/home/user/private/repository", [], []))).Throws<ArgumentException>();
            await Assert.That(async () => await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "//token@example.test/repository", [], []))).Throws<ArgumentException>();
            await Assert.That(async () => await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "git@github.com:owner/repository.git", [], []))).Throws<ArgumentException>();
            await Assert.That(async () => await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "repository?access_token=secret", [], []))).Throws<ArgumentException>();
            await Assert.That(async () => await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "token@example/repository", [], []))).Throws<ArgumentException>();
            await Assert.That(async () => await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", string.Empty, [null!], []))).Throws<ArgumentException>();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Reconcile_SbomUnknownAndMetadataMatched_ProducesMatchedComponent()
    {
        var index = new SpdxLicenseIndex(["MIT"], []);
        var component = new ScanComponent(
            "example",
            "1.0.0",
            "-",
            "npm",
            DependencyType.Unknown,
            LicenseStatus.Unknown,
            "pkg:npm/example@1.0.0",
            "pkg:npm/example@1.0.0",
            [LicenseCandidateFactory.Create("sbom", "id", "NOASSERTION"u8, index)],
            []);

        var result = LicenseReconciler.AddCandidate(component, LicenseCandidateFactory.Create("npm-registry", "license", "MIT"u8, index));

        await Assert.That(result.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(result.License).IsEqualTo("MIT");
        await Assert.That(result.LicenseCandidates.Length).IsEqualTo(2);
        await Assert.That(result.LicenseCandidates[1].Source).IsEqualTo("npm-registry");
    }

    [Test]
    public async Task Fetch_NpmVersionResponse_ProducesNormalizedRecord()
    {
        var client = CreateClient("""{ "license": "MIT", "repository": { "url": "https://github.com/example/package" } }""");

        var record = await client.FetchAsync(new PackageMetadataRequest("npm", "@scope", "package", "1.2.3", "pkg:npm/%40scope/package@1.2.3"));

        await Assert.That(record.Source).IsEqualTo("npm-registry");
        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/package");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_ProducesLicenseExpression()
    {
        var client = CreateClient("""{ "catalogEntry": { "licenseExpression": "Apache-2.0", "projectUrl": "https://example.test/project" } }""");

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.Source).IsEqualTo("nuget-registry");
        await Assert.That(record.RawLicense).IsEqualTo("Apache-2.0");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://example.test/project");
    }

    [Test]
    public async Task Fetch_CargoAndGoResponses_ProduceTheirAvailableEvidence()
    {
        var cargo = CreateClient("""{ "version": { "license": "MIT OR Apache-2.0", "repository": "https://github.com/example/crate" } }""");
        var go = CreateClient("""{ "Origin": { "URL": "https://github.com/example/module" } }""");

        var cargoRecord = await cargo.FetchAsync(new PackageMetadataRequest("cargo", "", "example", "1.0.0", "pkg:cargo/example@1.0.0"));
        var goRecord = await go.FetchAsync(new PackageMetadataRequest("golang", "github.com/example", "module", "v1.0.0", "pkg:golang/github.com/example/module@v1.0.0"));

        await Assert.That(cargoRecord.RawLicense).IsEqualTo("MIT OR Apache-2.0");
        await Assert.That(cargoRecord.RepositoryUrl).IsEqualTo("https://github.com/example/crate");
        await Assert.That(goRecord.Source).IsEqualTo("go-module-proxy");
        await Assert.That(goRecord.RawLicense).IsEmpty();
        await Assert.That(goRecord.RepositoryUrl).IsEqualTo("https://github.com/example/module");
    }

    [Test]
    public async Task RetryClassifier_TransientAndPermanentResponses_AreClassifiedCorrectly()
    {
        await Assert.That(PackageMetadataRegistryClient.IsTransient(HttpStatusCode.TooManyRequests)).IsTrue();
        await Assert.That(PackageMetadataRegistryClient.IsTransient(HttpStatusCode.ServiceUnavailable)).IsTrue();
        await Assert.That(PackageMetadataRegistryClient.IsTransient(HttpStatusCode.NotFound)).IsFalse();
        await Assert.That(PackageMetadataRegistryClient.IsTransient(HttpStatusCode.BadRequest)).IsFalse();
    }

    [Test]
    public async Task FetchScheduler_TransientFailureThenSuccess_RetriesAndReturnsRecord()
    {
        var handler = new SequenceResponseHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var client = new PackageMetadataRegistryClient(handler);
        var request = new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0");

        var record = await PackageMetadataFetchScheduler.FetchAsync(client, request, retryCount: 1);

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task FetchScheduler_PermanentAndExhaustedFailures_DoNotOverRetry()
    {
        var notFound = new SequenceResponseHandler(HttpStatusCode.NotFound);
        var unavailable = new SequenceResponseHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        var request = new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0");

        await Assert.That(async () => await PackageMetadataFetchScheduler.FetchAsync(new PackageMetadataRegistryClient(notFound), request, retryCount: 1)).Throws<PackageMetadataFetchException>();
        await Assert.That(async () => await PackageMetadataFetchScheduler.FetchAsync(new PackageMetadataRegistryClient(unavailable), request, retryCount: 1)).Throws<PackageMetadataFetchException>();
        await Assert.That(notFound.CallCount).IsEqualTo(1);
        await Assert.That(unavailable.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Fetch_NpmResponseWithoutLicense_ProducesUnknownEvidenceRecord()
    {
        var client = CreateClient("""{ "repository": "https://example.test/repository" }""");

        var record = await client.FetchAsync(new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0"));

        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://example.test/repository");
    }

    private static PackageMetadataRegistryClient CreateClient(string body)
        => new(new StaticResponseHandler(body));

    private static string CreatePackageCacheJson(int schemaVersion = 1)
    {
        var keyHash = PackageMetadataCache.GetCacheKeySha256("pkg:npm/example@1.0.0");
        return $$"""
            {
              "CacheKey": "pkg:npm/example@1.0.0",
              "Source": "npm-registry",
              "RawLicense": "MIT",
              "RepositoryUrl": "https://example.test/repository",
              "Warnings": [],
              "Errors": [],
              "FetchedAt": "2026-07-08T00:00:00+00:00",
              "SchemaVersion": {{schemaVersion}},
              "CacheKeySha256": "{{keyHash}}"
            }
            """;
    }

    private static async Task AssertCacheEntryIsMiss(string json)
    {
        const string cacheKey = "pkg:npm/example@1.0.0";
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(cache.GetPath(cacheKey), json);

            var read = await cache.TryReadAsync(cacheKey);

            await Assert.That(read.HasValue).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class TestPackageMetadataProvider : PackageMetadataProvider
    {
        public override string Ecosystem => "test";

        public override Uri CreateEndpoint(PackageMetadataRequest request)
            => new("https://registry.test/");

        public override PackageMetadataResponse ParseResponse(JsonElement root)
            => new("test-registry", root.GetProperty("license").GetString() ?? string.Empty, string.Empty);
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class SequenceResponseHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly HttpStatusCode[] statuses = statuses;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = statuses[Math.Min(CallCount, statuses.Length - 1)];
            CallCount++;
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new StringContent("""{ "license": "MIT" }""");
            }

            return Task.FromResult(response);
        }
    }
}
