using System.Text.Json;

namespace Ol.Core;

/// <summary>
/// Manages user-installed SPDX data selected at runtime.
/// </summary>
public static class SpdxStore
{
    private const string LicensesUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/licenses.json";
    private const string ExceptionsUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/exceptions.json";

    /// <summary>Gets the default user data directory.</summary>
    public static string DefaultRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "spdx");

    /// <summary>Downloads and activates the current SPDX JSON data.</summary>
    public static async Task<string> UpdateAsync(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient();
        var licenses = await http.GetByteArrayAsync(LicensesUrl, cancellationToken).ConfigureAwait(false);
        var exceptions = await http.GetByteArrayAsync(ExceptionsUrl, cancellationToken).ConfigureAwait(false);
        var version = ReadLicenseListVersion(licenses);
        var versionDirectory = Path.Combine(DefaultRoot, version);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, "licenses.json"), licenses, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, "exceptions.json"), exceptions, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(DefaultRoot, "current.txt"), version, cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <summary>Attempts to get the active user-installed SPDX directory.</summary>
    public static bool TryGetActiveDirectory(out string directory)
    {
        directory = string.Empty;
        var currentPath = Path.Combine(DefaultRoot, "current.txt");
        if (!File.Exists(currentPath))
        {
            return false;
        }

        var version = File.ReadAllText(currentPath).Trim();
        if (version.Length == 0)
        {
            return false;
        }

        var candidate = Path.Combine(DefaultRoot, version);
        if (!File.Exists(Path.Combine(candidate, "licenses.json")) || !File.Exists(Path.Combine(candidate, "exceptions.json")))
        {
            return false;
        }

        directory = candidate;
        return true;
    }

    /// <summary>Gets the active SPDX version or the generated data version.</summary>
    public static string GetActiveVersion()
    {
        var currentPath = Path.Combine(DefaultRoot, "current.txt");
        return File.Exists(currentPath) ? File.ReadAllText(currentPath).Trim() : SpdxGeneratedLicenseData.LicenseListVersion;
    }

    /// <summary>Lists installed SPDX versions.</summary>
    public static string[] ListInstalledVersions() => ListInstalledVersions(DefaultRoot);

    /// <summary>Lists SPDX versions installed under a specific root directory.</summary>
    /// <param name="root">The SPDX data root directory.</param>
    /// <returns>Ordinal-ignore-case sorted installed version names.</returns>
    public static string[] ListInstalledVersions(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var versions = Directory.GetDirectories(root);
        for (var i = 0; i < versions.Length; i++)
        {
            versions[i] = Path.GetFileName(versions[i]);
        }

        Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
        return versions;
    }

    /// <summary>Activates an installed SPDX version.</summary>
    public static void Use(string version)
    {
        var directory = Path.Combine(DefaultRoot, version);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"SPDX version is not installed: {version}");
        }

        Directory.CreateDirectory(DefaultRoot);
        File.WriteAllText(Path.Combine(DefaultRoot, "current.txt"), version);
    }

    /// <summary>Removes user-installed SPDX data.</summary>
    public static void Clear()
    {
        if (Directory.Exists(DefaultRoot))
        {
            Directory.Delete(DefaultRoot, recursive: true);
        }
    }

    private static string ReadLicenseListVersion(byte[] licensesJson)
    {
        using var document = JsonDocument.Parse(licensesJson);
        return document.RootElement.GetProperty("licenseListVersion").GetString() ?? "unknown";
    }
}
