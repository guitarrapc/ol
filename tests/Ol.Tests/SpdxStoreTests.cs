using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ol.Core.Generated;
using Ol.Core.Spdx;
using Ol.Internals;

namespace Ol.Tests;

public sealed class SpdxStoreTests
{
    private const string CoreFxMit = """
        The MIT License (MIT)

        Copyright (c) .NET Foundation and Contributors

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    [Test]
    public async Task Data_Bundled_ConstructsMatcherFromBundledCorpus()
    {
        var data = SpdxData.Load(null);

        await Assert.That(data.Matcher.CorpusVersion).IsEqualTo(data.LicenseListVersion);
        await Assert.That(data.Matcher.UnanchoredTemplateCount).IsLessThanOrEqualTo(10);
        await Assert.That(data.Matcher.TryMatch(Encoding.UTF8.GetBytes(CoreFxMit), out var id)).IsTrue();
        await Assert.That(id).IsEqualTo("MIT");
    }

    [Test]
    public async Task Data_Bundled_IndexPreservesUtf8LookupBehavior()
    {
        var index = SpdxData.Load(null).Index;

        await Assert.That(index.TryNormalizeLicenseIdUtf8("mit"u8, out var licenseId)).IsTrue();
        await Assert.That(licenseId).IsEqualTo("MIT");
        await Assert.That(index.TryNormalizeLicenseIdUtf8Slice("mit"u8, out var licenseUtf8, out var deprecated)).IsTrue();
        await Assert.That(licenseUtf8.ToString()).IsEqualTo("MIT");
        await Assert.That(deprecated).IsFalse();
        await Assert.That(index.TryNormalizeExceptionIdUtf8("classpath-exception-2.0"u8, out var exceptionId)).IsTrue();
        await Assert.That(exceptionId).IsEqualTo("Classpath-exception-2.0");
        await Assert.That(index.TryNormalizeLicenseNameUtf8Slice("MIT License"u8, out var nameLicenseId, out _)).IsTrue();
        await Assert.That(nameLicenseId.ToString()).IsEqualTo("MIT");
        await Assert.That(index.TryResolveLicenseUrl("https://www.apache.org/licenses/LICENSE-2.0"u8, out var urlLicenseId, out _)).IsTrue();
        await Assert.That(urlLicenseId.ToString()).IsEqualTo("Apache-2.0");
    }

    [Test]
    public async Task Data_Bundled_IndexKeepsEveryGeneratedUtf8IdentifierAligned()
    {
        var index = SpdxData.Load(null).Index;
        for (var i = 0; i < SpdxGeneratedLicenseData.LicenseIds.Length; i++)
        {
            var expected = SpdxGeneratedLicenseData.LicenseIds[i];
            var bytes = Encoding.UTF8.GetBytes(expected);

            await Assert.That(index.TryNormalizeLicenseIdUtf8Slice(bytes, out var actual)).IsTrue();
            await Assert.That(actual.ToString()).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task ScanPreparation_NormalScanCarriesBundledMatcher()
    {
        var input = Path.GetTempFileName();
        try
        {
            var prepared = ScanExecution.TryPrepare([input], "cyclonedx", null, null, noExternalEvidence: true, concurrency: 1, retry: 0, out var preparation, out var error);

            await Assert.That(prepared).IsTrue().Because(error);
            await Assert.That(preparation.Spdx.Matcher.CorpusVersion).IsEqualTo(preparation.Spdx.LicenseListVersion);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Test]
    public async Task Data_UserDirectoryWithCorpus_ConstructsVersionMatchedMatcher()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-corpus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "licenses.json"), """{ "licenseListVersion": "test", "licenses": [ { "licenseId": "Example" } ] }""");
        await File.WriteAllTextAsync(Path.Combine(root, "exceptions.json"), """{ "exceptions": [] }""");
        var corpus = SpdxLicenseTextCorpus.Create("test", [new("Example", "example terms")]);
        await File.WriteAllBytesAsync(Path.Combine(root, SpdxLicenseTextCorpus.FileName), corpus);

        try
        {
            var data = SpdxData.Load(root);

            await Assert.That(data.Matcher.TryMatch("example terms"u8, out var id)).IsTrue();
            await Assert.That(id).IsEqualTo("Example");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Data_UserDirectoryWithMismatchedCorpus_RejectsSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-corpus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "licenses.json"), """{ "licenseListVersion": "list", "licenses": [] }""");
        await File.WriteAllTextAsync(Path.Combine(root, "exceptions.json"), """{ "exceptions": [] }""");
        await File.WriteAllBytesAsync(Path.Combine(root, SpdxLicenseTextCorpus.FileName), SpdxLicenseTextCorpus.Create("other", [new("Example", "terms")]));

        try
        {
            await Assert.That(() => SpdxData.Load(root)).Throws<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Data_UserDirectoryWithMalformedTemplate_RejectsSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-corpus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "licenses.json"), """{ "licenseListVersion": "test", "licenses": [ { "licenseId": "Example" } ] }""");
        await File.WriteAllTextAsync(Path.Combine(root, "exceptions.json"), """{ "exceptions": [] }""");
        var corpus = SpdxLicenseTextCorpus.Create("test", [new("Example", "<<beginOptional>>unclosed")]);
        await File.WriteAllBytesAsync(Path.Combine(root, SpdxLicenseTextCorpus.FileName), corpus);

        try
        {
            await Assert.That(() => SpdxData.Load(root)).Throws<ArgumentException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    // The licenses digest covers names as well as identifiers, because both decide what a value
    // resolves to and the file digest used for installed data already distinguishes them.
    public async Task Data_Bundled_ReportsDigestsOfTheGeneratedData()
    {
        var data = SpdxData.Load(null);

        await Assert.That(data.Source).IsEqualTo("bundled");
        await Assert.That(data.GetLicensesSha256()).IsEqualTo(ExpectedDigest([.. SpdxGeneratedLicenseData.LicenseIds, .. SpdxGeneratedLicenseData.LicenseNames, .. SpdxGeneratedLicenseData.SeeAlsoUrls, .. SpdxGeneratedLicenseData.SeeAlsoLicenseIds]));
        await Assert.That(data.GetExceptionsSha256()).IsEqualTo(ExpectedDigest(SpdxGeneratedLicenseData.ExceptionIds));
        await Assert.That(data.GetLicensesSha256()).IsNotEqualTo(ExpectedDigest(SpdxGeneratedLicenseData.LicenseIds));
        // A snapshot that moves a URL to another license must not keep the digest of the one it left.
        await Assert.That(data.GetLicensesSha256()).IsNotEqualTo(ExpectedDigest([.. SpdxGeneratedLicenseData.LicenseIds, .. SpdxGeneratedLicenseData.LicenseNames]));
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
    public async Task Install_WithCorpus_PersistsVersionMatchedCorpus()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-store-{Guid.NewGuid():N}");
        var licenses = """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "MIT" } ] }"""u8.ToArray();
        var exceptions = """{ "exceptions": [] }"""u8.ToArray();
        var corpus = SpdxLicenseTextCorpus.Create("3.27.0", [new("MIT", "MIT License")]);

        try
        {
            await SpdxStore.InstallAsync(root, licenses, exceptions, corpus);

            await Assert.That(File.Exists(Path.Combine(root, "3.27.0", SpdxLicenseTextCorpus.FileName))).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Install_WithMismatchedCorpusVersion_RejectsBeforePersistingSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-corpus-{Guid.NewGuid():N}");
        var licenses = """{ "licenseListVersion": "3.27.0", "licenses": [] }"""u8.ToArray();
        var exceptions = """{ "exceptions": [] }"""u8.ToArray();
        var corpus = SpdxLicenseTextCorpus.Create("3.28.0", [new("MIT", "MIT License")]);

        try
        {
            await Assert.That(async () => await SpdxStore.InstallAsync(root, licenses, exceptions, corpus))
                .Throws<InvalidDataException>();
            await Assert.That(Directory.Exists(Path.Combine(root, "3.27.0"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Install_WithInvalidUtf8Corpus_RejectsBeforePersistingSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-corpus-{Guid.NewGuid():N}");
        var licenses = """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "MIT" } ] }"""u8.ToArray();
        var exceptions = """{ "exceptions": [] }"""u8.ToArray();
        var corpus = CorruptLastDecompressedByte(SpdxLicenseTextCorpus.Create("3.27.0", [new("MIT", "MIT License")]));

        try
        {
            await Assert.That(async () => await SpdxStore.InstallAsync(root, licenses, exceptions, corpus))
                .Throws<InvalidDataException>();
            await Assert.That(Directory.Exists(Path.Combine(root, "3.27.0"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Install_WithTrailingCorpusData_RejectsBeforePersistingSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ol-spdx-corpus-{Guid.NewGuid():N}");
        var licenses = """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "MIT" } ] }"""u8.ToArray();
        var exceptions = """{ "exceptions": [] }"""u8.ToArray();
        var corpus = AppendDecompressedByte(SpdxLicenseTextCorpus.Create("3.27.0", [new("MIT", "MIT License")]));

        try
        {
            await Assert.That(async () => await SpdxStore.InstallAsync(root, licenses, exceptions, corpus))
                .Throws<InvalidDataException>();
            await Assert.That(Directory.Exists(Path.Combine(root, "3.27.0"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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

    private static byte[] CorruptLastDecompressedByte(byte[] corpus)
    {
        using var input = new MemoryStream(corpus, writable: false);
        using var decompressed = new MemoryStream();
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true)) brotli.CopyTo(decompressed);
        var bytes = decompressed.ToArray();
        bytes[^1] = 0xff;

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) brotli.Write(bytes);
        return output.ToArray();
    }

    private static byte[] AppendDecompressedByte(byte[] corpus)
    {
        using var input = new MemoryStream(corpus, writable: false);
        using var decompressed = new MemoryStream();
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true)) brotli.CopyTo(decompressed);
        decompressed.WriteByte(0);

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) decompressed.WriteTo(brotli);
        return output.ToArray();
    }
}
