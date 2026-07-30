using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class MavenInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "MIT"], []);

    [Test]
    public async Task Scan_MavenDependencyTreeJson_PreservesResolvedGraphScopesAndArtifactIdentity()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("maven-dependency-tree.json")),
            Spdx,
            expectedFormat: ScanInputFormat.MavenDependencyTree);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.MavenDependencyTree);
        await Assert.That(inventory.Input.SpecificationVersion.IsEmpty).IsTrue();
        await Assert.That(inventory.Contexts).Count().IsEqualTo(1);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("com.example:demo");
        await Assert.That(inventory.Contexts[0].Variant.IsEmpty).IsTrue();

        await Assert.That(inventory.Components).Count().IsEqualTo(3);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(3);
        await Assert.That(inventory.Edges).Count().IsEqualTo(3);

        var direct = FindComponent(inventory, "org.example:direct:jar::2.0.0");
        var transitive = FindComponent(inventory, "org.example:transitive:test-jar:tests:3.0.0");
        var provided = FindComponent(inventory, "org.example:provided:jar::4.0.0");
        await Assert.That(direct.Name.ToString()).IsEqualTo("direct");
        await Assert.That(direct.Purl.ToString()).IsEqualTo("pkg:maven/org.example/direct@2.0.0");
        await Assert.That(direct.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(direct.Status).IsEqualTo(LicenseStatus.Unknown);
        await Assert.That(direct.CandidateCount).IsEqualTo(0);
        await Assert.That(transitive.Purl.ToString()).IsEqualTo("pkg:maven/org.example/transitive@3.0.0?classifier=tests&type=test-jar");
        await Assert.That(transitive.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(provided.DependencyType).IsEqualTo(DependencyType.Direct);

        await Assert.That(FindVariant(inventory, direct.SourceId.ToString()).ToString()).IsEqualTo("scope=compile");
        await Assert.That(FindVariant(inventory, transitive.SourceId.ToString()).ToString()).IsEqualTo("scope=runtime;optional");
        await Assert.That(FindVariant(inventory, provided.SourceId.ToString()).ToString()).IsEqualTo("scope=provided");
        await Assert.That(HasEdge(inventory, DependencyOccurrence.ContextRoot, FindOccurrence(inventory, direct.SourceId.ToString()))).IsTrue();
        await Assert.That(HasEdge(inventory, FindOccurrence(inventory, direct.SourceId.ToString()), FindOccurrence(inventory, transitive.SourceId.ToString()))).IsTrue();
    }

    [Test]
    public async Task Scan_MavenDependencyTreeJson_AutoDetectsRegisteredFormat()
    {
        var inventory = DependencyInputScanner.Scan(await File.ReadAllBytesAsync(GetFixturePath("maven-dependency-tree.json")), Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.MavenDependencyTree);
    }

    [Test]
    public async Task Registry_Default_MavenHandlerOwnsSignatureDirectoryDiscoveryAndIdentity()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("MAVEN-DEPENDENCY-TREE", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.MavenDependencyTree);
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["maven-dependency-tree.json"]);
        await Assert.That(handler.Signature.RequiredMarkers.Length).IsEqualTo(7);
        await Assert.That(handler.ComponentIdentityComparison).IsEqualTo(DependencyComponentIdentityComparison.OrdinalWithSourceId);
    }

    [Test]
    public async Task Scan_MavenDependencyTreeWithoutChildren_AcceptsEmptyResolvedGraph()
    {
        var json = """
            {
              "groupId": "com.example",
              "artifactId": "empty",
              "version": "1.0.0",
              "type": "jar",
              "scope": "",
              "classifier": "",
              "optional": "false"
            }
            """;

        var inventory = DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(json), Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.MavenDependencyTree);
        await Assert.That(inventory.Contexts).Count().IsEqualTo(1);
        await Assert.That(inventory.Components).IsEmpty();
        await Assert.That(inventory.Occurrences).IsEmpty();
        await Assert.That(inventory.Edges).IsEmpty();
    }

    [Test]
    [Arguments("true")]
    [Arguments("null")]
    public async Task Scan_WithWrongMavenDependencyTreeSignatureTypes_RejectsUnsupportedInput(string optional)
    {
        var json = $$"""
            {
              "groupId": "com.example",
              "artifactId": "demo",
              "version": "1.0.0",
              "type": "jar",
              "scope": "",
              "classifier": "",
              "optional": {{optional}},
              "children": []
            }
            """;

        await Assert.That(() => DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(json), Spdx)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_MavenDependencyTreeWithMissingChildCoordinate_RejectsKnownFormat()
    {
        var json = """
            {
              "groupId": "com.example",
              "artifactId": "demo",
              "version": "1.0.0",
              "type": "jar",
              "scope": "",
              "classifier": "",
              "optional": "false",
              "children": [{ "groupId": "org.example" }]
            }
            """;

        await Assert.That(() => DependencyInputScanner.Scan(
            Encoding.UTF8.GetBytes(json),
            Spdx,
            expectedFormat: ScanInputFormat.MavenDependencyTree)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_MavenDependencyTreeWithRepeatedArtifact_DeduplicatesComponentAndRetainsDistinctEdges()
    {
        var json = """
            {
              "groupId": "com.example",
              "artifactId": "demo",
              "version": "1.0.0",
              "type": "jar",
              "scope": "",
              "classifier": "",
              "optional": "false",
              "children": [
                {
                  "groupId": "org.example",
                  "artifactId": "first",
                  "version": "1.0.0",
                  "type": "jar",
                  "scope": "compile",
                  "classifier": "",
                  "optional": "false",
                  "children": [{
                    "groupId": "org.example",
                    "artifactId": "shared",
                    "version": "2.0.0",
                    "type": "jar",
                    "scope": "runtime",
                    "classifier": "",
                    "optional": "false"
                  }]
                },
                {
                  "groupId": "org.example",
                  "artifactId": "second",
                  "version": "1.0.0",
                  "type": "jar",
                  "scope": "compile",
                  "classifier": "",
                  "optional": "false",
                  "children": [{
                    "groupId": "org.example",
                  "artifactId": "shared",
                  "version": "2.0.0",
                  "type": "jar",
                  "scope": "test",
                  "classifier": "",
                  "optional": "true"
                  }]
                }
              ]
            }
            """;

        var inventory = DependencyInputScanner.Scan(
            Encoding.UTF8.GetBytes(json),
            Spdx,
            expectedFormat: ScanInputFormat.MavenDependencyTree);

        await Assert.That(inventory.Components).Count().IsEqualTo(3);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(4);
        await Assert.That(inventory.Edges).Count().IsEqualTo(4);
        var sharedComponent = FindComponentIndex(inventory, "org.example:shared:jar::2.0.0");
        var sharedOccurrences = inventory.Occurrences
            .Select((occurrence, index) => (occurrence, index))
            .Where(item => item.occurrence.ComponentIndex == sharedComponent)
            .Select(item => item.index)
            .ToArray();
        await Assert.That(sharedOccurrences).Count().IsEqualTo(2);
        await Assert.That(inventory.OccurrenceVariants!
            .Where(variant => sharedOccurrences.Contains(variant.OccurrenceIndex))
            .Select(variant => variant.Value.ToString())
            .ToArray()).IsEquivalentTo(["scope=runtime", "scope=test;optional"]);
        await Assert.That(inventory.Edges.Count(edge => sharedOccurrences.Contains(edge.ToOccurrenceIndex))).IsEqualTo(2);
    }

    [Test]
    public async Task Scan_MavenDependencyTree_ClassifiesTestScopeAsDevelopment()
    {
        // demo -> prod(compile), provided(provided), testtool(test) -> shared(test), prodparent(compile) -> shared(runtime)
        const string json = """
            {
              "groupId": "org.example", "artifactId": "demo", "version": "1.0.0", "type": "jar", "scope": "", "classifier": "", "optional": "false",
              "children": [
                { "groupId": "org.example", "artifactId": "prod", "version": "1.0.0", "type": "jar", "scope": "compile", "classifier": "", "optional": "false" },
                { "groupId": "org.example", "artifactId": "providedep", "version": "1.0.0", "type": "jar", "scope": "provided", "classifier": "", "optional": "false" },
                { "groupId": "org.example", "artifactId": "testtool", "version": "1.0.0", "type": "jar", "scope": "test", "classifier": "", "optional": "false",
                  "children": [ { "groupId": "org.example", "artifactId": "shared", "version": "2.0.0", "type": "jar", "scope": "test", "classifier": "", "optional": "false" } ] },
                { "groupId": "org.example", "artifactId": "prodparent", "version": "1.0.0", "type": "jar", "scope": "compile", "classifier": "", "optional": "false",
                  "children": [ { "groupId": "org.example", "artifactId": "shared", "version": "2.0.0", "type": "jar", "scope": "runtime", "classifier": "", "optional": "false" } ] }
              ]
            }
            """;

        var inventory = DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(json), Spdx, expectedFormat: ScanInputFormat.MavenDependencyTree);

        await Assert.That(inventory.UsageDeterminedRanges).IsNotNull();
        await Assert.That(inventory.UsageDeterminedRanges!.Sum(static range => range.Length)).IsEqualTo(inventory.Occurrences.Length);

        var usages = new DependencyUsage[inventory.Components.Length];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[FindComponentIndex(inventory, "org.example:testtool:jar::1.0.0")]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(usages[FindComponentIndex(inventory, "org.example:prod:jar::1.0.0")]).IsEqualTo(DependencyUsage.Runtime);
        await Assert.That(usages[FindComponentIndex(inventory, "org.example:providedep:jar::1.0.0")]).IsEqualTo(DependencyUsage.Runtime);
        // shared is reached by both a test path and a runtime path, so a single non-test occurrence keeps it runtime.
        await Assert.That(usages[FindComponentIndex(inventory, "org.example:shared:jar::2.0.0")]).IsEqualTo(DependencyUsage.Runtime);
    }

    [Test]
    public async Task Parser_WithoutRetainedGraph_DoesNotAllocateDiscardedOccurrenceVariants()
    {
        var emptyScope = Encoding.UTF8.GetBytes(CreateSingleDependencyJson(""));
        var compileScope = Encoding.UTF8.GetBytes(CreateSingleDependencyJson("compile"));
        DependencyInputRegistry.Default.TryGetInputFormat(ScanInputFormat.MavenDependencyTree.Name, out var handler);
        var parser = handler.Parser!;
        _ = parser(emptyScope, 0, Spdx, retainGraph: false);
        _ = parser(compileScope, 0, Spdx, retainGraph: false);

        var emptyScopeBytes = MeasureAllocations(parser, emptyScope);
        var compileScopeBytes = MeasureAllocations(parser, compileScope);

        await Assert.That(compileScopeBytes).IsEqualTo(emptyScopeBytes);
    }

    [Test]
    public async Task Scan_DeeplyNestedMavenDependencyTree_FailsWithinConfiguredDepth()
    {
        var builder = new StringBuilder();
        const string fields = """
            "groupId":"org.example","artifactId":"node","version":"1.0.0","type":"jar","scope":"compile","classifier":"","optional":"false"
            """;
        for (var depth = 0; depth < 70; depth++)
        {
            builder.Append('{').Append(fields).Append(",\"children\":[");
        }

        for (var depth = 0; depth < 70; depth++) builder.Append("]}");

        await Assert.That(() => DependencyInputScanner.Scan(
            Encoding.UTF8.GetBytes(builder.ToString()),
            Spdx,
            expectedFormat: ScanInputFormat.MavenDependencyTree)).Throws<JsonException>();
    }

    private static ScanComponent FindComponent(DependencyInventory inventory, string sourceId)
        => inventory.Components.Single(component => component.SourceId.ToString() == sourceId);

    private static int FindOccurrence(DependencyInventory inventory, string sourceId)
    {
        for (var occurrenceIndex = 0; occurrenceIndex < inventory.Occurrences.Length; occurrenceIndex++)
        {
            if (inventory.Components[inventory.Occurrences[occurrenceIndex].ComponentIndex].SourceId.ToString() == sourceId) return occurrenceIndex;
        }

        throw new InvalidOperationException($"Occurrence not found: {sourceId}");
    }

    private static int FindComponentIndex(DependencyInventory inventory, string sourceId)
    {
        for (var componentIndex = 0; componentIndex < inventory.Components.Length; componentIndex++)
        {
            if (inventory.Components[componentIndex].SourceId.ToString() == sourceId) return componentIndex;
        }

        throw new InvalidOperationException($"Component not found: {sourceId}");
    }

    private static Utf8Slice FindVariant(DependencyInventory inventory, string sourceId)
    {
        var occurrenceIndex = FindOccurrence(inventory, sourceId);
        return inventory.OccurrenceVariants!.Single(variant => variant.OccurrenceIndex == occurrenceIndex).Value;
    }

    private static bool HasEdge(DependencyInventory inventory, int fromOccurrenceIndex, int toOccurrenceIndex)
        => inventory.Edges.Any(edge => edge.ContextIndex == 0
            && edge.FromOccurrenceIndex == fromOccurrenceIndex
            && edge.ToOccurrenceIndex == toOccurrenceIndex);

    private static string CreateSingleDependencyJson(string scope)
        => $$"""
            {
              "groupId": "com.example",
              "artifactId": "demo",
              "version": "1.0.0",
              "type": "jar",
              "scope": "",
              "classifier": "",
              "optional": "false",
              "children": [{
                "groupId": "org.example",
                "artifactId": "library",
                "version": "2.0.0",
                "type": "jar",
                "scope": "{{scope}}",
                "classifier": "",
                "optional": "false"
              }]
            }
            """;

    private static long MeasureAllocations(DependencyInputParser parser, byte[] source)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 32; iteration++) _ = parser(source, 0, Spdx, retainGraph: false);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
