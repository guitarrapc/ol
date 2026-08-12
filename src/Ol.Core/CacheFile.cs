using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ol.Core;

/// <summary>
/// Provides the shared opaque-cache file primitives used by every cache category.
/// </summary>
/// <remarks>
/// Cache entries are read through <see cref="RandomAccess"/> into pooled storage instead of a
/// <see cref="FileStream"/>, because the first read on a buffered stream allocates its internal
/// 4 KB buffer for every entry. Cache-key hashing and path construction are separated so one
/// lookup hashes its key once and reuses the result for the path and for report evidence.
/// </remarks>
internal static class CacheFile
{
    /// <summary>The stack budget for UTF-8 encoding a cache key before hashing.</summary>
    private const int MaxStackKeyBytes = 512;

    private const string Extension = ".json";

    /// <summary>Calculates the lower-case SHA-256 of a logical cache key.</summary>
    /// <param name="cacheKey">The logical cache key.</param>
    /// <returns>The 64-character lower-case hexadecimal hash.</returns>
    public static string GetCacheKeySha256(string cacheKey)
    {
        var maximumByteCount = Encoding.UTF8.GetMaxByteCount(cacheKey.Length);
        byte[]? rented = null;
        var utf8 = maximumByteCount <= MaxStackKeyBytes
            ? stackalloc byte[MaxStackKeyBytes]
            : (rented = ArrayPool<byte>.Shared.Rent(maximumByteCount));
        try
        {
            return GetCacheKeySha256(utf8[..Encoding.UTF8.GetBytes(cacheKey, utf8)]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Calculates the lower-case SHA-256 of a UTF-8 cache key.</summary>
    /// <param name="cacheKey">The logical cache key, as the bytes it was read from.</param>
    /// <returns>The 64-character lower-case hexadecimal hash.</returns>
    /// <remarks>
    /// A key that already exists as UTF-8 is hashed where it lies. The text overload encodes into a bounded
    /// buffer and lands here, which is what the hash always did — it never wanted characters.
    /// </remarks>
    public static string GetCacheKeySha256(ReadOnlySpan<byte> cacheKey)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(cacheKey, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Builds the opaque cache file path from an already calculated cache-key hash.</summary>
    /// <param name="root">The cache category directory.</param>
    /// <param name="cacheKeySha256">The cache-key hash produced by <see cref="GetCacheKeySha256"/>.</param>
    /// <returns>The cache file path.</returns>
    public static string BuildPath(string root, string cacheKeySha256)
    {
        if (root.Length == 0)
        {
            return string.Concat(cacheKeySha256, Extension);
        }

        var separator = Path.EndsInDirectorySeparator(root) ? 0 : 1;
        return string.Create(root.Length + separator + cacheKeySha256.Length + Extension.Length, (root, separator, cacheKeySha256), static (destination, state) =>
        {
            state.root.CopyTo(destination);
            var offset = state.root.Length;
            if (state.separator != 0)
            {
                destination[offset++] = Path.DirectorySeparatorChar;
            }

            state.cacheKeySha256.CopyTo(destination[offset..]);
            Extension.CopyTo(destination[(offset + state.cacheKeySha256.Length)..]);
        });
    }

    /// <summary>Reads a cache entry into pooled storage without a buffered stream.</summary>
    /// <param name="path">The cache file path.</param>
    /// <param name="content">The rented buffer, which the caller must return.</param>
    /// <param name="length">The number of valid bytes in <paramref name="content"/>.</param>
    /// <returns><see langword="false"/> only when no entry exists; an unreadable entry reads as zero bytes.</returns>
    public static bool TryRentContent(string path, out byte[] content, out int length)
        => TryRentContent(path, int.MaxValue, out content, out length);

    /// <summary>Reads a cache entry only when its file length is within the caller's bound.</summary>
    public static bool TryRentContent(string path, int maximumLength, out byte[] content, out int length)
    {
        using var handle = TryOpen(path);
        if (handle is null)
        {
            content = [];
            length = 0;
            return false;
        }

        if (!TryRent(handle, maximumLength, out content, out var fileLength))
        {
            length = 0;
            return true;
        }

        var offset = 0;
        while (offset < fileLength)
        {
            var read = RandomAccess.Read(handle, content.AsSpan(offset, fileLength - offset), offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        length = offset;
        return true;
    }

    /// <summary>Reads a cache entry into pooled storage without a buffered stream.</summary>
    /// <param name="path">The cache file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rented buffer and its valid length, or a length of -1 when no entry exists.</returns>
    public static async ValueTask<(byte[] Content, int Length)> RentContentAsync(string path, CancellationToken cancellationToken)
    {
        using var handle = TryOpen(path);
        if (handle is null)
        {
            return ([], -1);
        }

        if (!TryRent(handle, int.MaxValue, out var content, out var fileLength))
        {
            return ([], 0);
        }

        var offset = 0;
        while (offset < fileLength)
        {
            var read = await RandomAccess.ReadAsync(handle, content.AsMemory(offset, fileLength - offset), offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return (content, offset);
    }

    /// <summary>Returns a buffer obtained from <see cref="TryRentContent"/> or <see cref="RentContentAsync"/>.</summary>
    public static void Return(byte[] content)
    {
        if (content.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(content);
        }
    }

    /// <remarks>
    /// A cold cache misses once per lookup, so absence is decided by <see cref="File.Exists(string)"/>
    /// rather than by throwing. The handle open still catches the same failures because the entry can
    /// disappear between the two calls.
    /// </remarks>
    private static SafeFileHandle? TryOpen(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.OpenHandle(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool TryRent(SafeFileHandle handle, int maximumLength, out byte[] content, out int length)
    {
        var fileLength = RandomAccess.GetLength(handle);
        if (fileLength <= 0 || fileLength > maximumLength || fileLength > Array.MaxLength)
        {
            content = [];
            length = 0;
            return false;
        }

        length = (int)fileLength;
        content = ArrayPool<byte>.Shared.Rent(length);
        return true;
    }
}
