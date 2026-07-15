using System.Text;
using Ol.Core;

namespace Ol.Tests;

public sealed class DependencyInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT"], []);

    [Test]
    public async Task Scan_CycloneDxGraph_ProducesInventoryWithOccurrencesAndEdges()
    {
        var input = Encoding.UTF8.GetBytes(
            """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "metadata": { "component": { "bom-ref": "root", "name": "app", "version": "1.0.0" } },
              "components": [
                { "bom-ref": "a", "name": "a", "version": "1.0.0", "purl": "pkg:npm/a@1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] },
                { "bom-ref": "b", "name": "b", "version": "2.0.0", "purl": "pkg:npm/b@2.0.0" }
              ],
              "dependencies": [
                { "ref": "root", "dependsOn": [ "a" ] },
                { "ref": "a", "dependsOn": [ "b" ] }
              ]
            }
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.Sbom);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.CycloneDx);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("1.6");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(0);
        await Assert.That(inventory.Components).Count().IsEqualTo(3);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(3);
        await Assert.That(inventory.Edges).Count().IsEqualTo(2);
        await Assert.That(inventory.Components[0].DependencyType).IsEqualTo(DependencyType.Root);
        await Assert.That(inventory.Components[1].DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(inventory.Components[2].DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(inventory.Occurrences[0]).IsEqualTo(new DependencyOccurrence(DependencyOccurrence.UnspecifiedContext, 0));
        await Assert.That(inventory.Occurrences[1]).IsEqualTo(new DependencyOccurrence(DependencyOccurrence.UnspecifiedContext, 1));
        await Assert.That(inventory.Occurrences[2]).IsEqualTo(new DependencyOccurrence(DependencyOccurrence.UnspecifiedContext, 2));
        await Assert.That(inventory.Edges[0]).IsEqualTo(new DependencyEdge(DependencyOccurrence.UnspecifiedContext, 0, 1));
        await Assert.That(inventory.Edges[1]).IsEqualTo(new DependencyEdge(DependencyOccurrence.UnspecifiedContext, 1, 2));
    }

    [Test]
    public async Task Scan_RegisteredDependencyInput_UsesHandlerOwnedFormatAndParser()
    {
        var format = new ScanInputFormat("test-json", "test-json-parser", "Test JSON");
        var registry = new DependencyInputRegistry([
            new DependencyInputHandler(
                ScanInputKind.Sbom,
                format,
                new(new DependencyInputMarker[] { new("testFormat"u8.ToArray(), DependencyInputMarkerValueKind.StringEquals, "test"u8.ToArray()) }),
                static (_, _, _, _) => new DependencyInventory(default, [], [], [], [])),
        ]);

        var inventory = DependencyInputScanner.Scan(Encoding.UTF8.GetBytes("""{ "testFormat": "test" }"""), Spdx, registry);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.Sbom);
        await Assert.That(inventory.Input.Format).IsEqualTo(format);
    }

    [Test]
    public async Task Scan_SpdxGraph_ProducesInventoryWithOccurrencesAndEdges()
    {
        var input = Encoding.UTF8.GetBytes(
            """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-App", "name": "app", "licenseDeclared": "MIT" },
                { "SPDXID": "SPDXRef-Direct", "name": "direct", "licenseDeclared": "MIT" },
                { "SPDXID": "SPDXRef-Transitive", "name": "transitive", "licenseDeclared": "MIT" }
              ],
              "relationships": [
                { "spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES", "relatedSpdxElement": "SPDXRef-App" },
                { "spdxElementId": "SPDXRef-App", "relationshipType": "DEPENDS_ON", "relatedSpdxElement": "SPDXRef-Direct" },
                { "spdxElementId": "SPDXRef-Transitive", "relationshipType": "DEPENDENCY_OF", "relatedSpdxElement": "SPDXRef-Direct" }
              ]
            }
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.Spdx);
        await Assert.That(inventory.Components[0].DependencyType).IsEqualTo(DependencyType.Root);
        await Assert.That(inventory.Components[1].DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(inventory.Components[2].DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(inventory.Edges).Count().IsEqualTo(2);
        await Assert.That(inventory.Edges[0]).IsEqualTo(new DependencyEdge(DependencyOccurrence.UnspecifiedContext, 0, 1));
        await Assert.That(inventory.Edges[1]).IsEqualTo(new DependencyEdge(DependencyOccurrence.UnspecifiedContext, 1, 2));
    }

    [Test]
    public async Task Scan_WithBothRegisteredFormatMarkers_RejectsAmbiguousInput()
    {
        var input = Encoding.UTF8.GetBytes("""{ "bomFormat": "CycloneDX", "spdxVersion": "SPDX-2.3", "components": [], "packages": [] }""");

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx)).Throws<System.Text.Json.JsonException>();
    }

    [Test]
    public async Task Scan_WithNoRegisteredFormatMarker_RejectsUnsupportedInput()
    {
        var input = Encoding.UTF8.GetBytes("""{ "bomFormat": "other", "components": [] }""");

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx)).Throws<System.Text.Json.JsonException>();
    }

    [Test]
    public async Task Scan_WithInvalidCycloneMarkerAndValidSpdxMarker_SelectsSpdx()
    {
        var input = Encoding.UTF8.GetBytes("""{ "bomFormat": "other", "spdxVersion": "SPDX-2.3", "packages": [] }""");

        var inventory = DependencyInputScanner.Scan(input, Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.Spdx);
    }

    [Test]
    public async Task Scan_WithEscapedMarkerPropertyName_DetectsCycloneDx()
    {
        var input = Encoding.UTF8.GetBytes("""{ "bom\u0046ormat": "CycloneDX", "components": [] }""");

        var inventory = DependencyInputScanner.Scan(input, Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.CycloneDx);
    }
}
