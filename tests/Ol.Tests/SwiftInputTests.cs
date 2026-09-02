using Ol.Core;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class SwiftInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT"], []);

    [Test]
    public async Task Scan_SwiftPackageResolvedV3_ProjectsPinsWithoutInventingGraph()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("Package.resolved")),
            Spdx,
            expectedFormat: ScanInputFormat.SwiftPackageResolved);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.SwiftPackageResolved);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("3");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(1);
        await Assert.That(inventory.Contexts[0].ProjectIdentity.ToString()).IsEqualTo("Package.resolved");
        await Assert.That(inventory.Contexts[0].Variant.ToString()).IsEqualTo("origin-hash=fixture-origin");
        await Assert.That(inventory.Components).Count().IsEqualTo(2);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(2);
        await Assert.That(inventory.Edges).Count().IsEqualTo(0);

        var swiftLog = inventory.Components[0];
        await Assert.That(swiftLog.Name.ToString()).IsEqualTo("swift-log");
        await Assert.That(swiftLog.Version.ToString()).IsEqualTo("1.6.2");
        await Assert.That(swiftLog.DependencyType).IsEqualTo(DependencyType.Unknown);
        await Assert.That(swiftLog.Purl.ToString()).IsEqualTo("pkg:swift/github.com/apple/swift-log@1.6.2");
        await Assert.That(swiftLog.SourceId.ToString()).IsEqualTo("swift-log@1.6.2");
        await Assert.That(swiftLog.RepositoryUrl.ToString()).IsEqualTo("https://github.com/apple/swift-log.git");
        await Assert.That(inventory.Occurrences[0].PackageSource).IsEqualTo(PackageSourceKind.Git);
        await Assert.That(inventory.OccurrenceVariants![0].Value.ToString()).IsEqualTo("kind=remoteSourceControl;revision=aaaaaaaa");

        var branchPin = inventory.Components[1];
        await Assert.That(branchPin.Version.ToString()).IsEqualTo("main");
        await Assert.That(branchPin.Purl.ToString()).IsEqualTo("pkg:swift/git.example.test/team/internal-kit@main");
    }

    [Test]
    public async Task Registry_Default_SwiftHandlerOwnsSignatureAndDirectoryDiscovery()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("SWIFT-PACKAGE-RESOLVED", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.SwiftPackageResolved);
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["Package.resolved"]);
        await Assert.That(handler.Signature.RequiredMarkers.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Scan_SwiftPackageResolvedWithInvalidPinState_RejectsInput()
    {
        var input = Encoding.UTF8.GetBytes("""
            { "version": 3, "pins": [ { "identity": "example", "kind": "remoteSourceControl", "location": "https://github.com/example/example.git", "state": {} } ] }
            """);

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.SwiftPackageResolved)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_SwiftPackageResolvedV2_AutoDetectsAndKeepsLocalPinPrivate()
    {
        var input = Encoding.UTF8.GetBytes("""
            { "version": 2, "pins": [ { "identity": "local-kit", "kind": "localSourceControl", "location": "../LocalKit", "state": { "revision": "cccccccc" } } ] }
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.SwiftPackageResolved);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("2");
        await Assert.That(inventory.Components[0].Purl.IsEmpty).IsTrue();
        await Assert.That(inventory.Components[0].RepositoryUrl.IsEmpty).IsTrue();
        await Assert.That(inventory.Occurrences[0].PackageSource).IsEqualTo(PackageSourceKind.LocalPath);
    }

    [Test]
    public async Task Scan_SwiftPackageResolvedWithDuplicateIdentity_RejectsInput()
    {
        var input = Encoding.UTF8.GetBytes("""
            { "version": 3, "pins": [
              { "identity": "example", "kind": "remoteSourceControl", "location": "https://github.com/example/example.git", "state": { "version": "1.0.0" } },
              { "identity": "example", "kind": "remoteSourceControl", "location": "https://github.com/example/example.git", "state": { "version": "2.0.0" } }
            ] }
            """);

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.SwiftPackageResolved)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_SwiftPackageResolvedWithRegistryPin_AcceptsDocumentedEmptyLocationWithoutSourcePurl()
    {
        var input = Encoding.UTF8.GetBytes("""
            { "version": 3, "pins": [ { "identity": "mona.LinkedList", "kind": "registry", "location": "", "state": { "version": "1.0.0" } } ] }
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.SwiftPackageResolved);

        await Assert.That(inventory.Components[0].Name.ToString()).IsEqualTo("mona.LinkedList");
        await Assert.That(inventory.Components[0].Purl.IsEmpty).IsTrue();
        await Assert.That(inventory.Components[0].RepositoryUrl.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Scan_SwiftPackageResolvedV3WithNullOriginHash_AcceptsOptionalField()
    {
        var input = Encoding.UTF8.GetBytes("""{ "version": 3, "originHash": null, "pins": [] }""");

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.SwiftPackageResolved);

        await Assert.That(inventory.Contexts[0].Variant.IsEmpty).IsTrue();
        await Assert.That(inventory.Components).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Scan_SwiftPackageResolvedWithCredentialedLocation_DoesNotExposeRemoteIdentity()
    {
        var input = Encoding.UTF8.GetBytes("""
            { "version": 3, "pins": [ { "identity": "private", "kind": "remoteSourceControl", "location": "https://token@github.com/example/private.git", "state": { "version": "1.0.0" } } ] }
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.SwiftPackageResolved);

        await Assert.That(inventory.Components[0].Purl.IsEmpty).IsTrue();
        await Assert.That(inventory.Components[0].RepositoryUrl.IsEmpty).IsTrue();
    }

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
