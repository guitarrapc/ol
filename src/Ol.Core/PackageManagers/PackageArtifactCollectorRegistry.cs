using System.Buffers;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Core.PackageManagers;

/// <summary>Summarizes local license-document collection from restored package artifacts.</summary>
public readonly record struct PackageArtifactCollectionSummary(int TargetCount, int DocumentCount, int MatchedCount);

/// <summary>Contains components enriched from restored package artifacts.</summary>
public readonly record struct PackageArtifactCollection(ScanComponent[] Components, PackageArtifactCollectionSummary Summary);

/// <summary>Collects license documents using physical artifact roots recorded by a resolved input.</summary>
public delegate PackageArtifactCollection PackageArtifactCollector(
    string inputPath,
    ScanComponent[] components,
    SpdxLicenseTextMatcher matcher,
    SpdxLicenseIndex spdxLicenseIndex);

/// <summary>Associates one resolved input format with its restored-artifact collector.</summary>
public readonly record struct PackageArtifactCollectorHandler(ScanInputFormat Format, PackageArtifactCollector Collector);

/// <summary>Immutable registry of collectors whose inputs record exact restored-artifact roots.</summary>
public sealed class PackageArtifactCollectorRegistry
{
    private readonly PackageArtifactCollectorHandler[] handlers;

    /// <summary>Gets collectors backed by resolver-recorded physical paths.</summary>
    public static PackageArtifactCollectorRegistry Default { get; } = new([
        new(ScanInputFormat.NuGetAssets, CollectNuGet),
        new(ScanInputFormat.NpmPackageLock, NpmRestoreArtifactCollector.Collect),
        new(ScanInputFormat.CargoMetadata, CargoRestoreArtifactCollector.Collect),
        new(ScanInputFormat.PipInspect, PipRestoreArtifactCollector.Collect),
        new(ScanInputFormat.GoModuleGraph, GoRestoreArtifactCollector.Collect),
    ]);

    /// <summary>Creates a registry from distinct format handlers.</summary>
    public PackageArtifactCollectorRegistry(PackageArtifactCollectorHandler[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = handlers.ToArray();
        for (var index = 0; index < this.handlers.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(this.handlers[index].Format.Name) || this.handlers[index].Collector is null)
            {
                throw new ArgumentException("Package artifact handlers require a format and collector.", nameof(handlers));
            }

            for (var prior = 0; prior < index; prior++)
            {
                if (string.Equals(this.handlers[prior].Format.Name, this.handlers[index].Format.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Duplicate package artifact format: {this.handlers[index].Format.Name}", nameof(handlers));
                }
            }
        }
    }

    /// <summary>Finds a collector by stable input-format name.</summary>
    public bool TryGet(ScanInputFormat format, out PackageArtifactCollectorHandler handler)
    {
        for (var index = 0; index < handlers.Length; index++)
        {
            if (!string.Equals(handlers[index].Format.Name, format.Name, StringComparison.OrdinalIgnoreCase)) continue;
            handler = handlers[index];
            return true;
        }

        handler = default;
        return false;
    }

    private static PackageArtifactCollection CollectNuGet(
        string inputPath,
        ScanComponent[] components,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        var result = NuGetRestoreArtifactCollector.Collect(inputPath, components, matcher, spdxLicenseIndex);
        return new PackageArtifactCollection(
            result.Components,
            new PackageArtifactCollectionSummary(result.Summary.TargetCount, result.Summary.DocumentCount, result.Summary.MatchedCount));
    }
}

internal static class PackageArtifactDocumentCollector
{
    private static readonly string[] ConventionalLicenseNames = ["COPYING", "LICENCE", "LICENSE", "UNLICENSE"];

    internal static PackageArtifactCollectionSummary CollectConventional(
        string packageDirectory,
        ScanComponent[] components,
        int componentIndex,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var documentCount = 0;
        var matchedCount = 0;
        for (var index = 0; index < files.Length; index++)
        {
            var stem = Path.GetFileNameWithoutExtension(files[index]);
            if (Array.BinarySearch(ConventionalLicenseNames, stem, StringComparer.OrdinalIgnoreCase) < 0) continue;
            if (!TryCollectFile(packageDirectory, files[index], components, componentIndex, matcher, spdxLicenseIndex, out var matched)) continue;
            documentCount++;
            if (matched) matchedCount++;
        }

        return new PackageArtifactCollectionSummary(1, documentCount, matchedCount);
    }

    internal static PackageArtifactCollectionSummary CollectDeclared(
        string packageDirectory,
        ReadOnlySpan<byte> relativePathUtf8,
        string? pathPrefix,
        ScanComponent[] components,
        int componentIndex,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        if (relativePathUtf8.IsEmpty) return default;
        var relativePath = System.Text.Encoding.UTF8.GetString(relativePathUtf8);
        if (!string.IsNullOrEmpty(pathPrefix)) relativePath = Path.Combine(pathPrefix, relativePath);
        var path = TryResolveContainedFile(packageDirectory, relativePath);
        if (path is null) return new PackageArtifactCollectionSummary(1, 0, 0);
        return TryCollectFile(packageDirectory, path, components, componentIndex, matcher, spdxLicenseIndex, out var matched)
            ? new PackageArtifactCollectionSummary(1, 1, matched ? 1 : 0)
            : new PackageArtifactCollectionSummary(1, 0, 0);
    }

    internal static PackageArtifactCollectionSummary Add(PackageArtifactCollectionSummary left, PackageArtifactCollectionSummary right)
        => new(left.TargetCount + right.TargetCount, left.DocumentCount + right.DocumentCount, left.MatchedCount + right.MatchedCount);

    internal static string? TryResolveContainedDirectory(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Length > root.Length
            && candidate.StartsWith(root, comparison)
            && Path.EndsInDirectorySeparator(candidate.AsSpan(0, root.Length + 1))
            && Directory.Exists(candidate)
            ? candidate
            : null;
    }

    private static bool TryCollectFile(
        string packageDirectory,
        string licensePath,
        ScanComponent[] components,
        int componentIndex,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex,
        out bool matched)
    {
        matched = false;
        if (!TryReadBounded(licensePath, matcher.MaximumTextBytes, out var content, out var length)) return false;
        try
        {
            var bytes = content.AsSpan(0, length);
            var logicalPath = Path.GetRelativePath(packageDirectory, licensePath).Replace(Path.DirectorySeparatorChar, '/');
            var artifact = components[componentIndex].Purl.IsEmpty
                ? components[componentIndex].SourceId.ToString()
                : components[componentIndex].Purl.ToString();
            matched = matcher.TryMatch(SkipUtf8Bom(bytes), out var licenseId, out var matchKind);
            var evidence = new LicenseEvidence(
                LicenseEvidenceKind.PackageArtifact,
                PackageArtifact: PackageArtifactEvidence.Create(artifact, logicalPath, bytes, matcher.CorpusVersion, matched ? matchKind.ToMatcherId() : "spdx-template"));
            LicenseCandidate candidate;
            if (matched)
            {
                candidate = LicenseCandidateFactory.Create(
                    LicenseCandidateSource.PackageArtifact,
                    LicenseCandidateKind.License,
                    Utf8Slice.FromString(licenseId),
                    spdxLicenseIndex,
                    evidence);
            }
            else
            {
                candidate = new LicenseCandidate(
                    LicenseCandidateSource.PackageArtifact,
                    LicenseCandidateKind.License,
                    default,
                    default,
                    LicenseStatus.Unknown,
                    false,
                    LicenseCandidateWarnings.SourceLicenseNotDetected,
                    evidence);
            }

            components[componentIndex] = LicenseReconciler.AddCandidate(components[componentIndex], candidate);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(content);
        }
    }

    private static string? TryResolveContainedFile(string packageDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Length > root.Length
            && candidate.StartsWith(root, comparison)
            && Path.EndsInDirectorySeparator(candidate.AsSpan(0, root.Length + 1))
            && File.Exists(candidate)
            ? candidate
            : null;
    }

    private static bool TryReadBounded(string path, int maximumBytes, out byte[] content, out int length)
    {
        content = [];
        length = 0;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            var fileLength = RandomAccess.GetLength(handle);
            if (fileLength <= 0 || fileLength > maximumBytes || fileLength > int.MaxValue) return false;
            content = ArrayPool<byte>.Shared.Rent((int)fileLength);
            while (length < fileLength)
            {
                var read = RandomAccess.Read(handle, content.AsSpan(length, (int)fileLength - length), length);
                if (read == 0) break;
                length += read;
            }

            if (length == fileLength) return true;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (content.Length != 0) ArrayPool<byte>.Shared.Return(content);
        content = [];
        length = 0;
        return false;
    }

    private static ReadOnlySpan<byte> SkipUtf8Bom(ReadOnlySpan<byte> value)
        => value.Length >= 3 && value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf ? value[3..] : value;
}

internal static class PackageArtifactComponentIndex
{
    internal static int[] Rent(ReadOnlySpan<ScanComponent> components, string ecosystem, out int length)
    {
        length = GetTableLength(components.Length);
        var table = ArrayPool<int>.Shared.Rent(length);
        table.AsSpan(0, length).Clear();
        var mask = length - 1;
        for (var index = 0; index < components.Length; index++)
        {
            if (!string.Equals(components[index].Ecosystem, ecosystem, StringComparison.OrdinalIgnoreCase)) continue;
            var slot = (int)(Fnv1a.Hash(components[index].SourceId.Span) & (uint)mask);
            while (table[slot] != 0) slot = (slot + 1) & mask;
            table[slot] = index + 1;
        }

        return table;
    }

    internal static int Find(ReadOnlySpan<ScanComponent> components, ReadOnlySpan<int> table, ReadOnlySpan<byte> sourceId)
    {
        var mask = table.Length - 1;
        var slot = (int)(Fnv1a.Hash(sourceId) & (uint)mask);
        while (true)
        {
            var stored = table[slot];
            if (stored == 0) return -1;
            var index = stored - 1;
            if (components[index].SourceId.Span.SequenceEqual(sourceId)) return index;
            slot = (slot + 1) & mask;
        }
    }

    internal static int FindNormalizedPython(
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<int> table,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> version)
    {
        var hash = HashNormalizedPythonName(name);
        hash = Fnv1a.Hash("@"u8, hash);
        hash = Fnv1a.Hash(version, hash);
        var mask = table.Length - 1;
        var slot = (int)(hash & (uint)mask);
        while (true)
        {
            var stored = table[slot];
            if (stored == 0) return -1;
            var index = stored - 1;
            var sourceId = components[index].SourceId.Span;
            var separator = sourceId.LastIndexOf((byte)'@');
            if (separator > 0
                && sourceId[(separator + 1)..].SequenceEqual(version)
                && PythonNameEquals(sourceId[..separator], name)) return index;
            slot = (slot + 1) & mask;
        }
    }

    private static int GetTableLength(int count)
        => count <= 1 ? 4 : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Min(count, 1 << 29) * 2);

    private static uint HashNormalizedPythonName(ReadOnlySpan<byte> value)
    {
        var hash = Fnv1a.OffsetBasis;
        Span<byte> currentBuffer = stackalloc byte[1];
        var separator = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current is (byte)'.' or (byte)'_' or (byte)'-')
            {
                if (separator) continue;
                current = (byte)'-';
                separator = true;
            }
            else
            {
                separator = false;
                if (current is >= (byte)'A' and <= (byte)'Z') current += 32;
            }

            currentBuffer[0] = current;
            hash = Fnv1a.Hash(currentBuffer, hash);
        }

        return hash;
    }

    private static bool PythonNameEquals(ReadOnlySpan<byte> normalized, ReadOnlySpan<byte> value)
    {
        var normalizedIndex = 0;
        var separator = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current is (byte)'.' or (byte)'_' or (byte)'-')
            {
                if (separator) continue;
                current = (byte)'-';
                separator = true;
            }
            else
            {
                separator = false;
                if (current is >= (byte)'A' and <= (byte)'Z') current += 32;
            }

            if ((uint)normalizedIndex >= (uint)normalized.Length || normalized[normalizedIndex++] != current) return false;
        }

        return normalizedIndex == normalized.Length;
    }
}
