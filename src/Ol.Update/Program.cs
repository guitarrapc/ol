using Ol.Update;

const string licensesUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/licenses.json";
const string exceptionsUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/exceptions.json";

if (args is not ["generate"])
{
    Console.Error.WriteLine("Usage: ol-update generate");
    return 1;
}

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var outputPath = Path.Combine(repositoryRoot, "src", "Ol.Core", "Generated", "SpdxGeneratedLicenseData.g.cs");
using var http = new HttpClient();
var licenses = await http.GetByteArrayAsync(licensesUrl);
var exceptions = await http.GetByteArrayAsync(exceptionsUrl);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, SpdxCodeGenerator.Generate(licenses, exceptions));
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
