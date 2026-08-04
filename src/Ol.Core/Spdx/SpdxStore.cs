using System.Text.Json;
using Ol.Core.Generated;

namespace Ol.Core.Spdx;

/// <summary>
/// Manages user-installed SPDX data selected at runtime.
/// </summary>
public static class SpdxStore
{
    private const string LicensesUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/licenses.json";
    private const string ExceptionsUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/exceptions.json";

    /// <summary>Gets the default user data directory.</summary>
    public static string DefaultRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "spdx");

    /// <summary>Gets the version of the SPDX data bundled with the CLI.</summary>
    public static string BundledVersion => SpdxGeneratedLicenseData.LicenseListVersion;

    /// <summary>Downloads and installs the current SPDX JSON data without changing the active selection.</summary>
    public static async Task<string> UpdateAsync(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient();
        var licenses = await http.GetByteArrayAsync(LicensesUrl, cancellationToken).ConfigureAwait(false);
        var exceptions = await http.GetByteArrayAsync(ExceptionsUrl, cancellationToken).ConfigureAwait(false);
        return await InstallAsync(DefaultRoot, licenses, exceptions, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> InstallAsync(string root, byte[] licenses, byte[] exceptions, CancellationToken cancellationToken = default)
    {
        var version = ReadLicenseListVersion(licenses);
        if (!IsVersionName(version))
        {
            throw new InvalidDataException($"SPDX License List version is not a valid version name: {version}");
        }

        var versionDirectory = Path.Combine(root, version);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, "licenses.json"), licenses, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, "exceptions.json"), exceptions, cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <summary>Attempts to get the active user-installed SPDX directory.</summary>
    public static bool TryGetActiveDirectory(out string directory)
        => TryGetActiveDirectory(DefaultRoot, out directory);

    internal static bool TryGetActiveDirectory(string root, out string directory)
    {
        directory = string.Empty;
        var selectedVersion = GetSelectedVersion(root);
        if (selectedVersion is null)
        {
            return false;
        }

        directory = Path.Combine(root, selectedVersion);
        return true;
    }

    /// <summary>Gets the selected user-managed SPDX version, or null when bundled data is selected.</summary>
    public static string? GetSelectedVersion() => GetSelectedVersion(DefaultRoot);

    internal static string? GetSelectedVersion(string root)
    {
        var currentPath = Path.Combine(root, "current.txt");
        if (!File.Exists(currentPath))
        {
            return null;
        }

        var version = File.ReadAllText(currentPath).Trim();
        if (!IsVersionName(version))
        {
            return null;
        }

        var versions = ListInstalledVersions(root);
        for (var i = 0; i < versions.Length; i++)
        {
            if (string.Equals(version, versions[i], StringComparison.OrdinalIgnoreCase))
            {
                return versions[i];
            }
        }

        return null;
    }

    /// <summary>Gets the active SPDX version or the generated data version.</summary>
    public static string GetActiveVersion()
        => GetSelectedVersion() ?? BundledVersion;

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

        var directories = Directory.GetDirectories(root);
        var versions = new List<string>(directories.Length);
        for (var i = 0; i < directories.Length; i++)
        {
            var version = Path.GetFileName(directories[i]);
            if (IsInstalledVersionDirectory(directories[i], version))
            {
                versions.Add(version);
            }
        }

        versions.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. versions];
    }

    /// <summary>Activates an installed SPDX version.</summary>
    public static void Use(string version)
        => Use(DefaultRoot, version);

    internal static void Use(string root, string version)
    {
        var currentPath = Path.Combine(root, "current.txt");
        if (string.Equals(version, "bundled", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(currentPath))
            {
                File.Delete(currentPath);
            }
            return;
        }

        var versions = ListInstalledVersions(root);
        string? installedVersion = null;
        for (var i = 0; i < versions.Length; i++)
        {
            if (string.Equals(version, versions[i], StringComparison.OrdinalIgnoreCase))
            {
                installedVersion = versions[i];
                break;
            }
        }

        if (installedVersion is null)
        {
            throw new DirectoryNotFoundException($"SPDX version is not installed: {version}");
        }

        Directory.CreateDirectory(root);
        File.WriteAllText(currentPath, installedVersion);
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
        if (!document.RootElement.TryGetProperty("licenseListVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.String
            || versionElement.GetString() is not { Length: > 0 } version)
        {
            throw new InvalidDataException("SPDX licenses.json must contain a non-empty licenseListVersion string.");
        }

        return version;
    }

    private static bool IsInstalledVersionDirectory(string directory, string version)
    {
        if (!IsVersionName(version))
        {
            return false;
        }

        var licensesPath = Path.Combine(directory, "licenses.json");
        var exceptionsPath = Path.Combine(directory, "exceptions.json");
        if (!File.Exists(licensesPath) || !File.Exists(exceptionsPath))
        {
            return false;
        }

        try
        {
            var licenses = File.ReadAllBytes(licensesPath);
            return string.Equals(ReadLicenseListVersion(licenses), version, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsVersionName(string version)
        => version.Length > 0
            && version is not "." and not ".."
            && !string.Equals(version, "bundled", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(version)
            && version.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !version.Contains(Path.DirectorySeparatorChar)
            && !version.Contains(Path.AltDirectorySeparatorChar);
}
