using Ol.Core.Licensing;

namespace Ol.Core.PackageMetadata;

/// <summary>
/// Contains one validated package metadata cache entry read from pooled storage.
/// </summary>
/// <remarks>
/// <para>
/// The UTF-8 values point into a pooled buffer that <see cref="Dispose"/> returns, so a consumer must
/// copy anything it keeps before disposing the entry. <see cref="RepositoryUrl"/> and
/// <see cref="RepositoryRef"/> are owned strings because entry validation and the later source
/// repository stage both require them, and materializing each once is cheaper than twice.
/// </para>
/// <para>
/// The entry is deliberately short-lived rather than retained for a whole scan: holding one pooled
/// buffer per cached lookup until the scan completes would keep more memory alive, and exhaust the
/// pool sooner, than copying the few values that outlive the read.
/// </para>
/// </remarks>
public readonly struct PackageMetadataCacheEntry : IDisposable
{
    private readonly byte[]? content;

    internal PackageMetadataCacheEntry(
        byte[] content,
        string cacheKeySha256,
        Utf8Slice source,
        Utf8Slice rawLicense,
        Utf8Slice warnings,
        string repositoryUrl,
        string repositoryRef,
        DateTimeOffset fetchedAt,
        int resolverVersion,
        DeclaredLicenseReferenceKind declaredLicenseReferenceKind,
        Utf8Slice declaredLicenseReference)
    {
        this.content = content;
        CacheKeySha256 = cacheKeySha256;
        Source = source;
        RawLicense = rawLicense;
        Warnings = warnings;
        RepositoryUrl = repositoryUrl;
        RepositoryRef = repositoryRef;
        FetchedAt = fetchedAt;
        ResolverVersion = resolverVersion;
        DeclaredLicenseReferenceKind = declaredLicenseReferenceKind;
        DeclaredLicenseReference = declaredLicenseReference;
        IsHit = true;
    }

    /// <summary>Gets whether a compatible entry was read.</summary>
    public bool IsHit { get; }

    /// <summary>Gets the cache-key hash used for the entry file name and report evidence.</summary>
    public string CacheKeySha256 { get; } = string.Empty;

    /// <summary>Gets the metadata source identifier.</summary>
    public Utf8Slice Source { get; }

    /// <summary>Gets the raw package license value.</summary>
    public Utf8Slice RawLicense { get; }

    /// <summary>Gets the retained warnings as raw JSON array text.</summary>
    public Utf8Slice Warnings { get; }

    /// <summary>Gets the repository URL for later source evidence planning.</summary>
    public string RepositoryUrl { get; } = string.Empty;

    /// <summary>Gets the repository commit or ref mapped to this package version.</summary>
    public string RepositoryRef { get; } = string.Empty;

    /// <summary>Gets the metadata fetch timestamp.</summary>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>Gets the metadata resolver capability version that produced this entry.</summary>
    public int ResolverVersion { get; }

    /// <summary>Gets what sort of place the publisher declared, when it declared one.</summary>
    public DeclaredLicenseReferenceKind DeclaredLicenseReferenceKind { get; }

    /// <summary>Gets the declared location, pointing into the pooled buffer like the other UTF-8 values.</summary>
    /// <remarks>
    /// Kept as a slice rather than an owned string because almost no entry declares one, and materializing
    /// it on every read would allocate for every cached component to serve the few that carry it.
    /// </remarks>
    public Utf8Slice DeclaredLicenseReference { get; }

    /// <summary>Returns the pooled buffer and invalidates every UTF-8 value on this entry.</summary>
    public void Dispose()
    {
        if (content is not null)
        {
            CacheFile.Return(content);
        }
    }
}
