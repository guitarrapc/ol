using Ol.Core.Licensing;

namespace Ol.Core.PackageMetadata;

/// <summary>
/// Contains one validated package metadata cache entry.
/// </summary>
/// <param name="IsHit">Whether a compatible entry was read.</param>
/// <param name="CacheKeySha256">The cache-key hash used for the entry file name and report evidence.</param>
/// <param name="Source">The evidence source the persisted token names.</param>
/// <param name="RawLicense">The raw package license value.</param>
/// <param name="Warnings">The retained warnings.</param>
/// <param name="RepositoryUrl">The repository URL for later source evidence planning.</param>
/// <param name="RepositoryRef">The repository commit or ref mapped to this package version.</param>
/// <param name="FetchedAt">The metadata fetch timestamp.</param>
/// <param name="ResolverVersion">The metadata resolver capability version that produced this entry.</param>
/// <param name="DeclaredLicenseReferenceKind">What sort of place the publisher declared, when it declared one.</param>
/// <param name="DeclaredLicenseReference">The declared location, empty when none was declared.</param>
public readonly record struct PackageMetadataCacheEntry(
    bool IsHit,
    string CacheKeySha256,
    LicenseCandidateSource Source,
    Utf8Slice RawLicense,
    LicenseCandidateWarnings Warnings,
    string RepositoryUrl,
    string RepositoryRef,
    DateTimeOffset FetchedAt,
    int ResolverVersion,
    DeclaredLicenseReferenceKind DeclaredLicenseReferenceKind,
    Utf8Slice DeclaredLicenseReference);
