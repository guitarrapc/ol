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

        try
        {
            await InstallAsync(root, "3.9.0");
            await InstallAsync(root, "3.27.0");
            await InstallAsync(root, "3.10.0");
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

    [Test]
    public async Task ListInstalledVersions_WithInvalidDirectory_ExcludesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-store-{Guid.NewGuid():N}");

        try
        {
            await InstallAsync(root, "3.27.0");
            Directory.CreateDirectory(Path.Combine(root, "not-installed"));
            var missingVersion = Path.Combine(root, "3.28.0");
            Directory.CreateDirectory(missingVersion);
            await File.WriteAllTextAsync(Path.Combine(missingVersion, "licenses.json"), """{ "licenses": [] }""");
            await File.WriteAllTextAsync(Path.Combine(missingVersion, "exceptions.json"), """{ "exceptions": [] }""");

            var versions = SpdxStore.ListInstalledVersions(root);

            await Assert.That(versions).IsEquivalentTo(["3.27.0"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Install_WithNoSelection_DoesNotSelectInstalledVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-store-{Guid.NewGuid():N}");

        try
        {
            await InstallAsync(root, "3.27.0");

            await Assert.That(SpdxStore.GetSelectedVersion(root)).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Use_WithInstalledVersion_SelectsVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-store-{Guid.NewGuid():N}");

        try
        {
            await InstallAsync(root, "3.27.0");

            SpdxStore.Use(root, "3.27.0");

            await Assert.That(SpdxStore.GetSelectedVersion(root)).IsEqualTo("3.27.0");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Use_WithBundled_ClearsSelectionAndPreservesInstallations()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-store-{Guid.NewGuid():N}");

        try
        {
            await InstallAsync(root, "3.27.0");
            SpdxStore.Use(root, "3.27.0");

            SpdxStore.Use(root, "bundled");

            await Assert.That(SpdxStore.GetSelectedVersion(root)).IsNull();
            await Assert.That(SpdxStore.ListInstalledVersions(root)).IsEquivalentTo(["3.27.0"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Use_WithRootedInstalledDirectory_RejectsPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-store-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"ol-spdx-outside-{Guid.NewGuid():N}");

        try
        {
            await InstallAsync(outside, "3.27.0");

            await Assert.That(() => SpdxStore.Use(root, Path.Combine(outside, "3.27.0"))).Throws<DirectoryNotFoundException>();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    public async Task GetSelectedVersion_WithInvalidSelection_ReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "current.txt"), "../outside");

            await Assert.That(SpdxStore.GetSelectedVersion(root)).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task InstallAsync(string root, string version)
    {
        var licenses = Encoding.UTF8.GetBytes($$"""{ "licenseListVersion": "{{version}}", "licenses": [ { "licenseId": "MIT" } ] }""");
        var exceptions = """{ "exceptions": [ { "licenseExceptionId": "LLVM-exception" } ] }"""u8.ToArray();
        await SpdxStore.InstallAsync(root, licenses, exceptions);
    }
}
