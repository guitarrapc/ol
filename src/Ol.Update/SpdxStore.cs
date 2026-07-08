using System.Text.Json;

namespace Ol.Update;

public static class SpdxStore
{
    private const string LicensesUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/licenses.json";
    private const string ExceptionsUrl = "https://raw.githubusercontent.com/spdx/license-list-data/main/json/exceptions.json";

    public static string DefaultRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ol", "spdx");

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

    public static string GetActiveVersion()
    {
        var currentPath = Path.Combine(DefaultRoot, "current.txt");
        return File.Exists(currentPath) ? File.ReadAllText(currentPath).Trim() : "bundled";
    }

    public static string[] ListInstalledVersions()
    {
        if (!Directory.Exists(DefaultRoot))
        {
            return [];
        }

        return Directory.GetDirectories(DefaultRoot)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrEmpty(name))
            .Select(static name => name!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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
