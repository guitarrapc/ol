using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ol.Core;

/// <summary>
/// Stores package metadata using opaque, hash-named cache files.
/// </summary>
/// <param name="root">The package metadata cache directory.</param>
public sealed class PackageMetadataCache(string root)
{
    /// <summary>
    /// Gets the cache directory.
    /// </summary>
    public string Root { get; } = root;

    /// <summary>
    /// Reads a cache entry by its logical key.
    /// </summary>
    /// <param name="cacheKey">The logical package metadata cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cache entry, or <see langword="null"/> when it is absent or corrupt.</returns>
    public async Task<PackageMetadataRecord?> TryReadAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var path = GetPath(cacheKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var record = await JsonSerializer.DeserializeAsync(stream, PackageMetadataJsonContext.Default.PackageMetadataRecord, cancellationToken).ConfigureAwait(false);
            return record is { CacheKey: var storedKey } && string.Equals(storedKey, cacheKey, StringComparison.Ordinal) ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a package metadata cache entry.
    /// </summary>
    /// <param name="record">The normalized metadata record to persist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task WriteAsync(PackageMetadataRecord record, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);
        var path = GetPath(record.CacheKey);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, record, PackageMetadataJsonContext.Default.PackageMetadataRecord, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes all package metadata cache entries.
    /// </summary>
    public void Clear()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>
    /// Gets the opaque cache file path for a logical key.
    /// </summary>
    /// <param name="cacheKey">The logical package metadata cache key.</param>
    /// <returns>The cache file path.</returns>
    public string GetPath(string cacheKey) => Path.Combine(Root, string.Concat(GetCacheKeySha256(cacheKey), ".json"));

    /// <summary>
    /// Calculates the cache key hash used for cache file names and report metadata.
    /// </summary>
    /// <param name="cacheKey">The logical package metadata cache key.</param>
    /// <returns>The lower-case SHA-256 cache key hash.</returns>
    public static string GetCacheKeySha256(string cacheKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();
}

/// <summary>
/// Represents normalized package registry metadata stored in the cache.
/// </summary>
/// <param name="CacheKey">The logical package identity.</param>
/// <param name="Source">The metadata source.</param>
/// <param name="RawLicense">The raw package license value.</param>
/// <param name="RepositoryUrl">The optional repository URL for later source evidence planning.</param>
/// <param name="Warnings">Non-fatal metadata warnings.</param>
/// <param name="Errors">Metadata errors retained for audit.</param>
/// <param name="FetchedAt">The metadata fetch timestamp.</param>
/// <param name="SchemaVersion">The cache entry schema version.</param>
public readonly record struct PackageMetadataRecord(
    string CacheKey,
    string Source,
    string RawLicense,
    string RepositoryUrl,
    string[] Warnings,
    string[] Errors,
    DateTimeOffset FetchedAt = default,
    int SchemaVersion = 1)
{
    /// <summary>
    /// Gets the SHA-256 hash of <see cref="CacheKey"/>.
    /// </summary>
    public string CacheKeySha256 => PackageMetadataCache.GetCacheKeySha256(CacheKey);
}

[JsonSerializable(typeof(PackageMetadataRecord))]
internal sealed partial class PackageMetadataJsonContext : JsonSerializerContext;
