using System.Buffers;
using System.Text.Json;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Internals;

namespace Ol.Tests;

public sealed class PackageArtifactEvidenceTests
{
    [Test]
    public async Task Create_LicenseDocument_RetainsLogicalProvenanceAndContentHash()
    {
        var evidence = PackageArtifactEvidence.Create(
            "pkg:nuget/System.Buffers@4.5.1",
            "LICENSE.TXT",
            "license body"u8,
            "3.28.0");

        await Assert.That(evidence.Artifact).IsEqualTo("pkg:nuget/System.Buffers@4.5.1");
        await Assert.That(evidence.Path).IsEqualTo("LICENSE.TXT");
        await Assert.That(evidence.ContentSha256).IsEqualTo("debcd294116433fd2825a64970bf1ea069ebad9fcc77fd36b6ccc7d3f6148b49");
        await Assert.That(evidence.Matcher).IsEqualTo("spdx-template");
        await Assert.That(evidence.CorpusVersion).IsEqualTo("3.28.0");
        await Assert.That(LicenseCandidateSource.PackageArtifact.ToUtf8().ToArray()).IsEquivalentTo("package-artifact"u8.ToArray());
        await Assert.That(LicenseCandidateIdentifiers.ParseSource("package-artifact"u8)).IsEqualTo(LicenseCandidateSource.PackageArtifact);
    }

    [Test]
    public async Task RenderJson_PackageArtifactEvidence_WritesAuditableFieldsWithoutLocalPath()
    {
        var artifact = PackageArtifactEvidence.Create("pkg:nuget/System.Buffers@4.5.1", "LICENSE.TXT", "license body"u8, "3.28.0");
        var candidate = new LicenseCandidate(
            LicenseCandidateSource.PackageArtifact,
            LicenseCandidateKind.License,
            default,
            Utf8Slice.FromOwnedBytes("MIT"u8.ToArray()),
            LicenseStatus.Matched,
            false,
            LicenseCandidateWarnings.None,
            new LicenseEvidence(LicenseEvidenceKind.PackageArtifact, PackageArtifact: artifact));
        var component = new ScanComponent(
            Utf8Slice.FromOwnedBytes("System.Buffers"u8.ToArray()),
            Utf8Slice.FromOwnedBytes("4.5.1"u8.ToArray()),
            Utf8Slice.FromOwnedBytes("MIT"u8.ToArray()),
            "nuget",
            DependencyType.Transitive,
            LicenseStatus.Matched,
            Utf8Slice.FromOwnedBytes("pkg:nuget/System.Buffers@4.5.1"u8.ToArray()),
            default,
            candidate,
            []);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ReportRenderer.WriteJson(
                writer,
                new DependencyInventory(default, [], [component], [], []),
                [component],
                SpdxData.Load(null),
                new PackageMetadataSummary(0, 0, 0, 0, 0, 0, 0, 1, 0),
                new SourceRepositorySummary(0, 0, 0, 0, 0, 0, "none", 1, 0),
                new ScanReportScope(true, null, 0, 0));
        }

        using var report = JsonDocument.Parse(buffer.WrittenMemory);
        var evidence = report.RootElement.GetProperty("components")[0].GetProperty("licenseCandidates")[0].GetProperty("evidence");
        await Assert.That(evidence.GetProperty("type").GetString()).IsEqualTo("package-artifact");
        await Assert.That(evidence.GetProperty("artifact").GetString()).IsEqualTo("pkg:nuget/System.Buffers@4.5.1");
        await Assert.That(evidence.GetProperty("path").GetString()).IsEqualTo("LICENSE.TXT");
        await Assert.That(evidence.GetProperty("contentSha256").GetString()).IsEqualTo(artifact.ContentSha256);
        await Assert.That(evidence.GetProperty("matcher").GetString()).IsEqualTo("spdx-template");
        await Assert.That(evidence.GetProperty("corpusVersion").GetString()).IsEqualTo("3.28.0");
        await Assert.That(buffer.WrittenSpan.IndexOf("C:\\"u8)).IsEqualTo(-1);
    }
}
