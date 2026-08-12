using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ol.Core.GitHub;

/// <summary>Classifies a declared GitHub file cache read.</summary>
public enum DeclaredGitHubFileCacheReadStatus : byte
{
    Missing,
    Hit,
    Invalid,
}

/// <summary>Contains a validated and reclassified declared GitHub file cache result.</summary>
public readonly record struct DeclaredGitHubFileCacheReadResult(
    DeclaredGitHubFileCacheReadStatus Status,
    DeclaredGitHubFileResult Result);

/// <summary>Persists bounded raw GitHub file content and validates it before every reuse.</summary>
public sealed class DeclaredGitHubFileCache(string root)
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private const int MaximumEntryBytes = ((MaximumDocumentBytes + 2) / 3 * 4) + 64 * 1024;

    public string Root { get; } = root;

    public string GetPath(string cacheKey) => CacheFile.BuildPath(Root, CacheFile.GetCacheKeySha256(cacheKey));

    /// <summary>Reads raw content, verifies its SHA-256, and classifies it with the active matcher.</summary>
    public DeclaredGitHubFileCacheReadResult Read(DeclaredGitHubFileTarget target, Spdx.SpdxLicenseTextMatcher matcher)
    {
        var cacheKeySha256 = CacheFile.GetCacheKeySha256(target.CacheKey);
        byte[] content;
        int length;
        try
        {
            if (!CacheFile.TryRentContent(CacheFile.BuildPath(Root, cacheKeySha256), MaximumEntryBytes, out content, out length))
            {
                return new(DeclaredGitHubFileCacheReadStatus.Missing, default);
            }
        }
        catch (IOException)
        {
            return new(DeclaredGitHubFileCacheReadStatus.Invalid, default);
        }

        try
        {
            return TryParse(content.AsSpan(0, length), target.CacheKey, cacheKeySha256, matcher, out var result)
                ? new(DeclaredGitHubFileCacheReadStatus.Hit, result)
                : new(DeclaredGitHubFileCacheReadStatus.Invalid, default);
        }
        catch (JsonException)
        {
            return new(DeclaredGitHubFileCacheReadStatus.Invalid, default);
        }
        finally
        {
            CacheFile.Return(content);
        }
    }

    /// <summary>Atomically writes one successful document or not-found response.</summary>
    public void Write(DeclaredGitHubFileTarget target, HttpStatusCode statusCode, ReadOnlySpan<byte> document)
    {
        if (statusCode is not (HttpStatusCode.OK or HttpStatusCode.NotFound)
            || (statusCode == HttpStatusCode.OK && (document.IsEmpty || document.Length > MaximumDocumentBytes))
            || (statusCode == HttpStatusCode.NotFound && !document.IsEmpty))
        {
            throw new ArgumentException("The declared GitHub file cache record is invalid.", nameof(document));
        }

        var cacheKeySha256 = CacheFile.GetCacheKeySha256(target.CacheKey);
        var path = CacheFile.BuildPath(Root, cacheKeySha256);
        Directory.CreateDirectory(Root);
        var temporaryPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            Span<byte> contentHash = stackalloc byte[SHA256.HashSizeInBytes];
            if (!document.IsEmpty) SHA256.HashData(document, contentHash);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("SchemaVersion", 1);
                writer.WriteString("CacheKey", target.CacheKey);
                writer.WriteString("CacheKeySha256", cacheKeySha256);
                writer.WriteString("Source", "github-contents-api");
                writer.WriteNumber("HttpStatus", (int)statusCode);
                writer.WriteString("ContentSha256", document.IsEmpty ? string.Empty : Convert.ToHexStringLower(contentHash));
                writer.WriteBase64String("Content", document);
                writer.WriteString("FetchedAt", DateTimeOffset.UtcNow);
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public void Clear()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private static bool TryParse(
        ReadOnlySpan<byte> utf8,
        string cacheKey,
        string cacheKeySha256,
        Spdx.SpdxLicenseTextMatcher matcher,
        out DeclaredGitHubFileResult result)
    {
        result = default;
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
        var fields = CacheField.None;
        var statusCode = default(HttpStatusCode);
        ReadOnlySpan<byte> persistedSha256 = default;
        ReadOnlySpan<byte> encodedContent = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) return false;
            if (reader.ValueTextEquals("SchemaVersion"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version) || version != 1) return false;
                fields |= CacheField.SchemaVersion;
            }
            else if (reader.ValueTextEquals("CacheKey"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(cacheKey)) return false;
                fields |= CacheField.CacheKey;
            }
            else if (reader.ValueTextEquals("CacheKeySha256"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(cacheKeySha256)) return false;
                fields |= CacheField.CacheKeySha256;
            }
            else if (reader.ValueTextEquals("Source"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("github-contents-api"u8)) return false;
                fields |= CacheField.Source;
            }
            else if (reader.ValueTextEquals("HttpStatus"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var status)
                    || status is not ((int)HttpStatusCode.OK) and not ((int)HttpStatusCode.NotFound)) return false;
                statusCode = (HttpStatusCode)status;
                fields |= CacheField.HttpStatus;
            }
            else if (reader.ValueTextEquals("ContentSha256"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.HasValueSequence || reader.ValueIsEscaped) return false;
                persistedSha256 = reader.ValueSpan;
                fields |= CacheField.ContentSha256;
            }
            else if (reader.ValueTextEquals("Content"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.HasValueSequence || reader.ValueIsEscaped) return false;
                encodedContent = reader.ValueSpan;
                fields |= CacheField.Content;
            }
            else if (reader.ValueTextEquals("FetchedAt"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.TryGetDateTimeOffset(out var fetchedAt) || fetchedAt.Offset != TimeSpan.Zero) return false;
                fields |= CacheField.FetchedAt;
            }
            else
            {
                if (!reader.Read()) return false;
                reader.Skip();
            }
        }

        if (fields != CacheField.Required) return false;
        if (statusCode == HttpStatusCode.NotFound)
        {
            if (!persistedSha256.IsEmpty || !encodedContent.IsEmpty) return false;
            result = new DeclaredGitHubFileResult(HttpStatusCode.NotFound, null, string.Empty);
            return true;
        }

        if (encodedContent.Length == 0 || (encodedContent.Length & 3) != 0 || persistedSha256.Length != 64) return false;
        var decodedLength = Base64.GetMaxDecodedFromUtf8Length(encodedContent.Length);
        if (encodedContent[^1] == (byte)'=') decodedLength--;
        if (encodedContent[^2] == (byte)'=') decodedLength--;
        if (decodedLength <= 0 || decodedLength > MaximumDocumentBytes) return false;
        var document = ArrayPool<byte>.Shared.Rent(decodedLength);
        try
        {
            if (Base64.DecodeFromUtf8(encodedContent, document, out var consumed, out var written) != OperationStatus.Done
                || consumed != encodedContent.Length || written != decodedLength) return false;
            Span<byte> actualHash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(document.AsSpan(0, written), actualHash);
            if (!HexEquals(persistedSha256, actualHash)) return false;
            var bytes = document.AsSpan(0, written);
            var licenseId = matcher.TryMatch(SkipUtf8Bom(bytes), out var matched) ? matched : null;
            result = new DeclaredGitHubFileResult(HttpStatusCode.OK, licenseId, Convert.ToHexStringLower(actualHash));
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(document);
        }
    }

    private static ReadOnlySpan<byte> SkipUtf8Bom(ReadOnlySpan<byte> value)
        => value.Length >= 3 && value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf ? value[3..] : value;

    private static bool HexEquals(ReadOnlySpan<byte> hex, ReadOnlySpan<byte> bytes)
    {
        if (hex.Length != bytes.Length * 2) return false;
        var difference = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            difference |= hex[index * 2] ^ ToLowerHex(value >> 4);
            difference |= hex[index * 2 + 1] ^ ToLowerHex(value & 0x0f);
        }

        return difference == 0;
    }

    private static byte ToLowerHex(int value) => (byte)(value < 10 ? '0' + value : 'a' + value - 10);

    [Flags]
    private enum CacheField : byte
    {
        None = 0,
        SchemaVersion = 1 << 0,
        CacheKey = 1 << 1,
        CacheKeySha256 = 1 << 2,
        Source = 1 << 3,
        HttpStatus = 1 << 4,
        ContentSha256 = 1 << 5,
        Content = 1 << 6,
        FetchedAt = 1 << 7,
        Required = SchemaVersion | CacheKey | CacheKeySha256 | Source | HttpStatus | ContentSha256 | Content | FetchedAt,
    }
}
