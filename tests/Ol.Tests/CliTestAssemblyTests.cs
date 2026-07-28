namespace Ol.Tests;

public sealed class CliTestAssemblyTests
{
    [Test]
    public async Task ResolveOlDllPath_UsesOlDllCopiedNextToTestAssembly()
    {
        var testBaseDirectory = Path.Combine(Path.GetFullPath("artifacts"), "Ol.Tests", "release");

        var result = CliTestAssembly.ResolveOlDllPath(testBaseDirectory);

        await Assert.That(result).IsEqualTo(Path.Combine(testBaseDirectory, "ol.dll"));
    }
}
