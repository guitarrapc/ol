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
    public async Task Client_Fetch_CustomApiBaseUri_SendsRequestToConfiguredMockHost()
    {
        var handler = new GitHubResponseHandler(HttpStatusCode.OK, """{ "license": { "spdx_id": "MIT" } }""");
        var client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create(), new Uri("http://127.0.0.1:19080/"));

        await client.FetchAsync(new SourceRepositoryTarget("owner", "repository", "main"));

        await Assert.That(handler.RequestUri).IsEqualTo("http://127.0.0.1:19080/repos/owner/repository/license?ref=main");
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

    private static string ReadGitHubLicenseFixture()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-license-api-license.json"));
}
