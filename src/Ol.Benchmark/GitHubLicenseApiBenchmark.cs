using BenchmarkDotNet.Attributes;
using Ol.Core.GitHub;

public class GitHubLicenseApiBenchmark : IDisposable
{
    private readonly GitHubLicenseApiClient client;
    private readonly FixtureResponseHandler handler;
    private readonly SourceRepositoryTarget target = new("owner", "repository", "main");

    public GitHubLicenseApiBenchmark()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-license-api-license.json"));
        handler = new FixtureResponseHandler(fixture);
        client = new GitHubLicenseApiClient(handler, GitHubAuthentication.Create());
    }

    [Benchmark]
    public string FetchFromFixture()
        => client.FetchAsync(target).GetAwaiter().GetResult().License!.Value.SpdxId!;

    public void Dispose() => handler.Dispose();

    private sealed class FixtureResponseHandler(string fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(fixture) });
    }
}
