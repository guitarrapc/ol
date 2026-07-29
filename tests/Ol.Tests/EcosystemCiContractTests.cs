using System.Text.Json;
using Ol.Core;
using System.Xml.Linq;

namespace Ol.Tests;

public sealed class EcosystemCiContractTests
{
    [Test]
    public async Task Manifest_EachRegisteredProviderHasExactlyOneCiRepository()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "sandbox", "ecosystems", "manifest.json")));
        await Assert.That(document.RootElement.GetProperty("schemaVersion").GetInt32()).IsEqualTo(2);
        var entries = document.RootElement.GetProperty("ecosystems");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ecosystemRoot = Path.GetFullPath(Path.Combine(root, "sandbox", "ecosystems")) + Path.DirectorySeparatorChar;

        await Assert.That(entries.GetArrayLength()).IsEqualTo(OlDefaults.PackageMetadataProviders.Count);
        foreach (var entry in entries.EnumerateArray())
        {
            var ecosystem = entry.GetProperty("ecosystem").GetString()!;
            var path = entry.GetProperty("path").GetString()!;
            var package = entry.GetProperty("package").GetString()!;
            var metadataSource = entry.GetProperty("metadataSource").GetString()!;
            var fixturePath = Path.GetFullPath(Path.Combine(root, path));
            await Assert.That(seen.Add(ecosystem)).IsTrue();
            await Assert.That(OlDefaults.PackageMetadataProviders.TryGet(ecosystem, out _)).IsTrue();
            await Assert.That(package).IsNotEmpty();
            await Assert.That(metadataSource).IsNotEmpty();
            await Assert.That(fixturePath.StartsWith(ecosystemRoot, StringComparison.OrdinalIgnoreCase)).IsTrue();
            await Assert.That(Directory.Exists(fixturePath)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(fixturePath, "prepare.ps1"))).IsTrue();
        }
    }

    [Test]
    public async Task Manifest_MavenPackage_MatchesFixturePurlPath()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "sandbox", "ecosystems", "manifest.json")));
        var entries = document.RootElement.GetProperty("ecosystems");
        JsonElement maven = default;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.GetProperty("ecosystem").ValueEquals("maven"))
            {
                maven = entry;
                break;
            }
        }

        await Assert.That(maven.ValueKind).IsEqualTo(JsonValueKind.Object);
        var pom = XDocument.Load(Path.Combine(root, maven.GetProperty("path").GetString()!, "pom.xml"));
        var xmlNamespace = pom.Root!.Name.Namespace;
        var dependency = pom.Root.Element(xmlNamespace + "dependencies")!.Element(xmlNamespace + "dependency")!;
        var expected = string.Concat(
            dependency.Element(xmlNamespace + "groupId")!.Value,
            "/",
            dependency.Element(xmlNamespace + "artifactId")!.Value);

        await Assert.That(maven.GetProperty("package").GetString()).IsEqualTo(expected);
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFilePath)! })
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
