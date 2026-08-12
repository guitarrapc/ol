using System.Net;
using System.Text;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Tests;

public sealed class DeclaredGitHubFileArtifactCollectorTests
{
    [Test]
    public async Task Enrich_CoreFxDeclaredBlobUrl_FetchesExactFileAndAddsMatchedArtifactEvidence()
    {
        var handler = new GitHubContentsHandler("MIT License");
        var (components, matcher, spdx) = CreateInputs("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT");
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create("secret"), new Uri("https://api.github.test/"));

        var result = await collector.EnrichAsync(components, concurrency: 1);

        await Assert.That(result.Summary.TargetCount).IsEqualTo(1);
        await Assert.That(result.Summary.GitHubRequestCount).IsEqualTo(1);
        await Assert.That(result.Summary.DocumentCount).IsEqualTo(1);
        await Assert.That(result.Summary.MatchedCount).IsEqualTo(1);
        await Assert.That(handler.RequestUri).IsEqualTo("https://api.github.test/repos/dotnet/corefx/contents/LICENSE.TXT?ref=master");
        var candidate = result.Components[0].GetCandidate(result.Components[0].CandidateCount - 1);
        await Assert.That(candidate.Source).IsEqualTo(LicenseCandidateSource.PackageArtifact);
        await Assert.That(candidate.Normalized.ToString()).IsEqualTo("MIT");
        await Assert.That(candidate.Evidence.PackageArtifact!.Artifact).IsEqualTo("pkg:nuget/System.Buffers@4.5.1");
        await Assert.That(candidate.Evidence.PackageArtifact.Path).IsEqualTo("LICENSE.TXT");
        await Assert.That(candidate.Evidence.PackageArtifact.ContentSha256).Length().IsEqualTo(64);
        await Assert.That(candidate.Evidence.PackageArtifact.CorpusVersion).IsEqualTo("test-corpus");
    }

    [Test]
    public async Task Enrich_SecondRun_ReadsCachedDocumentWithoutGitHubRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-github-file-cache-{Guid.NewGuid():N}");
        var handler = new GitHubContentsHandler("MIT License");
        var (firstComponents, matcher, spdx) = CreateInputs("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT");
        var cache = new DeclaredGitHubFileCache(root);
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"), cache, refresh: false);

        try
        {
            var first = await collector.EnrichAsync(firstComponents, concurrency: 1);
            var (secondComponents, _, _) = CreateInputs("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT");
            var second = await collector.EnrichAsync(secondComponents, concurrency: 1);

            await Assert.That(handler.CallCount).IsEqualTo(1);
            await Assert.That(first.Summary.GitHubRequestCount).IsEqualTo(1);
            await Assert.That(second.Summary.GitHubRequestCount).IsEqualTo(0);
            await Assert.That(second.Summary.CacheHitCount).IsEqualTo(1);
            await Assert.That(second.Components[0].License.ToString()).IsEqualTo("MIT");
            await Assert.That(second.Components[0].GetCandidate(second.Components[0].CandidateCount - 1).Evidence.PackageArtifact!.ContentSha256)
                .IsEqualTo(first.Components[0].GetCandidate(first.Components[0].CandidateCount - 1).Evidence.PackageArtifact!.ContentSha256);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrich_CachedDocumentWithMismatchedSha256_RefetchesFromGitHub()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-github-file-cache-{Guid.NewGuid():N}");
        var location = "https://github.com/dotnet/corefx/blob/master/LICENSE.TXT";
        var handler = new GitHubContentsHandler("MIT License");
        var (components, matcher, spdx) = CreateInputs(location);
        var cache = new DeclaredGitHubFileCache(root);
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"), cache, refresh: false);

        try
        {
            await collector.EnrichAsync(components, concurrency: 1);
            DeclaredGitHubFileTarget.TryCreate(location, out var target);
            var path = cache.GetPath(target.CacheKey);
            var json = await File.ReadAllTextAsync(path);
            json = json.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes("MIT License")), Convert.ToBase64String(Encoding.UTF8.GetBytes("bad content")), StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, json);
            var (secondComponents, _, _) = CreateInputs(location);

            var second = await collector.EnrichAsync(secondComponents, concurrency: 1);

            await Assert.That(handler.CallCount).IsEqualTo(2);
            await Assert.That(second.Summary.CacheHitCount).IsEqualTo(0);
            await Assert.That(second.Summary.CacheMissCount).IsEqualTo(1);
            await Assert.That(second.Components[0].License.ToString()).IsEqualTo("MIT");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrich_RefreshBypassesValidCachedDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-github-file-cache-{Guid.NewGuid():N}");
        var location = "https://github.com/dotnet/corefx/blob/master/LICENSE.TXT";
        var handler = new GitHubContentsHandler("MIT License");
        var (components, matcher, spdx) = CreateInputs(location);
        var cache = new DeclaredGitHubFileCache(root);
        var cachedCollector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"), cache);

        try
        {
            await cachedCollector.EnrichAsync(components, concurrency: 1);
            var (refreshComponents, _, _) = CreateInputs(location);
            var refreshCollector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"), cache, refresh: true);

            var result = await refreshCollector.EnrichAsync(refreshComponents, concurrency: 1);

            await Assert.That(handler.CallCount).IsEqualTo(2);
            await Assert.That(result.Summary.CacheHitCount).IsEqualTo(0);
            await Assert.That(result.Summary.CacheMissCount).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrich_SecondNotFoundRun_RequestsGitHubAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-github-file-cache-{Guid.NewGuid():N}");
        var location = "https://github.com/example/project/blob/v1/LICENSE";
        var handler = new GitHubContentsHandler("", HttpStatusCode.NotFound);
        var (components, matcher, spdx) = CreateInputs(location);
        var cache = new DeclaredGitHubFileCache(root);
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"), cache);

        try
        {
            await collector.EnrichAsync(components, concurrency: 1);
            var (secondComponents, _, _) = CreateInputs(location);
            var result = await collector.EnrichAsync(secondComponents, concurrency: 1);

            await Assert.That(handler.CallCount).IsEqualTo(2);
            await Assert.That(result.Summary.CacheHitCount).IsEqualTo(0);
            await Assert.That(Directory.Exists(root)).IsFalse();
            await Assert.That(result.Summary.DocumentCount).IsEqualTo(0);
            await Assert.That(result.Components[0].CandidateCount).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Enrich_PackageArtifactAlreadyCollected_DoesNotFetchDeclaredUrl()
    {
        var handler = new GitHubContentsHandler("MIT License");
        var (components, matcher, spdx) = CreateInputs("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT");
        components[0] = LicenseReconciler.AddCandidate(components[0], CreateArtifactCandidate(spdx));
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"));

        var result = await collector.EnrichAsync(components, concurrency: 1);

        await Assert.That(result.Summary).IsEqualTo(default(DeclaredGitHubFileArtifactCollectionSummary));
        await Assert.That(handler.CallCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("https://github.com/dotnet/corefx")]
    [Arguments("https://github.com/dotnet/corefx/tree/master/LICENSE.TXT")]
    [Arguments("https://example.test/LICENSE")]
    [Arguments("http://github.com/dotnet/corefx/blob/master/LICENSE.TXT")]
    [Arguments("https://github.com/dotnet/corefx/blob/master/../LICENSE.TXT")]
    public async Task Enrich_NonExactOrUntrustedLocation_DoesNotFetch(string location)
    {
        var handler = new GitHubContentsHandler("MIT License");
        var (components, matcher, spdx) = CreateInputs(location);
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"));

        var result = await collector.EnrichAsync(components, concurrency: 1);

        await Assert.That(result.Summary).IsEqualTo(default(DeclaredGitHubFileArtifactCollectionSummary));
        await Assert.That(handler.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Enrich_DuplicateDeclaredUrl_FetchesOnceAndProjectsEachArtifactIdentity()
    {
        var handler = new GitHubContentsHandler("MIT License");
        var (components, matcher, spdx) = CreateInputs("https://raw.githubusercontent.com/dotnet/corefx/master/LICENSE.TXT", count: 2);
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"));

        var result = await collector.EnrichAsync(components, concurrency: 2);

        await Assert.That(result.Summary.TargetCount).IsEqualTo(1);
        await Assert.That(result.Summary.GitHubRequestCount).IsEqualTo(1);
        await Assert.That(result.Summary.DocumentCount).IsEqualTo(1);
        await Assert.That(result.Summary.MatchedCount).IsEqualTo(2);
        await Assert.That(result.Components[0].GetCandidate(result.Components[0].CandidateCount - 1).Evidence.PackageArtifact!.Artifact)
            .IsEqualTo("pkg:nuget/System.Buffers@4.5.1");
        await Assert.That(result.Components[1].GetCandidate(result.Components[1].CandidateCount - 1).Evidence.PackageArtifact!.Artifact)
            .IsEqualTo("pkg:nuget/System.Memory@4.5.4");
    }

    [Test]
    public async Task Enrich_RepositoryIdentityWithDifferentCasing_FetchesOnce()
    {
        var handler = new GitHubContentsHandler("MIT License");
        var spdx = new SpdxLicenseIndex(["MIT"], []);
        var matcher = new SpdxLicenseTextMatcher("test-corpus", [new("MIT", "MIT License")]);
        var components = new[]
        {
            CreateComponent(spdx, "System.Buffers", "4.5.1", "https://github.com/dotnet/corefx/blob/master/LICENSE.TXT"),
            CreateComponent(spdx, "System.Memory", "4.5.4", "https://github.com/DotNet/CoreFx/blob/master/LICENSE.TXT"),
        };
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"));

        var result = await collector.EnrichAsync(components, concurrency: 2);

        await Assert.That(result.Summary.TargetCount).IsEqualTo(1);
        await Assert.That(result.Summary.GitHubRequestCount).IsEqualTo(1);
        await Assert.That(result.Summary.MatchedCount).IsEqualTo(2);
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Enrich_UnrecognizedDocument_RetainsUnknownHashedEvidence()
    {
        var handler = new GitHubContentsHandler("custom terms");
        var (components, matcher, spdx) = CreateInputs("https://github.com/example/project/blob/v1/LICENSE");
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"));

        var result = await collector.EnrichAsync(components, concurrency: 1);

        var candidate = result.Components[0].GetCandidate(result.Components[0].CandidateCount - 1);
        await Assert.That(candidate.Status).IsEqualTo(LicenseStatus.Unknown);
        await Assert.That(candidate.Warnings).IsEqualTo(LicenseCandidateWarnings.SourceLicenseNotDetected);
        await Assert.That(candidate.Evidence.PackageArtifact!.ContentSha256).Length().IsEqualTo(64);
        await Assert.That(result.Summary.DocumentCount).IsEqualTo(1);
        await Assert.That(result.Summary.MatchedCount).IsEqualTo(0);
    }

    [Test]
    public async Task Enrich_DeclaredFileNotFound_RetainsDeclarationWithoutArtifactCandidate()
    {
        var handler = new GitHubContentsHandler("", HttpStatusCode.NotFound);
        var (components, matcher, spdx) = CreateInputs("https://github.com/example/project/blob/v1/LICENSE");
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"));

        var result = await collector.EnrichAsync(components, concurrency: 1);

        await Assert.That(result.Summary.TargetCount).IsEqualTo(1);
        await Assert.That(result.Summary.GitHubRequestCount).IsEqualTo(1);
        await Assert.That(result.Summary.DocumentCount).IsEqualTo(0);
        await Assert.That(result.Summary.FetchErrorCount).IsEqualTo(0);
        await Assert.That(result.Components[0].CandidateCount).IsEqualTo(1);
    }

    [Test]
    public async Task Enrich_DocumentOverMatcherLimit_IsBoundedFetchError()
    {
        var handler = new GitHubContentsHandler("MIT License");
        var spdx = new SpdxLicenseIndex(["MIT"], []);
        var matcher = new SpdxLicenseTextMatcher("test-corpus", [new("MIT", "MIT License")], maximumTextBytes: 4);
        var components = new[] { CreateComponent(spdx, "Example", "1.0.0", "https://github.com/example/project/blob/v1/LICENSE") };
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"));

        var result = await collector.EnrichAsync(components, concurrency: 1);

        await Assert.That(result.Summary.DocumentCount).IsEqualTo(0);
        await Assert.That(result.Summary.FetchErrorCount).IsEqualTo(1);
        await Assert.That(result.Components[0].CandidateCount).IsEqualTo(1);
    }

    [Test]
    public async Task Enrich_TransientFailure_RetriesWithinSharedPolicy()
    {
        var handler = new GitHubContentsHandler("MIT License", HttpStatusCode.InternalServerError, failuresBeforeSuccess: 1);
        var (components, matcher, spdx) = CreateInputs("https://github.com/example/project/blob/v1/LICENSE");
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 1, new HttpClient(handler), GitHubAuthentication.Create(), new Uri("https://api.github.test/"));

        var result = await collector.EnrichAsync(components, concurrency: 1);

        await Assert.That(handler.CallCount).IsEqualTo(2);
        await Assert.That(result.Summary.MatchedCount).IsEqualTo(1);
        await Assert.That(result.Summary.FetchErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Enrich_OfficialApiEndpoint_AttachesDedicatedToken()
    {
        var handler = new GitHubContentsHandler("MIT License");
        var (components, matcher, spdx) = CreateInputs("https://github.com/example/project/blob/v1/LICENSE");
        var collector = new DeclaredGitHubFileArtifactCollector(matcher, spdx, retryCount: 0, new HttpClient(handler), GitHubAuthentication.Create("secret"));

        await collector.EnrichAsync(components, concurrency: 1);

        await Assert.That(handler.Authorization).IsEqualTo("Bearer secret");
    }

    private static (ScanComponent[] Components, SpdxLicenseTextMatcher Matcher, SpdxLicenseIndex Spdx) CreateInputs(string location, int count = 1)
    {
        var spdx = new SpdxLicenseIndex(["MIT"], []);
        var matcher = new SpdxLicenseTextMatcher("test-corpus", [new("MIT", "MIT License")]);
        var components = new ScanComponent[count];
        components[0] = CreateComponent(spdx, "System.Buffers", "4.5.1", location);
        if (count > 1) components[1] = CreateComponent(spdx, "System.Memory", "4.5.4", location);
        return (components, matcher, spdx);
    }

    private static ScanComponent CreateComponent(SpdxLicenseIndex spdx, string name, string version, string location)
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageRegistry,
            DeclaredReference: new(DeclaredLicenseReferenceKind.Location, Utf8Slice.FromString(location)));
        var candidate = LicenseCandidateFactory.Create(
            LicenseCandidateSource.NuGetRegistry,
            LicenseCandidateKind.License,
            "NOASSERTION"u8,
            spdx,
            evidence);
        return new ScanComponent(name, version, default, "nuget", DependencyType.Transitive, LicenseStatus.Unknown, $"pkg:nuget/{name}@{version}", default, candidate, []);
    }

    private static LicenseCandidate CreateArtifactCandidate(SpdxLicenseIndex spdx)
        => LicenseCandidateFactory.Create(
            LicenseCandidateSource.PackageArtifact,
            LicenseCandidateKind.License,
            "MIT"u8,
            spdx,
            new LicenseEvidence(
                LicenseEvidenceKind.PackageArtifact,
                PackageArtifact: new PackageArtifactEvidence("pkg:nuget/System.Buffers@4.5.1", "LICENSE", new string('0', 64), "spdx-template", "test-corpus")));

    private sealed class GitHubContentsHandler(
        string document,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        int failuresBeforeSuccess = 0) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string RequestUri { get; private set; } = string.Empty;
        public string Authorization { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            if (CallCount <= failuresBeforeSuccess)
            {
                return Task.FromResult(new HttpResponseMessage(statusCode));
            }

            if (statusCode != HttpStatusCode.OK && failuresBeforeSuccess == 0)
            {
                return Task.FromResult(new HttpResponseMessage(statusCode));
            }

            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(document));
            var response = $$"""{ "encoding": "base64", "content": "{{content}}", "sha": "git-sha", "path": "LICENSE" }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }
}
