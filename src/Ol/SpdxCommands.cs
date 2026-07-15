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
        Console.WriteLine($"active: {SpdxStore.GetActiveVersion()}");
        Console.WriteLine($"user-data: {SpdxStore.DefaultRoot}");
    }

    /// <summary>
    /// List installed SPDX data versions.
    /// </summary>
    [Command("list")]
    public void List()
    {
        var active = SpdxStore.GetActiveVersion();
        Console.WriteLine(active == "bundled" ? "* bundled" : "  bundled");
        var versions = SpdxStore.ListInstalledVersions();
        for (var i = 0; i < versions.Length; i++)
        {
            Console.WriteLine(string.Equals(active, versions[i], StringComparison.OrdinalIgnoreCase) ? $"* {versions[i]}" : $"  {versions[i]}");
        }
    }

    /// <summary>
    /// Download SPDX data into the user data directory.
    /// </summary>
    [Command("update")]
    public async Task<int> Update(CancellationToken cancellationToken = default)
    {
        var version = await SpdxStore.UpdateAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"updated: {version}");
        return 0;
    }

    /// <summary>
    /// Switch active SPDX data version.
    /// </summary>
    /// <param name="version">Version to activate.</param>
    [Command("use")]
    public void Use(string version)
    {
        SpdxStore.Use(version);
        Console.WriteLine($"active: {version}");
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
}
