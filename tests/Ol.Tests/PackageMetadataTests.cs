using Ol.Core;

namespace Ol.Tests;

public sealed class PackageMetadataTests
{
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
        var record = new PackageMetadataRecord(request.CacheKey, "npm-registry", "MIT", "https://example.test/repository", [], []);

        try
        {
            var cache = new PackageMetadataCache(root);
            await cache.WriteAsync(record);

            var read = await cache.TryReadAsync(request.CacheKey);

            await Assert.That(read.HasValue).IsTrue();
            await Assert.That(read!.Value.CacheKey).IsEqualTo(request.CacheKey);
            await Assert.That(read.Value.RawLicense).IsEqualTo("MIT");
            await Assert.That(Directory.GetFiles(root, "*.json")[0]).DoesNotContain("example");
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
            [LicenseCandidateFactory.Create("sbom", "id", "NOASSERTION", index)],
            [],
            []);

        var result = LicenseReconciler.AddCandidate(component, LicenseCandidateFactory.Create("npm-registry", "license", "MIT", index));

        await Assert.That(result.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(result.License).IsEqualTo("MIT");
        await Assert.That(result.LicenseCandidates.Length).IsEqualTo(2);
        await Assert.That(result.Evidence[1].Source).IsEqualTo("npm-registry");
    }
}
