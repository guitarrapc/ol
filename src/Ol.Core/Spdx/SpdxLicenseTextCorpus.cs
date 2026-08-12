using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Ol.Core.Spdx;

/// <summary>Contains one decoded, versioned SPDX license-text template corpus.</summary>
public readonly record struct SpdxLicenseTextCorpusData(
    string CorpusVersion,
    SpdxLicenseTextTemplate[] Templates);

/// <summary>Contains one coherent SPDX repository archive snapshot.</summary>
public readonly record struct SpdxLicenseListArchiveData(
    byte[] LicensesJson,
    byte[] ExceptionsJson,
    byte[] LicenseTextCorpus);

/// <summary>Reads and writes Ol's bounded, Brotli-compressed SPDX template corpus.</summary>
public static class SpdxLicenseTextCorpus
{
    /// <summary>The installed corpus file name.</summary>
    public const string FileName = "license-texts.bin.br";

    /// <summary>The manifest resource name used by the bundled corpus.</summary>
    public const string EmbeddedResourceName = "Ol.Core.Generated.license-texts.bin.br";
    private const int Magic = 0x4f4c5354;
    private const int FormatVersion = 1;
    private const int MaximumTemplateCount = 10_000;
    private const int MaximumVersionBytes = 128;
    private const int MaximumIdentifierBytes = 256;
    private const int MaximumTemplateBytes = 2 * 1024 * 1024;
    private const int MaximumCorpusBytes = 128 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Creates a deterministic compressed corpus.</summary>
    public static byte[] Create(string corpusVersion, SpdxLicenseTextTemplate[] templates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusVersion);
        ArgumentNullException.ThrowIfNull(templates);
        if (templates.Length > MaximumTemplateCount) throw new InvalidDataException("SPDX template corpus contains too many entries.");

        var ordered = templates.ToArray();
        Array.Sort(ordered, static (left, right) => StringComparer.Ordinal.Compare(left.LicenseId, right.LicenseId));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index - 1].LicenseId, ordered[index].LicenseId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"SPDX template corpus contains duplicate identifier: {ordered[index].LicenseId}");
            }
        }

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new BinaryWriter(brotli, StrictUtf8, leaveOpen: false))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            WriteString(writer, corpusVersion, MaximumVersionBytes);
            writer.Write(ordered.Length);
            for (var index = 0; index < ordered.Length; index++)
            {
                var template = ordered[index];
                if (string.IsNullOrWhiteSpace(template.LicenseId) || string.IsNullOrWhiteSpace(template.Template))
                {
                    throw new InvalidDataException("SPDX template corpus entries require an identifier and template.");
                }

                WriteString(writer, template.LicenseId, MaximumIdentifierBytes);
                WriteString(writer, template.Template, MaximumTemplateBytes);
            }
        }

        return output.ToArray();
    }

    /// <summary>Creates a corpus from SPDX license-list-data's repository archive.</summary>
    public static byte[] CreateFromLicenseListArchive(string corpusVersion, Stream archive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusVersion);
        ArgumentNullException.ThrowIfNull(archive);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        return CreateFromLicenseListArchive(corpusVersion, zip);
    }

    /// <summary>Extracts list data and a version-matched corpus from one SPDX repository snapshot.</summary>
    public static SpdxLicenseListArchiveData LoadLicenseListArchive(Stream archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var licenses = ReadArchiveEntry(zip, "/json/licenses.json", 16 * 1024 * 1024);
        var exceptions = ReadArchiveEntry(zip, "/json/exceptions.json", 16 * 1024 * 1024);
        using var document = JsonDocument.Parse(licenses);
        var version = document.RootElement.TryGetProperty("licenseListVersion", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("SPDX licenses.json has no licenseListVersion.");
        var corpus = CreateFromLicenseListArchive(version, zip);
        ValidateLicenseCoverage(document.RootElement, Load(corpus).Templates);
        return new SpdxLicenseListArchiveData(licenses, exceptions, corpus);
    }

    private static byte[] CreateFromLicenseListArchive(string corpusVersion, ZipArchive zip)
    {
        var templates = new List<SpdxLicenseTextTemplate>();
        for (var index = 0; index < zip.Entries.Count; index++)
        {
            var entry = zip.Entries[index];
            var name = entry.FullName.AsSpan();
            if (!name.EndsWith(".json".AsSpan(), StringComparison.Ordinal)
                || name.IndexOf("/json/details/".AsSpan(), StringComparison.Ordinal) < 0) continue;
            if (entry.Length <= 0 || entry.Length > MaximumTemplateBytes * 2L)
            {
                throw new InvalidDataException("SPDX license detail entry size is invalid.");
            }

            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var identifier = root.GetProperty("licenseId").GetString();
            var template = root.GetProperty("standardLicenseTemplate").GetString();
            if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(template))
            {
                throw new InvalidDataException("SPDX license detail is missing its identifier or standard template.");
            }

            templates.Add(new SpdxLicenseTextTemplate(identifier, template));
            if (templates.Count > MaximumTemplateCount) throw new InvalidDataException("SPDX template corpus contains too many entries.");
        }

        if (templates.Count == 0) throw new InvalidDataException("SPDX archive contains no license detail templates.");
        return Create(corpusVersion, [.. templates]);
    }

    private static byte[] ReadArchiveEntry(ZipArchive archive, string suffix, int maximumBytes)
    {
        ZipArchiveEntry? match = null;
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            if (!entry.FullName.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (match is not null) throw new InvalidDataException($"SPDX archive contains more than one {suffix} entry.");
            match = entry;
        }

        if (match is null || match.Length <= 0 || match.Length > maximumBytes)
        {
            throw new InvalidDataException($"SPDX archive {suffix} entry is missing or exceeds its limit.");
        }

        var result = new byte[checked((int)match.Length)];
        using var stream = match.Open();
        stream.ReadExactly(result);
        return result;
    }

    private static void ValidateLicenseCoverage(JsonElement licenses, ReadOnlySpan<SpdxLicenseTextTemplate> templates)
    {
        var values = licenses.GetProperty("licenses");
        if (values.GetArrayLength() != templates.Length)
        {
            throw new InvalidDataException("SPDX archive license details do not cover licenses.json.");
        }

        var identifiers = new HashSet<string>(templates.Length, StringComparer.Ordinal);
        for (var index = 0; index < templates.Length; index++) identifiers.Add(templates[index].LicenseId);
        foreach (var license in values.EnumerateArray())
        {
            var identifier = license.GetProperty("licenseId").GetString();
            if (identifier is null || !identifiers.Remove(identifier))
            {
                throw new InvalidDataException("SPDX archive license details do not cover licenses.json.");
            }
        }

        if (identifiers.Count != 0) throw new InvalidDataException("SPDX archive license details do not cover licenses.json.");
    }

    /// <summary>Loads a bounded compressed corpus from owned bytes.</summary>
    public static SpdxLicenseTextCorpusData Load(byte[] corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        using var input = new MemoryStream(corpus, writable: false);
        return Load(input);
    }

    /// <summary>Loads a bounded compressed corpus from borrowed bytes.</summary>
    public static SpdxLicenseTextCorpusData Load(ReadOnlySpan<byte> corpus)
    {
        using var input = new MemoryStream(corpus.ToArray(), writable: false);
        return Load(input);
    }

    /// <summary>Loads a bounded compressed corpus.</summary>
    public static SpdxLicenseTextCorpusData Load(Stream corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        using var brotli = new BrotliStream(corpus, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(brotli, StrictUtf8, leaveOpen: false);
        try
        {
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
            {
                throw new InvalidDataException("SPDX template corpus header is invalid.");
            }

            var consumed = 0;
            var version = ReadString(reader, MaximumVersionBytes, ref consumed);
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumTemplateCount) throw new InvalidDataException("SPDX template corpus entry count is invalid.");
            var templates = new SpdxLicenseTextTemplate[count];
            for (var index = 0; index < count; index++)
            {
                var identifier = ReadString(reader, MaximumIdentifierBytes, ref consumed);
                var template = ReadString(reader, MaximumTemplateBytes, ref consumed);
                if (identifier.Length == 0 || template.Length == 0) throw new InvalidDataException("SPDX template corpus contains an empty entry.");
                templates[index] = new SpdxLicenseTextTemplate(identifier, template);
            }

            return new SpdxLicenseTextCorpusData(version, templates);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("SPDX template corpus is truncated.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("SPDX template corpus contains invalid UTF-8.", exception);
        }
    }

    private static void WriteString(BinaryWriter writer, string value, int maximumBytes)
    {
        var byteCount = StrictUtf8.GetByteCount(value);
        if (byteCount == 0 || byteCount > maximumBytes) throw new InvalidDataException("SPDX template corpus string length is invalid.");
        writer.Write(byteCount);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = StrictUtf8.GetBytes(value, rented);
            writer.Write(rented, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string ReadString(BinaryReader reader, int maximumBytes, ref int consumed)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > maximumBytes || consumed > MaximumCorpusBytes - length)
        {
            throw new InvalidDataException("SPDX template corpus string length is invalid.");
        }

        consumed += length;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var offset = 0;
            while (offset < length)
            {
                var read = reader.Read(rented, offset, length - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }

            return StrictUtf8.GetString(rented, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
