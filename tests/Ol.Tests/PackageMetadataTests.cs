using System.Net;
using System.Text.Json;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.Spdx;
using Ol.Internals;

namespace Ol.Tests;

public sealed class PackageMetadataTests
{
    [Test]
    public async Task Enrichment_WithInsufficientMetadataWorkspace_RejectsCallerBuffer()
    {
        var service = new PackageMetadataService(
            new SpdxLicenseIndex(["MIT"], []),
            new PackageMetadataCache(Path.GetTempPath()),
            refresh: false,
            retryCount: 0);
        var component = new ScanComponent("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, default, default, default, [], []);
        using var workspace = new PackageMetadataWorkspace(0);

        await Assert.That(async () => await service.EnrichAsync([component], workspace, concurrency: 1)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Enrichment_PurlStatesNoVersion_SeparatesIncompleteIdentityFromUnsupportedEcosystem()
    {
        // A version-less purl and an ecosystem Ol cannot query both end in "no lookup was attempted", but they ask
        // the reader for different things: fix the input, or wait for Ol to gain a provider. Reporting an incomplete
        // Maven purl as an unsupported ecosystem tells a reviewer that Ol does not support Maven, which is false.
        var index = new SpdxLicenseIndex(["MIT"], []);
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
        try
        {
            var service = new PackageMetadataService(index, new PackageMetadataCache(root), refresh: false, retryCount: 0);
            var components = new[]
            {
                CreateEnrichmentComponent(index, "pkg:maven/com.tencent.polaris/auth-block-allow-list"),
                CreateEnrichmentComponent(index, "pkg:conan/zlib@1.3"),
            };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var (enriched, summary) = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enriched[0].Warnings).Contains("package_metadata_unversioned_purl");
            await Assert.That(enriched[0].Warnings).DoesNotContain("unsupported_package_metadata");
            await Assert.That(enriched[1].Warnings).Contains("unsupported_package_metadata");
            await Assert.That(summary.TargetCount).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Unresolved_WhenNoRegistryCouldBeAsked_NamesThatRatherThanTheMissingRepository()
    {
        // Every purl no registry can answer for also ends with no repository, because nothing ever produced one.
        // Reporting the repository sends the reader looking for something that was never sought; the reason they can
        // act on is why the registry was skipped. This is the same masking that once hid an unversioned purl.
        var index = new SpdxLicenseIndex(["MIT"], []);
        var cases = new[]
        {
            (Purl: "pkg:conan/zlib@1.3", Expected: "unsupported_package_metadata"),
            (Purl: "pkg:maven/org.example/module", Expected: "package_metadata_unversioned_purl"),
        };

        foreach (var (purl, expected) in cases)
        {
            var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
            try
            {
                var service = new PackageMetadataService(index, new PackageMetadataCache(root), refresh: false, retryCount: 0);
                var components = new[] { CreateEnrichmentComponent(index, purl) };
                using var workspace = new PackageMetadataWorkspace(components.Length);
                var (enriched, _) = await service.EnrichAsync(components, workspace, concurrency: 1);
                var withRepositoryOutcome = LicenseReconciler.AddCandidate(
                    enriched[0],
                    LicenseCandidateFactory.CreateError(
                        LicenseCandidateSource.SourceRepository,
                        LicenseCandidateKind.Unavailable,
                        LicenseCandidateWarnings.SourceRepositoryUnavailable));

                var buffer = new System.Buffers.ArrayBufferWriter<byte>(4 * 1024);
                var descriptor = new ScanInputDescriptor(ScanInputKind.Sbom, ScanInputFormat.CycloneDx, "test", string.Empty, default);
                var inventory = new DependencyInventory(descriptor, [], [withRepositoryOutcome], [], []);
                ReportRenderer.WriteText(buffer, inventory, new[] { withRepositoryOutcome }, verbose: false);
                var rendered = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);

                await Assert.That(rendered).Contains(expected);
                await Assert.That(rendered).DoesNotContain("source_repository_unavailable");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Enrichment_RegistryAnswersNotFound_RecordsUnknownRatherThanCollectionError()
    {
        // A 404 is a completed answer, not a failed operation: the registry successfully reported that the package
        // is not published there. Classifying it as an error would make it unacknowledgeable and inconclusive.
        var index = new SpdxLicenseIndex(["MIT"], []);
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
        using var handler = new SequenceResponseHandler(HttpStatusCode.NotFound);
        using var httpClient = new HttpClient(handler);
        try
        {
            var service = new PackageMetadataService(index, new PackageMetadataCache(root), refresh: false, retryCount: 0, uncollectedPackages: null, client: httpClient);
            var components = new[] { CreateEnrichmentComponent(index, "pkg:npm/private-pkg@1.0.0") };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var (enriched, summary) = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enriched[0].Status).IsEqualTo(LicenseStatus.Unknown);
            await Assert.That(enriched[0].Warnings).Contains("package_metadata_not_found");
            await Assert.That(summary.FetchErrorCount).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrichment_RegistryFailsTransiently_RemainsCollectionError()
    {
        var index = new SpdxLicenseIndex(["MIT"], []);
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
        using var handler = new SequenceResponseHandler(HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler);
        try
        {
            var service = new PackageMetadataService(index, new PackageMetadataCache(root), refresh: false, retryCount: 0, uncollectedPackages: null, client: httpClient);
            var components = new[] { CreateEnrichmentComponent(index, "pkg:npm/example@1.0.0") };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var (enriched, summary) = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enriched[0].Status).IsEqualTo(LicenseStatus.Error);
            await Assert.That(enriched[0].Warnings).Contains("package_metadata_fetch_failed");
            await Assert.That(summary.FetchErrorCount).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task MetadataWorkspace_AfterDisposal_RejectsAccessInsteadOfReadingReturnedRental()
    {
        var workspace = new PackageMetadataWorkspace(2);
        SetFirstRecord(workspace, new PackageMetadataResolution("pkg:npm/example@1.0.0", string.Empty, string.Empty));

        await Assert.That(GetRecordCount(workspace)).IsEqualTo(2);

        workspace.Dispose();
        workspace.Dispose();

        await Assert.That(() => GetRecordCount(workspace)).Throws<ObjectDisposedException>();
        await Assert.That(() => SetFirstRecord(workspace, null)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Enrichment_WithDisposedMetadataWorkspace_FailsInsteadOfWritingReturnedRental()
    {
        var index = new SpdxLicenseIndex(["MIT"], []);
        var service = new PackageMetadataService(index, new PackageMetadataCache(Path.GetTempPath()), refresh: false, retryCount: 0);
        var components = new[] { CreateEnrichmentComponent(index, default) };
        var workspace = new PackageMetadataWorkspace(components.Length);
        workspace.Dispose();

        await Assert.That(async () => await service.EnrichAsync(components, workspace, concurrency: 1)).Throws<ObjectDisposedException>();
    }

    private static int GetRecordCount(PackageMetadataWorkspace workspace) => workspace.Records.Length;

    private static void SetFirstRecord(PackageMetadataWorkspace workspace, PackageMetadataResolution? resolution) => workspace.Records[0] = resolution;

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
    public async Task Fetch_RegistryRequest_SendsOlUserAgent()
    {
        var provider = new TestPackageMetadataProvider();
        var providers = new PackageMetadataProviders([provider]);
        var handler = new RequestHeaderHandler();
        var client = new PackageMetadataRegistryClient(handler, providers);

        await client.FetchAsync(new PackageMetadataRequest("test", "", "example", "1.0.0", "pkg:test/example@1.0.0"));

        await Assert.That(handler.UserAgent).IsEqualTo("ol");
    }

    [Test]
    public async Task TryParse_ScopedNpmPurl_ProducesNormalizedPackageMetadataRequest()
    {
        var parsed = OlDefaults.TryCreatePackageMetadataRequest("pkg:npm/%40scope/example@1.2.3?download_url=https%3A%2F%2Fexample.test", out var request);

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

            using var read = await cache.TryReadAsync(request.CacheKey);

            await Assert.That(read.IsHit).IsTrue();
            await Assert.That(read.CacheKeySha256).IsEqualTo(PackageMetadataCache.GetCacheKeySha256(request.CacheKey));
            await Assert.That(read.RawLicense.ToString()).IsEqualTo("MIT");
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
    public async Task Cache_InvalidOptionalRepositoryRef_TreatsEntryAsMiss()
    {
        var json = CreatePackageCacheJson().Replace("\"Warnings\": []", $"\"RepositoryRef\": \"{new string('a', 257)}\",\n  \"Warnings\": []", StringComparison.Ordinal);

        await AssertCacheEntryIsMiss(json);
    }

    [Test]
    public async Task Cache_DifferentLogicalKey_TreatsEntryAsMiss()
    {
        var json = CreatePackageCacheJson().Replace("pkg:npm/example@1.0.0", "pkg:npm/other@1.0.0", StringComparison.Ordinal);

        await AssertCacheEntryIsMiss(json);
    }

    [Test]
    public async Task Cache_MissingRequiredErrors_TreatsEntryAsMiss()
    {
        var json = CreatePackageCacheJson().Replace("\n  \"Errors\": [],", string.Empty, StringComparison.Ordinal);

        await AssertCacheEntryIsMiss(json);
    }

    [Test]
    public async Task Cache_UnsafeRepositoryUrl_TreatsEntryAsMiss()
    {
        var credentials = CreatePackageCacheJson().Replace("https://example.test/repository", "https://user:secret@example.test/repository", StringComparison.Ordinal);
        var localPath = CreatePackageCacheJson().Replace("https://example.test/repository", "file:///tmp/repository", StringComparison.Ordinal);

        await AssertCacheEntryIsMiss(credentials);
        await AssertCacheEntryIsMiss(localPath);
    }

    [Test]
    public async Task Cache_UnknownProperty_RemainsCompatibleHit()
    {
        var json = CreatePackageCacheJson().Replace("\"Warnings\": []", "\"Unknown\": { \"nested\": [1, 2] },\n  \"Warnings\": []", StringComparison.Ordinal);

        await AssertCacheEntryRawLicense(json, "MIT");
    }

    [Test]
    public async Task Cache_EscapedLicenseValue_IsUnescaped()
    {
        var json = CreatePackageCacheJson().Replace("\"RawLicense\": \"MIT\"", "\"RawLicense\": \"MIT \\u0026 Apache-2.0\"", StringComparison.Ordinal);

        await AssertCacheEntryRawLicense(json, "MIT & Apache-2.0");
    }

    [Test]
    public async Task Enrichment_CachedEntryWithWarnings_RetainsThemOnTheComponent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-enrich-{Guid.NewGuid():N}");
        const string purl = "pkg:npm/example@1.0.0";
        try
        {
            var cache = new PackageMetadataCache(root);
            await cache.WriteAsync(new PackageMetadataRecord(purl, "npm-registry", "MIT", string.Empty, ["package_metadata_fetch_failed"], []));
            var index = new SpdxLicenseIndex(["MIT"], []);
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0);
            var components = new[] { CreateEnrichmentComponent(index, purl) };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(enrichment.Components[0].Warnings).Contains("package_metadata_fetch_failed");
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
            LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index),
            [],
            []);

        var result = LicenseReconciler.AddCandidate(component, LicenseCandidateFactory.Create(LicenseCandidateSource.NpmRegistry, LicenseCandidateKind.License, "MIT"u8, index));

        await Assert.That(result.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(result.License.ToString()).IsEqualTo("MIT");
        await Assert.That(result.CandidateCount).IsEqualTo(2);
        await Assert.That(result.GetCandidate(1).Source).IsEqualTo(LicenseCandidateSource.NpmRegistry);
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

    // npm carried its license declaration in three shapes before the current string field, and packages
    // published under the older ones are still installed today. Reading only the current shape drops a
    // license the registry states plainly: `wrench@1.5.9` publishes `licenses: [{ "type": "MIT" }]` and
    // no `license`, and Ol reported it unresolved. Equivalence classes: current shape, each legacy
    // shape, both shapes present, a collection stating no relationship, and nothing at all.

    [Test]
    [Arguments("string", """{ "license": "MIT" }""", "MIT")]
    [Arguments("object", """{ "license": { "type": "MIT", "url": "https://example.test/LICENSE" } }""", "MIT")]
    [Arguments("collection-single", """{ "licenses": [ { "type": "MIT", "url": "https://example.test/LICENSE" } ] }""", "MIT")]
    [Arguments("collection-object", """{ "licenses": { "type": "MIT" } }""", "MIT")]
    [Arguments("both-shapes", """{ "license": "Apache-2.0", "licenses": [ { "type": "MIT" } ] }""", "Apache-2.0")]
    [Arguments("collection-several", """{ "licenses": [ { "type": "MIT" }, { "type": "Apache-2.0" } ] }""", "")]
    [Arguments("absent", """{ "name": "example" }""", "")]
    public async Task Fetch_NpmVersionResponse_ReadsEveryDeclarationShape(string label, string body, string expected)
    {
        var client = CreateClient(body);

        var record = await client.FetchAsync(new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0"));

        await Assert.That(record.RawLicense).IsEqualTo(expected);
        await Assert.That(label).IsNotEmpty();
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_ProducesLicenseExpression()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "items": [
                    { "catalogEntry": { "version": "1.0.0", "licenseExpression": "Apache-2.0", "projectUrl": "https://example.test/project" } }
                  ]
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.Source).IsEqualTo("nuget-registry");
        await Assert.That(record.RawLicense).IsEqualTo("Apache-2.0");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://example.test/project");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationIndex_WithInlineLeaf_UsesDocumentedIndexEndpoint()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationIndex("1.0.0", "MIT"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(handler.RequestUris[1]).IsEqualTo("https://api.nuget.org/v3/registration5-gz-semver2/example/index.json");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationIndex_WithExternalPage_FollowsDiscoveredPageEndpoint()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/example/page/1.0.0/2.0.0.json",
                  "lower": "1.0.0",
                  "upper": "2.0.0"
                }
              ]
            }
            """,
            """
            {
              "items": [
                { "catalogEntry": { "version": "1.5.0", "licenseExpression": "Apache-2.0" } }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.5.0", "pkg:nuget/Example@1.5.0"));

        await Assert.That(record.RawLicense).IsEqualTo("Apache-2.0");
        await Assert.That(handler.RequestUris[2]).IsEqualTo("https://api.nuget.org/v3/registration5-gz-semver2/example/page/1.0.0/2.0.0.json");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationIndex_WithBuildMetadata_MatchesNormalizedVersion()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationIndex("1.0.0", "MIT"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0+commit", "pkg:nuget/Example@1.0.0%2Bcommit"));

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationIndex_WithUntrustedExternalPage_DoesNotFollowIt()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "@id": "https://example.test/private.json",
                  "lower": "1.0.0",
                  "upper": "2.0.0"
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.5.0", "pkg:nuget/Example@1.5.0"));

        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(handler.RequestUris.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_DiscoversSemVer2ResourceOnceForConcurrentRequests()
    {
        using var handler = new NuGetServiceIndexHandler();
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var records = await Task.WhenAll(
            client.FetchAsync(new PackageMetadataRequest("nuget", "", "First", "1.0.0", "pkg:nuget/First@1.0.0")),
            client.FetchAsync(new PackageMetadataRequest("nuget", "", "Second", "2.0.0", "pkg:nuget/Second@2.0.0")));

        await Assert.That(records[0].RawLicense).IsEqualTo("MIT");
        await Assert.That(records[1].RawLicense).IsEqualTo("Apache-2.0");
        await Assert.That(handler.ServiceIndexRequestCount).IsEqualTo(1);
        await Assert.That(handler.RequestUris).Contains("https://api.nuget.org/v3/discovered-semver2/first/index.json");
        await Assert.That(handler.RequestUris).Contains("https://api.nuget.org/v3/discovered-semver2/second/index.json");
        await Assert.That(handler.RequestUris.Any(static uri => uri.Contains("registration5-semver1", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Fetch_NuGetRegistrationIndex_WithInlineCatalogEntry_ProducesRepositoryMetadata()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "1.0.0",
                        "licenseExpression": "MIT",
                        "projectUrl": "https://github.com/example/project",
                        "repository": { "commit": "abcdef" }
                      }
                    }
                  ]
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.Source).IsEqualTo("nuget-registry");
        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/project");
        await Assert.That(record.RepositoryRef).IsEqualTo("abcdef");
        await Assert.That(handler.RequestUris).IsEquivalentTo([
            "https://api.nuget.org/v3/index.json",
            "https://api.nuget.org/v3/registration5-gz-semver2/example/index.json",
        ]);
    }

    [Test]
    [Arguments("https://github.com/example/project/blob/v1.2.3/LICENSE", "v1.2.3")]
    [Arguments("https://raw.githubusercontent.com/example/project/v1.2.3/LICENSE", "v1.2.3")]
    [Arguments("https://raw.github.com/example/project/v1.2.3/LICENSE.txt", "v1.2.3")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/LICENSE.md", "v1.2.3")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/license.txt", "v1.2.3")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/LICENCE", "v1.2.3")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/COPYING", "v1.2.3")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/UNLICENSE", "v1.2.3")]
    public async Task Fetch_NuGetRegistrationResponse_WithLegacyGitHubLicenseUrl_ProducesRepositoryTarget(string licenseUrl, string expectedRef)
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationMetadata("1.0.0", licenseUrl: licenseUrl));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/project");
        await Assert.That(record.RepositoryRef).IsEqualTo(expectedRef);
        await Assert.That(record.Warnings).IsEmpty();
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithUnsupportedProjectAndLegacyGitHubLicenseUrl_UsesLicenseRepository()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationMetadata(
                "1.0.0",
                projectUrl: "https://dot.net/",
                licenseUrl: "https://github.com/dotnet/core/blob/main/LICENSE.TXT"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/dotnet/core");
        await Assert.That(record.RepositoryRef).IsEqualTo("main");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithGitHubProjectAndVersionedLegacyLicenseUrl_PrefersVersionedLicenseRepository()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationMetadata(
                "1.0.0",
                projectUrl: "https://github.com/example/current-project",
                licenseUrl: "https://raw.githubusercontent.com/example/versioned-project/v1.0.0/LICENSE"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/versioned-project");
        await Assert.That(record.RepositoryRef).IsEqualTo("v1.0.0");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithRepositoryAndLegacyLicenseUrl_PrefersRepositoryMetadata()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "1.0.0",
                        "repository": { "url": "https://github.com/example/canonical", "commit": "abcdef" },
                        "licenseUrl": "https://github.com/example/legacy/blob/main/LICENSE"
                      }
                    }
                  ]
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/canonical");
        await Assert.That(record.RepositoryRef).IsEqualTo("abcdef");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithUnsafeRepositoryAndLegacyLicenseUrl_UsesLicenseRepository()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "1.0.0",
                        "repository": { "url": "https://user@github.com/example/unsafe", "commit": "abcdef" },
                        "licenseUrl": "https://github.com/example/versioned/blob/main/LICENSE"
                      }
                    }
                  ]
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/versioned");
        await Assert.That(record.RepositoryRef).IsEqualTo("main");
    }

    [Test]
    [Arguments("http://go.microsoft.com/fwlink/?LinkId=329770")]
    [Arguments("http://github.com/example/project/blob/main/LICENSE")]
    [Arguments("https://github.com/example/project/blob/main")]
    [Arguments("https://github.com/example/project/blob/main/LICENSE?raw=1")]
    [Arguments("https://raw.githubusercontent.com/example/project/main/%2e%2e/LICENSE")]
    [Arguments("https://raw.githubusercontent.com/example/project/main//LICENSE")]
    [Arguments("https://raw.githubusercontent.com/example/project/../other/main/LICENSE")]
    [Arguments("https://github.com/example/project/blob/release/1.0/LICENSE")]
    [Arguments("https://github.com/example/project/blob/0123456789abcdef0123456789abcdef01234567/licenses/LICENSE")]
    [Arguments("https://github.com/example/project/blob/0123456789abcdef0123456789abcdef01234567/src/Example/LICENSE.txt")]
    [Arguments("https://raw.githubusercontent.com/example/project/0123456789abcdef0123456789abcdef01234567/licenses/NOTICE")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/LICENSE.MIT")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/LICENSE-MIT")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/COPYING.LESSER")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/NOTICE")]
    [Arguments("https://github.com/example/project/blob/v1.2.3/license.rst")]
    public async Task Fetch_NuGetRegistrationResponse_WithUnsupportedLegacyLicenseUrl_DoesNotCreateRepositoryTarget(string licenseUrl)
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationMetadata("1.0.0", licenseUrl: licenseUrl));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEmpty();
        await Assert.That(record.RepositoryRef).IsEmpty();
        await Assert.That(record.Warnings).IsEmpty();
        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(DeclaredLicenseReferenceKind.Location);
        await Assert.That(record.DeclaredLicenseReference).IsEqualTo(licenseUrl);
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithOversizedLegacyLicenseRef_DoesNotCreateUncacheableTarget()
    {
        var licenseUrl = $"https://raw.githubusercontent.com/example/project/{new string('a', 257)}/LICENSE";
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationMetadata("1.0.0", licenseUrl: licenseUrl));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEmpty();
        await Assert.That(record.RepositoryRef).IsEmpty();
        await Assert.That(record.Warnings).IsEmpty();
        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(DeclaredLicenseReferenceKind.Location);
    }

    /// <summary>
    /// Guards the warning vocabulary as a whole: every flag survives the persisted round trip, no two
    /// flags share an identifier, and the set still fits its storage. The set is stored as one bit per
    /// warning in a <see langword="ushort"/>, so a vocabulary that grows past sixteen stops being
    /// representable — which is what spending flags on facts other fields already carry costs.
    /// </summary>
    [Test]
    public async Task WarningVocabulary_EveryFlag_RoundTripsAndFitsItsStorage()
    {
        var flags = Enum.GetValues<LicenseCandidateWarnings>().Where(static value => value != LicenseCandidateWarnings.None).ToArray();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flag in flags)
        {
            var identifier = flag.ToStrings().Single();

            await Assert.That(identifiers.Add(identifier)).IsTrue();
            await Assert.That(LicenseCandidateIdentifiers.ParseWarning(identifier)).IsEqualTo(flag);
            await Assert.That(LicenseCandidateIdentifiers.ParseWarning(System.Text.Encoding.UTF8.GetBytes(identifier))).IsEqualTo(flag);
            await Assert.That(identifier).DoesNotContain("nuget_");
        }

        var combined = flags.Aggregate(LicenseCandidateWarnings.None, static (accumulated, flag) => accumulated | flag);

        await Assert.That(combined.ToStrings()).Count().IsEqualTo(flags.Length);
        await Assert.That((uint)combined).IsLessThanOrEqualTo(ushort.MaxValue);
        await Assert.That(flags.Length).IsLessThanOrEqualTo(16);
    }

    [Test]
    public async Task WarningVocabulary_EveryFlag_ReachesTheCanonicalReport()
    {
        // The canonical report writes candidate warnings from its own list rather than from ToStrings, so a flag can
        // round-trip through the vocabulary and still never reach a reader. Rendering every flag catches that.
        var flags = Enum.GetValues<LicenseCandidateWarnings>().Where(static value => value != LicenseCandidateWarnings.None).ToArray();
        var combined = flags.Aggregate(LicenseCandidateWarnings.None, static (accumulated, flag) => accumulated | flag);
        var component = new ScanComponent(
            Utf8Slice.FromOwnedBytes("example"u8.ToArray()),
            Utf8Slice.FromOwnedBytes("1.0.0"u8.ToArray()),
            default,
            "npm",
            DependencyType.Unknown,
            LicenseStatus.Unknown,
            Utf8Slice.FromOwnedBytes("pkg:npm/example@1.0.0"u8.ToArray()),
            default,
            new LicenseCandidate(
                LicenseCandidateSource.PackageRegistry,
                LicenseCandidateKind.Unsupported,
                default,
                default,
                LicenseStatus.Unknown,
                false,
                combined,
                new LicenseEvidence(LicenseEvidenceKind.PackageRegistry)),
            [],
            []);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(4 * 1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ReportRenderer.WriteJson(
                writer,
                new DependencyInventory(default, [], [component], [], []),
                new[] { component },
                SpdxData.Load(null),
                new PackageMetadataSummary(0, 0, 0, 0, 0, 0, 0, 1, 0),
                new SourceRepositorySummary(0, 0, 0, 0, 0, 0, "none", 1, 0),
                new ScanReportScope(ExternalEvidenceCollected: true, DependencyFilter: null, ExcludedCount: 0, ExcludedUnknownCount: 0));
        }

        var rendered = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var report = JsonDocument.Parse(rendered);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warning in report.RootElement.GetProperty("components")[0].GetProperty("licenseCandidates")[0].GetProperty("warnings").EnumerateArray())
        {
            written.Add(warning.GetString()!);
        }

        foreach (var flag in flags)
        {
            await Assert.That(written).Contains(flag.ToStrings().Single());
        }
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithLicenseExpressionAndLegacyLicenseUrl_DoesNotProjectLicenseRepository()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "1.0.0",
                        "licenseExpression": "MIT",
                        "projectUrl": "https://dot.net/",
                        "licenseUrl": "https://github.com/example/legacy/blob/main/LICENSE"
                      }
                    }
                  ]
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://dot.net/");
        await Assert.That(record.RepositoryRef).IsEmpty();
        await Assert.That(record.Warnings).IsEmpty();
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithUnsupportedForgeRepository_KeepsDeclaredRepository()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "1.0.0",
                        "repository": { "url": "https://gitlab.com/real/project", "commit": "deadbeef" },
                        "licenseUrl": "https://github.com/someone/mirror/blob/main/LICENSE"
                      }
                    }
                  ]
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://gitlab.com/real/project");
        await Assert.That(record.Warnings).IsEmpty();
        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(DeclaredLicenseReferenceKind.Location);
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithLicenseFileAndSupportedRepository_DoesNotWarnUnresolvedFile()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "1.0.0",
                        "licenseFile": "LICENSE.txt",
                        "repository": { "url": "https://github.com/example/project", "commit": "0123456789abcdef0123456789abcdef01234567" }
                      }
                    }
                  ]
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/project");
        await Assert.That(record.Warnings).IsEmpty();
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithUnsafeProjectUrl_DiscardsRepositoryAndDeclaresNothing()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationMetadata("1.0.0", projectUrl: "https://user@github.com/example/project"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEmpty();
        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(record.Warnings).IsEmpty();
        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(DeclaredLicenseReferenceKind.None);
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithLicenseFile_RecordsUnresolvedFileWithoutGuessingLicense()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "items": [
                    { "catalogEntry": { "version": "1.0.0", "licenseFile": "LICENSE.txt" } }
                  ]
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(record.RepositoryUrl).IsEmpty();
        await Assert.That(record.Warnings).IsEmpty();
        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(DeclaredLicenseReferenceKind.ArtifactPath);
        await Assert.That(record.DeclaredLicenseReference).IsEqualTo("LICENSE.txt");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationIndex_WithoutLicenseExpression_FollowsCatalogEntryForRepository()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationIndexWithoutExpression("1.0.0"),
            NuGetCatalogLeaf("1.0.0", repositoryUrl: "https://github.com/example/project", repositoryCommit: "0123456789abcdef0123456789abcdef01234567"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(handler.RequestUris[2]).IsEqualTo(CatalogLeafUri);
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/project");
        await Assert.That(record.RepositoryRef).IsEqualTo("0123456789abcdef0123456789abcdef01234567");
        await Assert.That(record.Warnings).IsEmpty();
    }

    [Test]
    public async Task Fetch_NuGetRegistrationIndex_WithoutLicenseExpression_FollowsCatalogEntryForLicenseFile()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationIndexWithoutExpression("1.0.0"),
            NuGetCatalogLeaf("1.0.0", licenseFile: "MIT-LICENSE.txt"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(record.Warnings).IsEmpty();
        await Assert.That(record.DeclaredLicenseReference).IsEqualTo("MIT-LICENSE.txt");
    }

    [Test]
    public async Task Fetch_NuGetCatalogEntry_WithLicenseExpression_ProducesLicenseExpression()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationIndexWithoutExpression("1.0.0"),
            NuGetCatalogLeaf("1.0.0", licenseExpression: "MIT"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(record.Warnings).IsEmpty();
    }

    [Test]
    public async Task Fetch_NuGetCatalogEntry_WithoutAnyLicenseMetadata_DeclaresNothingWithoutWarning()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationIndexWithoutExpression("1.0.0"),
            NuGetCatalogLeaf("1.0.0"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEmpty();
        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(record.Warnings).IsEmpty();
        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(DeclaredLicenseReferenceKind.None);
    }

    [Test]
    public async Task Fetch_NuGetRegistration_WithoutTheRequestedVersion_AnswersNotFoundRatherThanMissingLicenseMetadata()
    {
        // The registration document is a completed answer that does not list this version, which is the
        // same fact a 404 states. Describing it as "the publisher declared no license" would assert
        // something about a package version the registry never described.
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationMetadata("1.0.0", licenseUrl: "https://example.test/license"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "9.9.9", "pkg:nuget/Example@9.9.9"));

        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(record.Warnings).IsEquivalentTo(["package_metadata_not_found"]);
    }

    [Test]
    public async Task Fetch_NuGetRegistrationIndex_WithLicenseExpression_DoesNotRequestCatalogEntry()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationIndex("1.0.0", "MIT"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(handler.RequestUris.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Fetch_NuGetRegistrationPage_WithoutLicenseExpression_FollowsPageThenCatalogEntry()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/example/page/1.0.0/2.0.0.json",
                  "lower": "1.0.0",
                  "upper": "2.0.0"
                }
              ]
            }
            """,
            NuGetRegistrationIndexWithoutExpression("1.5.0"),
            NuGetCatalogLeaf("1.5.0", licenseFile: "LICENSE.txt"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.5.0", "pkg:nuget/Example@1.5.0"));

        await Assert.That(handler.RequestUris[2]).IsEqualTo("https://api.nuget.org/v3/registration5-gz-semver2/example/page/1.0.0/2.0.0.json");
        await Assert.That(handler.RequestUris[3]).IsEqualTo(CatalogLeafUri);
        await Assert.That(record.DeclaredLicenseReference).IsEqualTo("LICENSE.txt");
    }

    [Test]
    public async Task Fetch_NuGetRegistrationIndex_WithUntrustedCatalogEntryId_DoesNotFollowIt()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """
            {
              "items": [
                {
                  "lower": "1.0.0",
                  "upper": "1.0.0",
                  "items": [
                    {
                      "catalogEntry": {
                        "@id": "https://example.test/private/example.1.0.0.json",
                        "version": "1.0.0",
                        "licenseUrl": "https://www.nuget.org/packages/Example/1.0.0/license"
                      }
                    }
                  ]
                }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(handler.RequestUris.Count).IsEqualTo(2);
        await Assert.That(record.RepositoryUrl).IsEmpty();
        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(DeclaredLicenseReferenceKind.Location);
    }

    [Test]
    public async Task Fetch_NuGetCatalogEntry_WithLegacyGitHubLicenseUrl_ProjectsRepositoryTarget()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationIndexWithoutExpression("1.0.0"),
            NuGetCatalogLeaf("1.0.0", licenseUrl: "https://github.com/example/project/blob/v1.0.0/LICENSE.txt"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/project");
        await Assert.That(record.RepositoryRef).IsEqualTo("v1.0.0");
        await Assert.That(record.Warnings).IsEmpty();
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithGzipContent_ProducesLicenseExpression()
    {
        using var handler = new GzipNuGetRegistrationHandler();
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(handler.RequestCount).IsEqualTo(2);
    }

    [Test]
    public async Task Fetch_NuGetRegistrationResponse_WithUntrustedCatalogEntryUrl_DoesNotFollowIt()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            """{ "catalogEntry": "https://example.test/private.json" }""");
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(handler.RequestUris.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Fetch_NuGetServiceIndex_WithTypeArray_UsesSemVer2Resource()
    {
        var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex("[\"RegistrationsBaseUrl/3.6.0\", \"PackageBaseAddress/3.0.0\"]"),
            NuGetRegistrationIndex("1.0.0", "MIT"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(handler.RequestUris[1]).IsEqualTo("https://api.nuget.org/v3/registration5-gz-semver2/example/index.json");
    }

    [Test]
    public async Task Fetch_NuGetServiceIndex_WithoutSemVer2Resource_RejectsMetadataRequest()
    {
        var handler = new SequenceJsonResponseHandler("""
            {
              "version": "3.0.0",
              "resources": [
                { "@id": "https://api.nuget.org/v3/registration5-gz-semver1/", "@type": "RegistrationsBaseUrl/3.4.0" }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        await Assert.That(async () => await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"))).Throws<PackageMetadataFetchException>();
        await Assert.That(handler.RequestUris.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Fetch_NuGetServiceIndex_WithUntrustedSemVer2Resource_RejectsMetadataRequest()
    {
        var handler = new SequenceJsonResponseHandler(NuGetServiceIndex("\"RegistrationsBaseUrl/3.6.0\"", "https://example.test/registration/"));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        await Assert.That(async () => await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"))).Throws<PackageMetadataFetchException>();
        await Assert.That(handler.RequestUris.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Fetch_NuGetServiceIndex_RateLimitedDiscoveryHonorsRetryAfterAndDoesNotCacheFailure()
    {
        using var handler = new RateLimitedNuGetServiceIndexHandler(TimeSpan.FromMilliseconds(20));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);
        var observedDelay = TimeSpan.Zero;

        var record = await PackageMetadataFetchScheduler.FetchAsync(
            client,
            new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"),
            retryCount: 1,
            (delay, _) =>
            {
                observedDelay = delay;
                return Task.CompletedTask;
            });

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(observedDelay).IsEqualTo(TimeSpan.FromMilliseconds(20));
        await Assert.That(handler.ServiceIndexRequestCount).IsEqualTo(2);
        await Assert.That(handler.RegistrationRequestCount).IsEqualTo(1);
    }

    [Test]
    public async Task Fetch_RateLimitedOrigin_BlocksSubsequentRequestUntilCancellation()
    {
        using var handler = new RateLimitedOriginHandler();
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);
        var request = new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0");

        await Assert.That(async () => await client.FetchAsync(request)).Throws<PackageMetadataFetchException>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.That(async () => await client.FetchAsync(request, cancellation.Token)).Throws<OperationCanceledException>();

        await Assert.That(handler.RequestCount).IsEqualTo(1);
    }

    [Test]
    public async Task Fetch_RateLimitedOrigin_AllowsOnlyOneProbeAfterCooldown()
    {
        using var handler = new ProbeRateLimitedOriginHandler();
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);
        var request = new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0");

        await Assert.That(async () => await client.FetchAsync(request)).Throws<PackageMetadataFetchException>();
        var first = client.FetchAsync(request);
        var second = client.FetchAsync(request);
        await handler.ProbeStarted;

        await Assert.That(handler.RequestCount).IsEqualTo(2);
        handler.CompleteProbe();
        var records = await Task.WhenAll(first, second);

        await Assert.That(records[0].RawLicense).IsEqualTo("MIT");
        await Assert.That(records[1].RawLicense).IsEqualTo("MIT");
        await Assert.That(handler.RequestCount).IsEqualTo(3);
    }

    [Test]
    public async Task FetchScheduler_RetryAfterBeyondWaitBudget_StopsInsteadOfRetryingEarly()
    {
        var handler = new ExcessiveRetryAfterHandler();
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);
        var request = new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0");
        var waited = false;

        await Assert.That(async () => await PackageMetadataFetchScheduler.FetchAsync(
                client,
                request,
                retryCount: 1,
                (delay, token) =>
                {
                    waited = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None))
            .Throws<PackageMetadataFetchException>();

        // The registry asked for far longer than a command-line run can absorb. Ol neither waits it out
        // nor shortens it into an early retry that the registry did not sanction.
        await Assert.That(waited).IsFalse();
        await Assert.That(handler.RequestCount).IsEqualTo(1);
    }

    [Test]
    public async Task Fetch_OriginCooldownEscalatingToStop_ReportsTheLongerDelayAndStaysNonRetryable()
    {
        var handler = new EscalatingOriginRateLimitHandler();
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        // Both requests are in flight before any cooldown exists for the origin.
        var shortLimit = Swallow(client.FetchAsync(new PackageMetadataRequest("npm", "", "first", "1.0.0", "pkg:npm/first@1.0.0")));
        var longLimit = Swallow(client.FetchAsync(new PackageMetadataRequest("npm", "", "second", "1.0.0", "pkg:npm/second@1.0.0")));
        await handler.LongLimitEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The short delay establishes the cooldown, so the origin's NotBefore is already in the future.
        handler.ReleaseShortLimit.TrySetResult();
        await shortLimit;

        // The long delay then escalates the same origin to stopped while NotBefore is still ahead of now.
        handler.ReleaseLongLimit.TrySetResult();
        await longLimit;

        var failure = await CaptureFetchFailureAsync(client, "pkg:npm/third@1.0.0");

        await Assert.That(failure.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(300));
        await Assert.That(failure.IsTransient).IsFalse();

        static async Task Swallow(Task<PackageMetadataRecord> task)
        {
            try { await task; } catch (PackageMetadataFetchException) { }
        }
    }

    private static async Task<PackageMetadataFetchException> CaptureFetchFailureAsync(PackageMetadataRegistryClient client, string purl)
    {
        try
        {
            await client.FetchAsync(new PackageMetadataRequest("npm", "", "third", "1.0.0", purl));
        }
        catch (PackageMetadataFetchException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected a package metadata fetch failure.");
    }

    [Test]
    public async Task FetchScheduler_ShortRetryAfter_StillWaitsAndRetries()
    {
        var handler = new RateLimitedThenSuccessfulNpmHandler(TimeSpan.FromSeconds(2));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);
        var request = new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0");
        var observedDelay = TimeSpan.MinValue;

        var record = await PackageMetadataFetchScheduler.FetchAsync(
            client,
            request,
            retryCount: 1,
            (delay, token) =>
            {
                observedDelay = delay;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(observedDelay).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(observedDelay).IsLessThanOrEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Fetch_NuGetServiceIndex_CancelingFirstWaiterDoesNotCancelSharedDiscovery()
    {
        using var handler = new CancelableNuGetServiceIndexHandler();
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);
        using var firstCancellation = new CancellationTokenSource();
        var first = client.FetchAsync(new PackageMetadataRequest("nuget", "", "First", "1.0.0", "pkg:nuget/First@1.0.0"), firstCancellation.Token);
        await handler.DiscoveryStarted;
        var second = client.FetchAsync(new PackageMetadataRequest("nuget", "", "Second", "2.0.0", "pkg:nuget/Second@2.0.0"));

        firstCancellation.Cancel();
        await Assert.That(async () => await first).Throws<OperationCanceledException>();
        handler.CompleteDiscovery();
        var record = await second;

        await Assert.That(record.RawLicense).IsEqualTo("Apache-2.0");
        await Assert.That(handler.ServiceIndexRequestCount).IsEqualTo(1);
    }

    [Test]
    public async Task Providers_ParseResponse_WithNonObjectRoot_ReturnUnknownMetadataWithoutThrowing()
    {
        using var document = JsonDocument.Parse("\"unexpected\"");
        PackageMetadataProvider[] providers = [new NpmPackageMetadataProvider(), new NuGetPackageMetadataProvider(), new CargoPackageMetadataProvider(), new GoPackageMetadataProvider(), new PyPiPackageMetadataProvider(), new PackagistPackageMetadataProvider(), new RubyGemsPackageMetadataProvider(), new MavenPackageMetadataProvider(), new CocoaPodsPackageMetadataProvider()];

        for (var i = 0; i < providers.Length; i++)
        {
            var response = providers[i].ParseResponse(document.RootElement, default);

            await Assert.That(response.RawLicense).IsEmpty();
            await Assert.That(response.RepositoryUrl).IsEmpty();
            await Assert.That(response.RepositoryRef).IsEmpty();
        }
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
    public async Task Enrichment_GoEntryCachedBeforeLicensesWereReadable_RecollectsOnce()
    {
        // Every Go entry written before the license source existed carries an empty license, so without a resolver
        // version bump an upgraded Ol would keep reporting the whole ecosystem unresolved until the cache aged out.
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-enrich-{Guid.NewGuid():N}");
        const string purl = "pkg:golang/golang.org/x/crypto@v0.52.0";
        using var handler = new SequenceJsonResponseHandler(
            """{ "Version": "v0.52.0", "Origin": { "URL": "https://go.googlesource.com/crypto", "Ref": "refs/tags/v0.52.0" } }""",
            """{ "licenses": ["BSD-3-Clause"] }""");
        using var httpClient = new HttpClient(handler);
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            var keyHash = PackageMetadataCache.GetCacheKeySha256(purl);
            await File.WriteAllTextAsync(cache.GetPath(purl), $$"""
                {
                  "SchemaVersion": 1,
                  "ResolverVersion": 5,
                  "CacheKey": "{{purl}}",
                  "CacheKeySha256": "{{keyHash}}",
                  "Source": "go-module-proxy",
                  "RawLicense": "",
                  "RepositoryUrl": "https://go.googlesource.com/crypto",
                  "Warnings": [],
                  "Errors": [],
                  "FetchedAt": "2026-08-09T00:00:00+00:00"
                }
                """);
            var index = new SpdxLicenseIndex(["BSD-3-Clause"], []);
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0, uncollectedPackages: null, client: httpClient);
            var components = new[] { CreateEnrichmentComponent(index, purl) };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheMissCount).IsEqualTo(1);
            await Assert.That(enrichment.Components[0].License.ToString()).IsEqualTo("BSD-3-Clause");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Fetch_GoModule_AddsDepsDevLicenseWithoutLosingTheProxyOrigin()
    {
        // The proxy states where a module version came from but never its license, so a Go module could only be
        // resolved by whatever its repository host happened to expose. deps.dev states the license for the module
        // contents, including modules hosted where Ol cannot collect. Both are kept: the tag-pinned origin is what
        // makes the repository evidence about the released version rather than about the default branch.
        var handler = new SequenceJsonResponseHandler(
            """{ "Version": "v0.52.0", "Origin": { "VCS": "git", "URL": "https://go.googlesource.com/crypto", "Ref": "refs/tags/v0.52.0" } }""",
            """{ "licenses": ["BSD-3-Clause"], "links": [ { "label": "SOURCE_REPO", "url": "https://go.googlesource.com/crypto" } ] }""");
        using var httpClient = new HttpClient(handler);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(httpClient);

        var record = await client.FetchAsync(new PackageMetadataRequest("golang", "golang.org/x", "crypto", "v0.52.0", "pkg:golang/golang.org/x/crypto@v0.52.0"));

        await Assert.That(record.RawLicense).IsEqualTo("BSD-3-Clause");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://go.googlesource.com/crypto");
        await Assert.That(record.RepositoryRef).IsEqualTo("refs/tags/v0.52.0");
        await Assert.That(handler.RequestUris[1]).Contains("api.deps.dev");
    }

    [Test]
    public async Task Fetch_GoModule_WhenDepsDevIsUnavailable_KeepsWhatTheProxyStated()
    {
        // The license source is an addition, not a new dependency for the whole ecosystem. If it cannot be reached,
        // Go resolution has to degrade to what it produced before rather than fail the component outright.
        var handler = new FollowUpFailureHandler(
            """{ "Version": "v1.0.9", "Origin": { "VCS": "git", "URL": "https://github.com/spf13/pflag", "Ref": "refs/tags/v1.0.9" } }""",
            HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(httpClient);

        var record = await client.FetchAsync(new PackageMetadataRequest("golang", "github.com/spf13", "pflag", "v1.0.9", "pkg:golang/github.com/spf13/pflag@v1.0.9"));

        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/spf13/pflag");
        await Assert.That(record.RepositoryRef).IsEqualTo("refs/tags/v1.0.9");
    }

    [Test]
    public async Task Fetch_GoModule_WithSeveralUnrelatedLicenses_StaysAmbiguous()
    {
        // deps.dev lists values without stating a relationship between them, exactly as it does for Maven. Joining
        // them with OR would assert a choice the source never made.
        var handler = new SequenceJsonResponseHandler(
            """{ "Version": "v3.0.1", "Origin": { "URL": "https://github.com/go-yaml/yaml", "Ref": "refs/tags/v3.0.1" } }""",
            """{ "licenses": ["MIT", "Apache-2.0"] }""");
        using var httpClient = new HttpClient(handler);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(httpClient);

        var record = await client.FetchAsync(new PackageMetadataRequest("golang", "gopkg.in", "yaml.v3", "v3.0.1", "pkg:golang/gopkg.in/yaml.v3@v3.0.1"));

        await Assert.That(record.RawLicense).IsEqualTo("MIT; Apache-2.0");
    }

    [Test]
    public async Task Fetch_GoResponseWithoutOrigin_DerivesRepositoryFromGitHubModulePath()
    {
        // proxy.golang.org omits Origin for module versions cached before it recorded one, and the
        // module path is the repository for github.com by the Go module resolution rule itself.
        var go = CreateClient("""{ "Version": "v1.1.1", "Time": "2018-02-21T23:26:28Z" }""");

        var record = await go.FetchAsync(new PackageMetadataRequest("golang", "github.com/davecgh", "go-spew", "v1.1.1", "pkg:golang/github.com/davecgh/go-spew@v1.1.1"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/davecgh/go-spew");
        await Assert.That(record.RepositoryRef).IsEmpty();
    }

    [Test]
    public async Task Fetch_GoResponseWithoutOrigin_DerivesRepositoryRootFromMajorVersionSuffixedModulePath()
    {
        var go = CreateClient("""{ "Version": "v2.2.4" }""");

        var record = await go.FetchAsync(new PackageMetadataRequest("golang", "github.com/pelletier/go-toml", "v2", "v2.2.4", "pkg:golang/github.com/pelletier/go-toml/v2@v2.2.4"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/pelletier/go-toml/v2");
    }

    [Test]
    [Arguments("gopkg.in", "yaml.v3")]
    [Arguments("rsc.io", "pdf")]
    [Arguments("", "example.test")]
    public async Task Fetch_GoResponseWithoutOrigin_LeavesNonGitHubModulePathUnresolved(string moduleNamespace, string moduleName)
    {
        // A vanity import path is not a repository URL, and Ol does not resolve one by following it.
        var go = CreateClient("""{ "Version": "v1.0.0" }""");

        var record = await go.FetchAsync(new PackageMetadataRequest("golang", moduleNamespace, moduleName, "v1.0.0", $"pkg:golang/{moduleName}@v1.0.0"));

        await Assert.That(record.RepositoryUrl).IsEmpty();
    }

    [Test]
    public async Task Fetch_GoResponseWithOrigin_KeepsProxyRepositoryOverModulePath()
    {
        var go = CreateClient("""{ "Origin": { "URL": "https://go.googlesource.com/sys", "Ref": "refs/tags/v0.13.0" } }""");

        var record = await go.FetchAsync(new PackageMetadataRequest("golang", "golang.org/x", "sys", "v0.13.0", "pkg:golang/golang.org/x/sys@v0.13.0"));

        await Assert.That(record.RepositoryUrl).IsEqualTo("https://go.googlesource.com/sys");
        await Assert.That(record.RepositoryRef).IsEqualTo("refs/tags/v0.13.0");
    }

    [Test]
    public async Task Fetch_NpmResponseDeclaringRepositoryDirectory_RecordsThatTheRepositoryHoldsMoreThanThisPackage()
    {
        var npm = CreateClient("""{ "license": "Apache-2.0", "repository": { "url": "https://github.com/eslint/js.git", "directory": "packages/eslint-visitor-keys" } }""");

        var record = await npm.FetchAsync(new PackageMetadataRequest("npm", "", "eslint-visitor-keys", "5.0.1", "pkg:npm/eslint-visitor-keys@5.0.1"));

        await Assert.That(record.RawLicense).IsEqualTo("Apache-2.0");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/eslint/js.git");
        await Assert.That(record.Warnings).Contains("source_repository_subdirectory");
    }

    [Test]
    public async Task Fetch_NpmResponseWithoutRepositoryDirectory_KeepsRepositoryAsThisPackagesSubject()
    {
        var npm = CreateClient("""{ "license": "MIT", "repository": { "url": "https://github.com/owner/repository.git" } }""");

        var record = await npm.FetchAsync(new PackageMetadataRequest("npm", "", "example", "1.0.0", "pkg:npm/example@1.0.0"));

        await Assert.That(record.Warnings).IsEmpty();
    }

    [Test]
    public async Task Fetch_PyPiResponse_UsesReleaseSpecificMetadata()
    {
        var handler = new SequenceJsonResponseHandler("""{ "info": { "license_expression": "MIT", "license": "Legacy", "project_urls": { "Source": "https://github.com/example/python" } } }""");
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("pypi", "", "example", "1.0.0", "pkg:pypi/example@1.0.0"));

        await Assert.That(handler.RequestUris).IsEquivalentTo(["https://pypi.org/pypi/example/1.0.0/json"]);
        await Assert.That(record.Source).IsEqualTo("pypi-registry");
        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/python");
    }

    [Test]
    public async Task Fetch_PackagistResponse_UsesRequestedComposerVersionMetadata()
    {
        var handler = new SequenceJsonResponseHandler(
            """
            {
              "package": {
                "repository": "https://github.com/Seldaek/monolog",
                "versions": {
                  "3.8.1": { "license": ["GPL-3.0-only"] },
                  "3.9.0": { "license": ["MIT", "Apache-2.0"] }
                }
              }
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var parsed = OlDefaults.TryCreatePackageMetadataRequest("pkg:composer/monolog/monolog@3.9.0", out var request);
        await Assert.That(parsed).IsTrue();
        var record = await client.FetchAsync(request);

        await Assert.That(handler.RequestUris).IsEquivalentTo(["https://packagist.org/packages/monolog/monolog.json"]);
        await Assert.That(record.Source).IsEqualTo("packagist-registry");
        await Assert.That(record.RawLicense).IsEqualTo("MIT OR Apache-2.0");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/Seldaek/monolog");
    }

    [Test]
    public async Task Fetch_PackagistResponse_WithoutRequestedVersion_DoesNotUseOtherVersion()
    {
        var handler = new SequenceJsonResponseHandler(
            """
            {
              "package": {
                "repository": "https://github.com/Seldaek/monolog",
                "versions": {
                  "3.8.1": { "license": ["GPL-3.0-only"] }
                }
              }
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("composer", "monolog", "monolog", "3.9.0", "pkg:composer/monolog/monolog@3.9.0"));

        await Assert.That(record.RawLicense).IsEmpty();
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/Seldaek/monolog");
    }

    [Test]
    public async Task Fetch_RubyGemsVersionResponse_UsesVersionAndPlatformSpecificMetadata()
    {
        var handler = new SequenceJsonResponseHandler("""{ "licenses": ["MIT", "Apache-2.0"], "source_code_uri": "https://github.com/example/gem" }""");
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var parsed = OlDefaults.TryCreatePackageMetadataRequest("pkg:gem/example@1.2.3?platform=java", out var request);
        await Assert.That(parsed).IsTrue();
        await Assert.That(request.CacheKey).IsEqualTo("pkg:gem/example@1.2.3?platform=java");
        var record = await client.FetchAsync(request);

        await Assert.That(handler.RequestUris).IsEquivalentTo(["https://rubygems.org/api/v2/rubygems/example/versions/1.2.3.json?platform=java"]);
        await Assert.That(record.Source).IsEqualTo("rubygems-registry");
        await Assert.That(record.RawLicense).IsEqualTo("MIT OR Apache-2.0");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/example/gem");
    }

    [Test]
    public async Task Fetch_MavenVersionResponse_UsesDepsDevLicenseAndSourceRepository()
    {
        var handler = new SequenceJsonResponseHandler(
            """
            {
              "licenses": ["Apache-2.0"],
              "links": [
                { "label": "HOMEPAGE", "url": "https://commons.apache.org/proper/commons-lang/" },
                { "label": "SOURCE_REPO", "url": "https://github.com/apache/commons-lang" }
              ]
            }
            """);
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var parsed = OlDefaults.TryCreatePackageMetadataRequest("pkg:maven/org.apache.commons/commons-lang3@3.17.0?type=jar", out var request);
        await Assert.That(parsed).IsTrue();
        var record = await client.FetchAsync(request);

        await Assert.That(handler.RequestUris).IsEquivalentTo([
            "https://api.deps.dev/v3/systems/maven/packages/org.apache.commons%3Acommons-lang3/versions/3.17.0",
        ]);
        await Assert.That(request.CacheKey).IsEqualTo("pkg:maven/org.apache.commons/commons-lang3@3.17.0");
        await Assert.That(record.Source).IsEqualTo("deps.dev");
        await Assert.That(record.RawLicense).IsEqualTo("Apache-2.0");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/apache/commons-lang");
    }

    [Test]
    public async Task Fetch_CocoaPodsPodspec_UsesVersionSpecificLicenseAndSource()
    {
        var handler = new SequenceJsonResponseHandler(
            """{ "license": { "type": "MIT", "file": "LICENSE" }, "source": { "git": "https://github.com/Moya/Moya.git", "tag": "15.0.0" } }""");
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var parsed = OlDefaults.TryCreatePackageMetadataRequest("pkg:cocoapods/Moya@15.0.0", out var request);
        await Assert.That(parsed).IsTrue();
        var record = await client.FetchAsync(request);

        await Assert.That(handler.RequestUris).IsEquivalentTo([
            "https://cdn.cocoapods.org/Specs/8/a/7/Moya/15.0.0/Moya.podspec.json",
        ]);
        await Assert.That(record.Source).IsEqualTo("cocoapods-cdn");
        await Assert.That(record.RawLicense).IsEqualTo("MIT");
        await Assert.That(record.RepositoryUrl).IsEqualTo("https://github.com/Moya/Moya.git");
        await Assert.That(record.RepositoryRef).IsEqualTo("15.0.0");
    }

    [Test]
    public async Task Fetch_CocoaPodsPodspecWithLongName_UsesCorrectShard()
    {
        var name = new string('a', 200);
        var handler = new SequenceJsonResponseHandler("""{ "license": "MIT" }""");
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var parsed = OlDefaults.TryCreatePackageMetadataRequest($"pkg:cocoapods/{name}@1.0.0", out var request);
        await Assert.That(parsed).IsTrue();
        await client.FetchAsync(request);

        await Assert.That(handler.RequestUris).IsEquivalentTo([
            $"https://cdn.cocoapods.org/Specs/8/8/7/{name}/1.0.0/{name}.podspec.json",
        ]);
    }

    [Test]
    public async Task TryCreate_CocoaPodsPurlWithNamespaceOrInvalidName_RejectsRequest()
    {
        await Assert.That(OlDefaults.TryCreatePackageMetadataRequest("pkg:cocoapods/team/Moya@15.0.0", out _)).IsFalse();
        await Assert.That(OlDefaults.TryCreatePackageMetadataRequest("pkg:cocoapods/Bad%20Pod@1.0.0", out _)).IsFalse();
    }

    [Test]
    public async Task TryCreate_CocoaPodsPurlWithQualifierOrSubpath_NormalizesRequest()
    {
        var qualifierParsed = OlDefaults.TryCreatePackageMetadataRequest("pkg:cocoapods/Moya@15.0.0?repository_url=https%3A%2F%2Fexample.com", out var qualifierRequest);
        var subpathParsed = OlDefaults.TryCreatePackageMetadataRequest("pkg:cocoapods/Moya@15.0.0#Core", out var subpathRequest);

        await Assert.That(qualifierParsed).IsTrue();
        await Assert.That(subpathParsed).IsTrue();
        await Assert.That(qualifierRequest.CacheKey).IsEqualTo("pkg:cocoapods/Moya@15.0.0");
        await Assert.That(subpathRequest.CacheKey).IsEqualTo("pkg:cocoapods/Moya@15.0.0");
    }

    [Test]
    public async Task Fetch_MavenVersionResponse_WithMultipleLicenses_DoesNotInventRelationship()
    {
        var handler = new SequenceJsonResponseHandler(
            """{ "licenses": ["MIT", "Apache-2.0"], "links": [] }""");
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest(
            "maven",
            "example.group",
            "example",
            "1.0.0",
            "pkg:maven/example.group/example@1.0.0"));
        var candidate = LicenseCandidateFactory.Create(
            LicenseCandidateSource.PackageRegistry,
            LicenseCandidateKind.License,
            System.Text.Encoding.UTF8.GetBytes(record.RawLicense),
            new SpdxLicenseIndex(["MIT", "Apache-2.0"], []));

        await Assert.That(record.RawLicense).IsEqualTo("MIT; Apache-2.0");
        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Ambiguous);
    }

    [Test]
    public async Task TryCreate_MavenPurlWithoutGroupId_RejectsRequest()
    {
        var parsed = OlDefaults.TryCreatePackageMetadataRequest("pkg:maven/example@1.0.0", out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryCreate_RubyGemsPurlWithUnsupportedQualifier_RejectsRequest()
    {
        var parsed = OlDefaults.TryCreatePackageMetadataRequest("pkg:gem/example@1.2.3?repository_url=example.test", out _);

        await Assert.That(parsed).IsFalse();
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
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);
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

        await Assert.That(async () => await PackageMetadataFetchScheduler.FetchAsync(OlDefaults.CreatePackageMetadataRegistryClient(notFound), request, retryCount: 1)).Throws<PackageMetadataFetchException>();
        await Assert.That(async () => await PackageMetadataFetchScheduler.FetchAsync(OlDefaults.CreatePackageMetadataRegistryClient(unavailable), request, retryCount: 1)).Throws<PackageMetadataFetchException>();
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

    [Test]
    public async Task Cache_TryRead_ValidEntry_MatchesAsyncHit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
        const string cacheKey = "pkg:npm/example@1.0.0";
        try
        {
            var cache = new PackageMetadataCache(root);
            await cache.WriteAsync(new PackageMetadataRecord(cacheKey, "npm-registry", "MIT", "https://example.test/repository", [], [], DateTimeOffset.UtcNow, "0123456789abcdef"));

            using var read = cache.TryRead(cacheKey);
            using var readAsync = await cache.TryReadAsync(cacheKey);

            await Assert.That(read.IsHit).IsTrue();
            await Assert.That(read.CacheKeySha256).IsEqualTo(readAsync.CacheKeySha256);
            await Assert.That(read.Source.ToString()).IsEqualTo(readAsync.Source.ToString());
            await Assert.That(read.RawLicense.ToString()).IsEqualTo(readAsync.RawLicense.ToString());
            await Assert.That(read.RepositoryUrl).IsEqualTo(readAsync.RepositoryUrl);
            await Assert.That(read.RepositoryRef).IsEqualTo(readAsync.RepositoryRef);
            await Assert.That(read.FetchedAt).IsEqualTo(readAsync.FetchedAt);
            await Assert.That(read.RawLicense.ToString()).IsEqualTo("MIT");
            await Assert.That(read.RepositoryRef).IsEqualTo("0123456789abcdef");
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
    public async Task Cache_TryRead_MissingCorruptAndInvalidEntries_MatchAsyncMiss()
    {
        await AssertSyncReadMatchesAsyncMiss(null);
        await AssertSyncReadMatchesAsyncMiss("{ invalid json");
        await AssertSyncReadMatchesAsyncMiss(CreatePackageCacheJson(schemaVersion: 2));
        await AssertSyncReadMatchesAsyncMiss(CreatePackageCacheJson().Replace(PackageMetadataCache.GetCacheKeySha256("pkg:npm/example@1.0.0"), new string('0', 64), StringComparison.Ordinal));
    }

    [Test]
    public async Task Cache_GetPath_RootSeparatorVariants_MatchesCombinedHashName()
    {
        const string cacheKey = "pkg:npm/example@1.0.0";
        var fileName = string.Concat(PackageMetadataCache.GetCacheKeySha256(cacheKey), ".json");
        var directory = Path.Combine(Path.GetTempPath(), "ol-package-cache-path");

        await Assert.That(new PackageMetadataCache(directory).GetPath(cacheKey)).IsEqualTo(Path.Combine(directory, fileName));
        await Assert.That(new PackageMetadataCache(directory + Path.DirectorySeparatorChar).GetPath(cacheKey)).IsEqualTo(Path.Combine(directory + Path.DirectorySeparatorChar, fileName));
        await Assert.That(new PackageMetadataCache(string.Empty).GetPath(cacheKey)).IsEqualTo(fileName);
    }

    [Test]
    public async Task Cache_TryRead_EmptyEntryFile_ReportsMiss()
    {
        await AssertSyncReadMatchesAsyncMiss(string.Empty);
    }

    [Test]
    public async Task Cache_TryRead_MissingCacheRoot_ReportsMissWithoutThrowing()
    {
        var cache = new PackageMetadataCache(Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}"));

        using var read = cache.TryRead("pkg:npm/example@1.0.0");

        await Assert.That(read.IsHit).IsFalse();
    }

    [Test]
    public async Task Enrichment_SingleComponentWithCachedMetadata_ReportsCacheHitWithoutFetching()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-enrich-{Guid.NewGuid():N}");
        const string purl = "pkg:npm/example@1.0.0";
        try
        {
            var cache = new PackageMetadataCache(root);
            await cache.WriteAsync(new PackageMetadataRecord(purl, "npm-registry", "MIT", string.Empty, [], []));
            var index = new SpdxLicenseIndex(["MIT"], []);
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0);
            var components = new[] { CreateEnrichmentComponent(index, purl) };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(enrichment.Summary.TargetCount).IsEqualTo(1);
            await Assert.That(enrichment.Summary.SupportedComponentCount).IsEqualTo(1);
            await Assert.That(enrichment.Components[0].License.ToString()).IsEqualTo("MIT");
            await Assert.That(GetRecord(workspace, 0)!.Value.CacheKey).IsEqualTo(purl);
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
    public async Task Enrichment_LegacyNuGetCacheWithoutLicenseOrSupportedRepository_RefreshesOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-enrich-{Guid.NewGuid():N}");
        const string purl = "pkg:nuget/Example@1.0.0";
        using var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationMetadata(
                "1.0.0",
                projectUrl: "https://dot.net/",
                licenseUrl: "https://github.com/dotnet/core/blob/main/LICENSE.TXT"));
        using var httpClient = new HttpClient(handler);
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            var keyHash = PackageMetadataCache.GetCacheKeySha256(purl);
            await File.WriteAllTextAsync(cache.GetPath(purl), $$"""
                {
                  "SchemaVersion": 1,
                  "CacheKey": "{{purl}}",
                  "CacheKeySha256": "{{keyHash}}",
                  "Source": "nuget-registry",
                  "RawLicense": "",
                  "RepositoryUrl": "https://dot.net/",
                  "Warnings": [],
                  "Errors": [],
                  "FetchedAt": "2026-07-08T00:00:00+00:00"
                }
                """);
            var index = new SpdxLicenseIndex(["MIT"], []);
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0, uncollectedPackages: null, client: httpClient);
            var components = new[] { CreateEnrichmentComponent(index, purl) };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheHitCount).IsEqualTo(0);
            await Assert.That(enrichment.Summary.CacheMissCount).IsEqualTo(1);
            await Assert.That(GetRecord(workspace, 0)!.Value.RepositoryUrl).IsEqualTo("https://github.com/dotnet/core");
            await Assert.That(GetRecord(workspace, 0)!.Value.RepositoryRef).IsEqualTo("main");

            using var refreshed = await cache.TryReadAsync(purl);
            await Assert.That(refreshed.RepositoryUrl).IsEqualTo("https://github.com/dotnet/core");
            await Assert.That(refreshed.RepositoryRef).IsEqualTo("main");

            var cachedComponents = new[] { CreateEnrichmentComponent(index, purl) };
            using var cachedWorkspace = new PackageMetadataWorkspace(cachedComponents.Length);
            var cached = await service.EnrichAsync(cachedComponents, cachedWorkspace, concurrency: 1);

            await Assert.That(cached.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(handler.RequestUris.Count).IsEqualTo(2);
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
    public async Task Enrichment_LegacyNuGetCacheWithSupportedProjectRepository_RefreshesOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-enrich-{Guid.NewGuid():N}");
        const string purl = "pkg:nuget/Example@1.0.0";
        using var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationMetadata(
                "1.0.0",
                projectUrl: "https://github.com/example/project",
                licenseUrl: "https://github.com/example/versioned/blob/main/LICENSE"));
        using var httpClient = new HttpClient(handler);
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            var keyHash = PackageMetadataCache.GetCacheKeySha256(purl);
            await File.WriteAllTextAsync(cache.GetPath(purl), $$"""
                {
                  "SchemaVersion": 1,
                  "CacheKey": "{{purl}}",
                  "CacheKeySha256": "{{keyHash}}",
                  "Source": "nuget-registry",
                  "RawLicense": "",
                  "RepositoryUrl": "https://github.com/example/project",
                  "Warnings": [],
                  "Errors": [],
                  "FetchedAt": "2026-07-08T00:00:00+00:00"
                }
                """);
            var index = new SpdxLicenseIndex(["MIT"], []);
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0, uncollectedPackages: null, client: httpClient);
            var components = new[] { CreateEnrichmentComponent(index, purl) };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheHitCount).IsEqualTo(0);
            await Assert.That(enrichment.Summary.CacheMissCount).IsEqualTo(1);
            await Assert.That(GetRecord(workspace, 0)!.Value.RepositoryUrl).IsEqualTo("https://github.com/example/versioned");
            await Assert.That(GetRecord(workspace, 0)!.Value.RepositoryRef).IsEqualTo("main");

            var cachedComponents = new[] { CreateEnrichmentComponent(index, purl) };
            using var cachedWorkspace = new PackageMetadataWorkspace(cachedComponents.Length);
            var cached = await service.EnrichAsync(cachedComponents, cachedWorkspace, concurrency: 1);

            await Assert.That(cached.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(handler.RequestUris.Count).IsEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// A cache written before the NuGet license warnings were retired still reads. An identifier a
    /// reader no longer knows contributes nothing rather than rejecting the entry, so a retired
    /// vocabulary costs one recollection at most and never a corrupt-cache error.
    /// </summary>
    [Test]
    public async Task Enrichment_LegacyNuGetCacheWithRetiredLicenseWarning_RefreshesOnceWithoutRejectingTheEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-enrich-{Guid.NewGuid():N}");
        const string purl = "pkg:nuget/Example@1.0.0";
        using var handler = new SequenceJsonResponseHandler(
            NuGetServiceIndex(),
            NuGetRegistrationIndexWithoutExpression("1.0.0"),
            NuGetCatalogLeaf("1.0.0", repositoryUrl: "https://github.com/example/project", repositoryCommit: "main"));
        using var httpClient = new HttpClient(handler);
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            var keyHash = PackageMetadataCache.GetCacheKeySha256(purl);
            await File.WriteAllTextAsync(cache.GetPath(purl), $$"""
                {
                  "SchemaVersion": 1,
                  "ResolverVersion": 2,
                  "CacheKey": "{{purl}}",
                  "CacheKeySha256": "{{keyHash}}",
                  "Source": "nuget-registry",
                  "RawLicense": "",
                  "RepositoryUrl": "",
                  "Warnings": ["nuget_license_url_unsupported"],
                  "Errors": [],
                  "FetchedAt": "2026-07-08T00:00:00+00:00"
                }
                """);
            var index = new SpdxLicenseIndex(["MIT"], []);
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0, uncollectedPackages: null, client: httpClient);
            var components = new[] { CreateEnrichmentComponent(index, purl) };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheMissCount).IsEqualTo(1);
            await Assert.That(GetRecord(workspace, 0)!.Value.RepositoryUrl).IsEqualTo("https://github.com/example/project");

            var cachedComponents = new[] { CreateEnrichmentComponent(index, purl) };
            using var cachedWorkspace = new PackageMetadataWorkspace(cachedComponents.Length);
            var cached = await service.EnrichAsync(cachedComponents, cachedWorkspace, concurrency: 1);

            await Assert.That(cached.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(handler.RequestUris.Count).IsEqualTo(3);
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
    public async Task Enrichment_ResolvedNpmCacheWrittenBeforeRepositoryDirectoryWasRead_RefreshesOnce()
    {
        // A license does not make the subdirectory fact observable, so an npm entry that predates reading
        // it is collected again once rather than keeping a monorepo package reported as conflicting.
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-enrich-{Guid.NewGuid():N}");
        const string purl = "pkg:npm/example@1.0.0";
        using var handler = new SequenceJsonResponseHandler("""{ "license": "MIT", "repository": { "url": "https://github.com/owner/monorepo", "directory": "packages/example" } }""");
        using var httpClient = new HttpClient(handler);
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(cache.GetPath(purl), $$"""
                {
                  "SchemaVersion": 1,
                  "ResolverVersion": 4,
                  "CacheKey": "{{purl}}",
                  "CacheKeySha256": "{{PackageMetadataCache.GetCacheKeySha256(purl)}}",
                  "Source": "npm-registry",
                  "RawLicense": "MIT",
                  "RepositoryUrl": "https://github.com/owner/monorepo",
                  "Warnings": [],
                  "Errors": [],
                  "FetchedAt": "2026-07-08T00:00:00+00:00"
                }
                """);
            var index = new SpdxLicenseIndex(["MIT"], []);
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0, uncollectedPackages: null, client: httpClient);
            var components = new[] { CreateEnrichmentComponent(index, purl) };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheMissCount).IsEqualTo(1);
            await Assert.That(GetRecord(workspace, 0)!.Value.RepositorySubdirectoryDeclared).IsTrue();

            var cachedComponents = new[] { CreateEnrichmentComponent(index, purl) };
            using var cachedWorkspace = new PackageMetadataWorkspace(cachedComponents.Length);
            var cached = await service.EnrichAsync(cachedComponents, cachedWorkspace, concurrency: 1);

            await Assert.That(cached.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(GetRecord(cachedWorkspace, 0)!.Value.RepositorySubdirectoryDeclared).IsTrue();
            await Assert.That(handler.RequestUris.Count).IsEqualTo(1);
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
    public async Task Enrichment_NuGetCacheWithLicenseExpression_RemainsCacheHit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-enrich-{Guid.NewGuid():N}");
        const string purl = "pkg:nuget/Example@1.0.0";
        using var handler = new SequenceJsonResponseHandler(NuGetServiceIndex(), NuGetRegistrationIndex("1.0.0", "MIT"));
        using var httpClient = new HttpClient(handler);
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            var keyHash = PackageMetadataCache.GetCacheKeySha256(purl);
            await File.WriteAllTextAsync(cache.GetPath(purl), $$"""
                {
                  "SchemaVersion": 1,
                  "ResolverVersion": 2,
                  "CacheKey": "{{purl}}",
                  "CacheKeySha256": "{{keyHash}}",
                  "Source": "nuget-registry",
                  "RawLicense": "MIT",
                  "RepositoryUrl": "",
                  "Warnings": [],
                  "Errors": [],
                  "FetchedAt": "2026-07-08T00:00:00+00:00"
                }
                """);
            var index = new SpdxLicenseIndex(["MIT"], []);
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0, uncollectedPackages: null, client: httpClient);
            var components = new[] { CreateEnrichmentComponent(index, purl) };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(handler.RequestUris).IsEmpty();
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
    public async Task Enrichment_SingleComponentWithoutSupportedPurl_ReportsUnsupportedWithoutTarget()
    {
        var index = new SpdxLicenseIndex(["MIT"], []);
        var service = new PackageMetadataService(index, new PackageMetadataCache(Path.GetTempPath()), refresh: false, retryCount: 0);
        var unsupported = new[] { CreateEnrichmentComponent(index, "pkg:unknown-ecosystem/example@1.0.0") };
        var empty = new[] { CreateEnrichmentComponent(index, default) };
        using var unsupportedWorkspace = new PackageMetadataWorkspace(unsupported.Length);
        using var emptyWorkspace = new PackageMetadataWorkspace(empty.Length);

        var unsupportedEnrichment = await service.EnrichAsync(unsupported, unsupportedWorkspace, concurrency: 1);
        var emptyEnrichment = await service.EnrichAsync(empty, emptyWorkspace, concurrency: 1);

        await Assert.That(unsupportedEnrichment.Summary.UnsupportedEcosystemCount).IsEqualTo(1);
        await Assert.That(unsupportedEnrichment.Summary.SupportedComponentCount).IsEqualTo(1);
        await Assert.That(unsupportedEnrichment.Summary.TargetCount).IsEqualTo(0);
        await Assert.That(unsupportedEnrichment.Components[0].Warnings).Contains("unsupported_package_metadata");
        await Assert.That(GetRecord(unsupportedWorkspace, 0).HasValue).IsFalse();
        await Assert.That(emptyEnrichment.Summary.SupportedComponentCount).IsEqualTo(0);
        await Assert.That(emptyEnrichment.Summary.UnsupportedEcosystemCount).IsEqualTo(0);
        await Assert.That(emptyEnrichment.Summary.TargetCount).IsEqualTo(0);
        await Assert.That(emptyEnrichment.Components[0].CandidateCount).IsEqualTo(1);
        await Assert.That(GetRecord(emptyWorkspace, 0).HasValue).IsFalse();
    }

    /// <summary>
    /// Separates the two reasons a purl cannot be queried in the summary as well as in the warning.
    /// </summary>
    /// <remarks>
    /// A supported ecosystem whose purl names no version is not an unsupported ecosystem, and summing
    /// them reports that Ol has no provider for an ecosystem it does support.
    /// </remarks>
    [Test]
    public async Task Enrichment_WithUnversionedPurl_CountsItApartFromAnUnsupportedEcosystem()
    {
        var index = new SpdxLicenseIndex(["MIT"], []);
        var service = new PackageMetadataService(index, new PackageMetadataCache(Path.GetTempPath()), refresh: false, retryCount: 0);
        var unversioned = new[] { CreateEnrichmentComponent(index, "pkg:maven/com.example/module") };
        var unsupported = new[] { CreateEnrichmentComponent(index, "pkg:unknown-ecosystem/example@1.0.0") };
        using var unversionedWorkspace = new PackageMetadataWorkspace(unversioned.Length);
        using var unsupportedWorkspace = new PackageMetadataWorkspace(unsupported.Length);

        var unversionedEnrichment = await service.EnrichAsync(unversioned, unversionedWorkspace, concurrency: 1);
        var unsupportedEnrichment = await service.EnrichAsync(unsupported, unsupportedWorkspace, concurrency: 1);

        await Assert.That(unversionedEnrichment.Summary.UnversionedPurlCount).IsEqualTo(1);
        await Assert.That(unversionedEnrichment.Summary.UnsupportedEcosystemCount).IsEqualTo(0);
        await Assert.That(unversionedEnrichment.Components[0].Warnings).Contains("package_metadata_unversioned_purl");
        await Assert.That(unsupportedEnrichment.Summary.UnversionedPurlCount).IsEqualTo(0);
        await Assert.That(unsupportedEnrichment.Summary.UnsupportedEcosystemCount).IsEqualTo(1);
    }

    private static PackageMetadataResolution? GetRecord(PackageMetadataWorkspace workspace, int index) => workspace.Records[index];

    private static ScanComponent CreateEnrichmentComponent(SpdxLicenseIndex index, Utf8Slice purl)
        => new("example", "1.0.0", default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, purl, default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), [], []);

    private static async Task AssertSyncReadMatchesAsyncMiss(string? json)
    {
        const string cacheKey = "pkg:npm/example@1.0.0";
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            if (json is not null)
            {
                await File.WriteAllTextAsync(cache.GetPath(cacheKey), json);
            }

            using var read = cache.TryRead(cacheKey);
            using var readAsync = await cache.TryReadAsync(cacheKey);

            await Assert.That(read.IsHit).IsFalse();
            await Assert.That(readAsync.IsHit).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static PackageMetadataRegistryClient CreateClient(string body)
        => OlDefaults.CreatePackageMetadataRegistryClient(new StaticResponseHandler(body));

    private static string NuGetServiceIndex(
        string registrationType = "\"RegistrationsBaseUrl/3.6.0\"",
        string registrationBase = "https://api.nuget.org/v3/registration5-gz-semver2/")
        => $$"""
            {
              "version": "3.0.0",
              "resources": [
                { "@id": "{{registrationBase}}", "@type": {{registrationType}} }
              ]
            }
            """;

    private static string NuGetRegistrationIndex(string version, string license)
        => $$"""
            {
              "items": [
                {
                  "lower": "{{version}}",
                  "upper": "{{version}}",
                  "items": [
                    { "catalogEntry": { "version": "{{version}}", "licenseExpression": "{{license}}" } }
                  ]
                }
              ]
            }
            """;

    private const string CatalogLeafUri = "https://api.nuget.org/v3/catalog0/data/2026.01.02.03.04.05/example.json";

    /// <summary>Reproduces the registration shape nuget.org serves for a package with no SPDX expression.</summary>
    /// <remarks>
    /// The inlined catalog entry omits <c>licenseFile</c> and <c>repository</c>, and rewrites
    /// <c>licenseUrl</c> to the gallery license page, so only the catalog entry carries them.
    /// </remarks>
    private static string NuGetRegistrationIndexWithoutExpression(string version)
        => $$"""
            {
              "items": [
                {
                  "lower": "{{version}}",
                  "upper": "{{version}}",
                  "items": [
                    {
                      "catalogEntry": {
                        "@id": "{{CatalogLeafUri}}",
                        "version": "{{version}}",
                        "licenseExpression": "",
                        "licenseUrl": "https://www.nuget.org/packages/Example/{{version}}/license",
                        "projectUrl": "https://example.test/project"
                      }
                    }
                  ]
                }
              ]
            }
            """;

    private static string NuGetCatalogLeaf(
        string version,
        string licenseExpression = "",
        string licenseFile = "",
        string licenseUrl = "",
        string repositoryUrl = "",
        string repositoryCommit = "")
        => $$"""
            {
              "@id": "{{CatalogLeafUri}}",
              "@type": ["PackageDetails", "catalog:Permalink"],
              "id": "Example",
              "version": "{{version}}",
              "licenseExpression": "{{licenseExpression}}",
              "licenseFile": "{{licenseFile}}",
              "licenseUrl": "{{licenseUrl}}",
              "repository": { "type": "git", "url": "{{repositoryUrl}}", "commit": "{{repositoryCommit}}" }
            }
            """;

    private static string NuGetRegistrationMetadata(string version, string projectUrl = "", string licenseUrl = "")
        => $$"""
            {
              "items": [
                {
                  "lower": "{{version}}",
                  "upper": "{{version}}",
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "{{version}}",
                        "projectUrl": "{{projectUrl}}",
                        "licenseUrl": "{{licenseUrl}}"
                      }
                    }
                  ]
                }
              ]
            }
            """;

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

    private static async Task AssertCacheEntryRawLicense(string json, string expected)
    {
        const string cacheKey = "pkg:npm/example@1.0.0";
        var root = Path.Combine(Path.GetTempPath(), $"ol-package-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(cache.GetPath(cacheKey), json);

            using var read = await cache.TryReadAsync(cacheKey);

            await Assert.That(read.IsHit).IsTrue();
            await Assert.That(read.RawLicense.ToString()).IsEqualTo(expected);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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

            using var read = await cache.TryReadAsync(cacheKey);

            await Assert.That(read.IsHit).IsFalse();
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

        public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
            => new("test-registry", root.GetProperty("license").GetString() ?? string.Empty, string.Empty);
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class RequestHeaderHandler : HttpMessageHandler
    {
        public string UserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "license": "MIT" }""") });
        }
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

    private sealed class SequenceJsonResponseHandler(params string[] bodies) : HttpMessageHandler
    {
        private readonly string[] bodies = bodies;

        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.AbsoluteUri);
            var body = bodies[Math.Min(RequestUris.Count - 1, bodies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    /// <summary>Answers the first request and fails every follow-up.</summary>
    private sealed class FollowUpFailureHandler(string firstBody, HttpStatusCode followUpStatus) : HttpMessageHandler
    {
        private int requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Interlocked.Increment(ref requestCount) == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(firstBody) }
                : new HttpResponseMessage(followUpStatus));
    }

    private sealed class RateLimitedOriginHandler : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return Task.FromResult(response);
        }
    }

    private sealed class ExcessiveRetryAfterHandler : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.MaxValue);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "license": "MIT" }""") });
        }
    }

    /// <summary>Rate limits one origin twice: first with a short delay, then with one past the wait budget.</summary>
    private sealed class EscalatingOriginRateLimitHandler : HttpMessageHandler
    {
        public TaskCompletionSource LongLimitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseShortLimit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLongLimit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("second", StringComparison.Ordinal))
            {
                LongLimitEntered.TrySetResult();
                await ReleaseLongLimit.Task.WaitAsync(cancellationToken);
                return RateLimited(TimeSpan.FromSeconds(300));
            }

            if (path.Contains("first", StringComparison.Ordinal))
            {
                await ReleaseShortLimit.Task.WaitAsync(cancellationToken);
                return RateLimited(TimeSpan.FromSeconds(5));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "license": "MIT" }""") };
        }

        private static HttpResponseMessage RateLimited(TimeSpan retryAfter)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            return response;
        }
    }

    private sealed class RateLimitedThenSuccessfulNpmHandler(TimeSpan retryAfter) : HttpMessageHandler
    {
        private int requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "license": "MIT" }""") });
        }
    }

    private sealed class ProbeRateLimitedOriginHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource probeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource probeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requestCount;

        public Task ProbeStarted => probeStarted.Task;
        public int RequestCount => requestCount;

        public void CompleteProbe() => probeCompletion.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref requestCount);
            if (current == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(20));
                return response;
            }

            if (current == 2)
            {
                probeStarted.TrySetResult();
                await probeCompletion.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "license": "MIT" }""") };
        }
    }

    private sealed class CancelableNuGetServiceIndexHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource discoveryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource discoveryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int serviceIndexRequestCount;

        public Task DiscoveryStarted => discoveryStarted.Task;
        public int ServiceIndexRequestCount => serviceIndexRequestCount;

        public void CompleteDiscovery() => discoveryCompletion.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == "https://api.nuget.org/v3/index.json")
            {
                Interlocked.Increment(ref serviceIndexRequestCount);
                discoveryStarted.TrySetResult();
                await discoveryCompletion.Task.WaitAsync(cancellationToken);
                return Json(NuGetServiceIndex());
            }

            return Json(NuGetRegistrationIndex("2.0.0", "Apache-2.0"));
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private sealed class NuGetServiceIndexHandler : HttpMessageHandler
    {
        private int serviceIndexRequestCount;

        public int ServiceIndexRequestCount => serviceIndexRequestCount;
        public List<string> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            lock (RequestUris)
            {
                RequestUris.Add(uri);
            }

            if (uri == "https://api.nuget.org/v3/index.json")
            {
                Interlocked.Increment(ref serviceIndexRequestCount);
                await Task.Yield();
                return Json("""
                    {
                      "version": "3.0.0",
                      "resources": [
                        { "@id": "https://api.nuget.org/v3/ignored-semver1/", "@type": "RegistrationsBaseUrl/3.4.0" },
                        { "@id": "https://api.nuget.org/v3/discovered-semver2/", "@type": "RegistrationsBaseUrl/3.6.0" }
                      ]
                    }
                    """);
            }

            if (uri.EndsWith("/first/index.json", StringComparison.Ordinal))
            {
                return Json(NuGetRegistrationIndex("1.0.0", "MIT"));
            }

            if (uri.EndsWith("/second/index.json", StringComparison.Ordinal))
            {
                return Json(NuGetRegistrationIndex("2.0.0", "Apache-2.0"));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private sealed class RateLimitedNuGetServiceIndexHandler(TimeSpan retryAfter) : HttpMessageHandler
    {
        private int serviceIndexRequestCount;
        private int registrationRequestCount;

        public int ServiceIndexRequestCount => serviceIndexRequestCount;
        public int RegistrationRequestCount => registrationRequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == "https://api.nuget.org/v3/index.json")
            {
                if (Interlocked.Increment(ref serviceIndexRequestCount) == 1)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
                    return Task.FromResult(limited);
                }

                return Task.FromResult(Json(NuGetServiceIndex()));
            }

            Interlocked.Increment(ref registrationRequestCount);
            return Task.FromResult(Json(NuGetRegistrationIndex("1.0.0", "MIT")));
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private sealed class GzipNuGetRegistrationHandler : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(NuGetServiceIndex()) });
            }

            var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            using (var writer = new StreamWriter(gzip))
            {
                writer.Write(NuGetRegistrationIndex("1.0.0", "MIT"));
            }

            output.Position = 0;
            var content = new StreamContent(output);
            content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
