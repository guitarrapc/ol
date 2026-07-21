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
}
