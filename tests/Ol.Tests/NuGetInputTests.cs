using System.Text.Json;
using Ol.Core;

namespace Ol.Tests;

public sealed class NuGetInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT"], []);

    [Test]
    public async Task Scan_ProjectAssetsWithTargetsAndRid_PreservesContextsOccurrencesAndPackageGraph()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("nuget-project.assets.json")),
            Spdx,
            expectedFormat: ScanInputFormat.NuGetAssets);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.NuGetAssets);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("3");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(2);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("/private/src/App/App.csproj");
        await Assert.That(inventory.Contexts[0].Target.ToString()).IsEqualTo("net8.0");
        await Assert.That(inventory.Contexts[0].Runtime.IsEmpty).IsTrue();
        await Assert.That(inventory.Contexts[1].Target.ToString()).IsEqualTo("net8.0");
        await Assert.That(inventory.Contexts[1].Runtime.ToString()).IsEqualTo("win-x64");
        await Assert.That(inventory.Contexts[1].Platform.IsEmpty).IsTrue();
        await Assert.That(inventory.Contexts[1].Architecture.IsEmpty).IsTrue();

        await Assert.That(inventory.Occurrences).Count().IsEqualTo(6);
        await Assert.That(inventory.Edges).Count().IsEqualTo(5);
        await Assert.That(inventory.Components.Any(static component => component.Name.ToString() == "App")).IsFalse();
        await Assert.That(inventory.Components.Any(static component => component.Name.ToString() == "Referenced.Project")).IsFalse();
        await Assert.That(inventory.Components.Any(static component => component.Name.ToString() == "Unresolved.Package")).IsFalse();

        var direct = FindComponent(inventory, 0, "Direct.Package");
        var netShared = FindComponent(inventory, 0, "Shared.Package");
        var winShared = FindComponent(inventory, 1, "Shared.Package");
        var native = FindComponent(inventory, 1, "Native.Package");
        var projectTransitive = FindComponent(inventory, 0, "Project.Transitive");
        await Assert.That(direct.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(netShared.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(winShared.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(native.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(projectTransitive.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(native.Purl.ToString()).IsEqualTo("pkg:nuget/Native.Package@4.0.0");
        await Assert.That(netShared.Purl.ToString()).IsEqualTo(winShared.Purl.ToString());
        await Assert.That(inventory.Edges[0].FromOccurrenceIndex).IsEqualTo(DependencyOccurrence.ContextRoot);
    }

    [Test]
    public async Task Scan_ProjectAssetsVersion4_AcceptsCurrentNuGetSchema()
    {
        var input = await File.ReadAllTextAsync(GetFixturePath("nuget-project.assets.json"));

        var inventory = DependencyInputScanner.Scan(System.Text.Encoding.UTF8.GetBytes(input.Replace("\"version\": 3", "\"version\": 4", StringComparison.Ordinal)), Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.NuGetAssets);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("4");
    }

    [Test]
    public async Task Scan_ProjectAssetsUnsupportedVersion_RejectsKnownFormat()
    {
        var input = System.Text.Encoding.UTF8.GetBytes("""{ "version": 5, "targets": {}, "libraries": {}, "project": {} }""");

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_WithNuGetAndCycloneDxMarkers_RejectsAmbiguousInput()
    {
        var input = System.Text.Encoding.UTF8.GetBytes("""{ "version": 3, "targets": {}, "libraries": {}, "project": {}, "bomFormat": "CycloneDX" }""");

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx)).Throws<JsonException>();
    }

    [Test]
    [Arguments("""{ "targets": {} }""")]
    [Arguments("""{ "version": "3", "targets": {}, "libraries": {}, "project": {} }""")]
    [Arguments("""{ "wrapper": { "version": 3, "targets": {}, "libraries": {}, "project": {} } }""")]
    public async Task Scan_WithIncompleteWrongTypeOrNestedNuGetSignature_RejectsUnsupportedInput(string json)
    {
        var input = System.Text.Encoding.UTF8.GetBytes(json);

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx)).Throws<JsonException>();
    }

    [Test]
    public async Task Registry_Default_ContainsNuGetAssetsHandler()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("NUGET-ASSETS", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.NuGetAssets);
    }

    [Test]
    public async Task Scan_ProjectAssetsWithMalformedTargets_RejectsInput()
    {
        var input = await File.ReadAllBytesAsync(GetFixturePath("nuget-project.assets-malformed.json"));

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.NuGetAssets)).Throws<JsonException>();
    }

    private static ScanComponent FindComponent(DependencyInventory inventory, int contextIndex, string name)
    {
        for (var i = 0; i < inventory.Occurrences.Length; i++)
        {
            var occurrence = inventory.Occurrences[i];
            if (occurrence.ContextIndex == contextIndex)
            {
                var component = inventory.Components[occurrence.ComponentIndex];
                if (component.Name.Span.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(name)))
                {
                    return component;
                }
            }
        }

        throw new InvalidOperationException($"Component not found: {contextIndex}/{name}");
    }

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
