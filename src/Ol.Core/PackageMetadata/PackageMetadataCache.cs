using Ol.Core.Licensing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ol.Core.PackageMetadata;

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
    /// <returns>The cache entry, which the caller must dispose. Absent or corrupt entries are not hits.</returns>
    public async Task<PackageMetadataCacheEntry> TryReadAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var cacheKeySha256 = CacheFile.GetCacheKeySha256(cacheKey);
        var (content, length) = await CacheFile.RentContentAsync(CacheFile.BuildPath(Root, cacheKeySha256), cancellationToken).ConfigureAwait(false);
        return length < 0 ? default : Parse(content, length, cacheKey, cacheKeySha256);
    }

    /// <summary>
    /// Reads a cache entry by its logical key without asynchronous file access.
    /// </summary>
    /// <param name="cacheKey">The logical package metadata cache key.</param>
    /// <returns>The cache entry, which the caller must dispose. Absent or corrupt entries are not hits.</returns>
    public PackageMetadataCacheEntry TryRead(string cacheKey)
    {
        var cacheKeySha256 = CacheFile.GetCacheKeySha256(cacheKey);
        return CacheFile.TryRentContent(CacheFile.BuildPath(Root, cacheKeySha256), out var content, out var length)
            ? Parse(content, length, cacheKey, cacheKeySha256)
            : default;
    }

    /// <summary>Validates and captures one entry in a single pass, without materializing discarded text.</summary>
    /// <remarks>
    /// The entry adopts <paramref name="content"/> only when it is a hit, so a rejected entry returns
    /// its buffer here and a hit returns it from <see cref="PackageMetadataCacheEntry.Dispose"/>.
    /// </remarks>
    private static PackageMetadataCacheEntry Parse(byte[] content, int length, string cacheKey, string cacheKeySha256)
    {
        if (TryParseVersion1(content, length, cacheKey, cacheKeySha256, out var entry))
        {
            return entry;
        }

        CacheFile.Return(content);
        return default;
    }

    private static bool TryParseVersion1(byte[] content, int length, string cacheKey, string cacheKeySha256, out PackageMetadataCacheEntry entry)
    {
        entry = default;
        var reader = new Utf8JsonReader(content.AsSpan(0, length));
        var present = CacheField.None;
        Utf8Slice source = default;
        Utf8Slice rawLicense = default;
        Utf8Slice warnings = default;
        var repositoryUrl = string.Empty;
        var repositoryRef = string.Empty;
        var fetchedAt = default(DateTimeOffset);
        var resolverVersion = 0;
        var referenceKind = DeclaredLicenseReferenceKind.None;
        Utf8Slice reference = default;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                if (reader.ValueTextEquals("SchemaVersion"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version) || version != 1)
                    {
                        return false;
                    }

                    present |= CacheField.SchemaVersion;
                }
                else if (reader.ValueTextEquals("CacheKey"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(cacheKey))
                    {
                        return false;
                    }

                    present |= CacheField.CacheKey;
                }
                else if (reader.ValueTextEquals("CacheKeySha256"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(cacheKeySha256))
                    {
                        return false;
                    }

                    present |= CacheField.CacheKeySha256;
                }
                else if (reader.ValueTextEquals("Source"u8))
                {
                    if (!TryCapture(content, ref reader, out source))
                    {
                        return false;
                    }

                    present |= CacheField.Source;
                }
                else if (reader.ValueTextEquals("RawLicense"u8))
                {
                    if (!TryCapture(content, ref reader, out rawLicense))
                    {
                        return false;
                    }

                    present |= CacheField.RawLicense;
                }
                else if (reader.ValueTextEquals("RepositoryUrl"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    repositoryUrl = reader.GetString()!;
                    if (!IsSafeRepositoryReference(repositoryUrl))
                    {
                        return false;
                    }

                    present |= CacheField.RepositoryUrl;
                }
                else if (reader.ValueTextEquals("RepositoryRef"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    repositoryRef = reader.GetString()!;
                    if (!IsSafeRepositoryRef(repositoryRef))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("FetchedAt"u8))
                {
                    if (!reader.Read()
                        || reader.TokenType != JsonTokenType.String
                        || !HasExplicitUtcOffset(reader.ValueSpan)
                        || !reader.TryGetDateTimeOffset(out fetchedAt)
                        || fetchedAt.Offset != TimeSpan.Zero)
                    {
                        return false;
                    }

                    present |= CacheField.FetchedAt;
                }
                else if (reader.ValueTextEquals("ResolverVersion"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out resolverVersion) || resolverVersion < 0)
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("DeclaredLicenseReferenceKind"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String || !TryParseReferenceKind(reader.ValueSpan, out referenceKind))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("DeclaredLicenseReference"u8))
                {
                    if (!TryCapture(content, ref reader, out reference))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("Warnings"u8))
                {
                    if (!TryCaptureStringArray(content, ref reader, out warnings))
                    {
                        return false;
                    }

                    present |= CacheField.Warnings;
                }
                else if (reader.ValueTextEquals("Errors"u8))
                {
                    if (!TryCaptureStringArray(content, ref reader, out _))
                    {
                        return false;
                    }

                    present |= CacheField.Errors;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (present != CacheField.Required)
        {
            return false;
        }

        entry = new PackageMetadataCacheEntry(content, cacheKeySha256, source, rawLicense, warnings, repositoryUrl, repositoryRef, fetchedAt, resolverVersion, referenceKind, reference);
        return true;
    }

    /// <summary>Captures a string value as a slice of the entry buffer, copying only escaped text.</summary>
    private static bool TryCapture(byte[] content, ref Utf8JsonReader reader, out Utf8Slice value)
    {
        value = default;
        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
        {
            return false;
        }

        if (reader.ValueIsEscaped)
        {
            value = Utf8Slice.FromString(reader.GetString()!);
            return true;
        }

        var span = reader.ValueSpan;
        if (span.Length != 0)
        {
            value = new Utf8Slice(content, (int)reader.TokenStartIndex + 1, span.Length);
        }

        return true;
    }

    /// <summary>Validates a string array and captures its raw JSON text as a slice of the entry buffer.</summary>
    private static bool TryCaptureStringArray(byte[] content, ref Utf8JsonReader reader, out Utf8Slice value)
    {
        value = default;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        var start = (int)reader.TokenStartIndex;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                return false;
            }
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            return false;
        }

        value = new Utf8Slice(content, start, (int)reader.TokenStartIndex + 1 - start);
        return true;
    }

    /// <summary>Tracks which required properties a version 1 entry supplied. <c>RepositoryRef</c> is optional.</summary>
    [Flags]
    private enum CacheField : ushort
    {
        None = 0,
        SchemaVersion = 1 << 0,
        CacheKey = 1 << 1,
        CacheKeySha256 = 1 << 2,
        Source = 1 << 3,
        RawLicense = 1 << 4,
        RepositoryUrl = 1 << 5,
        FetchedAt = 1 << 6,
        Warnings = 1 << 7,
        Errors = 1 << 8,
        Required = SchemaVersion | CacheKey | CacheKeySha256 | Source | RawLicense | RepositoryUrl | FetchedAt | Warnings | Errors,
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
        ArgumentNullException.ThrowIfNull(record.RepositoryRef);
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

        if (!IsSafeRepositoryRef(record.RepositoryRef))
        {
            throw new ArgumentException("Repository ref is invalid.", nameof(record));
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
    public string GetPath(string cacheKey) => CacheFile.BuildPath(Root, CacheFile.GetCacheKeySha256(cacheKey));

    /// <summary>
    /// Calculates the cache key hash used for cache file names and report metadata.
    /// </summary>
    /// <param name="cacheKey">The logical package metadata cache key.</param>
    /// <returns>The lower-case SHA-256 cache key hash.</returns>
    public static string GetCacheKeySha256(string cacheKey) => CacheFile.GetCacheKeySha256(cacheKey);

    /// <summary>Parses the persisted reference kind. An unknown value rejects the entry rather than losing it silently.</summary>
    private static bool TryParseReferenceKind(ReadOnlySpan<byte> value, out DeclaredLicenseReferenceKind kind)
    {
        if (value.SequenceEqual("None"u8)) { kind = DeclaredLicenseReferenceKind.None; return true; }
        if (value.SequenceEqual("Location"u8)) { kind = DeclaredLicenseReferenceKind.Location; return true; }
        if (value.SequenceEqual("ArtifactPath"u8)) { kind = DeclaredLicenseReferenceKind.ArtifactPath; return true; }
        if (value.SequenceEqual("InlineText"u8)) { kind = DeclaredLicenseReferenceKind.InlineText; return true; }
        kind = DeclaredLicenseReferenceKind.None;
        return false;
    }

    private static bool HasExplicitUtcOffset(ReadOnlySpan<byte> value)
        => value.EndsWith("Z"u8) || value.EndsWith("+00:00"u8);

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

    private static bool IsSafeRepositoryRef(string value)
    {
        if (value.Length > 256)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeScpReference(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && value.AsSpan(at + 1).Contains(':');
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
/// <param name="RepositoryRef">The repository commit or ref mapped to this package version.</param>
/// <param name="DeclaredLicenseReferenceKind">What sort of place the publisher declared, when it declared one.</param>
/// <param name="DeclaredLicenseReference">The declared location, empty for embedded text whose content is not retained.</param>
public readonly record struct PackageMetadataRecord(
    string CacheKey,
    string Source,
    string RawLicense,
    string RepositoryUrl,
    string[] Warnings,
    string[] Errors,
    DateTimeOffset FetchedAt = default,
    string RepositoryRef = "",
    DeclaredLicenseReferenceKind DeclaredLicenseReferenceKind = DeclaredLicenseReferenceKind.None,
    string DeclaredLicenseReference = "")
{
    /// <summary>
    /// Gets the package metadata cache schema version.
    /// </summary>
    public int SchemaVersion => 1;

    /// <summary>
    /// Gets the metadata resolver capability version.
    /// </summary>
    public int ResolverVersion => CurrentResolverVersion;

    /// <summary>The resolver capability version this build writes.</summary>
    public const int CurrentResolverVersion = 4;

    /// <summary>
    /// Gets the SHA-256 hash of <see cref="CacheKey"/>.
    /// </summary>
    public string CacheKeySha256 => PackageMetadataCache.GetCacheKeySha256(CacheKey);
}

[JsonSerializable(typeof(PackageMetadataRecord))]
internal sealed partial class PackageMetadataJsonContext : JsonSerializerContext;
