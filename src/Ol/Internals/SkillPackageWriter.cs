using System.Text;

namespace Ol.Internals;

internal static class SkillPackageWriter
{
    private static readonly byte[] AgentPluginManifest = Encoding.UTF8.GetBytes("""
        {
          "$schema": "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
          "name": "ol",
          "description": "Scan resolved dependencies and evaluate license evidence with ol."
        }

        """);

    private static readonly byte[] ClaudePluginManifest = Encoding.UTF8.GetBytes("""
        {
          "$schema": "https://json.schemastore.org/claude-code-plugin-manifest.json",
          "name": "ol",
          "description": "Scan resolved dependencies and evaluate license evidence with ol."
        }

        """);

    public static void Install(string destination, bool force)
        => Install(destination, force, Directory.Delete);

    internal static void Install(string destination, bool force, Action<string, bool> deleteDirectory)
        => WriteAtomically(destination, force, static staging => WriteSkill(staging), deleteDirectory);

    public static void ExportPlugin(string destination, bool withClaude, bool force)
        => WriteAtomically(destination, force, staging =>
        {
            WriteFile(staging, "plugin.json", AgentPluginManifest);
            WriteSkill(Path.Combine(staging, "skills", "license-scan"));
            if (withClaude)
            {
                WriteFile(staging, ".claude-plugin/plugin.json", ClaudePluginManifest);
            }
        }, Directory.Delete);

    private static void WriteSkill(string destination)
    {
        var resources = SkillResources.ReadAll();
        if (resources.Length == 0)
        {
            throw new InvalidOperationException("No embedded license-scan skill resources were found.");
        }

        for (var i = 0; i < resources.Length; i++)
        {
            WriteFile(destination, resources[i].RelativePath, resources[i].Content);
        }
    }

    private static void WriteAtomically(string destination, bool force, Action<string> write, Action<string, bool> deleteDirectory)
    {
        ArgumentNullException.ThrowIfNull(deleteDirectory);
        var fullDestination = Path.GetFullPath(destination);
        if (File.Exists(fullDestination))
        {
            throw new IOException($"Output path exists and is a file: {fullDestination}");
        }
        if (Directory.Exists(fullDestination) && !force)
        {
            throw new IOException($"Output directory already exists: {fullDestination}. Use --force to replace it.");
        }

        var parent = Path.GetDirectoryName(fullDestination)
            ?? throw new IOException($"Output directory has no parent: {fullDestination}");
        Directory.CreateDirectory(parent);
        var name = Path.GetFileName(fullDestination);
        var suffix = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(parent, $".{name}.{suffix}.tmp");
        var backup = Path.Combine(parent, $".{name}.{suffix}.bak");
        var committed = false;

        try
        {
            Directory.CreateDirectory(staging);
            write(staging);
            if (Directory.Exists(fullDestination))
            {
                Directory.Move(fullDestination, backup);
            }
            try
            {
                Directory.Move(staging, fullDestination);
                committed = true;
            }
            catch
            {
                if (!Directory.Exists(fullDestination) && Directory.Exists(backup))
                {
                    Directory.Move(backup, fullDestination);
                }
                throw;
            }
        }
        finally
        {
            TryDeleteDirectory(staging, deleteDirectory);
            if (committed)
            {
                TryDeleteDirectory(backup, deleteDirectory);
            }
        }
    }

    private static void TryDeleteDirectory(string path, Action<string, bool> deleteDirectory)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            deleteDirectory(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteFile(string root, string relativePath, byte[] content)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException($"Embedded skill path escapes its output directory: {relativePath}");
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(fullPath, content);
    }
}
