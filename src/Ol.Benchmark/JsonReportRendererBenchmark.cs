using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.PackageManagers;
using Ol.Internals;
using System.Buffers;
using System.Text;
using System.Text.Json;

[MemoryDiagnoser]
public class JsonReportRendererBenchmark
{
    private readonly ArrayBufferWriter<byte> buffer = new(4 * 1024);
    private readonly ScanComponent[] components;
    private readonly GroupRow[] groups;
    private readonly DependencyInventory inventory;
    private readonly PackageArtifactCollectionSummary packageArtifactSummary = new(1, 1, 1);
    private readonly DeclaredGitHubFileArtifactCollectionSummary declaredGitHubFileSummary = new(1, 0, 1, 0, 1, 1, 0);
    private readonly PackageMetadataSummary packageMetadataSummary = new(0, 0, 0, 0, 0, 0, 0, 0, 1, 0);
    private readonly SourceRepositorySummary sourceRepositorySummary = new(0, 0, 0, 0, 0, 0, "none", 1, 0);
    private readonly ScanReportScope scope = new(ExternalEvidenceCollected: true, DependencyFilter: null, ExcludedCount: 0, ExcludedUnknownCount: 0);
    private readonly SpdxData spdx;
    private readonly Utf8JsonWriter writer;

    public JsonReportRendererBenchmark()
    {
        writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
        spdx = SpdxData.Load(null);
        inventory = DependencyInputScanner.Scan(
            Encoding.UTF8.GetBytes(
                """
                {
                  "bomFormat": "CycloneDX",
                  "specVersion": "1.6",
                  "components": [
                    { "bom-ref": "pkg:npm/example@1.0.0", "name": "example", "version": "1.0.0", "purl": "pkg:npm/example@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] }
                  ]
                }
                """),
            spdx.Index);
        components = inventory.Components;
        groups = ScanView.Group(components, null, components.Length, "license");
    }

    [Benchmark]
    public int WriteJson()
    {
        buffer.Clear();
        writer.Reset(buffer);
        ReportRenderer.WriteJson(writer, inventory, components, spdx, packageArtifactSummary, declaredGitHubFileSummary, packageMetadataSummary, sourceRepositorySummary, scope);
        writer.Flush();
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int WriteGroupedJson()
    {
        buffer.Clear();
        writer.Reset(buffer);
        ReportRenderer.WriteJson(writer, inventory, groups, "license", spdx, packageArtifactSummary, declaredGitHubFileSummary, packageMetadataSummary, sourceRepositorySummary, scope);
        writer.Flush();
        return buffer.WrittenCount;
    }
}
