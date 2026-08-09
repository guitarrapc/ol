using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ol.Core.SourceRepository;

/// <summary>Stores source-repository evidence using opaque hash-named cache files.</summary>
public sealed class SourceRepositoryCache(string root)
{
    /// <summary>Gets the source cache directory.</summary>
    public string Root { get; } = root;

    /// <summary>Gets the opaque cache path for a logical key.</summary>
    public string GetPath(string cacheKey) => CacheFile.BuildPath(Root, CacheFile.GetCacheKeySha256(cacheKey));

    /// <summary>Gets the lower-case SHA-256 of a logical cache key.</summary>
    public static string GetCacheKeySha256(string cacheKey) => CacheFile.GetCacheKeySha256(cacheKey);

    /// <summary>Reads a compatible source evidence entry, or null when absent or corrupt.</summary>
    public async Task<SourceRepositoryRecord?> TryReadAsync(string cacheKey, CancellationToken cancellationToken = default)
        => (await ReadAsync(cacheKey, cancellationToken).ConfigureAwait(false)).Record;

    /// <summary>Reads an entry and distinguishes a cache miss from invalid or stale entries.</summary>
    public async Task<SourceRepositoryCacheReadResult> ReadAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var cacheKeySha256 = CacheFile.GetCacheKeySha256(cacheKey);
        byte[] content;
        int length;
        try
        {
            (content, length) = await CacheFile.RentContentAsync(CacheFile.BuildPath(Root, cacheKeySha256), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { return new(SourceRepositoryCacheReadStatus.Invalid, null); }

        if (length < 0)
        {
            return new(SourceRepositoryCacheReadStatus.Missing, null);
        }

        try
        {
            return Parse(content.AsSpan(0, length), cacheKey, cacheKeySha256);
        }
        finally
        {
            CacheFile.Return(content);
        }
    }

    /// <summary>Reads an entry without asynchronous file access and distinguishes a cache miss from invalid or stale entries.</summary>
    public SourceRepositoryCacheReadResult Read(string cacheKey)
    {
        var cacheKeySha256 = CacheFile.GetCacheKeySha256(cacheKey);
        byte[] content;
        int length;
        try
        {
            if (!CacheFile.TryRentContent(CacheFile.BuildPath(Root, cacheKeySha256), out content, out length))
            {
                return new(SourceRepositoryCacheReadStatus.Missing, null);
            }
        }
        catch (IOException) { return new(SourceRepositoryCacheReadStatus.Invalid, null); }

        try
        {
            return Parse(content.AsSpan(0, length), cacheKey, cacheKeySha256);
        }
        finally
        {
            CacheFile.Return(content);
        }
    }

    /// <summary>
    /// Validates and materializes one entry in a single pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generated <see cref="JsonSerializer"/> path materializes <c>CacheKey</c>, <c>Source</c>, and
    /// <c>AuthMode</c> that only ever get compared, and pays constructor-argument overhead for a
    /// sixteen-parameter record shape. Only the values that reach report evidence are decoded here.
    /// </para>
    /// <para>
    /// This pass also validates the persisted <c>SchemaVersion</c> and <c>CacheKeySha256</c>, which
    /// specs/cache_format.md requires. The previous reader compared the deserialized record's calculated
    /// properties instead, and those are never populated from the file, so neither value was checked.
    /// </para>
    /// </remarks>
    private static SourceRepositoryCacheReadResult Parse(ReadOnlySpan<byte> utf8, string cacheKey, string cacheKeySha256)
    {
        try
        {
            if (!TryParse(utf8, cacheKey, cacheKeySha256, out var record, out var resolverVersion))
            {
                return new(SourceRepositoryCacheReadStatus.Invalid, null);
            }

            return IsLegacyRateLimitError(record, resolverVersion) || IsLegacyPinnedRefNotFound(record, resolverVersion)
                ? new(SourceRepositoryCacheReadStatus.Stale, null)
                : new(SourceRepositoryCacheReadStatus.Hit, record, cacheKeySha256);
        }
        catch (JsonException) { return new(SourceRepositoryCacheReadStatus.Invalid, null); }
    }

    private static bool IsLegacyRateLimitError(in SourceRepositoryRecord record, int resolverVersion)
        => resolverVersion < 2
        && record.Errors.Length != 0
        && record.HttpStatus is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    /// <summary>Rejects a pinned-ref not-found entry written before the resolver retried at the default ref.</summary>
    /// <remarks>
    /// Such an entry records that one named ref answered nothing, which the current resolver no longer
    /// treats as the repository having no license. Keeping it would hold a stale unresolved result for as
    /// long as the cache lives, so the entry is refetched instead.
    /// </remarks>
    private static bool IsLegacyPinnedRefNotFound(in SourceRepositoryRecord record, int resolverVersion)
        => resolverVersion < 3
        && record.HttpStatus == HttpStatusCode.NotFound
        && !string.Equals(record.Ref, "default", StringComparison.Ordinal);

    private static bool TryParse(ReadOnlySpan<byte> utf8, string cacheKey, string cacheKeySha256, out SourceRepositoryRecord record, out int resolverVersion)
    {
        record = default;
        resolverVersion = 0;
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        var present = CacheField.None;
        var repository = string.Empty;
        var repositoryRef = string.Empty;
        var authMode = string.Empty;
        HttpStatusCode? httpStatus = null;
        GitHubLicenseResult? license = null;
        string[] warnings = [];
        string[] errors = [];
        var fetchedAt = default(DateTimeOffset);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            if (reader.ValueTextEquals("SchemaVersion"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version) || version != 1) return false;
                present |= CacheField.SchemaVersion;
            }
            else if (reader.ValueTextEquals("CacheKey"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(cacheKey)) return false;
                present |= CacheField.CacheKey;
            }
            else if (reader.ValueTextEquals("CacheKeySha256"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(cacheKeySha256)) return false;
                present |= CacheField.CacheKeySha256;
            }
            else if (reader.ValueTextEquals("Source"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("github-license-api"u8)) return false;
                present |= CacheField.Source;
            }
            else if (reader.ValueTextEquals("AuthMode"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                if (reader.ValueTextEquals("none"u8)) authMode = "none";
                else if (reader.ValueTextEquals("ol_github_token"u8)) authMode = "ol_github_token";
                else return false;
                present |= CacheField.AuthMode;
            }
            else if (reader.ValueTextEquals("Repository"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                repository = reader.GetString()!;
                if (repository.Length == 0) return false;
                present |= CacheField.Repository;
            }
            else if (reader.ValueTextEquals("Ref"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                repositoryRef = reader.GetString()!;
                if (repositoryRef.Length == 0) return false;
                present |= CacheField.Ref;
            }
            else if (reader.ValueTextEquals("HttpStatus"u8))
            {
                if (!reader.Read()) return false;
                if (reader.TokenType == JsonTokenType.Number)
                {
                    if (!reader.TryGetInt32(out var status)) return false;
                    httpStatus = (HttpStatusCode)status;
                }
                else if (reader.TokenType != JsonTokenType.Null)
                {
                    return false;
                }

                present |= CacheField.HttpStatus;
            }
            else if (reader.ValueTextEquals("License"u8))
            {
                if (!TryReadLicense(ref reader, out license)) return false;
                present |= CacheField.License;
            }
            else if (reader.ValueTextEquals("Warnings"u8))
            {
                if (!TryReadStringArray(ref reader, out warnings)) return false;
                present |= CacheField.Warnings;
            }
            else if (reader.ValueTextEquals("Errors"u8))
            {
                if (!TryReadStringArray(ref reader, out errors)) return false;
                present |= CacheField.Errors;
            }
            else if (reader.ValueTextEquals("FetchedAt"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.TryGetDateTimeOffset(out fetchedAt) || fetchedAt.Offset != TimeSpan.Zero) return false;
                present |= CacheField.FetchedAt;
            }
            else if (reader.ValueTextEquals("ResolverVersion"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out resolverVersion) || resolverVersion < 0) return false;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (present != CacheField.Required)
        {
            return false;
        }

        record = new SourceRepositoryRecord(cacheKey, "github-license-api", authMode, repository, repositoryRef, httpStatus, license, warnings, errors, fetchedAt);
        return true;
    }

    private static bool TryReadLicense(ref Utf8JsonReader reader, out GitHubLicenseResult? license)
    {
        license = null;
        if (!reader.Read()) return false;
        if (reader.TokenType == JsonTokenType.Null) return true;
        if (reader.TokenType != JsonTokenType.StartObject) return false;

        string? spdxId = null;
        var key = string.Empty;
        var name = string.Empty;
        var path = string.Empty;
        var sha = string.Empty;
        var htmlUrl = string.Empty;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) return false;

            if (reader.ValueTextEquals("SpdxId"u8))
            {
                if (!reader.Read()) return false;
                if (reader.TokenType == JsonTokenType.String) spdxId = reader.GetString();
                else if (reader.TokenType != JsonTokenType.Null) return false;
            }
            else if (reader.ValueTextEquals("Key"u8)) { if (!TryReadString(ref reader, out key)) return false; }
            else if (reader.ValueTextEquals("Name"u8)) { if (!TryReadString(ref reader, out name)) return false; }
            else if (reader.ValueTextEquals("Path"u8)) { if (!TryReadString(ref reader, out path)) return false; }
            else if (reader.ValueTextEquals("Sha"u8)) { if (!TryReadString(ref reader, out sha)) return false; }
            else if (reader.ValueTextEquals("HtmlUrl"u8)) { if (!TryReadString(ref reader, out htmlUrl)) return false; }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        license = new GitHubLicenseResult(spdxId, key, name, path, sha, htmlUrl);
        return true;
    }

    private static bool TryReadString(ref Utf8JsonReader reader, out string value)
    {
        value = string.Empty;
        if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
        value = reader.GetString()!;
        return true;
    }

    /// <summary>Reads a JSON string array into an exactly sized array by counting it from a reader copy first.</summary>
    private static bool TryReadStringArray(ref Utf8JsonReader reader, out string[] values)
    {
        values = [];
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return false;

        var counter = reader;
        var count = 0;
        while (counter.Read() && counter.TokenType != JsonTokenType.EndArray)
        {
            if (counter.TokenType != JsonTokenType.String) return false;
            count++;
        }

        if (counter.TokenType != JsonTokenType.EndArray) return false;
        if (count != 0)
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                reader.Read();
                result[i] = reader.GetString()!;
            }

            values = result;
        }

        reader = counter;
        return true;
    }

    /// <summary>Tracks the properties that specs/cache_format.md requires of a source entry.</summary>
    [Flags]
    private enum CacheField : ushort
    {
        None = 0,
        SchemaVersion = 1 << 0,
        CacheKey = 1 << 1,
        CacheKeySha256 = 1 << 2,
        Source = 1 << 3,
        AuthMode = 1 << 4,
        Repository = 1 << 5,
        Ref = 1 << 6,
        HttpStatus = 1 << 7,
        License = 1 << 8,
        Warnings = 1 << 9,
        Errors = 1 << 10,
        FetchedAt = 1 << 11,
        Required = SchemaVersion | CacheKey | CacheKeySha256 | Source | AuthMode | Repository | Ref | HttpStatus | License | Warnings | Errors | FetchedAt,
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

    /// <summary>Validates a record on the write path. Reads validate the persisted JSON instead.</summary>
    private static bool IsValid(SourceRepositoryRecord record, string requestedKey)
        => record.SchemaVersion == 1
            && string.Equals(record.CacheKey, requestedKey, StringComparison.Ordinal)
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
    /// <summary>An entry is valid but predates a required resolver capability.</summary>
    Stale,
}

/// <summary>Contains a classified source repository cache read.</summary>
/// <param name="Status">The outcome of the read.</param>
/// <param name="Record">The entry, when the read was a hit.</param>
/// <param name="CacheKeySha256">
/// The requested key's digest, which the read already derived to locate the entry file. Report evidence
/// needs the same value, and <see cref="SourceRepositoryRecord.CacheKeySha256"/> re-derives it.
/// </param>
public readonly record struct SourceRepositoryCacheReadResult(SourceRepositoryCacheReadStatus Status, SourceRepositoryRecord? Record, string CacheKeySha256 = "");

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
    /// <summary>Gets the source resolver capability version.</summary>
    public int ResolverVersion => 3;
    /// <summary>Gets the source cache-key SHA-256.</summary>
    /// <remarks>
    /// This exists so the digest is persisted with the entry. It hashes <see cref="CacheKey"/> on every
    /// read, so a caller that already holds the digest — every reader, which derived it to locate the
    /// entry file — should use that value instead. See <see cref="SourceRepositoryCacheReadResult.CacheKeySha256"/>.
    /// </remarks>
    public string CacheKeySha256 => SourceRepositoryCache.GetCacheKeySha256(CacheKey);
}

[JsonSerializable(typeof(SourceRepositoryRecord))]
internal sealed partial class SourceRepositoryJsonContext : JsonSerializerContext;
