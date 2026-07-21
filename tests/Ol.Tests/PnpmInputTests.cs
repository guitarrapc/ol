using Ol.Core;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class PnpmInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT"], []);

    [Test]
    public async Task Scan_PnpmLockV9_PreservesImportersSnapshotGraphAndVariants()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("pnpm-lock.yaml")),
            Spdx,
            expectedFormat: ScanInputFormat.PnpmLock);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.PnpmLock);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("9.0");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(2);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo(".");
        await Assert.That(inventory.Contexts[1].ProjectOrigin.ToString()).IsEqualTo("packages/a");

        await Assert.That(inventory.Components).Count().IsEqualTo(5);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(6);
        await Assert.That(inventory.Edges).Count().IsEqualTo(6);
        await Assert.That(inventory.Components.Any(static component => component.Name.ToString() == "workspace-a")).IsFalse();

        var direct = FindComponent(inventory, "direct-package@1.0.0(peer-package@2.0.0)");
        var shared = FindComponent(inventory, "shared-package@5.0.0");
        await Assert.That(direct.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(direct.Purl.ToString()).IsEqualTo("pkg:npm/direct-package@1.0.0");
        await Assert.That(shared.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(FindVariant(inventory, 0, direct.SourceId.ToString()).ToString()).IsEqualTo("peer=peer-package@2.0.0");
        await Assert.That(FindVariant(inventory, 0, "optional-native@3.0.0").ToString()).IsEqualTo("optional;os=linux;cpu=x64");
        await Assert.That(FindVariant(inventory, 0, "dev-tool@4.0.0").ToString()).IsEqualTo("dev");
    }

    [Test]
    public async Task Scan_PnpmLock_AutoDetectsAndRejectsUnsupportedKnownVersion()
    {
        var input = await File.ReadAllBytesAsync(GetFixturePath("pnpm-lock.yaml"));
        var inventory = DependencyInputScanner.Scan(input, Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.PnpmLock);
        var unsupported = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).Replace("'9.0'", "'6.0'", StringComparison.Ordinal));
        await Assert.That(() => DependencyInputScanner.Scan(unsupported, Spdx)).Throws<JsonException>();
    }

    [Test]
    public async Task Registry_Default_PnpmHandlerOwnsDetectorAndDirectoryDiscovery()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("PNPM-LOCK", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["pnpm-lock.yaml"]);
        await Assert.That(handler.Detector).IsNotNull();
        await Assert.That(handler.Signature.RequiredMarkers.Length).IsEqualTo(0);
        await Assert.That(handler.ComponentIdentityComparison).IsEqualTo(DependencyComponentIdentityComparison.OrdinalWithSourceId);
    }

    private static ScanComponent FindComponent(DependencyInventory inventory, string sourceId)
        => inventory.Components.Single(component => component.SourceId.ToString() == sourceId);

    private static Utf8Slice FindVariant(DependencyInventory inventory, int contextIndex, string sourceId)
    {
        for (var i = 0; i < inventory.Occurrences.Length; i++)
        {
            var occurrence = inventory.Occurrences[i];
            if (occurrence.ContextIndex == contextIndex && inventory.Components[occurrence.ComponentIndex].SourceId.ToString() == sourceId)
            {
                return inventory.OccurrenceVariants!.Single(variant => variant.OccurrenceIndex == i).Value;
            }
        }

        throw new InvalidOperationException($"Occurrence not found: {contextIndex}/{sourceId}");
    }

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
