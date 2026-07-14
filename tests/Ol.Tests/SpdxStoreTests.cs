using Ol.Core;

namespace Ol.Tests;

public sealed class SpdxStoreTests
{
    [Test]
    public async Task ListInstalledVersions_WithDirectory_ReturnsOrdinalIgnoreCaseSortedDirectoryNames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "3.9.0"));
        Directory.CreateDirectory(Path.Combine(root, "3.27.0"));
        Directory.CreateDirectory(Path.Combine(root, "3.10.0"));

        try
        {
            var versions = SpdxStore.ListInstalledVersions(root);

            await Assert.That(versions.Length).IsEqualTo(3);
            await Assert.That(versions[0]).IsEqualTo("3.10.0");
            await Assert.That(versions[1]).IsEqualTo("3.27.0");
            await Assert.That(versions[2]).IsEqualTo("3.9.0");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
