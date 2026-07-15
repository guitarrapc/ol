using System.Text;
using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

[DisassemblyDiagnoser(maxDepth: 1)]
public class DependencyInputScannerBenchmark
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

    private readonly byte[] nugetAssets = Encoding.UTF8.GetBytes(
        """
        {
          "version": 3,
          "targets": {
            "net8.0": {
              "Direct.Package/1.0.0": { "type": "package", "dependencies": { "Shared.Package": "2.0.0" } },
              "Shared.Package/2.0.0": { "type": "package" }
            }
          },
          "libraries": {
            "Direct.Package/1.0.0": { "type": "package" },
            "Shared.Package/2.0.0": { "type": "package" }
          },
          "project": {
            "version": "1.0.0",
            "restore": { "projectName": "App", "projectPath": "src/App/App.csproj" },
            "frameworks": { "net8.0": { "dependencies": { "Direct.Package": { "target": "Package" } } } }
          }
        }
        """);

    private readonly SpdxLicenseIndex spdx = new(["Apache-2.0", "MIT"], ["Classpath-exception-2.0"]);
    private readonly DependencyInputRegistry singleMarkerDetectionRegistry = CreateDetectionRegistry(useNuGetSignature: false);
    private readonly DependencyInputRegistry signatureDetectionRegistry = CreateDetectionRegistry(useNuGetSignature: true);
    private readonly Utf8Slice projectOrigin = "src/App/App.csproj";
    private readonly Utf8Slice target = "net8.0";
    private readonly Utf8Slice directName = "Direct.Package";
    private readonly Utf8Slice directVersion = "1.0.0";
    private readonly Utf8Slice directSourceId = "Direct.Package/1.0.0";
    private readonly Utf8Slice sharedName = "Shared.Package";
    private readonly Utf8Slice sharedVersion = "2.0.0";
    private readonly Utf8Slice sharedSourceId = "Shared.Package/2.0.0";

    [Benchmark]
    public DependencyInventory ScanCycloneDx()
    {
        return DependencyInputScanner.Scan(cycloneDx, spdx);
    }

    [Benchmark]
    public DependencyInventory ScanCycloneDxInventory()
    {
        return DependencyInputScanner.Scan(cycloneDx, spdx);
    }

    [Benchmark]
    public DependencyInventory ScanNuGetAssetsInventory()
    {
        return DependencyInputScanner.Scan(nugetAssets, spdx, expectedFormat: ScanInputFormat.NuGetAssets);
    }

    [Benchmark]
    public DependencyInventory DetectNuGetSingleMarker()
    {
        return DependencyInputScanner.Scan(nugetAssets, spdx, singleMarkerDetectionRegistry);
    }

    [Benchmark]
    public DependencyInventory DetectNuGetSignature()
    {
        return DependencyInputScanner.Scan(nugetAssets, spdx, signatureDetectionRegistry);
    }

    [Benchmark]
    public DependencyInventory CreateNuGetInventoryResultFloor()
    {
        var components = new ScanComponent[2];
        components[0] = new ScanComponent(directName, directVersion, default, "nuget", DependencyType.Direct, LicenseStatus.Unknown, Utf8Slice.FromOwnedBytes("pkg:nuget/Direct.Package@1.0.0"u8.ToArray()), directSourceId, default, [], []);
        components[1] = new ScanComponent(sharedName, sharedVersion, default, "nuget", DependencyType.Transitive, LicenseStatus.Unknown, Utf8Slice.FromOwnedBytes("pkg:nuget/Shared.Package@2.0.0"u8.ToArray()), sharedSourceId, default, [], []);
        return new DependencyInventory(
            default,
            [new DependencyResolutionContext(projectOrigin, target, default, default, default, default)],
            components,
            [new DependencyOccurrence(0, 0), new DependencyOccurrence(0, 1)],
            [new DependencyEdge(0, DependencyOccurrence.ContextRoot, 0), new DependencyEdge(0, 0, 1)]);
    }

    [Benchmark]
    public DependencyInventory ScanCycloneDxUnknownLicense()
    {
        return DependencyInputScanner.Scan(cycloneDxUnknown, spdx);
    }

    [Benchmark]
    public DependencyInventory ScanCycloneDxExpression()
    {
        return DependencyInputScanner.Scan(cycloneDxExpression, spdx);
    }

    private static DependencyInputRegistry CreateDetectionRegistry(bool useNuGetSignature)
    {
        var nugetMarkers = useNuGetSignature
            ? new DependencyInputMarker[]
            {
                new("version"u8.ToArray(), DependencyInputMarkerValueKind.Number),
                new("targets"u8.ToArray(), DependencyInputMarkerValueKind.Object),
                new("libraries"u8.ToArray(), DependencyInputMarkerValueKind.Object),
                new("project"u8.ToArray(), DependencyInputMarkerValueKind.Object),
            }
            : [new("targets"u8.ToArray(), DependencyInputMarkerValueKind.Object)];
        return new DependencyInputRegistry([
            new(ScanInputKind.Sbom, ScanInputFormat.CycloneDx, new(new DependencyInputMarker[] { new("bomFormat"u8.ToArray(), DependencyInputMarkerValueKind.StringEquals, "CycloneDX"u8.ToArray()) }), static (_, _, _, _) => default),
            new(ScanInputKind.Sbom, ScanInputFormat.Spdx, new(new DependencyInputMarker[] { new("spdxVersion"u8.ToArray(), DependencyInputMarkerValueKind.String) }), static (_, _, _, _) => default),
            new(ScanInputKind.PackageManager, ScanInputFormat.NuGetAssets, new(nugetMarkers), static (_, _, _, _) => default),
        ]);
    }
}
