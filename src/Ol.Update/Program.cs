using Ol.Update;

const string archiveUrl = "https://github.com/spdx/license-list-data/archive/refs/heads/main.zip";

if (args is not ["generate"])
{
    Console.Error.WriteLine("Usage: ol-update generate");
    return 1;
}

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var outputPath = Path.Combine(repositoryRoot, "src", "Ol.Core", "Generated", "SpdxGeneratedLicenseData.g.cs");
using var http = new HttpClient();
var archive = await http.GetByteArrayAsync(archiveUrl);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using (var archiveStream = new MemoryStream(archive, writable: false))
{
    var data = Ol.Core.Spdx.SpdxLicenseTextCorpus.LoadLicenseListArchive(archiveStream);
    await File.WriteAllTextAsync(outputPath, SpdxCodeGenerator.Generate(data.LicensesJson, data.ExceptionsJson));
    var corpusPath = Path.Combine(repositoryRoot, "src", "Ol.Core", "Generated", Ol.Core.Spdx.SpdxLicenseTextCorpus.FileName);
    await File.WriteAllBytesAsync(corpusPath, data.LicenseTextCorpus);
    Console.WriteLine($"generated: {corpusPath}");
}
Console.WriteLine($"generated: {outputPath}");
return 0;

static string FindRepositoryRoot(string startPath)
{
    for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new DirectoryNotFoundException("Could not find the Ol repository root (Ol.slnx).");
}
