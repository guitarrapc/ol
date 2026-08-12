using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ol.Core.Generated;
using Ol.Core.Spdx;

/// <summary>
/// Describes how to derive the active SPDX data digests, without deriving them.
/// </summary>
/// <remarks>
/// Only the JSON report writes these digests, but <see cref="SpdxData"/> is built for every run.
/// Deriving them eagerly cost 33 KB per process — a joined string of every generated identifier plus
/// its UTF-8 copy, or a second full read of each installed SPDX file — for a value that text and
/// Markdown output never read. A run that writes JSON to both a file and standard output asks twice,
/// so each digest is retained after the first request.
/// </remarks>
internal sealed class SpdxDataDigest
{
    private readonly string? exceptionsPath;
    private readonly string? licensesPath;
    private string? exceptions;
    private string? licenses;

    private SpdxDataDigest(string? licensesPath, string? exceptionsPath)
    {
        this.licensesPath = licensesPath;
        this.exceptionsPath = exceptionsPath;
    }

    /// <summary>Describes the digests of the bundled generated identifiers.</summary>
    public static SpdxDataDigest ForGeneratedData() => new(null, null);

    /// <summary>Describes the digests of an installed SPDX data directory.</summary>
    public static SpdxDataDigest ForFiles(string licensesPath, string exceptionsPath) => new(licensesPath, exceptionsPath);

    /// <summary>Calculates the active licenses digest once per run.</summary>
    /// <remarks>
    /// Covers names and <c>seeAlso</c> URLs as well as identifiers, because all of them decide what a
    /// value resolves to. Hashing only the identifiers would give two snapshots that rename a license, or
    /// move a URL between licenses, the same digest.
    /// </remarks>
    public string GetLicensesSha256()
        => licenses ??= licensesPath is null
            ? ComputeGeneratedDataHash(SpdxGeneratedLicenseData.LicenseIds, SpdxGeneratedLicenseData.LicenseNames, SpdxGeneratedLicenseData.SeeAlsoUrls, SpdxGeneratedLicenseData.SeeAlsoLicenseIds)
            : HashFile(licensesPath);

    /// <summary>Calculates the active exceptions digest once per run.</summary>
    public string GetExceptionsSha256()
        => exceptions ??= exceptionsPath is null ? ComputeGeneratedDataHash(SpdxGeneratedLicenseData.ExceptionIds) : HashFile(exceptionsPath);

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string ComputeGeneratedDataHash(params string[][] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values.SelectMany(static value => value))))).ToLowerInvariant();
}

/// <remarks>
/// <see cref="Digest"/> is never null in practice: every instance comes from <see cref="Load"/>.
/// </remarks>
internal readonly record struct SpdxData(
    SpdxLicenseIndex Index,
    SpdxLicenseTextMatcher Matcher,
    string Source,
    string LicenseListVersion,
    string DataRef,
    SpdxDataDigest Digest)
{
    private static readonly Lazy<SpdxData> Bundled = new(CreateBundled, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Calculates the active licenses digest. Named as a method because the calculation is not free.</summary>
    public string GetLicensesSha256() => Digest.GetLicensesSha256();

    /// <summary>Calculates the active exceptions digest. Named as a method because the calculation is not free.</summary>
    public string GetExceptionsSha256() => Digest.GetExceptionsSha256();

    public static SpdxData Load(string? directory)
    {
        if (directory is not null and not "")
        {
            return LoadFromDirectory(directory, "cli-argument", "cli-argument");
        }

        if (SpdxStore.TryGetActiveDirectory(out var activeDirectory))
        {
            var version = SpdxStore.GetActiveVersion();
            return LoadFromDirectory(activeDirectory, "user", $"ol/spdx/{version}");
        }

        return Bundled.Value;
    }

    private static SpdxData CreateBundled()
    {
        return new SpdxData(
            new SpdxLicenseIndex(
                SpdxGeneratedLicenseData.LicenseIds,
                SpdxGeneratedLicenseData.ExceptionIds,
                SpdxGeneratedLicenseData.DeprecatedLicenseIds,
                SpdxGeneratedLicenseData.LicenseNames,
                SpdxGeneratedLicenseData.SeeAlsoUrls,
                SpdxGeneratedLicenseData.SeeAlsoLicenseIds),
            LoadBundledMatcher(),
            "bundled",
            SpdxGeneratedLicenseData.LicenseListVersion,
            "bundled/spdx/builtin",
            SpdxDataDigest.ForGeneratedData());
    }

    private static SpdxData LoadFromDirectory(string directory, string source, string dataRef)
    {
        var licensesPath = Path.Combine(directory, "licenses.json");
        var exceptionsPath = Path.Combine(directory, "exceptions.json");
        if (!File.Exists(licensesPath) || !File.Exists(exceptionsPath))
        {
            throw new DirectoryNotFoundException("SPDX data directory must contain licenses.json and exceptions.json.");
        }

        var licenses = ReadSpdxData(licensesPath, "licenses", "licenseId");
        var exceptions = ReadSpdxData(exceptionsPath, "exceptions", "licenseExceptionId");
        var matcher = LoadMatcher(directory, licenses.Version);
        return new SpdxData(
            new SpdxLicenseIndex(licenses.Ids, exceptions.Ids, licenses.DeprecatedIds, licenses.Names, licenses.SeeAlsoUrls, licenses.SeeAlsoIds),
            matcher,
            source,
            licenses.Version,
            dataRef,
            SpdxDataDigest.ForFiles(licensesPath, exceptionsPath));
    }

    private static SpdxLicenseTextMatcher LoadBundledMatcher()
    {
        using var stream = typeof(SpdxGeneratedLicenseData).Assembly.GetManifestResourceStream(SpdxLicenseTextCorpus.EmbeddedResourceName)
            ?? throw new InvalidOperationException("Bundled SPDX license-text corpus is missing.");
        var corpus = SpdxLicenseTextCorpus.Load(stream);
        if (!string.Equals(corpus.CorpusVersion, SpdxGeneratedLicenseData.LicenseListVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Bundled SPDX license-text corpus version does not match the generated license list.");
        }

        return new SpdxLicenseTextMatcher(corpus.CorpusVersion, corpus.Templates);
    }

    private static SpdxLicenseTextMatcher LoadMatcher(string directory, string version)
    {
        var path = Path.Combine(directory, SpdxLicenseTextCorpus.FileName);
        if (!File.Exists(path)) return new SpdxLicenseTextMatcher(version, []);
        using var stream = File.OpenRead(path);
        var corpus = SpdxLicenseTextCorpus.Load(stream);
        if (!string.Equals(corpus.CorpusVersion, version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("SPDX license-text corpus version does not match licenses.json.");
        }

        return new SpdxLicenseTextMatcher(corpus.CorpusVersion, corpus.Templates);
    }

    /// <summary>Reads identifiers, their SPDX names, and the deprecated set from one SPDX document.</summary>
    /// <remarks>
    /// <c>Names</c> shares its index with <c>Ids</c>, and an entry that states no name keeps an empty
    /// string so the two stay aligned. The exceptions document carries names too, but Ol resolves an
    /// exception only as an operand of <c>WITH</c>, where the operand is an identifier.
    /// </remarks>
    private static (string Version, string[] Ids, string[] Names, string[] DeprecatedIds, string[] SeeAlsoUrls, string[] SeeAlsoIds) ReadSpdxData(string path, string arrayName, string propertyName)
    {
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(SkipUtf8Bom(bytes));
        var values = document.RootElement.GetProperty(arrayName);
        var ids = new string[values.GetArrayLength()];
        var names = new string[ids.Length];
        var deprecatedIds = new List<string>();
        // One license publishes several URLs and one URL several licenses, so these cannot share the
        // identifier index.
        var seeAlsoUrls = new List<string>();
        var seeAlsoIds = new List<string>();
        var index = 0;
        foreach (var item in values.EnumerateArray())
        {
            var id = item.GetProperty(propertyName).GetString() ?? string.Empty;
            ids[index] = id;
            names[index] = item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty;
            if (item.TryGetProperty("isDeprecatedLicenseId", out var deprecated) && deprecated.ValueKind == JsonValueKind.True)
            {
                deprecatedIds.Add(id);
            }

            if (item.TryGetProperty("seeAlso", out var seeAlso) && seeAlso.ValueKind == JsonValueKind.Array)
            {
                foreach (var url in seeAlso.EnumerateArray())
                {
                    if (url.ValueKind != JsonValueKind.String) continue;

                    var value = url.GetString();
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    seeAlsoUrls.Add(value);
                    seeAlsoIds.Add(id);
                }
            }

            index++;
        }

        return (
            document.RootElement.TryGetProperty("licenseListVersion", out var version) ? version.GetString() ?? "unknown" : "unknown",
            ids,
            names,
            deprecatedIds.ToArray(),
            seeAlsoUrls.ToArray(),
            seeAlsoIds.ToArray());
    }

    private static ReadOnlyMemory<byte> SkipUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsMemory(3) : bytes;
}
