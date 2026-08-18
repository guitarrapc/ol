namespace Ol.Update.Tests;

public sealed class UpdateCommandTests
{
    [Test]
    public async Task LicenseListArchive_OneSnapshot_ProvidesListsAndVersionMatchedCorpus()
    {
        using var archiveBytes = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(archiveBytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "snapshot/json/licenses.json", """{ "licenseListVersion": "snapshot", "licenses": [ { "licenseId": "MIT" } ] }""");
            WriteEntry(archive, "snapshot/json/exceptions.json", """{ "exceptions": [] }""");
            WriteEntry(archive, "snapshot/json/details/MIT.json", """{ "licenseId": "MIT", "standardLicenseTemplate": "MIT License" }""");
        }

        archiveBytes.Position = 0;
        var data = Ol.Core.Spdx.SpdxLicenseTextCorpus.LoadLicenseListArchive(archiveBytes);
        var corpus = Ol.Core.Spdx.SpdxLicenseTextCorpus.Load(data.LicenseTextCorpus);

        await Assert.That(corpus.CorpusVersion).IsEqualTo("snapshot");
        await Assert.That(System.Text.Encoding.UTF8.GetString(data.LicensesJson)).Contains("snapshot");
        await Assert.That(corpus.Templates[0].LicenseId).IsEqualTo("MIT");
    }

    [Test]
    public async Task LicenseListArchive_MissingLicenseDetail_RejectsIncompleteCorpus()
    {
        using var archiveBytes = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(archiveBytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "snapshot/json/licenses.json", """{ "licenseListVersion": "snapshot", "licenses": [ { "licenseId": "MIT" }, { "licenseId": "Apache-2.0" } ] }""");
            WriteEntry(archive, "snapshot/json/exceptions.json", """{ "exceptions": [] }""");
            WriteEntry(archive, "snapshot/json/details/MIT.json", """{ "licenseId": "MIT", "standardLicenseTemplate": "MIT License" }""");
        }

        archiveBytes.Position = 0;

        await Assert.That(() => Ol.Core.Spdx.SpdxLicenseTextCorpus.LoadLicenseListArchive(archiveBytes))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Generate_SpdxJson_ProducesCoreGeneratedLicenseData()
    {
        var generated = SpdxCodeGenerator.Generate(
            """{ "licenseListVersion": "3.27.0", "licenses": [ { "licenseId": "MIT" }, { "licenseId": "Apache-2.0" } ] }"""u8,
            """{ "exceptions": [ { "licenseExceptionId": "Classpath-exception-2.0" } ] }"""u8);

        await Assert.That(generated).Contains("namespace Ol.Core.Generated;");
        await Assert.That(generated).Contains("\"3.27.0\"");
        await Assert.That(generated).Contains("\"MIT\"");
        await Assert.That(generated).Contains("\"Classpath-exception-2.0\"");
        await Assert.That(generated).Contains("LicenseIdsUtf8 => \"Apache-2.0\\nMIT\"u8;");
    }

    // The name array is read by index against the identifier array, so the two must stay aligned
    // through the sort. A license that states no name keeps an empty entry rather than shifting the rest.
    [Test]
    public async Task Generate_SpdxJson_EmitsLicenseNamesAlignedWithSortedIdentifiers()
    {
        var generated = SpdxCodeGenerator.Generate(
            """
            {
              "licenseListVersion": "3.27.0",
              "licenses": [
                { "licenseId": "MIT", "name": "MIT License" },
                { "licenseId": "Apache-2.0", "name": "Apache License 2.0" },
                { "licenseId": "Zlib" }
              ]
            }
            """u8,
            """{ "exceptions": [ { "licenseExceptionId": "Classpath-exception-2.0" } ] }"""u8);

        var ids = ReadArray(generated, "LicenseIds");
        var names = ReadArray(generated, "LicenseNames");

        await Assert.That(string.Join(" | ", ids.Zip(names, static (id, name) => $"{id}={name}")))
            .IsEqualTo("Apache-2.0=Apache License 2.0 | MIT=MIT License | Zlib=");
    }

    private static string[] ReadArray(string generated, string name)
    {
        var start = generated.IndexOf($"{name} =", StringComparison.Ordinal);
        var open = generated.IndexOf('[', start);
        var close = generated.IndexOf("];", open, StringComparison.Ordinal);
        return generated[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.Trim('"'))
            .ToArray();
    }

    private static void WriteEntry(System.IO.Compression.ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(content);
    }
}
