using System.Net;
using System.Text;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.Licensing;
using Ol.Internals;

namespace Ol.Tests;

public sealed class ScanExecutionArtifactTests
{
    private const string MitLicense = """
        MIT License

        Copyright (c) Example

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    [Test]
    public async Task Execute_NormalScan_CollectsRestoredPackageWithPreparedMatcher()
    {
        var root = CreateTemporaryRoot();
        var packageRoot = Path.Combine(root, "packages");
        var packageDirectory = Path.Combine(packageRoot, "system.buffers", "4.5.1");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "LICENSE.TXT"), MitLicense, Encoding.UTF8);
        var assetsPath = Path.Combine(root, "project.assets.json");
        await File.WriteAllTextAsync(assetsPath, CreateAssets(packageRoot), Encoding.UTF8);

        try
        {
            var prepared = ScanExecution.TryPrepare(
                [root],
                inputFormat: null,
                spdxData: null,
                cacheDir: Path.Combine(root, "cache"),
                noExternalEvidence: false,
                skipEvidencePackages: ["pkg:nuget/"],
                concurrency: 1,
                retry: 0,
                out var preparation,
                out var preparationError);

            await Assert.That(prepared).IsTrue().Because(preparationError);
            var executed = ScanExecution.TryExecute(preparation, refresh: false, noExternalEvidence: false, includeHash: false, out var completed, out var executionError);

            await Assert.That(executed).IsTrue().Because(executionError);
            var component = completed.Result.Components.Single(static item => item.Purl.ToString() == "pkg:nuget/System.Buffers@4.5.1");
            await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
            await Assert.That(component.License.ToString()).IsEqualTo("MIT");
            var artifact = Enumerable.Range(0, component.CandidateCount)
                .Select(component.GetCandidate)
                .Single(static candidate => candidate.Source == LicenseCandidateSource.PackageArtifact)
                .Evidence.PackageArtifact!;
            await Assert.That(artifact.Path).IsEqualTo("LICENSE.TXT");
            await Assert.That(artifact.CorpusVersion).IsEqualTo(preparation.Spdx.LicenseListVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Execute_NormalScan_FetchesDeclaredGitHubFileBeforeRepositoryCollection()
    {
        var root = CreateTemporaryRoot();
        var inputPath = Path.Combine(root, "bom.json");
        await File.WriteAllTextAsync(inputPath, """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                {
                  "bom-ref": "example",
                  "type": "library",
                  "name": "Example",
                  "version": "1.0.0",
                  "purl": "pkg:generic/Example@1.0.0",
                  "licenses": [ { "license": { "name": "See URL", "url": "https://github.com/dotnet/corefx/blob/master/LICENSE.TXT" } } ]
                }
              ]
            }
            """, Encoding.UTF8);
        var handler = new GitHubContentsHandler(MitLicense);

        try
        {
            var prepared = ScanExecution.TryPrepare(
                [inputPath],
                "cyclonedx",
                spdxData: null,
                cacheDir: Path.Combine(root, "cache"),
                noExternalEvidence: false,
                concurrency: 1,
                retry: 0,
                out var preparation,
                out var preparationError);
            await Assert.That(prepared).IsTrue().Because(preparationError);
            var collector = new DeclaredGitHubFileArtifactCollector(
                preparation.Spdx.Matcher,
                preparation.Spdx.Index,
                retryCount: 0,
                new HttpClient(handler),
                GitHubAuthentication.Create(),
                new Uri("https://api.github.test/"));

            var executed = ScanExecution.TryExecute(
                preparation,
                refresh: false,
                noExternalEvidence: false,
                includeHash: false,
                out var completed,
                out var executionError,
                collector);

            await Assert.That(executed).IsTrue().Because(executionError);
            await Assert.That(handler.CallCount).IsEqualTo(1);
            await Assert.That(handler.RequestUri).IsEqualTo("https://api.github.test/repos/dotnet/corefx/contents/LICENSE.TXT?ref=master");
            var component = completed.Result.Components.Single();
            await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
            await Assert.That(component.License.ToString()).IsEqualTo("MIT");
            var artifact = Enumerable.Range(0, component.CandidateCount)
                .Select(component.GetCandidate)
                .Single(static candidate => candidate.Source == LicenseCandidateSource.PackageArtifact)
                .Evidence.PackageArtifact!;
            await Assert.That(artifact.CorpusVersion).IsEqualTo(preparation.Spdx.LicenseListVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Execute_NormalScan_UsesPersistentDeclaredGitHubFileCache()
    {
        var root = CreateTemporaryRoot();
        var inputPath = Path.Combine(root, "bom.json");
        var cacheRoot = Path.Combine(root, "cache");
        const string location = "https://github.com/dotnet/corefx/blob/master/LICENSE.TXT";
        await File.WriteAllTextAsync(inputPath, $$"""
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "components": [
                {
                  "bom-ref": "example",
                  "type": "library",
                  "name": "Example",
                  "version": "1.0.0",
                  "purl": "pkg:generic/Example@1.0.0",
                  "licenses": [ { "license": { "name": "See URL", "url": "{{location}}" } } ]
                }
              ]
            }
            """, Encoding.UTF8);
        DeclaredGitHubFileTarget.TryCreate(location, out var target);
        new DeclaredGitHubFileCache(Path.Combine(cacheRoot, "github-file")).Write(target, HttpStatusCode.OK, Encoding.UTF8.GetBytes(MitLicense));

        try
        {
            var prepared = ScanExecution.TryPrepare([inputPath], "cyclonedx", null, cacheRoot, false, 1, 0, out var preparation, out var preparationError);
            await Assert.That(prepared).IsTrue().Because(preparationError);

            var executed = ScanExecution.TryExecute(preparation, refresh: false, noExternalEvidence: false, includeHash: false, out var completed, out var executionError);

            await Assert.That(executed).IsTrue().Because(executionError);
            await Assert.That(completed.Result.Components.Single().License.ToString()).IsEqualTo("MIT");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-scan-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateAssets(string packageRoot)
        => $$"""
            {
              "version": 3,
              "targets": { "net8.0": { "System.Buffers/4.5.1": { "type": "package" } } },
              "libraries": { "System.Buffers/4.5.1": { "type": "package", "path": "system.buffers/4.5.1" } },
              "packageFolders": { {{System.Text.Json.JsonSerializer.Serialize(Path.EndsInDirectorySeparator(packageRoot) ? packageRoot : packageRoot + Path.DirectorySeparatorChar)}}: {} },
              "projectFileDependencyGroups": { "net8.0": [ "System.Buffers >= 4.5.1" ] },
              "project": {
                "version": "1.0.0",
                "restore": { "projectName": "App", "projectPath": "App.csproj" },
                "frameworks": { "net8.0": { "targetAlias": "net8.0", "dependencies": { "System.Buffers": { "target": "Package", "version": "[4.5.1, )" } } } }
              }
            }
            """;

    private sealed class GitHubContentsHandler(string document) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string RequestUri { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri!.AbsoluteUri;
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(document));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "encoding": "base64", "content": "{{content}}", "sha": "git-sha", "path": "LICENSE.TXT" }""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
