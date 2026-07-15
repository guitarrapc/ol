using System.Text.Json;
using Ol.Core.PackageMetadata;

namespace Ol.Tests;

public sealed class EcosystemCiContractTests
{
    [Test]
    public async Task Manifest_EachRegisteredProviderHasExactlyOneCiRepository()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "sandbox", "ecosystems", "manifest.json")));
        await Assert.That(document.RootElement.GetProperty("schemaVersion").GetInt32()).IsEqualTo(1);
        var entries = document.RootElement.GetProperty("ecosystems");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ecosystemRoot = Path.GetFullPath(Path.Combine(root, "sandbox", "ecosystems")) + Path.DirectorySeparatorChar;

        await Assert.That(entries.GetArrayLength()).IsEqualTo(PackageMetadataProviders.Default.Count);
        foreach (var entry in entries.EnumerateArray())
        {
            var ecosystem = entry.GetProperty("ecosystem").GetString()!;
            var path = entry.GetProperty("path").GetString()!;
            var fixturePath = Path.GetFullPath(Path.Combine(root, path));
            await Assert.That(seen.Add(ecosystem)).IsTrue();
            await Assert.That(PackageMetadataProviders.Default.TryGet(ecosystem, out _)).IsTrue();
            await Assert.That(fixturePath.StartsWith(ecosystemRoot, StringComparison.OrdinalIgnoreCase)).IsTrue();
            await Assert.That(Directory.Exists(fixturePath)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(fixturePath, "prepare.ps1"))).IsTrue();
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
