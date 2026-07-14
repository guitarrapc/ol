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
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!IsValidVersion1(document.RootElement, cacheKey))
            {
                return null;
            }

            return document.RootElement.Deserialize(PackageMetadataJsonContext.Default.PackageMetadataRecord);
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
        ArgumentException.ThrowIfNullOrEmpty(record.CacheKey);
        ArgumentException.ThrowIfNullOrEmpty(record.Source);
        ArgumentNullException.ThrowIfNull(record.RawLicense);
        ArgumentNullException.ThrowIfNull(record.RepositoryUrl);
        ArgumentNullException.ThrowIfNull(record.Warnings);
        ArgumentNullException.ThrowIfNull(record.Errors);
        if (Array.Exists(record.Warnings, static value => value is null)
            || Array.Exists(record.Errors, static value => value is null))
        {
            throw new ArgumentException("Warnings and errors must contain only strings.", nameof(record));
        }

        if (!IsSafeRepositoryReference(record.RepositoryUrl))
        {
            throw new ArgumentException("Repository URL must not contain credentials or reference a local file.", nameof(record));
        }

        var persistedRecord = record with
        {
            FetchedAt = record.FetchedAt == default ? DateTimeOffset.UtcNow : record.FetchedAt.ToUniversalTime(),
        };
        Directory.CreateDirectory(Root);
        var path = GetPath(persistedRecord.CacheKey);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, persistedRecord, PackageMetadataJsonContext.Default.PackageMetadataRecord, cancellationToken).ConfigureAwait(false);
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

    private static bool IsValidVersion1(JsonElement root, string requestedCacheKey)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("SchemaVersion", out var schemaVersion)
            || !schemaVersion.TryGetInt32(out var version)
            || version != 1
            || !TryGetString(root, "CacheKey", out var cacheKey)
            || !string.Equals(cacheKey, requestedCacheKey, StringComparison.Ordinal)
            || !TryGetString(root, "CacheKeySha256", out var cacheKeySha256)
            || !string.Equals(cacheKeySha256, GetCacheKeySha256(cacheKey), StringComparison.Ordinal)
            || !TryGetString(root, "Source", out _)
            || !TryGetString(root, "RawLicense", out _)
            || !TryGetString(root, "RepositoryUrl", out var repositoryUrl)
            || !IsSafeRepositoryReference(repositoryUrl)
            || !TryGetString(root, "FetchedAt", out var fetchedAtText)
            || !HasExplicitUtcOffset(fetchedAtText)
            || !root.GetProperty("FetchedAt").TryGetDateTimeOffset(out var fetchedAt)
            || fetchedAt.Offset != TimeSpan.Zero)
        {
            return false;
        }

        return IsStringArray(root, "Warnings") && IsStringArray(root, "Errors");
    }

    private static bool HasExplicitUtcOffset(string value)
        => value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal);

    private static bool IsSafeRepositoryReference(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (Path.IsPathRooted(value)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || LooksLikeScpReference(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return !uri.IsFile
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0;
    }

    private static bool LooksLikeScpReference(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && value.AsSpan(at + 1).Contains(':');
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        if (root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }
        }

        return true;
    }
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
public readonly record struct PackageMetadataRecord(
    string CacheKey,
    string Source,
    string RawLicense,
    string RepositoryUrl,
    string[] Warnings,
    string[] Errors,
    DateTimeOffset FetchedAt = default)
{
    /// <summary>
    /// Gets the package metadata cache schema version.
    /// </summary>
    public int SchemaVersion => 1;

    /// <summary>
    /// Gets the SHA-256 hash of <see cref="CacheKey"/>.
    /// </summary>
    public string CacheKeySha256 => PackageMetadataCache.GetCacheKeySha256(CacheKey);
}

[JsonSerializable(typeof(PackageMetadataRecord))]
internal sealed partial class PackageMetadataJsonContext : JsonSerializerContext;
