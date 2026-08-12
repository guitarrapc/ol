using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.Licensing;
using Ol.Core.SourceRepository;
using Ol.Core.Spdx;

[MemoryDiagnoser]
public class DeclaredGitHubFileArtifactCollectorBenchmark : IDisposable
{
    private readonly ScanComponent[] components = new ScanComponent[1];
    private readonly ScanComponent[] existingArtifactComponents = new ScanComponent[1];
    private readonly ScanComponent[] unrelatedComponents = new ScanComponent[10_000];
    private readonly DeclaredGitHubFileArtifactCollector collector;
    private readonly GitHubContentsHandler handler;
    private readonly HttpClient httpClient;
    private readonly ScanComponent template;

    public DeclaredGitHubFileArtifactCollectorBenchmark()
    {
        var spdx = new SpdxLicenseIndex(["MIT"], []);
        var matcher = new SpdxLicenseTextMatcher("benchmark", [new("MIT", "MIT License")]);
        handler = new GitHubContentsHandler();
        httpClient = new HttpClient(handler, disposeHandler: false);
        collector = new DeclaredGitHubFileArtifactCollector(
            matcher,
            spdx,
            retryCount: 0,
            httpClient,
            GitHubAuthentication.Create(),
            new Uri("https://api.github.test/"));
        template = CreateComponent(spdx, "https://github.com/dotnet/corefx/blob/master/LICENSE.TXT");
        existingArtifactComponents[0] = LicenseReconciler.AddCandidate(template, LicenseCandidateFactory.Create(
            LicenseCandidateSource.PackageArtifact,
            LicenseCandidateKind.License,
            "MIT"u8,
            spdx,
            new LicenseEvidence(
                LicenseEvidenceKind.PackageArtifact,
                PackageArtifact: new PackageArtifactEvidence("pkg:nuget/Example@1.0.0", "LICENSE", new string('0', 64), "spdx-template", "benchmark"))));
        for (var index = 0; index < unrelatedComponents.Length; index++)
        {
            unrelatedComponents[index] = CreateComponent(spdx, "https://example.test/LICENSE");
        }
    }

    [Benchmark]
    public int FetchOneMatchedDocument()
    {
        components[0] = template;
        return collector.EnrichAsync(components, concurrency: 1).Result.Summary.MatchedCount;
    }

    [Benchmark]
    public int SkipOneExistingArtifact()
        => collector.EnrichAsync(existingArtifactComponents, concurrency: 1).Result.Summary.TargetCount;

    [Benchmark]
    public int PlanTenThousandUnrelatedLocations()
        => collector.EnrichAsync(unrelatedComponents, concurrency: 8).Result.Summary.TargetCount;

    public void Dispose()
    {
        httpClient.Dispose();
        handler.Dispose();
    }

    private static ScanComponent CreateComponent(SpdxLicenseIndex spdx, string location)
    {
        var candidate = LicenseCandidateFactory.Create(
            LicenseCandidateSource.NuGetRegistry,
            LicenseCandidateKind.License,
            "NOASSERTION"u8,
            spdx,
            new LicenseEvidence(
                LicenseEvidenceKind.PackageRegistry,
                DeclaredReference: new(DeclaredLicenseReferenceKind.Location, Utf8Slice.FromString(location))));
        return new ScanComponent("Example", "1.0.0", default, "nuget", DependencyType.Transitive, LicenseStatus.Unknown, "pkg:nuget/Example@1.0.0", default, candidate, []);
    }

    private sealed class GitHubContentsHandler : HttpMessageHandler
    {
        private static readonly string Response = $$"""{ "encoding": "base64", "content": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes("MIT License"))}}" }""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Response, Encoding.UTF8, "application/json"),
            });
    }
}
