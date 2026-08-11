using System.Net;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageMetadata;
using Ol.Core.Spdx;
using Ol.Internals;

namespace Ol.Tests;

/// <summary>
/// Guards that a registry response's declared license location survives to the report.
/// </summary>
/// <remarks>
/// Every ecosystem Ol reads lets a publisher point at its license instead of naming one, and each
/// spells that differently. Retaining them as one reference is what keeps the concept from becoming a
/// per-ecosystem vocabulary. The value has to survive the cache too, or a report would depend on
/// whether a component happened to be collected in this run.
/// </remarks>
public sealed class DeclaredLicenseReferenceRegistryTests
{
    // Equivalence classes across ecosystems: a path inside the artifact, a URL, embedded text whose
    // content is deliberately not retained, and a response declaring no location at all.

    [Test]
    [Arguments("nuget-file", "\"licenseFile\": \"MIT-LICENSE.txt\",", DeclaredLicenseReferenceKind.ArtifactPath, "MIT-LICENSE.txt")]
    [Arguments("nuget-url", "\"licenseUrl\": \"https://example.test/eula\",", DeclaredLicenseReferenceKind.Location, "https://example.test/eula")]
    [Arguments("nuget-both-prefers-file", "\"licenseFile\": \"LICENSE.txt\", \"licenseUrl\": \"https://example.test/eula\",", DeclaredLicenseReferenceKind.ArtifactPath, "LICENSE.txt")]
    [Arguments("nuget-none", "", DeclaredLicenseReferenceKind.None, "")]
    public async Task Fetch_NuGetCatalogEntry_RetainsTheDeclaredLocation(string label, string declaration, DeclaredLicenseReferenceKind expectedKind, string expectedValue)
    {
        var handler = new NuGetCatalogHandler(NuGetCatalogWith(declaration));
        var client = OlDefaults.CreatePackageMetadataRegistryClient(handler);

        var record = await client.FetchAsync(new PackageMetadataRequest("nuget", "", "Example", "1.0.0", "pkg:nuget/Example@1.0.0"));

        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(expectedKind);
        await Assert.That(record.DeclaredLicenseReference).IsEqualTo(expectedValue);
        await Assert.That(label).IsNotEmpty();
    }

    [Test]
    [Arguments("cargo", """{ "version": { "license": null, "license_file": "LICENSE-CUSTOM", "repository": "" } }""", DeclaredLicenseReferenceKind.ArtifactPath, "LICENSE-CUSTOM")]
    [Arguments("pypi", """{ "info": { "license": "", "license_files": ["LICENSE.rst"] } }""", DeclaredLicenseReferenceKind.ArtifactPath, "LICENSE.rst")]
    public async Task Fetch_RegistryResponse_RetainsTheDeclaredLocationForEveryEcosystem(string ecosystem, string body, DeclaredLicenseReferenceKind expectedKind, string expectedValue)
    {
        var client = OlDefaults.CreatePackageMetadataRegistryClient(new StaticHandler(body));

        var record = await client.FetchAsync(new PackageMetadataRequest(ecosystem, "", "example", "1.0.0", $"pkg:{ecosystem}/example@1.0.0"));

        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(expectedKind);
        await Assert.That(record.DeclaredLicenseReference).IsEqualTo(expectedValue);
    }

    // Embedded text is recorded as existing and never persisted. A cache is not a place to keep a
    // license document, and the report contract keeps license text out of default output.
    [Test]
    public async Task Fetch_CocoaPodsLicenseText_RecordsThatTextExistsWithoutRetainingIt()
    {
        var client = OlDefaults.CreatePackageMetadataRegistryClient(new StaticHandler("""{ "license": { "text": "Permission is hereby granted, free of charge..." } }"""));

        var record = await client.FetchAsync(new PackageMetadataRequest("cocoapods", "", "Example", "1.0.0", "pkg:cocoapods/Example@1.0.0"));

        await Assert.That(record.DeclaredLicenseReferenceKind).IsEqualTo(DeclaredLicenseReferenceKind.InlineText);
        await Assert.That(record.DeclaredLicenseReference).IsEmpty();
    }

    [Test]
    public async Task Cache_DeclaredLicenseReference_SurvivesARoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-declared-reference-{Guid.NewGuid():N}");
        const string purl = "pkg:nuget/Example@1.0.0";
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            await cache.WriteAsync(new PackageMetadataRecord(purl, "nuget-registry", string.Empty, string.Empty, [], [], DateTimeOffset.UtcNow, string.Empty, DeclaredLicenseReferenceKind.ArtifactPath, "MIT-LICENSE.txt"));

            using var entry = await cache.TryReadAsync(purl);

            await Assert.That(entry.IsHit).IsTrue();
            await Assert.That(entry.DeclaredLicenseReferenceKind).IsEqualTo(DeclaredLicenseReferenceKind.ArtifactPath);
            await Assert.That(entry.DeclaredLicenseReference.ToString()).IsEqualTo("MIT-LICENSE.txt");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrichment_CachedDeclaredLicenseReference_ReachesTheComponentEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-declared-reference-{Guid.NewGuid():N}");
        const string purl = "pkg:nuget/Example@1.0.0";
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            await cache.WriteAsync(new PackageMetadataRecord(purl, "nuget-registry", string.Empty, string.Empty, [], [], DateTimeOffset.UtcNow, string.Empty, DeclaredLicenseReferenceKind.Location, "https://example.test/eula"));
            var index = new SpdxLicenseIndex(["MIT"], []);
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0);
            var components = new[] { CreateComponent(index, purl) };
            using var workspace = new PackageMetadataWorkspace(components.Length);

            var enrichment = await service.EnrichAsync(components, workspace, concurrency: 1);
            var component = enrichment.Components[0];
            var reference = component.GetCandidate(component.CandidateCount - 1).Evidence.DeclaredReference;

            await Assert.That(reference?.Kind ?? DeclaredLicenseReferenceKind.None).IsEqualTo(DeclaredLicenseReferenceKind.Location);
            await Assert.That(reference?.Value.ToString() ?? string.Empty).IsEqualTo("https://example.test/eula");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Every provider can now state a declared location, so an unresolved observation from before that
    // is stale in every ecosystem, not only the one whose resolver changed first.
    [Test]
    [Arguments("cargo-registry", "pkg:cargo/example@1.0.0", 1)]
    [Arguments("nuget-registry", "pkg:nuget/Example@1.0.0", 1)]
    public async Task Enrichment_UnresolvedEntryFromAnOlderResolver_IsCollectedAgainOnce(string source, string purl, int expectedMisses)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-stale-entry-{Guid.NewGuid():N}");
        try
        {
            var cache = new PackageMetadataCache(root);
            Directory.CreateDirectory(root);
            var keyHash = PackageMetadataCache.GetCacheKeySha256(purl);
            await File.WriteAllTextAsync(cache.GetPath(purl), $$"""
                {
                  "SchemaVersion": 1,
                  "ResolverVersion": 3,
                  "CacheKey": "{{purl}}",
                  "CacheKeySha256": "{{keyHash}}",
                  "Source": "{{source}}",
                  "RawLicense": "",
                  "RepositoryUrl": "",
                  "Warnings": [],
                  "Errors": [],
                  "FetchedAt": "2026-07-08T00:00:00+00:00"
                }
                """);
            var index = new SpdxLicenseIndex(["MIT"], []);
            using var httpClient = new HttpClient(new StaticHandler("""{ "version": { "license": "MIT" } }"""));
            var service = new PackageMetadataService(index, cache, refresh: false, retryCount: 0, uncollectedPackages: null, client: httpClient);
            using var workspace = new PackageMetadataWorkspace(1);

            var enrichment = await service.EnrichAsync([CreateComponent(index, purl)], workspace, concurrency: 1);

            await Assert.That(enrichment.Summary.CacheMissCount).IsEqualTo(expectedMisses);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ScanComponent CreateComponent(SpdxLicenseIndex index, Utf8Slice purl)
        => new("example", "1.0.0", default, "nuget", DependencyType.Unknown, LicenseStatus.Unknown, purl, default, LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "NOASSERTION"u8, index), []);

    private const string NuGetServiceIndexJson = """
        {
          "version": "3.0.0",
          "resources": [ { "@id": "https://api.nuget.org/v3/registration5-gz-semver2/", "@type": "RegistrationsBaseUrl/3.6.0" } ]
        }
        """;

    private const string RegistrationJson = """
        {
          "items": [
            {
              "lower": "1.0.0",
              "upper": "1.0.0",
              "items": [
                { "catalogEntry": { "@id": "https://api.nuget.org/v3/catalog0/data/2026.01.02.03.04.05/example.json", "version": "1.0.0", "licenseExpression": "" } }
              ]
            }
          ]
        }
        """;

    private static string NuGetCatalogWith(string declaration)
        => $$"""{ "@id": "https://api.nuget.org/v3/catalog0/data/2026.01.02.03.04.05/example.json", "id": "Example", "version": "1.0.0", {{declaration}} "licenseExpression": "" }""";

    private sealed class NuGetCatalogHandler(string catalog) : HttpMessageHandler
    {
        private int count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = ++count switch { 1 => NuGetServiceIndexJson, 2 => RegistrationJson, _ => catalog };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
