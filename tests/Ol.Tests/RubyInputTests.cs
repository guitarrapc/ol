using Ol.Core;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class RubyInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "MIT"], []);

    [Test]
    public async Task Scan_BundlerLock_ProjectsResolvedGraphSourcesAndPlatform()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("Gemfile.lock")),
            Spdx,
            expectedFormat: ScanInputFormat.BundlerLock);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.BundlerLock);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("2.6.5");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(2);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("Gemfile.lock");
        await Assert.That(inventory.Contexts[0].Runtime.ToString()).IsEqualTo("ruby 3.3.6p108");
        await Assert.That(inventory.Contexts[0].Platform.ToString()).IsEqualTo("ruby");
        await Assert.That(inventory.Contexts[0].Variant.ToString()).IsEqualTo("bundler=2.6.5");
        await Assert.That(inventory.Contexts[1].Platform.ToString()).IsEqualTo("x86_64-linux-gnu");

        await Assert.That(inventory.Components).Count().IsEqualTo(7);
        var concurrentRuby = FindComponent(inventory, "concurrent-ruby@1.3.5");
        var i18n = FindComponent(inventory, "i18n@1.14.7");
        var nokogiri = FindComponent(inventory, "nokogiri@1.18.0-x86_64-linux-gnu");
        var rack = FindComponent(inventory, "rack@3.1.8");
        var rackProtection = FindComponent(inventory, "rack-protection@4.1.1");
        var privateGem = FindComponent(inventory, "private-gem@2.0.0");
        var localGem = FindComponent(inventory, "local-gem@0.1.0");

        await Assert.That(i18n.Purl.ToString()).IsEqualTo("pkg:gem/i18n@1.14.7");
        await Assert.That(i18n.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(concurrentRuby.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(rack.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(rackProtection.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(nokogiri.Purl.ToString()).IsEqualTo("pkg:gem/nokogiri@1.18.0?platform=x86_64-linux-gnu");
        await Assert.That(FindVariant(inventory, nokogiri.SourceId.ToString()).ToString()).IsEqualTo("platform=x86_64-linux-gnu");
        await Assert.That(privateGem.Purl.IsEmpty).IsTrue();
        await Assert.That(localGem.Purl.IsEmpty).IsTrue();
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(13);

        var concurrentOccurrence = FindOccurrence(inventory, concurrentRuby.SourceId.ToString());
        var i18nOccurrence = FindOccurrence(inventory, i18n.SourceId.ToString());
        var rackOccurrence = FindOccurrence(inventory, rack.SourceId.ToString());
        var rackProtectionOccurrence = FindOccurrence(inventory, rackProtection.SourceId.ToString());
        var privateOccurrence = FindOccurrence(inventory, privateGem.SourceId.ToString());
        var localOccurrence = FindOccurrence(inventory, localGem.SourceId.ToString());
        await Assert.That(HasEdge(inventory, DependencyOccurrence.ContextRoot, i18nOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, i18nOccurrence, concurrentOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, DependencyOccurrence.ContextRoot, rackProtectionOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, rackProtectionOccurrence, rackOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, DependencyOccurrence.ContextRoot, privateOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, privateOccurrence, rackOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, DependencyOccurrence.ContextRoot, localOccurrence)).IsTrue();
        await Assert.That(HasEdge(inventory, localOccurrence, i18nOccurrence)).IsTrue();
    }

    [Test]
    public async Task Registry_Default_BundlerHandlerOwnsLockfileAndIdentity()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("BUNDLER-LOCK", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.BundlerLock);
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["Gemfile.lock"]);
        await Assert.That(handler.Parser is not null).IsTrue();
        await Assert.That(handler.Detector is not null).IsTrue();
        await Assert.That(handler.ComponentIdentityComparison).IsEqualTo(DependencyComponentIdentityComparison.OrdinalWithSourceId);
    }

    [Test]
    public async Task Scan_BundlerLock_AutoDetectsRegisteredFormat()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("Gemfile.lock")),
            Spdx);

        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.BundlerLock);
    }

    [Test]
    public async Task Scan_WithMalformedBundlerLock_RejectsInput()
    {
        var input = Encoding.UTF8.GetBytes("""
            GEM
              remote: https://rubygems.org/
              specs:
                example

            PLATFORMS
              ruby

            DEPENDENCIES
              example
            """);

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.BundlerLock)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_BundlerLockWithPrivateGemSource_DoesNotCreateRubyGemsOrgIdentity()
    {
        var input = Encoding.UTF8.GetBytes("""
            GEM
              remote: https://gems.example.test/
              specs:
                private-package (1.0.0)

            PLATFORMS
              ruby

            DEPENDENCIES
              private-package
            """);

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.BundlerLock);

        await Assert.That(inventory.Components[0].Purl.IsEmpty).IsTrue();
        await Assert.That(inventory.Components[0].Ecosystem).IsEqualTo("-");
        await Assert.That(inventory.OccurrenceVariants![0].Value.ToString()).IsEqualTo("source=registry");
    }

    [Test]
    public async Task Scan_BundlerLockWithDuplicatePlatform_RejectsAmbiguousContexts()
    {
        var input = Encoding.UTF8.GetBytes("""
            GEM
              remote: https://rubygems.org/
              specs:
                example (1.0.0)

            PLATFORMS
              ruby
              ruby

            DEPENDENCIES
              example
            """);

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.BundlerLock)).Throws<JsonException>();
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
