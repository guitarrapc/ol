using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class NpmInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "MIT"], []);

    [Test]
    public async Task Scan_PackageLockV3_PreservesWorkspacesNestedOccurrencesConditionsAndEvidence()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("package-lock.json")),
            Spdx,
            expectedFormat: ScanInputFormat.NpmPackageLock);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.NpmPackageLock);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("3");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(2);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("root-app");
        await Assert.That(inventory.Contexts[1].ProjectOrigin.ToString()).IsEqualTo("packages/a");

        await Assert.That(inventory.Components).Count().IsEqualTo(7);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(9);
        await Assert.That(inventory.Edges).Count().IsEqualTo(8);
        await Assert.That(inventory.Components.Any(static component => component.Name.ToString() == "root-app")).IsFalse();
        await Assert.That(inventory.Components.Any(static component => component.Name.ToString() == "workspace-a")).IsFalse();

        var rootShared = FindComponent(inventory, "node_modules/shared");
        var nestedShared = FindComponent(inventory, "node_modules/alpha/node_modules/shared");
        var workspaceOnly = FindComponent(inventory, "node_modules/workspace-only");
        await Assert.That(rootShared.Purl.ToString()).IsEqualTo("pkg:npm/shared@1.0.0");
        await Assert.That(nestedShared.Purl.ToString()).IsEqualTo(rootShared.Purl.ToString());
        await Assert.That(rootShared.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(nestedShared.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(workspaceOnly.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(nestedShared.License.ToString()).IsEqualTo("Apache-2.0");
        await Assert.That(nestedShared.PrimaryCandidate.Source).IsEqualTo(LicenseCandidateSource.DependencyInput);
        await Assert.That(nestedShared.PrimaryCandidate.Evidence.Kind).IsEqualTo(LicenseEvidenceKind.DependencyInput);
        await Assert.That(nestedShared.PrimaryCandidate.Evidence.DependencyInput!.Format).IsEqualTo("npm-package-lock");
        await Assert.That(nestedShared.PrimaryCandidate.Evidence.DependencyInput!.Field).IsEqualTo("packages[].license");

        var optionalOccurrence = FindOccurrence(inventory, 0, "node_modules/native-addon");
        var devOccurrence = FindOccurrence(inventory, 0, "node_modules/dev-tool");
        var peerOccurrence = FindOccurrence(inventory, 0, "node_modules/peer-host");
        await Assert.That(FindVariant(inventory, optionalOccurrence).ToString()).IsEqualTo("optional;os=linux,!win32;cpu=x64");
        await Assert.That(FindVariant(inventory, devOccurrence).ToString()).IsEqualTo("dev");
        await Assert.That(FindVariant(inventory, peerOccurrence).ToString()).IsEqualTo("peer");
    }

    [Test]
    public async Task Scan_PackageLockV2_AcceptsCompatiblePackagesGraph()
    {
        var input = await File.ReadAllTextAsync(GetFixturePath("package-lock.json"));

        var inventory = DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(input.Replace("\"lockfileVersion\": 3", "\"lockfileVersion\": 2", StringComparison.Ordinal)), Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.NpmPackageLock);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("2");
        await Assert.That(inventory.Components).Count().IsEqualTo(7);
    }

    [Test]
    public async Task Scan_PackageLockWithScopedPackage_EncodesCanonicalNpmPurlAndIgnoresUninstalledEntries()
    {
        var input = Encoding.UTF8.GetBytes(
            """
            {
              "name": "app",
              "lockfileVersion": 3,
              "packages": {
                "": {
                  "name": "app",
                  "dependencies": { "@scope/pkg": "1.2.3" },
                  "optionalDependencies": { "not-installed": "1.0.0" },
                  "peerDependencies": { "peer-not-installed": "2.0.0" }
                },
                "node_modules/@scope/pkg": { "version": "1.2.3", "license": "MIT" }
              }
            }
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx);

        await Assert.That(inventory.Components).Count().IsEqualTo(1);
        await Assert.That(inventory.Components[0].Name.ToString()).IsEqualTo("@scope/pkg");
        await Assert.That(inventory.Components[0].Purl.ToString()).IsEqualTo("pkg:npm/%40scope/pkg@1.2.3");
        await Assert.That(inventory.Components[0].DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(1);
        await Assert.That(inventory.Edges).IsEquivalentTo([new DependencyEdge(0, DependencyOccurrence.ContextRoot, 0)]);
    }

    [Test]
    public async Task Registry_Default_NpmHandlerOwnsSignatureDirectoryDiscoveryAndParser()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("NPM-PACKAGE-LOCK", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.NpmPackageLock);
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["package-lock.json"]);
        await Assert.That(handler.Signature.RequiredMarkers.Length).IsEqualTo(2);
        await Assert.That(handler.ComponentIdentityComparison).IsEqualTo(DependencyComponentIdentityComparison.OrdinalWithSourceId);
    }

    [Test]
    [Arguments("""{ "lockfileVersion": 1, "packages": { "": { "name": "app" } } }""")]
    [Arguments("""{ "lockfileVersion": 3, "packages": {} }""")]
    [Arguments("""{ "lockfileVersion": 3, "packages": { "": [], "node_modules/a": { "version": "1.0.0" } } }""")]
    public async Task Scan_WithUnsupportedOrMalformedPackageLock_RejectsKnownFormat(string json)
    {
        await Assert.That(() => DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(json), Spdx)).Throws<JsonException>();
    }

    [Test]
    [Arguments("""{ "packages": {} }""")]
    [Arguments("""{ "lockfileVersion": "3", "packages": {} }""")]
    [Arguments("""{ "wrapper": { "lockfileVersion": 3, "packages": {} } }""")]
    public async Task Scan_WithIncompleteWrongTypeOrNestedNpmSignature_RejectsUnsupportedInput(string json)
    {
        await Assert.That(() => DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(json), Spdx)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_WithNpmAndNuGetMarkers_RejectsAmbiguousInput()
    {
        var input = Encoding.UTF8.GetBytes("""{ "lockfileVersion": 3, "packages": {}, "version": 3, "targets": {}, "libraries": {}, "project": {} }""");

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx)).Throws<JsonException>();
    }

    private static ScanComponent FindComponent(DependencyInventory inventory, string sourceId)
        => inventory.Components.Single(component => component.SourceId.ToString() == sourceId);

    private static DependencyOccurrence FindOccurrence(DependencyInventory inventory, int contextIndex, string sourceId)
        => inventory.Occurrences.Single(occurrence => occurrence.ContextIndex == contextIndex && inventory.Components[occurrence.ComponentIndex].SourceId.ToString() == sourceId);

    private static Utf8Slice FindVariant(DependencyInventory inventory, DependencyOccurrence occurrence)
    {
        var occurrenceIndex = Array.IndexOf(inventory.Occurrences, occurrence);
        return inventory.OccurrenceVariants!.Single(variant => variant.OccurrenceIndex == occurrenceIndex).Value;
    }

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
