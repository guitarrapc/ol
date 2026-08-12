using System.Buffers;
using System.Text.Json;
using System.Xml;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Core.PackageManagers;

/// <summary>Summarizes local license-document collection from restored NuGet packages.</summary>
public readonly record struct NuGetArtifactCollectionSummary(int TargetCount, int DocumentCount, int MatchedCount);

/// <summary>Contains components enriched from restored NuGet package artifacts.</summary>
public readonly record struct NuGetArtifactCollection(ScanComponent[] Components, NuGetArtifactCollectionSummary Summary);

/// <summary>Collects license documents from packages named by a NuGet <c>project.assets.json</c>.</summary>
/// <remarks>
/// The assets file is read into pooled storage and streamed twice: package roots first, then libraries.
/// A pooled open-addressing table joins UTF-8 library identities to components without per-package strings.
/// Only paths passed to filesystem APIs and provenance retained by the result become owned text.
/// </remarks>
public static class NuGetRestoreArtifactCollector
{
    private static readonly string[] ConventionalLicenseNames = ["COPYING", "LICENCE", "LICENSE", "UNLICENSE"];

    /// <summary>Matches bounded license documents from already-restored package directories.</summary>
    public static NuGetArtifactCollection Collect(
        string assetsPath,
        ScanComponent[] components,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsPath);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(spdxLicenseIndex);

        if (!CacheFile.TryRentContent(assetsPath, out var assetsBytes, out var assetsLength) || assetsLength == 0)
        {
            return new NuGetArtifactCollection(components, default);
        }

        var roots = ArrayPool<string>.Shared.Rent(4);
        var componentTableLength = GetComponentTableLength(components.Length);
        var componentTable = ArrayPool<int>.Shared.Rent(componentTableLength);
        try
        {
            var assets = SkipUtf8Bom(assetsBytes.AsSpan(0, assetsLength));
            var rootCount = ReadPackageFolders(assets, ref roots);
            if (rootCount == 0) return new NuGetArtifactCollection(components, default);

            var table = componentTable.AsSpan(0, componentTableLength);
            table.Clear();
            BuildComponentTable(components, table);
            return CollectLibraries(assets, components, matcher, spdxLicenseIndex, roots.AsSpan(0, rootCount), table);
        }
        finally
        {
            ArrayPool<string>.Shared.Return(roots, clearArray: true);
            ArrayPool<int>.Shared.Return(componentTable);
            CacheFile.Return(assetsBytes);
        }
    }

    private static NuGetArtifactCollection CollectLibraries(
        ReadOnlySpan<byte> assets,
        ScanComponent[] components,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex,
        ReadOnlySpan<string> roots,
        ReadOnlySpan<int> componentTable)
    {
        var targetCount = 0;
        var documentCount = 0;
        var matchedCount = 0;
        var reader = new Utf8JsonReader(assets);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("NuGet project.assets.json root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("NuGet project.assets.json contains an invalid root property.");
            if (!reader.ValueTextEquals("libraries"u8))
            {
                reader.Read();
                reader.Skip();
                continue;
            }

            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("NuGet libraries must be an object.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("NuGet library identity is invalid.");
                var identity = reader.ValueSpan;
                var componentIndex = reader.ValueIsEscaped
                    ? FindComponent(components, reader.GetString() ?? string.Empty)
                    : FindComponent(components, componentTable, identity);
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("NuGet library must be an object.");
                var isPackage = false;
                string? relativePackagePath = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("NuGet library property is invalid.");
                    if (reader.ValueTextEquals("type"u8))
                    {
                        reader.Read();
                        isPackage = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("package"u8);
                    }
                    else if (componentIndex >= 0 && reader.ValueTextEquals("path"u8))
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.String) relativePackagePath = reader.GetString();
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                if (componentIndex < 0 || !isPackage || string.IsNullOrWhiteSpace(relativePackagePath)) continue;

                var packageDirectory = FindPackageDirectory(roots, relativePackagePath);
                if (packageDirectory is null) continue;
                targetCount++;

                string[] licensePaths;
                try
                {
                    licensePaths = FindLicensePaths(packageDirectory);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                for (var pathIndex = 0; pathIndex < licensePaths.Length; pathIndex++)
                {
                    var licensePath = licensePaths[pathIndex];
                    if (!TryReadBounded(licensePath, matcher.MaximumTextBytes, out var content, out var length)) continue;
                    try
                    {
                        documentCount++;
                        var bytes = content.AsSpan(0, length);
                        var logicalPath = Path.GetRelativePath(packageDirectory, licensePath).Replace(Path.DirectorySeparatorChar, '/');
                        var artifact = components[componentIndex].Purl.ToString();
                        var evidence = new LicenseEvidence(
                            LicenseEvidenceKind.PackageArtifact,
                            PackageArtifact: PackageArtifactEvidence.Create(artifact, logicalPath, bytes, matcher.CorpusVersion));
                        LicenseCandidate candidate;
                        if (matcher.TryMatch(SkipUtf8Bom(bytes), out var licenseId))
                        {
                            candidate = LicenseCandidateFactory.Create(
                                LicenseCandidateSource.PackageArtifact,
                                LicenseCandidateKind.License,
                                Utf8Slice.FromString(licenseId),
                                spdxLicenseIndex,
                                evidence);
                            matchedCount++;
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
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(content);
                    }
                }
            }

            return new NuGetArtifactCollection(components, new NuGetArtifactCollectionSummary(targetCount, documentCount, matchedCount));
        }

        return new NuGetArtifactCollection(components, default);
    }

    private static int ReadPackageFolders(ReadOnlySpan<byte> assets, ref string[] roots)
    {
        var count = 0;
        var reader = new Utf8JsonReader(assets);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("NuGet project.assets.json root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("NuGet project.assets.json contains an invalid root property.");
            if (!reader.ValueTextEquals("packageFolders"u8))
            {
                reader.Read();
                reader.Skip();
                continue;
            }

            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject) return 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("NuGet package folder is invalid.");
                var folder = reader.GetString();
                reader.Read();
                reader.Skip();
                if (string.IsNullOrWhiteSpace(folder)) continue;
                if (count == roots.Length)
                {
                    var replacement = ArrayPool<string>.Shared.Rent(roots.Length * 2);
                    roots.AsSpan(0, count).CopyTo(replacement);
                    ArrayPool<string>.Shared.Return(roots, clearArray: true);
                    roots = replacement;
                }

                roots[count++] = Path.GetFullPath(folder);
            }

            return count;
        }

        return 0;
    }

    private static int GetComponentTableLength(int componentCount)
    {
        if (componentCount <= 1) return 4;
        return (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Min(componentCount, 1 << 29) * 2);
    }

    private static void BuildComponentTable(ReadOnlySpan<ScanComponent> components, Span<int> table)
    {
        var mask = table.Length - 1;
        for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
        {
            ref readonly var component = ref components[componentIndex];
            if (!string.Equals(component.Ecosystem, "nuget", StringComparison.OrdinalIgnoreCase)) continue;
            var slot = (int)(HashIdentity(component.Name.Span, component.Version.Span) & (uint)mask);
            while (table[slot] != 0) slot = (slot + 1) & mask;
            table[slot] = componentIndex + 1;
        }
    }

    private static int FindComponent(ReadOnlySpan<ScanComponent> components, ReadOnlySpan<int> table, ReadOnlySpan<byte> identity)
    {
        var separator = identity.LastIndexOf((byte)'/');
        if (separator <= 0 || separator == identity.Length - 1) return -1;
        var name = identity[..separator];
        var version = identity[(separator + 1)..];
        var mask = table.Length - 1;
        var slot = (int)(HashIdentity(name, version) & (uint)mask);
        while (true)
        {
            var stored = table[slot];
            if (stored == 0) return -1;
            var componentIndex = stored - 1;
            ref readonly var component = ref components[componentIndex];
            if (AsciiEqualsIgnoreCase(component.Name.Span, name) && AsciiEqualsIgnoreCase(component.Version.Span, version)) return componentIndex;
            slot = (slot + 1) & mask;
        }
    }

    private static int FindComponent(ReadOnlySpan<ScanComponent> components, string identity)
    {
        var separator = identity.LastIndexOf('/');
        if (separator <= 0 || separator == identity.Length - 1) return -1;
        var name = identity.AsSpan(0, separator);
        var version = identity.AsSpan(separator + 1);
        for (var i = 0; i < components.Length; i++)
        {
            ref readonly var component = ref components[i];
            if (!string.Equals(component.Ecosystem, "nuget", StringComparison.OrdinalIgnoreCase)) continue;
            if (System.Text.Encoding.UTF8.GetByteCount(name) != component.Name.Length
                || System.Text.Encoding.UTF8.GetByteCount(version) != component.Version.Length)
            {
                continue;
            }

            if (component.Name.ToString().AsSpan().Equals(name, StringComparison.OrdinalIgnoreCase)
                && component.Version.ToString().AsSpan().Equals(version, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static uint HashIdentity(ReadOnlySpan<byte> name, ReadOnlySpan<byte> version)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        for (var i = 0; i < name.Length; i++) hash = (hash ^ ToLowerAscii(name[i])) * prime;
        hash = (hash ^ (byte)'/') * prime;
        for (var i = 0; i < version.Length; i++) hash = (hash ^ ToLowerAscii(version[i])) * prime;
        return hash;
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (ToLowerAscii(left[i]) != ToLowerAscii(right[i])) return false;
        }

        return true;
    }

    private static byte ToLowerAscii(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private static string? FindPackageDirectory(ReadOnlySpan<string> roots, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath)) return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i < roots.Length; i++)
        {
            var root = Path.TrimEndingDirectorySeparator(roots[i]);
            var candidate = Path.GetFullPath(relativePath, root);
            if (candidate.Length <= root.Length
                || !candidate.StartsWith(root, comparison)
                || !Path.EndsInDirectorySeparator(candidate.AsSpan(0, root.Length + 1))
                || !Directory.Exists(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static string[] FindLicensePaths(string packageDirectory)
    {
        var files = Directory.GetFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var declared = TryReadDeclaredLicensePath(packageDirectory, files);
        if (declared is not null) return [declared];

        var count = 0;
        for (var i = 0; i < files.Length; i++)
        {
            var stem = Path.GetFileNameWithoutExtension(files[i]);
            if (Array.BinarySearch(ConventionalLicenseNames, stem, StringComparer.OrdinalIgnoreCase) < 0) continue;
            files[count++] = files[i];
        }

        return count == files.Length ? files : files.AsSpan(0, count).ToArray();
    }

    private static string? TryReadDeclaredLicensePath(string packageDirectory, ReadOnlySpan<string> files)
    {
        for (var i = 0; i < files.Length; i++)
        {
            if (!Path.GetExtension(files[i]).Equals(".nuspec", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var reader = XmlReader.Create(files[i], new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element
                        || !string.Equals(reader.LocalName, "license", StringComparison.Ordinal)
                        || !string.Equals(reader.GetAttribute("type"), "file", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relativePath = reader.ReadElementContentAsString();
                    return TryResolveContainedFile(packageDirectory, relativePath);
                }
            }
            catch (XmlException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
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
        => HasUtf8Bom(value) ? value[3..] : value;

    private static bool HasUtf8Bom(ReadOnlySpan<byte> value)
        => value.Length >= 3 && value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf;
}
