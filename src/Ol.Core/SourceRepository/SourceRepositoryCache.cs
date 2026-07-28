using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ol.Core.SourceRepository;

/// <summary>Stores source-repository evidence using opaque hash-named cache files.</summary>
public sealed class SourceRepositoryCache(string root)
{
    /// <summary>Gets the source cache directory.</summary>
    public string Root { get; } = root;

    /// <summary>Gets the opaque cache path for a logical key.</summary>
    public string GetPath(string cacheKey) => Path.Combine(Root, string.Concat(GetCacheKeySha256(cacheKey), ".json"));

    /// <summary>Gets the lower-case SHA-256 of a logical cache key.</summary>
    public static string GetCacheKeySha256(string cacheKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();

    /// <summary>Reads a compatible source evidence entry, or null when absent or corrupt.</summary>
    public async Task<SourceRepositoryRecord?> TryReadAsync(string cacheKey, CancellationToken cancellationToken = default)
        => (await ReadAsync(cacheKey, cancellationToken).ConfigureAwait(false)).Record;

    /// <summary>Reads an entry and distinguishes a cache miss from an invalid entry.</summary>
    public async Task<SourceRepositoryCacheReadResult> ReadAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var path = GetPath(cacheKey);
        try
        {
            await using var stream = File.OpenRead(path);
            var record = await JsonSerializer.DeserializeAsync(stream, SourceRepositoryJsonContext.Default.SourceRepositoryRecord, cancellationToken).ConfigureAwait(false);
            return record is { } value && IsValid(value, cacheKey)
                ? new(SourceRepositoryCacheReadStatus.Hit, value)
                : new(SourceRepositoryCacheReadStatus.Invalid, null);
        }
        catch (FileNotFoundException) { return new(SourceRepositoryCacheReadStatus.Missing, null); }
        catch (DirectoryNotFoundException) { return new(SourceRepositoryCacheReadStatus.Missing, null); }
        catch (JsonException) { return new(SourceRepositoryCacheReadStatus.Invalid, null); }
        catch (IOException) { return new(SourceRepositoryCacheReadStatus.Invalid, null); }
    }

    /// <summary>Writes a normalized source evidence entry.</summary>
    public async Task WriteAsync(SourceRepositoryRecord record, CancellationToken cancellationToken = default)
    {
        if (!IsValid(record with { FetchedAt = record.FetchedAt == default ? DateTimeOffset.UtcNow : record.FetchedAt.ToUniversalTime() }, record.CacheKey))
        {
            throw new ArgumentException("The source repository cache record is invalid.", nameof(record));
        }

        record = record with { FetchedAt = record.FetchedAt == default ? DateTimeOffset.UtcNow : record.FetchedAt.ToUniversalTime() };
        Directory.CreateDirectory(Root);
        await using var stream = File.Create(GetPath(record.CacheKey));
        await JsonSerializer.SerializeAsync(stream, record, SourceRepositoryJsonContext.Default.SourceRepositoryRecord, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes all source repository cache entries.</summary>
    public void Clear()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private static bool IsValid(SourceRepositoryRecord record, string requestedKey)
        => record.SchemaVersion == 1
            && string.Equals(record.CacheKey, requestedKey, StringComparison.Ordinal)
            && string.Equals(record.CacheKeySha256, GetCacheKeySha256(record.CacheKey), StringComparison.Ordinal)
            && record.Source == "github-license-api"
            && (record.AuthMode == "none" || record.AuthMode == "ol_github_token")
            && record.Repository.Length > 0
            && record.Ref.Length > 0
            && record.Warnings is not null && record.Errors is not null
            && record.FetchedAt.Offset == TimeSpan.Zero;
}

/// <summary>Classifies the outcome of a source repository cache read.</summary>
public enum SourceRepositoryCacheReadStatus : byte
{
    /// <summary>No entry exists for the requested key.</summary>
    Missing,
    /// <summary>A compatible entry was read.</summary>
    Hit,
    /// <summary>An entry exists but cannot be safely consumed.</summary>
    Invalid,
}

/// <summary>Contains a classified source repository cache read.</summary>
public readonly record struct SourceRepositoryCacheReadResult(SourceRepositoryCacheReadStatus Status, SourceRepositoryRecord? Record);

/// <summary>Represents normalized GitHub license metadata.</summary>
public readonly record struct GitHubLicenseResult(string? SpdxId, string Key, string Name, string Path, string Sha, string HtmlUrl);

/// <summary>Represents a persisted GitHub License API result.</summary>
public readonly record struct SourceRepositoryRecord(
    string CacheKey,
    string Source,
    string AuthMode,
    string Repository,
    string Ref,
    HttpStatusCode? HttpStatus,
    GitHubLicenseResult? License,
    string[] Warnings,
    string[] Errors,
    DateTimeOffset FetchedAt = default)
{
    /// <summary>Gets the source-cache schema version.</summary>
    public int SchemaVersion => 1;
    /// <summary>Gets the source cache-key SHA-256.</summary>
    public string CacheKeySha256 => SourceRepositoryCache.GetCacheKeySha256(CacheKey);
}

[JsonSerializable(typeof(SourceRepositoryRecord))]
internal sealed partial class SourceRepositoryJsonContext : JsonSerializerContext;
