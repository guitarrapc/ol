using Ol.Core;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class CocoaPodsInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT"], []);

    [Test]
    public async Task Scan_PodfileLock_ProjectsRootPodGraphAndPublicIdentity()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("Podfile.lock")),
            Spdx,
            expectedFormat: ScanInputFormat.CocoaPodsLock);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.CocoaPodsLock);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("1.16.2");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(1);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("Podfile.lock");
        await Assert.That(inventory.Contexts[0].Variant.ToString()).IsEqualTo("cocoapods=1.16.2");
        await Assert.That(inventory.Components).Count().IsEqualTo(2);

        var moya = FindComponent(inventory, "Moya@15.0.0");
        var alamofire = FindComponent(inventory, "Alamofire@5.10.2");
        await Assert.That(moya.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(alamofire.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(moya.Purl.ToString()).IsEqualTo("pkg:cocoapods/Moya@15.0.0");
        await Assert.That(alamofire.Purl.ToString()).IsEqualTo("pkg:cocoapods/Alamofire@5.10.2");
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(2);
        await Assert.That(inventory.Edges).Count().IsEqualTo(2);
        await Assert.That(HasEdge(inventory, DependencyOccurrence.ContextRoot, FindOccurrence(inventory, moya))).IsTrue();
        await Assert.That(HasEdge(inventory, FindOccurrence(inventory, moya), FindOccurrence(inventory, alamofire))).IsTrue();
    }

    [Test]
    public async Task Registry_Default_CocoaPodsHandlerOwnsDetectorAndDirectoryDiscovery()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("COCOAPODS-LOCK", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.CocoaPodsLock);
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["Podfile.lock"]);
        await Assert.That(handler.Detector is not null).IsTrue();
    }

    [Test]
    public async Task Scan_PodfileLockWithPrivateSpecRepo_DoesNotCreatePublicCocoaPodsIdentity()
    {
        var input = Encoding.UTF8.GetBytes("""
            PODS:
              - PrivatePod (1.0.0)
            DEPENDENCIES:
              - PrivatePod
            SPEC REPOS:
              https://specs.example.test/:
                - PrivatePod
            COCOAPODS: 1.16.2
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.CocoaPodsLock);

        await Assert.That(inventory.Components[0].Purl.IsEmpty).IsTrue();
        await Assert.That(inventory.Components[0].Ecosystem).IsEqualTo("-");
        await Assert.That(inventory.OccurrenceVariants![0].Value.ToString()).IsEqualTo("source=private-spec-repo");
    }

    [Test]
    public async Task Scan_PodfileLockWithPublicCdnUrl_CreatesCocoaPodsIdentity()
    {
        var input = Encoding.UTF8.GetBytes("""
            PODS:
              - PublicPod (1.0.0)
            DEPENDENCIES:
              - PublicPod
            SPEC REPOS:
              https://cdn.cocoapods.org/:
                - PublicPod
            COCOAPODS: 1.16.2
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.CocoaPodsLock);

        await Assert.That(inventory.Components[0].Purl.ToString()).IsEqualTo("pkg:cocoapods/PublicPod@1.0.0");
    }

    [Test]
    public async Task Scan_PodfileLockWithMissingPodVersion_RejectsInput()
    {
        var input = Encoding.UTF8.GetBytes("""
            PODS:
              - InvalidPod
            DEPENDENCIES:
              - InvalidPod
            COCOAPODS: 1.16.2
            """);

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.CocoaPodsLock)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_PodfileLock_AutoDetectsRegisteredFormat()
    {
        var inventory = DependencyInputScanner.Scan(await File.ReadAllBytesAsync(GetFixturePath("Podfile.lock")), Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.CocoaPodsLock);
    }

    [Test]
    public async Task Scan_PodfileLockWithExternalSource_DoesNotCreateCocoaPodsCdnIdentity()
    {
        var input = Encoding.UTF8.GetBytes("""
            PODS:
              - ExternalPod (1.0.0)
            DEPENDENCIES:
              - ExternalPod
            EXTERNAL SOURCES:
              ExternalPod:
                :git: https://github.com/example/external-pod.git
            COCOAPODS: 1.16.2
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.CocoaPodsLock);

        await Assert.That(inventory.Components[0].Purl.IsEmpty).IsTrue();
        await Assert.That(inventory.OccurrenceVariants![0].Value.ToString()).IsEqualTo("source=external");
    }

    private static ScanComponent FindComponent(DependencyInventory inventory, string sourceId)
        => inventory.Components.Single(component => component.SourceId.ToString() == sourceId);

    private static int FindOccurrence(DependencyInventory inventory, ScanComponent component)
        => Array.FindIndex(inventory.Occurrences, occurrence => inventory.Components[occurrence.ComponentIndex].SourceId.Equals(component.SourceId));

    private static bool HasEdge(DependencyInventory inventory, int from, int to)
        => inventory.Edges.Any(edge => edge.FromOccurrenceIndex == from && edge.ToOccurrenceIndex == to);

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
