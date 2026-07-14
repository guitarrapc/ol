using Ol.Update;

const string licensesUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/licenses.json";
const string exceptionsUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/exceptions.json";

if (args is not ["generate"])
{
    Console.Error.WriteLine("Usage: ol-update generate");
    return 1;
}

var outputPath = Path.Combine(Environment.CurrentDirectory, "src", "Ol.Core", "Generated", "SpdxGeneratedLicenseData.g.cs");
using var http = new HttpClient();
var licenses = await http.GetByteArrayAsync(licensesUrl);
var exceptions = await http.GetByteArrayAsync(exceptionsUrl);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, SpdxCodeGenerator.Generate(licenses, exceptions));
Console.WriteLine($"generated: {outputPath}");
return 0;
