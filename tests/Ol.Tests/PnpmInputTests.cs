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
    public async Task Scan_PnpmLockV9_ClassifiesStrictlyDevOccurrenceAsDevelopment()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("pnpm-lock.yaml")),
            Spdx,
            expectedFormat: ScanInputFormat.PnpmLock);

        await Assert.That(inventory.UsageDeterminedRanges).IsNotNull();
        await Assert.That(inventory.UsageDeterminedRanges!.Sum(static range => range.Length)).IsEqualTo(inventory.Occurrences.Length);

        var usages = new DependencyUsage[inventory.Components.Length];
        DependencyUsageResolver.Resolve(inventory, usages);

        // Strictly-dev is Development; strictly-optional is a production-optional path and must not be mistaken for it.
        await Assert.That(usages[FindComponentIndex(inventory, "dev-tool@4.0.0")]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(usages[FindComponentIndex(inventory, "optional-native@3.0.0")]).IsEqualTo(DependencyUsage.Runtime);
        await Assert.That(usages[FindComponentIndex(inventory, "shared-package@5.0.0")]).IsEqualTo(DependencyUsage.Runtime);
        await Assert.That(usages[FindComponentIndex(inventory, "direct-package@1.0.0(peer-package@2.0.0)")]).IsEqualTo(DependencyUsage.Runtime);
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
    public async Task Scan_PnpmLockV9_TransitivePeerDependenciesSequence_IsIgnoredAndGraphResolves()
    {
        var input = Encoding.UTF8.GetBytes(
            """
            lockfileVersion: '9.0'

            importers:

              .:
                dependencies:
                  direct-package:
                    specifier: ^1.0.0
                    version: 1.0.0

            packages:

              direct-package@1.0.0:
                resolution: {integrity: sha512-direct}

              child-package@2.0.0:
                resolution: {integrity: sha512-child}

            snapshots:

              direct-package@1.0.0:
                dependencies:
                  child-package: 2.0.0
                transitivePeerDependencies:
                  - '@algolia/client-search'
                  - supports-color

              child-package@2.0.0: {}
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PnpmLock);

        await Assert.That(inventory.Components).Count().IsEqualTo(2);
        await Assert.That(FindComponent(inventory, "direct-package@1.0.0").DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(FindComponent(inventory, "child-package@2.0.0").DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(inventory.Edges).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Scan_PnpmLockV9_BlockSequenceRestrictions_ProduceOsAndCpuVariant()
    {
        var input = Encoding.UTF8.GetBytes(
            """
            lockfileVersion: '9.0'

            importers:

              .:
                dependencies:
                  native-package:
                    specifier: 1.0.0
                    version: 1.0.0

            packages:

              native-package@1.0.0:
                resolution: {integrity: sha512-native}
                cpu:
                  - x64
                  - arm64
                os:
                  - linux
                  - darwin

            snapshots:

              native-package@1.0.0: {}
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PnpmLock);

        await Assert.That(FindVariant(inventory, 0, "native-package@1.0.0").ToString()).IsEqualTo("os=linux,darwin;cpu=x64,arm64");
    }

    [Test]
    public async Task Scan_PnpmLockV9_SequencesUnderUnreadKeys_AreIgnored()
    {
        var input = Encoding.UTF8.GetBytes(
            """
            lockfileVersion: '9.0'

            onlyBuiltDependencies:
              - esbuild

            ignoredOptionalDependencies:
              - fsevents

            importers:

              .:
                dependencies:
                  direct-package:
                    specifier: 1.0.0
                    version: 1.0.0
                unknownFutureKey:
                  - something

            packages:

              direct-package@1.0.0:
                resolution: {integrity: sha512-direct}
                libc:
                  - glibc
                bundledDependencies:
                  - inner-thing

            snapshots:

              direct-package@1.0.0: {}
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PnpmLock);

        await Assert.That(inventory.Components).Count().IsEqualTo(1);
        await Assert.That(FindComponent(inventory, "direct-package@1.0.0").DependencyType).IsEqualTo(DependencyType.Direct);

        // libc and bundledDependencies are sequences the parser skips, not os and cpu restrictions.
        await Assert.That(inventory.OccurrenceVariants).IsEmpty();
    }

    [Test]
    public async Task Scan_PnpmLockV9_SequenceWhereMappingRequired_IsRejected()
    {
        var sectionEntry = Encoding.UTF8.GetBytes(
            """
            lockfileVersion: '9.0'

            importers:
              - direct-package

            packages:

              direct-package@1.0.0:
                resolution: {integrity: sha512-direct}

            snapshots:

              direct-package@1.0.0: {}
            """);

        var importerDependencies = Encoding.UTF8.GetBytes(
            """
            lockfileVersion: '9.0'

            importers:

              .:
                dependencies:
                  - direct-package

            packages:

              direct-package@1.0.0:
                resolution: {integrity: sha512-direct}

            snapshots:

              direct-package@1.0.0: {}
            """);

        var snapshotDependencies = Encoding.UTF8.GetBytes(
            """
            lockfileVersion: '9.0'

            importers:

              .:
                dependencies:
                  direct-package:
                    specifier: 1.0.0
                    version: 1.0.0

            packages:

              direct-package@1.0.0:
                resolution: {integrity: sha512-direct}

            snapshots:

              direct-package@1.0.0:
                dependencies:
                  - child-package
            """);

        await Assert.That(() => DependencyInputScanner.Scan(sectionEntry, Spdx, expectedFormat: ScanInputFormat.PnpmLock)).Throws<JsonException>();
        await Assert.That(() => DependencyInputScanner.Scan(importerDependencies, Spdx, expectedFormat: ScanInputFormat.PnpmLock)).Throws<JsonException>();
        await Assert.That(() => DependencyInputScanner.Scan(snapshotDependencies, Spdx, expectedFormat: ScanInputFormat.PnpmLock)).Throws<JsonException>();
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

    private static int FindComponentIndex(DependencyInventory inventory, string sourceId)
    {
        for (var i = 0; i < inventory.Components.Length; i++)
        {
            if (inventory.Components[i].SourceId.ToString() == sourceId) return i;
        }

        throw new InvalidOperationException($"Component not found: {sourceId}");
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
