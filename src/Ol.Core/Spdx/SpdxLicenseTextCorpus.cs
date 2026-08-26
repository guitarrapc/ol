using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Ol.Core.Spdx;

internal readonly record struct SpdxLicenseTextTemplateUtf8(
    string LicenseId,
    int Offset,
    int Length);

internal readonly record struct SpdxLicenseTextCorpusUtf8Data(
    string CorpusVersion,
    byte[] TemplateBytes,
    SpdxLicenseTextTemplateUtf8[] Templates);

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
    private const int LegacyFormatVersion = 1;
    private const int FormatVersion = 2;
    private const int MaximumTemplateCount = 10_000;
    private const int MaximumVersionBytes = 128;
    private const int MaximumIdentifierBytes = 256;
    private const int MaximumTemplateBytes = 2 * 1024 * 1024;
    private const int MaximumCorpusBytes = 128 * 1024 * 1024;
    private static readonly int MaximumCompressedCorpusBytes = BrotliEncoder.GetMaxCompressedLength(
        MaximumCorpusBytes + MaximumTemplateCount * 8 + 20);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Creates a deterministic compressed corpus.</summary>
    public static byte[] Create(string corpusVersion, SpdxLicenseTextTemplate[] templates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusVersion);
        ArgumentNullException.ThrowIfNull(templates);
        if (templates.Length > MaximumTemplateCount) throw new InvalidDataException("SPDX template corpus contains too many entries.");

        var ordered = templates.ToArray();
        Array.Sort(ordered, static (left, right) => StringComparer.Ordinal.Compare(left.LicenseId, right.LicenseId));
        var templateByteCount = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var template = ordered[index];
            if (string.IsNullOrWhiteSpace(template.LicenseId) || string.IsNullOrWhiteSpace(template.Template))
            {
                throw new InvalidDataException("SPDX template corpus entries require an identifier and template.");
            }

            var length = StrictUtf8.GetByteCount(template.Template);
            if (length > MaximumTemplateBytes || templateByteCount > MaximumCorpusBytes - length)
            {
                throw new InvalidDataException("SPDX template corpus string length is invalid.");
            }

            templateByteCount += length;
        }
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
            writer.Write(templateByteCount);
            WriteString(writer, corpusVersion, MaximumVersionBytes);
            writer.Write(ordered.Length);
            for (var index = 0; index < ordered.Length; index++)
            {
                var template = ordered[index];
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
        return Create(corpusVersion, ReadLicenseTemplates(zip));
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
        var templates = ReadLicenseTemplates(zip);
        ValidateLicenseCoverage(document.RootElement, templates);
        var corpus = Create(version, templates);
        return new SpdxLicenseListArchiveData(licenses, exceptions, corpus);
    }

    private static SpdxLicenseTextTemplate[] ReadLicenseTemplates(ZipArchive zip)
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
        return [.. templates];
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

    internal static string Validate(byte[] corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ValidateCompressedCorpusSize(corpus.LongLength);
        using var input = new MemoryStream(corpus, writable: false);
        return Validate(input);
    }

    private static string Validate(Stream corpus)
    {
        using var brotli = new BrotliStream(corpus, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(brotli, StrictUtf8, leaveOpen: false);
        byte[]? bytes = null;
        char[]? chars = null;
        try
        {
            var declaredTemplateBytes = ReadHeader(reader);

            var consumed = 0;
            var version = ReadString(reader, MaximumVersionBytes, ref consumed);
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumTemplateCount) throw new InvalidDataException("SPDX template corpus entry count is invalid.");
            bytes = ArrayPool<byte>.Shared.Rent(4096);
            chars = ArrayPool<char>.Shared.Rent(StrictUtf8.GetMaxCharCount(bytes.Length));
            var decoder = StrictUtf8.GetDecoder();
            var templateByteCount = 0;
            for (var index = 0; index < count; index++)
            {
                ValidateString(reader, MaximumIdentifierBytes, ref consumed, bytes, chars, decoder);
                var templateLength = ValidateString(reader, MaximumTemplateBytes, ref consumed, bytes, chars, decoder);
                templateByteCount = checked(templateByteCount + templateLength);
            }

            if (declaredTemplateBytes >= 0 && declaredTemplateBytes != templateByteCount)
            {
                throw new InvalidDataException("SPDX template corpus payload length is invalid.");
            }
            EnsureEndOfCorpus(brotli);

            return version;
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("SPDX template corpus is truncated.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("SPDX template corpus contains invalid UTF-8.", exception);
        }
        finally
        {
            if (chars is not null) ArrayPool<char>.Shared.Return(chars);
            if (bytes is not null) ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    internal static SpdxLicenseTextMatcher LoadMatcher(
        Stream corpus,
        SpdxLicenseIndex licenseIndex,
        int maximumTextBytes = SpdxLicenseTextMatcher.DefaultMaximumTextBytes)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(licenseIndex);

        if (corpus.CanSeek)
        {
            ValidateCompressedCorpusSize(corpus.Length - corpus.Position);
        }

        MemoryStream? ownedInput = null;
        if (!corpus.CanSeek)
        {
            ownedInput = new MemoryStream();
            CopyBounded(corpus, ownedInput, MaximumCompressedCorpusBytes);
            ownedInput.Position = 0;
            corpus = ownedInput;
        }

        try
        {
            var start = corpus.Position;
            var templateBytes = MeasureTemplateBytes(corpus);
            corpus.Position = start;
            var data = LoadUtf8(corpus, licenseIndex, templateBytes);
            return new SpdxLicenseTextMatcher(data, maximumTextBytes, licenseIndex);
        }
        finally
        {
            ownedInput?.Dispose();
        }
    }

    private static int MeasureTemplateBytes(Stream corpus)
    {
        using var brotli = new BrotliStream(corpus, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(brotli, StrictUtf8, leaveOpen: false);
        byte[]? rented = null;
        try
        {
            var declaredTemplateBytes = ReadHeader(reader);
            if (declaredTemplateBytes >= 0) return declaredTemplateBytes;
            rented = ArrayPool<byte>.Shared.Rent(81920);
            var consumed = 0;
            SkipString(reader, MaximumVersionBytes, ref consumed, rented);
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumTemplateCount) throw new InvalidDataException("SPDX template corpus entry count is invalid.");
            var templateBytes = 0;
            for (var index = 0; index < count; index++)
            {
                SkipString(reader, MaximumIdentifierBytes, ref consumed, rented);
                var length = ReadStringLength(reader, MaximumTemplateBytes, ref consumed);
                templateBytes = checked(templateBytes + length);
                SkipBytes(reader, length, rented);
            }

            return templateBytes;
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("SPDX template corpus is truncated.", exception);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static SpdxLicenseTextCorpusUtf8Data LoadUtf8(Stream corpus, SpdxLicenseIndex licenseIndex, int templateByteCount)
    {
        using var brotli = new BrotliStream(corpus, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(brotli, StrictUtf8, leaveOpen: false);
        try
        {
            var declaredTemplateBytes = ReadHeader(reader);
            if (declaredTemplateBytes >= 0 && declaredTemplateBytes != templateByteCount)
            {
                throw new InvalidDataException("SPDX template corpus payload length is invalid.");
            }
            var consumed = 0;
            var version = ReadString(reader, MaximumVersionBytes, ref consumed);
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumTemplateCount) throw new InvalidDataException("SPDX template corpus entry count is invalid.");
            var templateBytes = new byte[templateByteCount];
            var templates = new SpdxLicenseTextTemplateUtf8[count];
            Span<byte> identifierBytes = stackalloc byte[MaximumIdentifierBytes];
            var offset = 0;
            for (var index = 0; index < count; index++)
            {
                var identifierLength = ReadStringBytes(reader, MaximumIdentifierBytes, ref consumed, identifierBytes);
                var identifier = licenseIndex.TryNormalizeLicenseIdUtf8(identifierBytes[..identifierLength], out var canonical)
                    ? canonical
                    : StrictUtf8.GetString(identifierBytes[..identifierLength]);
                var templateLength = ReadStringLength(reader, MaximumTemplateBytes, ref consumed);
                if (templateLength > templateBytes.Length - offset)
                {
                    throw new InvalidDataException("SPDX template corpus payload length is invalid.");
                }
                ReadExactly(reader, templateBytes.AsSpan(offset, templateLength));
                _ = StrictUtf8.GetCharCount(templateBytes.AsSpan(offset, templateLength));
                templates[index] = new SpdxLicenseTextTemplateUtf8(identifier, offset, templateLength);
                offset += templateLength;
            }

            if (offset != templateBytes.Length) throw new InvalidDataException("SPDX template corpus payload length is invalid.");
            EnsureEndOfCorpus(brotli);

            return new SpdxLicenseTextCorpusUtf8Data(version, templateBytes, templates);
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

    private static int ReadHeader(BinaryReader reader)
    {
        if (reader.ReadInt32() != Magic)
        {
            throw new InvalidDataException("SPDX template corpus header is invalid.");
        }

        var formatVersion = reader.ReadInt32();
        if (formatVersion == LegacyFormatVersion) return -1;
        if (formatVersion != FormatVersion) throw new InvalidDataException("SPDX template corpus header is invalid.");
        var templateByteCount = reader.ReadInt32();
        if (templateByteCount < 0 || templateByteCount > MaximumCorpusBytes)
        {
            throw new InvalidDataException("SPDX template corpus payload length is invalid.");
        }

        return templateByteCount;
    }

    private static int ReadStringLength(BinaryReader reader, int maximumBytes, ref int consumed)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > maximumBytes || consumed > MaximumCorpusBytes - length)
        {
            throw new InvalidDataException("SPDX template corpus string length is invalid.");
        }

        consumed += length;
        return length;
    }

    private static int ReadStringBytes(BinaryReader reader, int maximumBytes, ref int consumed, Span<byte> destination)
    {
        var length = ReadStringLength(reader, maximumBytes, ref consumed);
        ReadExactly(reader, destination[..length]);
        _ = StrictUtf8.GetCharCount(destination[..length]);
        return length;
    }

    private static void SkipString(BinaryReader reader, int maximumBytes, ref int consumed, byte[] buffer)
    {
        var length = ReadStringLength(reader, maximumBytes, ref consumed);
        SkipBytes(reader, length, buffer);
    }

    private static int ValidateString(BinaryReader reader, int maximumBytes, ref int consumed, byte[] bytes, char[] chars, Decoder decoder)
    {
        var length = ReadStringLength(reader, maximumBytes, ref consumed);
        var remaining = length;
        decoder.Reset();
        while (remaining > 0)
        {
            var read = reader.Read(bytes, 0, Math.Min(remaining, bytes.Length));
            if (read == 0) throw new EndOfStreamException();
            remaining -= read;
            decoder.Convert(bytes.AsSpan(0, read), chars, remaining == 0, out var bytesUsed, out _, out var completed);
            if (bytesUsed != read || (remaining == 0 && !completed))
            {
                throw new InvalidDataException("SPDX template corpus contains invalid UTF-8.");
            }
        }

        return length;
    }

    private static void SkipBytes(BinaryReader reader, int length, byte[] buffer)
    {
        while (length > 0)
        {
            var read = reader.Read(buffer, 0, Math.Min(length, buffer.Length));
            if (read == 0) throw new EndOfStreamException();
            length -= read;
        }
    }

    private static void EnsureEndOfCorpus(Stream corpus)
    {
        if (corpus.ReadByte() >= 0) throw new InvalidDataException("SPDX template corpus contains trailing data.");
    }

    private static void ValidateCompressedCorpusSize(long length)
    {
        if (length < 0 || length > MaximumCompressedCorpusBytes)
        {
            throw new InvalidDataException("SPDX template corpus exceeds its compressed size limit.");
        }
    }

    private static void CopyBounded(Stream source, Stream destination, int maximumBytes)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var total = 0;
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) return;
                if (total > maximumBytes - read)
                {
                    throw new InvalidDataException("SPDX template corpus exceeds its compressed size limit.");
                }

                destination.Write(buffer, 0, read);
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadExactly(BinaryReader reader, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var read = reader.Read(destination);
            if (read == 0) throw new EndOfStreamException();
            destination = destination[read..];
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
        var length = ReadStringLength(reader, maximumBytes, ref consumed);
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
