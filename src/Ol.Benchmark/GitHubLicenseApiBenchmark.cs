using BenchmarkDotNet.Attributes;
using Ol.Core;

public class GitHubLicenseApiBenchmark
{
    private readonly GitHubLicenseApiClient client;
    private readonly SourceRepositoryTarget target = new("owner", "repository", "main");

    public GitHubLicenseApiBenchmark()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-license-api-license.json"));
        client = new GitHubLicenseApiClient(new FixtureResponseHandler(fixture), GitHubAuthentication.Create());
    }

    [Benchmark]
    public string FetchFromFixture()
        => client.FetchAsync(target).GetAwaiter().GetResult().License!.Value.SpdxId!;

    private sealed class FixtureResponseHandler(string fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(fixture) });
    }
}
