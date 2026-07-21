using Ol.Core;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class GoModuleGraphInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT"], []);

    [Test]
    public async Task Scan_GoResolvedPair_FiltersToBuildListAndPreservesReplacementEvidence()
    {
        var inventory = DependencyInputScanner.ScanBundle(
            [
                await File.ReadAllBytesAsync(GetFixturePath("go-list-modules.json")),
                await File.ReadAllBytesAsync(GetFixturePath("go-mod-graph.txt")),
            ],
            Spdx,
            ScanInputFormat.GoModuleGraph);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.GoModuleGraph);
        await Assert.That(inventory.Contexts).Count().IsEqualTo(1);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("example.com/app");
        await Assert.That(inventory.Contexts[0].Target.IsEmpty).IsTrue();
        await Assert.That(inventory.Contexts[0].Platform.IsEmpty).IsTrue();
        await Assert.That(inventory.Contexts[0].Architecture.IsEmpty).IsTrue();

        await Assert.That(inventory.Components).Count().IsEqualTo(5);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(5);
        await Assert.That(inventory.Edges).Count().IsEqualTo(5);
        await Assert.That(inventory.Components.Any(static component => component.SourceId.ToString() == "example.com/direct@v1.0.0")).IsFalse();
        await Assert.That(inventory.Components.Any(static component => component.Name.ToString() == "go")).IsFalse();

        var direct = FindComponent(inventory, "example.com/direct@v1.2.0");
        var transitive = FindComponent(inventory, "example.com/transitive@v0.0.0-20250101120000-abcdef123456");
        var local = FindComponent(inventory, "example.com/local@v0.5.0");
        var forked = FindComponent(inventory, "example.com/forked@v1.0.0");
        var retracted = FindComponent(inventory, "example.com/retracted@v1.0.0");
        await Assert.That(direct.Purl.ToString()).IsEqualTo("pkg:golang/example.com/direct@v1.2.0");
        await Assert.That(direct.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(transitive.Purl.ToString()).IsEqualTo("pkg:golang/example.com/transitive@v0.0.0-20250101120000-abcdef123456");
        await Assert.That(transitive.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(local.Purl.IsEmpty).IsTrue();
        await Assert.That(forked.Purl.ToString()).IsEqualTo("pkg:golang/github.com/acme/forked@v1.1.0");
        await Assert.That(retracted.Purl.ToString()).IsEqualTo("pkg:golang/example.com/retracted@v1.0.0");

        await Assert.That(FindVariant(inventory, local.SourceId.ToString()).ToString()).IsEqualTo("replace=local");
        await Assert.That(FindVariant(inventory, forked.SourceId.ToString()).ToString()).IsEqualTo("replace=github.com/acme/forked@v1.1.0");
        await Assert.That(FindVariant(inventory, retracted.SourceId.ToString()).ToString()).IsEqualTo("retracted");
        await Assert.That(FindVariant(inventory, transitive.SourceId.ToString()).ToString()).IsEqualTo("indirect");
    }

    [Test]
    public async Task Registry_Default_GoHandlerOwnsCompanionFilesAndIdentity()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("GO-MODULE-GRAPH", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.GoModuleGraph);
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["go-list-modules.json", "go-mod-graph.txt"]);
        await Assert.That(handler.Parser is null).IsTrue();
        await Assert.That(handler.BundleParser is not null).IsTrue();
        await Assert.That(handler.ComponentIdentityComparison).IsEqualTo(DependencyComponentIdentityComparison.OrdinalWithSourceId);
    }

    [Test]
    public async Task ScanBundle_WithMissingGoCompanion_RejectsIncompleteInput()
    {
        byte[][] inputs = [await File.ReadAllBytesAsync(GetFixturePath("go-list-modules.json"))];

        await Assert.That(() => DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.GoModuleGraph)).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("{ \"Path\": \"example.com/app\", \"Main\": true }", "malformed")]
    [Arguments("{ \"Path\": \"example.com/app\", \"Main\": true }{ \"Path\": \"example.com/dependency\" }", "example.com/app example.com/dependency@v1.0.0")]
    [Arguments("{ \"Path\": \"example.com/app\", \"Main\": true }{ \"Path\": \"example.com/dependency\", \"Version\": \"v1.0.0\", \"Error\": { \"Err\": \"failed\" } }", "example.com/app example.com/dependency@v1.0.0")]
    public async Task ScanBundle_WithMalformedGoToolOutput_RejectsInput(string modules, string graph)
    {
        byte[][] inputs = [Encoding.UTF8.GetBytes(modules), Encoding.UTF8.GetBytes(graph)];

        await Assert.That(() => DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.GoModuleGraph)).Throws<JsonException>();
    }

    [Test]
    public async Task ScanBundle_WithEmptyRetractionList_DoesNotMarkModuleRetracted()
    {
        byte[][] inputs =
        [
            "{ \"Path\": \"example.com/app\", \"Main\": true }{ \"Path\": \"example.com/dependency\", \"Version\": \"v1.0.0\", \"Retracted\": [] }"u8.ToArray(),
            "example.com/app example.com/dependency@v1.0.0"u8.ToArray(),
        ];

        var inventory = DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.GoModuleGraph);

        await Assert.That(inventory.OccurrenceVariants).IsEmpty();
    }

    [Test]
    public async Task ScanBundle_WithMultipleIdentitySeparators_RejectsGraph()
    {
        byte[][] inputs =
        [
            "{ \"Path\": \"example.com/app\", \"Main\": true }{ \"Path\": \"example.com/dependency\", \"Version\": \"v1.0.0\" }"u8.ToArray(),
            "example.com/app example.com/dependency@v1.0.0@invalid"u8.ToArray(),
        ];

        await Assert.That(() => DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.GoModuleGraph)).Throws<JsonException>();
    }

    private static ScanComponent FindComponent(DependencyInventory inventory, string sourceId)
        => inventory.Components.Single(component => component.SourceId.ToString() == sourceId);

    private static Utf8Slice FindVariant(DependencyInventory inventory, string sourceId)
    {
        for (var occurrenceIndex = 0; occurrenceIndex < inventory.Occurrences.Length; occurrenceIndex++)
        {
            var occurrence = inventory.Occurrences[occurrenceIndex];
            if (inventory.Components[occurrence.ComponentIndex].SourceId.ToString() != sourceId) continue;
            return inventory.OccurrenceVariants!.Single(variant => variant.OccurrenceIndex == occurrenceIndex).Value;
        }

        throw new InvalidOperationException($"Occurrence not found: {sourceId}");
    }

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
