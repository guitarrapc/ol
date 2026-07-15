using System.Text;
using BenchmarkDotNet.Attributes;
using Ol.Core;

[DisassemblyDiagnoser(maxDepth: 1)]
public class SbomScannerBenchmark
{
    private readonly byte[] cycloneDx = Encoding.UTF8.GetBytes(
        """
        {
          "bomFormat": "CycloneDX",
          "components": [
            {
              "name": "left-pad",
              "version": "1.3.0",
              "purl": "pkg:npm/left-pad@1.3.0",
              "licenses": [
                { "license": { "id": "MIT" } }
              ]
            }
          ]
        }
        """);

    private readonly byte[] cycloneDxUnknown = Encoding.UTF8.GetBytes(
        """
        {
          "bomFormat": "CycloneDX",
          "components": [
            { "name": "unknown", "licenses": [ { "license": { "id": "NOASSERTION" } } ] }
          ]
        }
        """);

    private readonly byte[] cycloneDxExpression = Encoding.UTF8.GetBytes(
        """
        {
          "bomFormat": "CycloneDX",
          "components": [
            { "name": "expression", "licenses": [ { "license": { "expression": "mit OR (apache-2.0 WITH classpath-exception-2.0)" } } ] }
          ]
        }
        """);

    private readonly SpdxLicenseIndex spdx = new(["Apache-2.0", "MIT"], ["Classpath-exception-2.0"]);

    [Benchmark]
    public ScanReport ScanCycloneDx()
    {
        return SbomScanner.Scan(cycloneDx, spdx);
    }

    [Benchmark]
    public DependencyInventory ScanCycloneDxInventory()
    {
        return DependencyInputScanner.Scan(cycloneDx, spdx);
    }

    [Benchmark]
    public ScanReport ScanCycloneDxUnknownLicense()
    {
        return SbomScanner.Scan(cycloneDxUnknown, spdx);
    }

    [Benchmark]
    public ScanReport ScanCycloneDxExpression()
    {
        return SbomScanner.Scan(cycloneDxExpression, spdx);
    }
}
