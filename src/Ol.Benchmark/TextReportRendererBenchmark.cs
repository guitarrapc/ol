using BenchmarkDotNet.Attributes;
using Ol.Core;
using System.Buffers;
using System.Text;

[MemoryDiagnoser]
public class TextReportRendererBenchmark
{
    private readonly ArrayBufferWriter<byte> buffer = new(4 * 1024);
    private readonly ScanComponent[] components;
    private readonly GroupRow[] groups;
    private readonly DependencyInventory inventory;

    public TextReportRendererBenchmark()
    {
        var spdx = SpdxData.Load(null);
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
    public int WriteText()
    {
        buffer.Clear();
        ReportRenderer.WriteText(buffer, inventory, components, verbose: false);
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int WriteVerboseText()
    {
        buffer.Clear();
        ReportRenderer.WriteText(buffer, inventory, components, verbose: true);
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int WriteGroupedText()
    {
        buffer.Clear();
        ReportRenderer.WriteText(buffer, inventory.Input, groups, "license");
        return buffer.WrittenCount;
    }
}
