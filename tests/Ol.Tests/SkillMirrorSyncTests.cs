namespace Ol.Tests;

public sealed class SkillMirrorSyncTests
{
    [Test]
    [Arguments(".agents")]
    [Arguments(".claude")]
    public async Task LicenseScanSkill_ProjectMirror_MatchesEmbeddedSource(string agentDirectory)
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src", "Ol", "Skills", "license-scan");
        var mirrorRoot = Path.Combine(repositoryRoot, agentDirectory, "skills", "license-scan");

        var sourceFiles = GetRelativeFiles(sourceRoot);
        var mirrorFiles = GetRelativeFiles(mirrorRoot);
        await Assert.That(mirrorFiles).IsEquivalentTo(sourceFiles);
        for (var i = 0; i < sourceFiles.Length; i++)
        {
            var relativePath = sourceFiles[i];
            await Assert.That(await File.ReadAllBytesAsync(Path.Combine(mirrorRoot, relativePath)))
                .IsEquivalentTo(await File.ReadAllBytesAsync(Path.Combine(sourceRoot, relativePath)));
        }
    }

    private static string[] GetRelativeFiles(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ol.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
