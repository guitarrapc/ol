namespace Ol.Update.Tests;

public sealed class UpdateCommandTests
{
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

        await Assert.That(ids).IsEquivalentTo(["Apache-2.0", "MIT", "Zlib"]);
        await Assert.That(names).IsEquivalentTo(["Apache License 2.0", "MIT License", ""]);
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
}
