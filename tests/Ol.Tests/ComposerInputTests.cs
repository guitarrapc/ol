using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Text.Json;

namespace Ol.Tests;

public sealed class ComposerInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "BSD-3-Clause", "MIT"], []);

    [Test]
    public async Task Scan_ComposerResolvedPair_ProjectsRootGraphDevScopeAndLicenseEvidence()
    {
        var inventory = DependencyInputScanner.ScanBundle(
            [
                await File.ReadAllBytesAsync(GetFixturePath("composer.json")),
                await File.ReadAllBytesAsync(GetFixturePath("composer.lock")),
            ],
            Spdx,
            ScanInputFormat.ComposerLock);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.ComposerLock);
        await Assert.That(inventory.Contexts).Count().IsEqualTo(1);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("example/app");
        await Assert.That(inventory.Contexts[0].Variant.ToString()).IsEqualTo("plugin-api=2.6.0");

        await Assert.That(inventory.Components).Count().IsEqualTo(5);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(5);
        var monolog = FindComponent(inventory, "monolog/monolog@3.9.0");
        var psrLog = FindComponent(inventory, "psr/log@3.0.2");
        var container = FindComponent(inventory, "example/container@1.1.0");
        var phpunit = FindComponent(inventory, "phpunit/phpunit@11.5.0");
        var sebastian = FindComponent(inventory, "sebastian/version@5.0.2");

        await Assert.That(monolog.Purl.ToString()).IsEqualTo("pkg:composer/monolog/monolog@3.9.0");
        await Assert.That(monolog.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(monolog.License.ToString()).IsEqualTo("MIT");
        await Assert.That(monolog.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(monolog.PrimaryCandidate.Evidence.DependencyInput!.Format).IsEqualTo("composer-lock");
        await Assert.That(monolog.RepositoryUrl.ToString()).IsEqualTo("https://github.com/Seldaek/monolog.git");
        await Assert.That(psrLog.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(container.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(phpunit.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(sebastian.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(FindVariant(inventory, phpunit.SourceId.ToString()).ToString()).IsEqualTo("dev");
        await Assert.That(FindVariant(inventory, sebastian.SourceId.ToString()).ToString()).IsEqualTo("dev");

        var monologOccurrence = FindOccurrence(inventory, monolog.SourceId.ToString());
        var psrLogOccurrence = FindOccurrence(inventory, psrLog.SourceId.ToString());
        var containerOccurrence = FindOccurrence(inventory, container.SourceId.ToString());
        var phpunitOccurrence = FindOccurrence(inventory, phpunit.SourceId.ToString());
        var sebastianOccurrence = FindOccurrence(inventory, sebastian.SourceId.ToString());
        await Assert.That(HasEdge(inventory, DependencyOccurrence.ContextRoot, monologOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, monologOccurrence, psrLogOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, DependencyOccurrence.ContextRoot, containerOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, DependencyOccurrence.ContextRoot, phpunitOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, phpunitOccurrence, sebastianOccurrence)).IsTrue();
    }

    [Test]
    public async Task Registry_Default_ComposerHandlerOwnsResolvedPairAndIdentity()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("COMPOSER-LOCK", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.ComposerLock);
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["composer.json", "composer.lock"]);
        await Assert.That(handler.Parser is null).IsTrue();
        await Assert.That(handler.BundleParser is not null).IsTrue();
        await Assert.That(handler.ComponentIdentityComparison).IsEqualTo(DependencyComponentIdentityComparison.OrdinalWithSourceId);
    }

    [Test]
    public async Task ScanBundle_WithMissingComposerCompanion_RejectsIncompleteInput()
    {
        byte[][] inputs = [await File.ReadAllBytesAsync(GetFixturePath("composer.json"))];

        await Assert.That(() => DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.ComposerLock)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ScanBundle_WithMalformedComposerPair_RejectsInput()
    {
        byte[][] inputs =
        [
            """{ "name": "example/app", "require": { "example/package": "*" } }"""u8.ToArray(),
            """{ "packages": [{ "name": "example/package" }], "packages-dev": [] }"""u8.ToArray(),
        ];

        await Assert.That(() => DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.ComposerLock)).Throws<JsonException>();
    }

    [Test]
    public async Task ScanBundle_WithAmbiguousVirtualProviders_DoesNotGuessEdgesOrRelationship()
    {
        byte[][] inputs =
        [
            """{ "name": "example/app", "require": { "example/implementation": "*" } }"""u8.ToArray(),
            """
            {
              "packages": [
                { "name": "example/first", "version": "1.0.0", "provide": { "example/implementation": "1.0" } },
                { "name": "example/second", "version": "1.0.0", "provide": { "example/implementation": "1.0" } }
              ],
              "packages-dev": []
            }
            """u8.ToArray(),
        ];

        var inventory = DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.ComposerLock);

        await Assert.That(inventory.Components.All(component => component.DependencyType == DependencyType.Unknown)).IsTrue();
        await Assert.That(inventory.Edges).IsEmpty();
    }

    [Test]
    public async Task ScanBundle_WithPlatformAndMissingRequirements_DoesNotCreatePackageEdges()
    {
        byte[][] inputs =
        [
            """{ "require": { "php": "^8.2", "ext-json": "*", "example/missing": "*" } }"""u8.ToArray(),
            """{ "packages": [{ "name": "example/unreferenced", "version": "1.0.0" }], "packages-dev": [] }"""u8.ToArray(),
        ];

        var inventory = DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.ComposerLock);

        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("composer-project");
        await Assert.That(inventory.Components[0].DependencyType).IsEqualTo(DependencyType.Unknown);
        await Assert.That(inventory.Edges).IsEmpty();
    }

    [Test]
    public async Task ScanBundle_WithDuplicatePackageNames_RejectsAmbiguousIdentity()
    {
        byte[][] inputs =
        [
            """{ "require": { "example/package": "*" } }"""u8.ToArray(),
            """
            {
              "packages": [
                { "name": "example/package", "version": "1.0.0" },
                { "name": "example/package", "version": "2.0.0" }
              ],
              "packages-dev": []
            }
            """u8.ToArray(),
        ];

        await Assert.That(() => DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.ComposerLock)).Throws<JsonException>();
    }

    [Test]
    public async Task ScanBundle_WithDisjunctiveLicenseArray_ProjectsSpdxOrExpression()
    {
        byte[][] inputs =
        [
            """{ "require": { "example/package": "*" } }"""u8.ToArray(),
            """{ "packages": [{ "name": "example/package", "version": "1.0.0", "license": ["MIT", "Apache-2.0"] }], "packages-dev": [] }"""u8.ToArray(),
        ];

        var inventory = DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.ComposerLock);

        await Assert.That(inventory.Components[0].License.ToString()).IsEqualTo("MIT OR Apache-2.0");
        await Assert.That(inventory.Components[0].Status).IsEqualTo(LicenseStatus.Matched);
    }

    [Test]
    public async Task ScanBundle_WithBranchVersion_PercentEncodesComposerPurlVersion()
    {
        byte[][] inputs =
        [
            """{ "require": { "example/package": "*" } }"""u8.ToArray(),
            """{ "packages": [{ "name": "example/package", "version": "dev-feature/foo" }], "packages-dev": [] }"""u8.ToArray(),
        ];

        var inventory = DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.ComposerLock);

        await Assert.That(inventory.Components[0].Purl.ToString()).IsEqualTo("pkg:composer/example/package@dev-feature%2Ffoo");
        await Assert.That(inventory.Components[0].SourceId.ToString()).IsEqualTo("example/package@dev-feature/foo");
    }

    [Test]
    [Arguments(".example/package")]
    [Arguments("example/package-")]
    [Arguments("Example/package")]
    public async Task ScanBundle_WithInvalidComposerPackageName_RejectsInput(string name)
    {
        byte[][] inputs =
        [
            """{ "require": {} }"""u8.ToArray(),
            System.Text.Encoding.UTF8.GetBytes($$"""{ "packages": [{ "name": "{{name}}", "version": "1.0.0" }], "packages-dev": [] }"""),
        ];

        await Assert.That(() => DependencyInputScanner.ScanBundle(inputs, Spdx, ScanInputFormat.ComposerLock)).Throws<JsonException>();
    }

    private static ScanComponent FindComponent(DependencyInventory inventory, string sourceId)
        => inventory.Components.Single(component => component.SourceId.ToString() == sourceId);

    private static int FindOccurrence(DependencyInventory inventory, string sourceId)
        => Array.FindIndex(inventory.Occurrences, occurrence => inventory.Components[occurrence.ComponentIndex].SourceId.ToString() == sourceId);

    private static Utf8Slice FindVariant(DependencyInventory inventory, string sourceId)
    {
        var occurrenceIndex = FindOccurrence(inventory, sourceId);
        return inventory.OccurrenceVariants!.Single(variant => variant.OccurrenceIndex == occurrenceIndex).Value;
    }

    private static bool HasEdge(DependencyInventory inventory, int from, int to)
        => inventory.Edges.Any(edge => edge.FromOccurrenceIndex == from && edge.ToOccurrenceIndex == to);

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
