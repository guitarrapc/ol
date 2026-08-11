using Ol.Core;

namespace Ol.Internals;

/// <summary>
/// Contains the package-metadata facts one component carries into the source repository stage.
/// </summary>
/// <param name="CacheKey">The logical package identity that produced the metadata.</param>
/// <param name="RepositoryUrl">The repository URL for source evidence planning, or an empty value.</param>
/// <param name="RepositoryRef">The repository commit or ref mapped to this package version.</param>
/// <param name="RepositorySubdirectoryDeclared">Whether the publisher placed this package in one directory of a repository that holds several.</param>
internal readonly record struct PackageMetadataResolution(
    Utf8Slice CacheKey,
    string RepositoryUrl,
    string RepositoryRef,
    bool RepositorySubdirectoryDeclared = false);
