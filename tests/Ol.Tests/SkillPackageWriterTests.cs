using Ol.Internals;

namespace Ol.Tests;

public sealed class SkillPackageWriterTests
{
    [Test]
    public async Task Install_CleanupFailsAfterCommit_PreservesSuccessfulReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "Ol.Tests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "license-scan");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "stale.txt"), "stale");

        try
        {
            var cleanupAttempts = 0;
            SkillPackageWriter.Install(destination, force: true, (_, _) =>
            {
                cleanupAttempts++;
                throw new IOException("Simulated transient cleanup failure.");
            });

            await Assert.That(cleanupAttempts).IsGreaterThan(0);
            await Assert.That(File.Exists(Path.Combine(destination, "SKILL.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(destination, "stale.txt"))).IsFalse();
            await Assert.That(Directory.GetDirectories(root, ".license-scan.*.bak")).Count().IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
