using ConsoleAppFramework;
using Ol.Internals;

/// <summary>
/// Install or export the bundled license-scan agent skill.
/// </summary>
internal sealed class SkillCommands
{
    /// <summary>
    /// Install the skill into the current workspace.
    /// </summary>
    /// <param name="target">Target agent: codex or claude.</param>
    /// <param name="output">Override the destination skill directory.</param>
    /// <param name="force">Replace an existing destination directory.</param>
    [Command("install")]
    public int Install(string target = "codex", string? output = null, bool force = false)
    {
        var destination = ResolveInstallDestination(target, output, Directory.GetCurrentDirectory());
        if (destination is null)
        {
            Console.Error.WriteLine("Skill target must be codex or claude.");
            return 1;
        }

        return Write(destination, force, static (path, replace) => SkillPackageWriter.Install(path, replace), $"Skill installed to {destination}");
    }

    /// <summary>
    /// Export a portable Agent Plugin package.
    /// </summary>
    /// <param name="output">Destination plugin directory.</param>
    /// <param name="withClaude">Include a Claude Code plugin manifest adapter.</param>
    /// <param name="force">Replace an existing destination directory.</param>
    [Command("export-plugin")]
    public int ExportPlugin(string output, bool withClaude = false, bool force = false)
    {
        var destination = Path.GetFullPath(output, Directory.GetCurrentDirectory());
        return Write(destination, force, (path, replace) => SkillPackageWriter.ExportPlugin(path, withClaude, replace), $"Agent Plugin exported to {destination}");
    }

    private static string? ResolveInstallDestination(string target, string? output, string currentDirectory)
    {
        if (target is not ("codex" or "claude"))
        {
            return null;
        }
        if (output is not null)
        {
            return Path.GetFullPath(output, currentDirectory);
        }

        return target == "codex"
            ? Path.Combine(currentDirectory, ".agents", "skills", "license-scan")
            : Path.Combine(currentDirectory, ".claude", "skills", "license-scan");
    }

    private static int Write(string destination, bool force, Action<string, bool> write, string successMessage)
    {
        try
        {
            write(destination, force);
            Console.WriteLine(successMessage);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
