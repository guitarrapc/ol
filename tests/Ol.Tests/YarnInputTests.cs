using Ol.Core;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class YarnInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT"], []);

    [Test]
    public async Task Scan_YarnClassicV1_PreservesDescriptorGraphAndOptionalEdge()
    {
        var inventory = Scan("yarn-classic.lock", ScanInputFormat.YarnClassicLock);

        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("1");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(1);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("yarn.lock");
        await Assert.That(inventory.Components).Count().IsEqualTo(3);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(3);
        await Assert.That(inventory.Edges).Count().IsEqualTo(2);
        await Assert.That(inventory.Components.All(static component => component.DependencyType == DependencyType.Unknown)).IsTrue();
        await Assert.That(FindComponent(inventory, "direct-package@^1.0.0").Purl.ToString()).IsEqualTo("pkg:npm/direct-package@1.0.1");
        await Assert.That(FindVariant(inventory, "optional-child@^3.0.0").ToString()).IsEqualTo("optional");
    }

    [Test]
    public async Task Scan_YarnBerryV8_PreservesWorkspacesAndVirtualResolution()
    {
        var inventory = Scan("yarn-berry.lock", ScanInputFormat.YarnBerryLock);

        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("8");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(2);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo(".");
        await Assert.That(inventory.Contexts[1].ProjectOrigin.ToString()).IsEqualTo("packages/a");
        await Assert.That(inventory.Components).Count().IsEqualTo(3);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(4);
        await Assert.That(inventory.Edges).Count().IsEqualTo(4);
        await Assert.That(inventory.Components.Any(static component => component.Name.ToString() == "workspace-a")).IsFalse();
        await Assert.That(FindComponent(inventory, "direct-package@npm:1.0.1").DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(FindComponent(inventory, "shared-package@npm:2.1.0").DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(FindComponent(inventory, "peer-package@virtual:abc123#npm:3.0.0").DependencyType).IsEqualTo(DependencyType.Unknown);
        await Assert.That(FindVariant(inventory, "peer-package@virtual:abc123#npm:3.0.0").ToString()).IsEqualTo("virtual=abc123");
    }

    [Test]
    public async Task Scan_YarnLocks_AutoDetectsClassicAndBerrySeparately()
    {
        var classic = DependencyInputScanner.Scan(File.ReadAllBytes(GetFixturePath("yarn-classic.lock")), Spdx);
        var berry = DependencyInputScanner.Scan(File.ReadAllBytes(GetFixturePath("yarn-berry.lock")), Spdx);

        await Assert.That(classic.Input.Format).IsEqualTo(ScanInputFormat.YarnClassicLock);
        await Assert.That(berry.Input.Format).IsEqualTo(ScanInputFormat.YarnBerryLock);
    }

    [Test]
    public async Task Scan_YarnBerryScopedPackage_EncodesCanonicalNpmPurl()
    {
        var input = Encoding.UTF8.GetBytes(
            """
            __metadata:
              version: 8
            "app@workspace:.":
              version: 0.0.0-use.local
              resolution: "app@workspace:."
              dependencies:
                "@scope/pkg": "npm:^1.2.0"
            "@scope/pkg@npm:^1.2.0":
              version: 1.2.3
              resolution: "@scope/pkg@npm:1.2.3"
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx);

        await Assert.That(inventory.Components.Single().Purl.ToString()).IsEqualTo("pkg:npm/%40scope/pkg@1.2.3");
    }

    [Test]
    public async Task Scan_YarnClassicQuotedDescriptorAliases_ResolveScopedDependency()
    {
        var input = Encoding.UTF8.GetBytes(
            """
            # yarn lockfile v1
            consumer@^1.0.0:
              version "1.0.0"
              dependencies:
                "@scope/pkg" "^1.0.0"
            "@scope/pkg@^1.0.0", "@scope/pkg@~1.2.0":
              version "1.2.3"
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx);

        await Assert.That(inventory.Components).Count().IsEqualTo(2);
        await Assert.That(inventory.Edges).Count().IsEqualTo(1);
        await Assert.That(inventory.Components.Single(static component => component.Name.ToString() == "@scope/pkg").Purl.ToString()).IsEqualTo("pkg:npm/%40scope/pkg@1.2.3");
    }

    [Test]
    public async Task Scan_YarnLocks_RejectMalformedOrUnsupportedKnownFormats()
    {
        var unsupportedBerry = Encoding.UTF8.GetBytes("__metadata:\n  version: 7\n\"app@workspace:.\":\n  version: 0\n  resolution: \"app@workspace:.\"\n");
        var malformedClassic = Encoding.UTF8.GetBytes("# yarn lockfile v1\npackage@^1:\n  resolved \"https://example.invalid/package.tgz\"\n");

        await Assert.That(() => DependencyInputScanner.Scan(unsupportedBerry, Spdx)).Throws<JsonException>();
        await Assert.That(() => DependencyInputScanner.Scan(malformedClassic, Spdx)).Throws<JsonException>();
    }

    [Test]
    public async Task Registry_Default_YarnHandlersOwnSameFileNameAndDistinctDetectors()
    {
        var foundClassic = DependencyInputRegistry.Default.TryGetInputFormat("YARN-CLASSIC-LOCK", out var classic);
        var foundBerry = DependencyInputRegistry.Default.TryGetInputFormat("YARN-BERRY-LOCK", out var berry);

        await Assert.That(foundClassic).IsTrue();
        await Assert.That(foundBerry).IsTrue();
        await Assert.That(classic.DirectoryFileNames.ToArray()).IsEquivalentTo(["yarn.lock"]);
        await Assert.That(berry.DirectoryFileNames.ToArray()).IsEquivalentTo(["yarn.lock"]);
        await Assert.That(classic.Detector).IsNotNull();
        await Assert.That(berry.Detector).IsNotNull();
        await Assert.That(classic.Detector).IsNotEqualTo(berry.Detector);
    }

    private const string ClassicManifest = """{ "name": "app", "dependencies": { "prod-pkg": "^1.0.0" }, "devDependencies": { "dev-pkg": "^1.0.0" } }""";
    private const string ClassicLock = """
        # yarn lockfile v1

        prod-pkg@^1.0.0:
          version "1.0.0"
          dependencies:
            shared "^2.0.0"

        dev-pkg@^1.0.0:
          version "1.0.0"
          dependencies:
            shared "^2.0.0"

        shared@^2.0.0:
          version "2.0.0"
        """;

    private const string BerryManifest = """{ "name": "app", "dependencies": { "prod-pkg": "^1.0.0" }, "devDependencies": { "dev-pkg": "^1.0.0" } }""";
    private const string BerryLock = """
        __metadata:
          version: 8
          cacheKey: 10c0

        "app@workspace:.":
          version: 0.0.0-use.local
          resolution: "app@workspace:."
          dependencies:
            prod-pkg: "npm:^1.0.0"
            dev-pkg: "npm:^1.0.0"

        "prod-pkg@npm:^1.0.0":
          version: 1.0.0
          resolution: "prod-pkg@npm:1.0.0"
          dependencies:
            shared: "npm:^2.0.0"

        "dev-pkg@npm:^1.0.0":
          version: 1.0.0
          resolution: "dev-pkg@npm:1.0.0"
          dependencies:
            shared: "npm:^2.0.0"

        "shared@npm:^2.0.0":
          version: 2.0.0
          resolution: "shared@npm:2.0.0"
        """;

    [Test]
    public async Task Scan_YarnClassicWithManifest_ClassifiesDevOnlyDependencyAsDevelopment()
        => await AssertManifestUsage(ClassicLock, ClassicManifest, ScanInputFormat.YarnClassicLock);

    [Test]
    public async Task Scan_YarnBerryWithManifest_ClassifiesDevOnlyDependencyAsDevelopment()
        => await AssertManifestUsage(BerryLock, BerryManifest, ScanInputFormat.YarnBerryLock);

    private static async Task AssertManifestUsage(string lockContent, string manifest, ScanInputFormat format)
    {
        var inventory = DependencyInputScanner.Scan(
            Encoding.UTF8.GetBytes(lockContent),
            Encoding.UTF8.GetBytes(manifest),
            Spdx,
            expectedFormat: format);

        await Assert.That(inventory.UsageDeterminedRanges).IsNotNull();
        await Assert.That(inventory.UsageDeterminedRanges!.Sum(static range => range.Length)).IsEqualTo(inventory.Occurrences.Length);

        var usages = new DependencyUsage[inventory.Components.Length];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[FindComponentIndex(inventory, "dev-pkg")]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(usages[FindComponentIndex(inventory, "prod-pkg")]).IsEqualTo(DependencyUsage.Runtime);
        await Assert.That(usages[FindComponentIndex(inventory, "shared")]).IsEqualTo(DependencyUsage.Runtime);
    }

    [Test]
    public async Task Scan_YarnLockWithoutManifest_LeavesUsageUnknown()
    {
        var inventory = DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(ClassicLock), Spdx, expectedFormat: ScanInputFormat.YarnClassicLock);

        await Assert.That(inventory.UsageDeterminedRanges).IsNull();
        await Assert.That(inventory.DevelopmentOccurrences).IsNull();
    }

    [Test]
    public async Task Scan_YarnWorkspaceManifest_DoesNotClassifyUsage()
    {
        const string workspaceManifest = """{ "name": "root", "workspaces": ["packages/*"], "devDependencies": { "dev-pkg": "^1.0.0" } }""";

        var inventory = DependencyInputScanner.Scan(
            Encoding.UTF8.GetBytes(ClassicLock),
            Encoding.UTF8.GetBytes(workspaceManifest),
            Spdx,
            expectedFormat: ScanInputFormat.YarnClassicLock);

        // A workspace manifest cannot classify per-workspace dev scope from one root package.json; stay Unknown.
        await Assert.That(inventory.UsageDeterminedRanges).IsNull();
    }

    private static int FindComponentIndex(DependencyInventory inventory, string name)
        => Array.FindIndex(inventory.Components, component => component.Name.ToString() == name);

    private static DependencyInventory Scan(string fixture, ScanInputFormat format)
        => DependencyInputScanner.Scan(File.ReadAllBytes(GetFixturePath(fixture)), Spdx, expectedFormat: format);

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
