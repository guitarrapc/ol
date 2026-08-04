using ConsoleAppFramework;
using Ol.Core.Spdx;

/// <summary>
/// Manage SPDX data.
/// </summary>
internal sealed class SpdxCommands
{
    /// <summary>
    /// Show the active SPDX data source.
    /// </summary>
    [Command("version")]
    public void Version()
    {
        WriteVersion(Console.Out, SpdxStore.GetSelectedVersion(), SpdxStore.BundledVersion);
    }

    /// <summary>
    /// List installed SPDX data versions.
    /// </summary>
    [Command("list")]
    public void List()
    {
        WriteList(Console.Out, SpdxStore.GetSelectedVersion(), SpdxStore.BundledVersion, SpdxStore.ListInstalledVersions());
    }

    /// <summary>
    /// Download SPDX data into the user data directory.
    /// </summary>
    [Command("update")]
    public async Task<int> Update(CancellationToken cancellationToken = default)
    {
        var version = await SpdxStore.UpdateAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"installed: {version}");
        return 0;
    }

    /// <summary>
    /// Switch active SPDX data version.
    /// </summary>
    /// <param name="version">Installed version to activate, or bundled.</param>
    [Command("use")]
    public void Use([Argument] string version)
    {
        SpdxStore.Use(version);
        var selectedVersion = SpdxStore.GetSelectedVersion();
        var activeVersion = selectedVersion ?? SpdxStore.BundledVersion;
        Console.WriteLine($"active: {activeVersion} ({(selectedVersion is null ? "bundled" : "user")})");
    }

    /// <summary>
    /// Clear user-managed SPDX data.
    /// </summary>
    [Command("clear")]
    public void Clear()
    {
        SpdxStore.Clear();
        Console.WriteLine("cleared");
    }

    internal static void WriteVersion(TextWriter writer, string? selectedVersion, string bundledVersion)
    {
        writer.WriteLine($"active: {selectedVersion ?? bundledVersion} ({(selectedVersion is null ? "bundled" : "user")})");
        writer.WriteLine($"user-selected: {selectedVersion ?? "none"}");
        writer.WriteLine($"bundled: {bundledVersion}");
    }

    internal static void WriteList(TextWriter writer, string? selectedVersion, string bundledVersion, string[] installedVersions)
    {
        writer.WriteLine($"{(selectedVersion is null ? '*' : ' ')} {bundledVersion} (bundled)");
        for (var i = 0; i < installedVersions.Length; i++)
        {
            var marker = string.Equals(selectedVersion, installedVersions[i], StringComparison.OrdinalIgnoreCase) ? '*' : ' ';
            writer.WriteLine($"{marker} {installedVersions[i]} (user)");
        }
    }
}
