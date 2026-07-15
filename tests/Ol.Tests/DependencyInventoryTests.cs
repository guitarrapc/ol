using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.PackageMetadata;
using Ol.Core.Spdx;

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
        var components = new[]
        {
            new ScanComponent("App", "1.0.0", default, "nuget", DependencyType.Root, LicenseStatus.Unknown, default, "App/1.0.0", default, [], []),
            new ScanComponent("Example.Native", "1.0.0", default, "nuget", DependencyType.Transitive, LicenseStatus.Unknown, "pkg:nuget/Example.Native@1.0.0", "Example.Native/1.0.0", default, [], []),
            new ScanComponent("App", "1.0.0", default, "nuget", DependencyType.Root, LicenseStatus.Unknown, default, "App/1.0.0", default, [], []),
            new ScanComponent("Example.Native", "1.0.0", default, "nuget", DependencyType.Transitive, LicenseStatus.Unknown, "pkg:nuget/Example.Native@1.0.0", "Example.Native/1.0.0", default, [], []),
        };
        var occurrences = new[]
        {
            new DependencyOccurrence(0, 0),
            new DependencyOccurrence(0, 1),
            new DependencyOccurrence(1, 2),
            new DependencyOccurrence(1, 3),
        };
        var edges = new[]
        {
            new DependencyEdge(0, 0, 1),
            new DependencyEdge(1, 2, 3),
        };
        var inventory = new DependencyInventory(input, contexts, components, occurrences, edges);
        var result = ScanResult.FromInventory(inventory);

        await Assert.That(result.Inventory.Contexts).Count().IsEqualTo(2);
        await Assert.That(result.Inventory.Occurrences).Count().IsEqualTo(4);
        await Assert.That(result.Inventory.Edges).Count().IsEqualTo(2);
        await Assert.That(result.Inventory.Occurrences[1].ContextIndex).IsNotEqualTo(result.Inventory.Occurrences[3].ContextIndex);
        await Assert.That(result.Inventory.Contexts[0].Platform.ToString()).IsEqualTo("windows");
        await Assert.That(result.Inventory.Contexts[1].Platform.ToString()).IsEqualTo("linux");

        var first = result.Inventory.Components[result.Inventory.Occurrences[1].ComponentIndex];
        var second = result.Inventory.Components[result.Inventory.Occurrences[3].ComponentIndex];
        var firstLookup = PackageMetadataRequest.TryCreate(first.Purl.ToString(), out var firstRequest);
        var secondLookup = PackageMetadataRequest.TryCreate(second.Purl.ToString(), out var secondRequest);
        await Assert.That(firstLookup).IsTrue();
        await Assert.That(secondLookup).IsTrue();
        await Assert.That(firstRequest.CacheKey).IsEqualTo(secondRequest.CacheKey);
    }

    [Test]
    public async Task Component_WithDependencyInputLicense_RetainsNonSbomProvenance()
    {
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.DependencyInput,
            DependencyInput: new DependencyInputEvidence("nuget-assets", "libraries.license"));
        var candidate = LicenseCandidateFactory.Create("nuget-assets", "license", "MIT"u8, new SpdxLicenseIndex(["MIT"], []), evidence);
        var component = new ScanComponent("Example", "1.0.0", "MIT", "nuget", DependencyType.Direct, LicenseStatus.Matched, "pkg:nuget/Example@1.0.0", "Example/1.0.0", candidate, [], []);

        await Assert.That(component.PrimaryCandidate.Evidence.Kind).IsEqualTo(LicenseEvidenceKind.DependencyInput);
        await Assert.That(component.PrimaryCandidate.Evidence.Kind).IsNotEqualTo(LicenseEvidenceKind.Sbom);
        await Assert.That(component.PrimaryCandidate.Evidence.DependencyInput!.Format).IsEqualTo("nuget-assets");
        await Assert.That(component.PrimaryCandidate.Evidence.DependencyInput!.Field).IsEqualTo("libraries.license");
    }
}
