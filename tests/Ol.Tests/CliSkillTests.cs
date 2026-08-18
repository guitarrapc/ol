using System.Diagnostics;
using System.Text.Json;

namespace Ol.Tests;

public sealed class CliSkillTests
{
    private static readonly SemaphoreSlim CliGate = new(1, 1);

    [Test]
    public async Task SkillInstall_CodexTarget_WritesSkillPackage()
    {
        var directory = CreateTempDirectory();
        try
        {
            var result = await RunOlAsync(directory, "skill", "install", "--target", "codex");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();

            var skillDirectory = Path.Combine(directory, ".agents", "skills", "license-scan");
            await Assert.That(File.Exists(Path.Combine(skillDirectory, "SKILL.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(skillDirectory, "agents", "openai.yaml"))).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"))).Contains("name: license-scan");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Test]
    public async Task SkillExportPlugin_Default_WritesPortableAgentPlugin()
    {
        var directory = CreateTempDirectory();
        try
        {
            var output = Path.Combine(directory, "ol-plugin");
            var result = await RunOlAsync(directory, "skill", "export-plugin", "--output", output);

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(File.Exists(Path.Combine(output, "skills", "license-scan", "SKILL.md"))).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(output, ".claude-plugin"))).IsFalse();

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "plugin.json")));
            await Assert.That(manifest.RootElement.GetProperty("$schema").GetString()).IsEqualTo("https://agent-plugins.org/schemas/1.0.0/plugin.schema.json");
            await Assert.That(manifest.RootElement.GetProperty("name").GetString()).IsEqualTo("ol");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Test]
    public async Task SkillExportPlugin_WithClaude_WritesClaudeAdapterWithoutDuplicatingSkill()
    {
        var directory = CreateTempDirectory();
        try
        {
            var output = Path.Combine(directory, "ol-plugin");
            var result = await RunOlAsync(directory, "skill", "export-plugin", "--output", output, "--with-claude");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Stderr).IsEmpty();
            await Assert.That(File.Exists(Path.Combine(output, "skills", "license-scan", "SKILL.md"))).IsTrue();

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, ".claude-plugin", "plugin.json")));
            await Assert.That(manifest.RootElement.GetProperty("name").GetString()).IsEqualTo("ol");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Test]
    public async Task SkillInstall_ClaudeTarget_WritesClaudeSkill()
    {
        var directory = CreateTempDirectory();
        try
        {
            var result = await RunOlAsync(directory, "skill", "install", "--target", "claude");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(File.Exists(Path.Combine(directory, ".claude", "skills", "license-scan", "SKILL.md"))).IsTrue();
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Test]
    public async Task SkillInstall_ExistingDirectoryWithoutForce_PreservesExistingFiles()
    {
        var directory = CreateTempDirectory();
        try
        {
            var skillDirectory = Path.Combine(directory, ".agents", "skills", "license-scan");
            Directory.CreateDirectory(skillDirectory);
            var skillPath = Path.Combine(skillDirectory, "SKILL.md");
            await File.WriteAllTextAsync(skillPath, "existing");

            var result = await RunOlAsync(directory, "skill", "install");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("already exists");
            await Assert.That(await File.ReadAllTextAsync(skillPath)).IsEqualTo("existing");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Test]
    public async Task SkillInstall_ExistingDirectoryWithForce_ReplacesWholePackage()
    {
        var directory = CreateTempDirectory();
        try
        {
            var skillDirectory = Path.Combine(directory, ".agents", "skills", "license-scan");
            Directory.CreateDirectory(skillDirectory);
            await File.WriteAllTextAsync(Path.Combine(skillDirectory, "stale.txt"), "stale");

            var result = await RunOlAsync(directory, "skill", "install", "--force");

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(File.Exists(Path.Combine(skillDirectory, "SKILL.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(skillDirectory, "stale.txt"))).IsFalse();
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Test]
    public async Task SkillInstall_UnknownTarget_ReturnsCommandError()
    {
        var directory = CreateTempDirectory();
        try
        {
            var result = await RunOlAsync(directory, "skill", "install", "--target", "unknown");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr.Trim()).IsEqualTo("Skill target must be codex or claude.");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Test]
    public async Task SkillExportPlugin_WithoutOutput_ReturnsArgumentError()
    {
        var directory = CreateTempDirectory();
        try
        {
            var result = await RunOlAsync(directory, "skill", "export-plugin");

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(result.Stderr).Contains("output");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOlAsync(string workingDirectory, params string[] args)
    {
        await CliGate.WaitAsync();
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(CliTestAssembly.ResolveOlDllPath(AppContext.BaseDirectory));
            for (var i = 0; i < args.Length; i++) startInfo.ArgumentList.Add(args[i]);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ol CLI.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            CliGate.Release();
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Ol.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
