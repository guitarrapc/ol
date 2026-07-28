using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class CargoInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "MIT"], []);

    [Test]
    public async Task Scan_CargoMetadataV1_PreservesWorkspaceGraphSourcesFeaturesAndTargets()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("cargo-metadata.json")),
            Spdx,
            expectedFormat: ScanInputFormat.CargoMetadata);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.CargoMetadata);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("1");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(2);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("workspace-app");
        await Assert.That(inventory.Contexts[0].Variant.ToString()).IsEqualTo("features=default,cli");
        await Assert.That(inventory.Contexts[1].ProjectOrigin.ToString()).IsEqualTo("workspace-tool");

        await Assert.That(inventory.Components).Count().IsEqualTo(4);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(6);
        await Assert.That(inventory.Edges).Count().IsEqualTo(5);
        await Assert.That(inventory.Components.Any(static component => component.Name.ToString() == "workspace-app")).IsFalse();

        var serde = FindComponent(inventory, "registry+https://github.com/rust-lang/crates.io-index#serde@1.0.0");
        var itoa = FindComponent(inventory, "registry+https://github.com/rust-lang/crates.io-index#itoa@1.0.0");
        var git = FindComponent(inventory, "git+https://github.com/example/git-dep?rev=abc#abc");
        var local = FindComponent(inventory, "path+file:///repo/vendor/local-dep#0.2.0");
        await Assert.That(serde.Purl.ToString()).IsEqualTo("pkg:cargo/serde@1.0.0");
        await Assert.That(serde.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(serde.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(serde.PrimaryCandidate.Evidence.DependencyInput!.Format).IsEqualTo("cargo-metadata");
        await Assert.That(itoa.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(git.Purl.IsEmpty).IsTrue();
        await Assert.That(git.RepositoryUrl.ToString()).IsEqualTo("https://github.com/example/git-dep");
        await Assert.That(local.Purl.IsEmpty).IsTrue();

        await Assert.That(FindVariant(inventory, 0, serde.SourceId.ToString()).ToString()).IsEqualTo("source=registry;features=derive");
        await Assert.That(FindVariant(inventory, 0, git.SourceId.ToString()).ToString()).IsEqualTo("source=git;target=cfg(unix)");
        await Assert.That(FindVariant(inventory, 1, itoa.SourceId.ToString()).ToString()).IsEqualTo("source=registry;kind=build;target=cfg(target_os = \"linux\")");
        await Assert.That(FindVariant(inventory, 1, local.SourceId.ToString()).ToString()).IsEqualTo("source=path");
    }

    [Test]
    public async Task Scan_CargoMetadataV1_AutoDetectsRegisteredFormat()
    {
        var inventory = DependencyInputScanner.Scan(await File.ReadAllBytesAsync(GetFixturePath("cargo-metadata.json")), Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.CargoMetadata);
    }

    [Test]
    public async Task Registry_Default_CargoHandlerOwnsSignatureDirectoryDiscoveryAndIdentity()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("CARGO-METADATA", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.CargoMetadata);
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["cargo-metadata.json"]);
        await Assert.That(handler.Signature.RequiredMarkers.Length).IsEqualTo(6);
        await Assert.That(handler.ComponentIdentityComparison).IsEqualTo(DependencyComponentIdentityComparison.OrdinalWithSourceId);
    }

    [Test]
    [Arguments("2", "{}")]
    [Arguments("1", "null")]
    public async Task Scan_WithUnsupportedOrUnresolvedCargoMetadata_RejectsKnownFormat(string version, string resolve)
    {
        var json = $$"""
            {
              "packages": [],
              "workspace_members": [],
              "resolve": {{resolve}},
              "target_directory": "/repo/target",
              "version": {{version}},
              "workspace_root": "/repo"
            }
            """;

        await Assert.That(() => DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(json), Spdx, expectedFormat: ScanInputFormat.CargoMetadata)).Throws<JsonException>();
    }

    [Test]
    [Arguments("{}", "[]")]
    [Arguments("[]", "[]")]
    [Arguments("[]", "null")]
    public async Task Scan_WithWrongCargoSignatureTypes_RejectsUnsupportedInput(string packages, string resolve)
    {
        var json = $$"""
            {
              "packages": {{packages}},
              "workspace_members": [],
              "resolve": {{resolve}},
              "target_directory": "/repo/target",
              "version": 1,
              "workspace_root": "/repo"
            }
            """;

        await Assert.That(() => DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(json), Spdx)).Throws<JsonException>();
    }

    private static ScanComponent FindComponent(DependencyInventory inventory, string sourceId)
        => inventory.Components.Single(component => component.SourceId.ToString() == sourceId);

    private static Utf8Slice FindVariant(DependencyInventory inventory, int contextIndex, string sourceId)
    {
        for (var occurrenceIndex = 0; occurrenceIndex < inventory.Occurrences.Length; occurrenceIndex++)
        {
            var occurrence = inventory.Occurrences[occurrenceIndex];
            if (occurrence.ContextIndex != contextIndex || inventory.Components[occurrence.ComponentIndex].SourceId.ToString() != sourceId) continue;
            return inventory.OccurrenceVariants!.Single(variant => variant.OccurrenceIndex == occurrenceIndex).Value;
        }

        throw new InvalidOperationException($"Occurrence not found: {contextIndex}/{sourceId}");
    }

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
