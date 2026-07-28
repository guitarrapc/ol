using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Text;
using System.Text.Json;

namespace Ol.Tests;

public sealed class PythonInputTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["Apache-2.0", "MIT"], []);

    [Test]
    public async Task Scan_PipInspectV1_ProjectsInstalledEnvironmentAndProvenEdges()
    {
        var inventory = DependencyInputScanner.Scan(
            await File.ReadAllBytesAsync(GetFixturePath("pip-inspect.json")),
            Spdx,
            expectedFormat: ScanInputFormat.PipInspect);

        await Assert.That(inventory.Input.Kind).IsEqualTo(ScanInputKind.PackageManager);
        await Assert.That(inventory.Input.Format).IsEqualTo(ScanInputFormat.PipInspect);
        await Assert.That(inventory.Input.SpecificationVersion.ToString()).IsEqualTo("1");
        await Assert.That(inventory.Contexts).Count().IsEqualTo(1);
        await Assert.That(inventory.Contexts[0].ProjectOrigin.ToString()).IsEqualTo("pip-environment");
        await Assert.That(inventory.Contexts[0].Target.ToString()).IsEqualTo("3.12.3");
        await Assert.That(inventory.Contexts[0].Runtime.ToString()).IsEqualTo("cpython");
        await Assert.That(inventory.Contexts[0].Platform.ToString()).IsEqualTo("linux");
        await Assert.That(inventory.Contexts[0].Architecture.ToString()).IsEqualTo("x86_64");
        await Assert.That(inventory.Contexts[0].Variant.ToString()).IsEqualTo("pip=25.1");

        await Assert.That(inventory.Components).Count().IsEqualTo(5);
        await Assert.That(inventory.Occurrences).Count().IsEqualTo(5);
        await Assert.That(inventory.Edges).Count().IsEqualTo(4);
        var requests = FindComponent(inventory, "requests@2.32.4");
        var urllib3 = FindComponent(inventory, "urllib3@2.5.0");
        var charset = FindComponent(inventory, "charset-normalizer@3.4.2");
        var socks = FindComponent(inventory, "pysocks@1.7.1");
        var local = FindComponent(inventory, "local-package@1.0.0");
        await Assert.That(requests.Name.ToString()).IsEqualTo("Requests");
        await Assert.That(requests.Purl.ToString()).IsEqualTo("pkg:pypi/requests@2.32.4");
        await Assert.That(requests.DependencyType).IsEqualTo(DependencyType.Direct);
        await Assert.That(requests.License.ToString()).IsEqualTo("Apache-2.0");
        await Assert.That(urllib3.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(charset.Purl.ToString()).IsEqualTo("pkg:pypi/charset-normalizer@3.4.2");
        await Assert.That(charset.License.ToString()).IsEqualTo("MIT");
        await Assert.That(socks.DependencyType).IsEqualTo(DependencyType.Transitive);
        await Assert.That(local.Purl.IsEmpty).IsTrue();
        await Assert.That(local.Status).IsEqualTo(LicenseStatus.Unknown);
        await Assert.That(local.CandidateCount).IsEqualTo(0);
        await Assert.That(FindVariant(inventory, local.SourceId.ToString()).ToString()).IsEqualTo("source=direct");

        var requestsOccurrence = FindOccurrence(inventory, requests.SourceId.ToString());
        var urllib3Occurrence = FindOccurrence(inventory, urllib3.SourceId.ToString());
        var charsetOccurrence = FindOccurrence(inventory, charset.SourceId.ToString());
        var socksOccurrence = FindOccurrence(inventory, socks.SourceId.ToString());
        await Assert.That(inventory.Edges.Any(edge => edge.FromOccurrenceIndex == DependencyOccurrence.ContextRoot && edge.ToOccurrenceIndex == requestsOccurrence)).IsTrue();
        await Assert.That(inventory.Edges.Any(edge => edge.FromOccurrenceIndex == requestsOccurrence && edge.ToOccurrenceIndex == urllib3Occurrence)).IsTrue();
        await Assert.That(inventory.Edges.Any(edge => edge.FromOccurrenceIndex == requestsOccurrence && edge.ToOccurrenceIndex == charsetOccurrence)).IsTrue();
        await Assert.That(inventory.Edges.Any(edge => edge.FromOccurrenceIndex == requestsOccurrence && edge.ToOccurrenceIndex == socksOccurrence)).IsFalse();
    }

    [Test]
    public async Task Registry_Default_PipInspectHandlerOwnsResolvedFileAndIdentity()
    {
        var found = DependencyInputRegistry.Default.TryGetInputFormat("PIP-INSPECT", out var handler);

        await Assert.That(found).IsTrue();
        await Assert.That(handler.Format).IsEqualTo(ScanInputFormat.PipInspect);
        await Assert.That(handler.DirectoryFileNames.ToArray()).IsEquivalentTo(["pip-inspect.json"]);
        await Assert.That(handler.ComponentIdentityComparison).IsEqualTo(DependencyComponentIdentityComparison.OrdinalWithSourceId);
    }

    [Test]
    [Arguments("2", "[]", "{}")]
    [Arguments("1", "{}", "{}")]
    [Arguments("1", "[]", "[]")]
    public async Task Scan_WithUnsupportedOrMalformedPipInspect_RejectsInput(string version, string installed, string environment)
    {
        var input = Encoding.UTF8.GetBytes($$"""{ "version": "{{version}}", "pip_version": "25.1", "installed": {{installed}}, "environment": {{environment}} }""");

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PipInspect)).Throws<Exception>();
    }

    [Test]
    public async Task Scan_PipInspectMissingDistributionVersion_RejectsInput()
    {
        var input = "{ \"version\": \"1\", \"pip_version\": \"25.1\", \"installed\": [{ \"metadata\": { \"name\": \"example\" } }], \"environment\": {} }"u8.ToArray();

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PipInspect)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_PipInspectWithoutRequested_RetainsUnknownRelationship()
    {
        var input = CreateSingleDistribution("example-package");

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PipInspect);

        await Assert.That(inventory.Components[0].DependencyType).IsEqualTo(DependencyType.Unknown);
        await Assert.That(inventory.Edges).IsEmpty();
    }

    [Test]
    public async Task Scan_PipInspectNonPipInstallerWithoutRequestedMetadata_RetainsUnknownRelationship()
    {
        var input = "{ \"version\": \"1\", \"pip_version\": \"25.1\", \"installed\": [{ \"metadata\": { \"name\": \"example\", \"version\": \"1.0.0\" }, \"installer\": \"uv\", \"requested\": false }], \"environment\": { \"implementation_name\": \"cpython\", \"python_full_version\": \"3.12.3\", \"sys_platform\": \"linux\", \"platform_machine\": \"x86_64\" } }"u8.ToArray();

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PipInspect);

        await Assert.That(inventory.Components[0].DependencyType).IsEqualTo(DependencyType.Unknown);
    }

    [Test]
    public async Task Scan_PipInspectEmptyInstalledSet_ReturnsEmptyEnvironmentInventory()
    {
        var input = "{ \"version\": \"1\", \"pip_version\": \"25.1\", \"installed\": [], \"environment\": { \"implementation_name\": \"cpython\", \"python_full_version\": \"3.12.3\", \"sys_platform\": \"linux\", \"platform_machine\": \"x86_64\" } }"u8.ToArray();

        var inventory = DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PipInspect);

        await Assert.That(inventory.Contexts).Count().IsEqualTo(1);
        await Assert.That(inventory.Components).IsEmpty();
        await Assert.That(inventory.Occurrences).IsEmpty();
        await Assert.That(inventory.Edges).IsEmpty();
    }

    [Test]
    public async Task Scan_PipInspectWithEquivalentNormalizedNames_RejectsDuplicateIdentity()
    {
        var input = Encoding.UTF8.GetBytes(
            """
            {
              "version": "1", "pip_version": "25.1",
              "installed": [
                { "metadata": { "name": "Friendly_Bard", "version": "1.0.0" } },
                { "metadata": { "name": "friendly--bard", "version": "2.0.0" } }
              ],
              "environment": { "implementation_name": "cpython", "python_full_version": "3.12.3", "sys_platform": "linux", "platform_machine": "x86_64" }
            }
            """);

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PipInspect)).Throws<JsonException>();
    }

    [Test]
    public async Task Scan_PipInspectWithTrailingJson_RejectsMultipleDocuments()
    {
        var input = CreateSingleDistribution("example-package").Concat("{}"u8.ToArray()).ToArray();

        await Assert.That(() => DependencyInputScanner.Scan(input, Spdx, expectedFormat: ScanInputFormat.PipInspect)).Throws<JsonException>();
    }

    private static byte[] CreateSingleDistribution(string name) => Encoding.UTF8.GetBytes(
        $$"""
        {
          "version": "1", "pip_version": "25.1",
          "installed": [{ "metadata": { "name": "{{name}}", "version": "1.0.0" } }],
          "environment": { "implementation_name": "cpython", "python_full_version": "3.12.3", "sys_platform": "linux", "platform_machine": "x86_64" }
        }
        """);

    private static ScanComponent FindComponent(DependencyInventory inventory, string sourceId)
        => inventory.Components.Single(component => component.SourceId.ToString() == sourceId);

    private static int FindOccurrence(DependencyInventory inventory, string sourceId)
        => Array.FindIndex(inventory.Occurrences, occurrence => inventory.Components[occurrence.ComponentIndex].SourceId.ToString() == sourceId);

    private static Utf8Slice FindVariant(DependencyInventory inventory, string sourceId)
    {
        var occurrenceIndex = FindOccurrence(inventory, sourceId);
        return inventory.OccurrenceVariants!.Single(variant => variant.OccurrenceIndex == occurrenceIndex).Value;
    }

    private static string GetFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
