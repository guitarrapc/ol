using System.Security.Cryptography;
using System.Text;
using Ol.Core.Generated;
using Ol.Core.Spdx;

namespace Ol.Tests;

public sealed class SpdxStoreTests
{
    [Test]
    public async Task Data_Bundled_ReportsDigestsOfTheGeneratedIdentifiers()
    {
        var data = SpdxData.Load(null);

        await Assert.That(data.Source).IsEqualTo("bundled");
        await Assert.That(data.GetLicensesSha256()).IsEqualTo(ExpectedDigest(SpdxGeneratedLicenseData.LicenseIds));
        await Assert.That(data.GetExceptionsSha256()).IsEqualTo(ExpectedDigest(SpdxGeneratedLicenseData.ExceptionIds));
        await Assert.That(data.GetLicensesSha256()).IsNotEqualTo(data.GetExceptionsSha256());
    }

    [Test]
    public async Task Data_UserDirectory_ReportsDigestsOfTheInstalledFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-digest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var licenses = """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "MIT" } ] }""";
        var exceptions = """{ "exceptions": [ { "licenseExceptionId": "LLVM-exception" } ] }""";
        await File.WriteAllTextAsync(Path.Combine(root, "licenses.json"), licenses);
        await File.WriteAllTextAsync(Path.Combine(root, "exceptions.json"), exceptions);

        try
        {
            var data = SpdxData.Load(root);

            await Assert.That(data.Source).IsEqualTo("cli-argument");
            await Assert.That(data.GetLicensesSha256()).IsEqualTo(ExpectedFileDigest(Path.Combine(root, "licenses.json")));
            await Assert.That(data.GetExceptionsSha256()).IsEqualTo(ExpectedFileDigest(Path.Combine(root, "exceptions.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ExpectedDigest(string[] identifiers)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', identifiers)))).ToLowerInvariant();

    private static string ExpectedFileDigest(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

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
