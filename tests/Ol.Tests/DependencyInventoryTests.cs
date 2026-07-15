using Ol.Core;

namespace Ol.Tests;

public sealed class DependencyInventoryTests
{
    [Test]
    public async Task Inventory_WithSamePackageInDifferentContexts_PreservesOccurrencesAndSharesLookupIdentity()
    {
        var input = new ScanInputDescriptor(ScanInputKind.PackageManager, ScanInputFormat.NuGetAssets, "project.assets.json", "sha256", "1");
        var contexts = new[]
        {
            new DependencyResolutionContext("src/App/App.csproj", "net10.0", "win-x64", "windows", "x64", "runtime"),
            new DependencyResolutionContext("src/App/App.csproj", "net10.0", "linux-x64", "linux", "x64", "runtime"),
        };
        var occurrences = new[]
        {
            new DependencyOccurrence(0, "App", "1.0.0", "nuget", default, "App/1.0.0", DependencyType.Root, [], []),
            new DependencyOccurrence(0, "Example.Native", "1.0.0", "nuget", "pkg:nuget/Example.Native@1.0.0", "Example.Native/1.0.0", DependencyType.Transitive, [], []),
            new DependencyOccurrence(1, "App", "1.0.0", "nuget", default, "App/1.0.0", DependencyType.Root, [], []),
            new DependencyOccurrence(1, "Example.Native", "1.0.0", "nuget", "pkg:nuget/Example.Native@1.0.0", "Example.Native/1.0.0", DependencyType.Transitive, [], []),
        };
        var edges = new[]
        {
            new DependencyEdge(0, 0, 1),
            new DependencyEdge(1, 2, 3),
        };
        var inventory = new DependencyInventory(input, contexts, occurrences, edges);
        var result = new ScanResult(inventory, []);

        await Assert.That(result.Inventory.Contexts).Count().IsEqualTo(2);
        await Assert.That(result.Inventory.Occurrences).Count().IsEqualTo(4);
        await Assert.That(result.Inventory.Edges).Count().IsEqualTo(2);
        await Assert.That(result.Inventory.Occurrences[1].ContextIndex).IsNotEqualTo(result.Inventory.Occurrences[3].ContextIndex);
        await Assert.That(result.Inventory.Contexts[0].Platform.ToString()).IsEqualTo("windows");
        await Assert.That(result.Inventory.Contexts[1].Platform.ToString()).IsEqualTo("linux");

        var firstLookup = PackageMetadataRequest.TryCreate(result.Inventory.Occurrences[1].Purl.ToString(), out var firstRequest);
        var secondLookup = PackageMetadataRequest.TryCreate(result.Inventory.Occurrences[3].Purl.ToString(), out var secondRequest);
        await Assert.That(firstLookup).IsTrue();
        await Assert.That(secondLookup).IsTrue();
        await Assert.That(firstRequest.CacheKey).IsEqualTo(secondRequest.CacheKey);
    }

    [Test]
    public async Task Occurrence_WithDependencyInputLicense_RetainsNonSbomProvenance()
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.DependencyInput,
            DependencyInput: new DependencyInputEvidence("nuget-assets", "libraries.license"));
        var candidate = LicenseCandidateFactory.Create("nuget-assets", "license", "MIT"u8, new SpdxLicenseIndex(["MIT"], []), evidence);
        var occurrence = new DependencyOccurrence(0, "Example", "1.0.0", "nuget", "pkg:nuget/Example@1.0.0", "Example/1.0.0", DependencyType.Direct, [candidate], []);

        await Assert.That(occurrence.LicenseCandidates[0].Evidence.Kind).IsEqualTo(LicenseEvidenceKind.DependencyInput);
        await Assert.That(occurrence.LicenseCandidates[0].Evidence.Kind).IsNotEqualTo(LicenseEvidenceKind.Sbom);
        await Assert.That(occurrence.LicenseCandidates[0].Evidence.DependencyInput!.Format).IsEqualTo("nuget-assets");
        await Assert.That(occurrence.LicenseCandidates[0].Evidence.DependencyInput!.Field).IsEqualTo("libraries.license");
    }
}
