using System.Buffers;
using Ol.Core.PackageMetadata;

namespace Ol.Internals;

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
    private PackageMetadataRecord?[]? records;

    /// <summary>Rents a workspace covering every component of one scan.</summary>
    /// <param name="componentCount">The number of components the workspace must cover.</param>
    public PackageMetadataWorkspace(int componentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(componentCount);
        records = ArrayPool<PackageMetadataRecord?>.Shared.Rent(Math.Max(componentCount, 1));
        Length = componentCount;
    }

    /// <summary>Gets the number of components this workspace covers.</summary>
    public int Length { get; }

    /// <summary>Gets the per-component records for the duration of one synchronous region.</summary>
    /// <exception cref="ObjectDisposedException">The rental has already been returned.</exception>
    public Span<PackageMetadataRecord?> Records
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
        ArrayPool<PackageMetadataRecord?>.Shared.Return(returned);
    }
}
