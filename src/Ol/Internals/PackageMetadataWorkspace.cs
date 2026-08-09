using System.Buffers;

namespace Ol.Internals;

/// <summary>
/// Contains the package-metadata facts one component carries into the source repository stage.
/// </summary>
/// <param name="CacheKey">The logical package identity that produced the metadata.</param>
/// <param name="RepositoryUrl">The repository URL for source evidence planning, or an empty value.</param>
/// <param name="RepositoryRef">The repository commit or ref mapped to this package version.</param>
/// <param name="RepositorySubdirectoryDeclared">Whether the publisher placed this package in one directory of a repository that holds several.</param>
internal readonly record struct PackageMetadataResolution(string CacheKey, string RepositoryUrl, string RepositoryRef, bool RepositorySubdirectoryDeclared = false);

/// <summary>
/// Owns the pooled per-component package metadata records that the enrichment stages share.
/// </summary>
/// <remarks>
/// The rented array is never handed out. Callers reach it only through <see cref="Records"/>, whose
/// <see cref="Span{T}"/> type prevents the buffer from being stored in a field, captured by a closure,
/// or held across an <c>await</c>. Disposal drops the reference before returning the rental, so an
/// access that outlives the owning scope fails instead of reading recycled pool storage.
/// </remarks>
internal sealed class PackageMetadataWorkspace : IDisposable
{
    private PackageMetadataResolution?[]? records;

    /// <summary>Rents a workspace covering every component of one scan.</summary>
    /// <param name="componentCount">The number of components the workspace must cover.</param>
    public PackageMetadataWorkspace(int componentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(componentCount);
        records = ArrayPool<PackageMetadataResolution?>.Shared.Rent(Math.Max(componentCount, 1));
        Length = componentCount;
    }

    /// <summary>Gets the number of components this workspace covers.</summary>
    public int Length { get; }

    /// <summary>Gets the per-component records for the duration of one synchronous region.</summary>
    /// <exception cref="ObjectDisposedException">The rental has already been returned.</exception>
    public Span<PackageMetadataResolution?> Records
        => (records ?? throw new ObjectDisposedException(nameof(PackageMetadataWorkspace))).AsSpan(0, Length);

    /// <summary>Returns the rental and invalidates every later access.</summary>
    public void Dispose()
    {
        var returned = records;
        if (returned is null)
        {
            return;
        }

        records = null;
        returned.AsSpan(0, Length).Clear();
        ArrayPool<PackageMetadataResolution?>.Shared.Return(returned);
    }
}
