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

        var assetsBytes = File.ReadAllBytes(assetsPath);
        using var document = JsonDocument.Parse(assetsBytes.AsMemory(HasUtf8Bom(assetsBytes) ? 3 : 0));
        var root = document.RootElement;
        if (!root.TryGetProperty("packageFolders", out var packageFolders) || packageFolders.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
        {
            return new NuGetArtifactCollection(components, default);
        }

        var componentIndexes = new Dictionary<string, int>(components.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < components.Length; i++)
        {
            if (!string.Equals(components[i].Ecosystem, "nuget", StringComparison.OrdinalIgnoreCase)) continue;
            componentIndexes.TryAdd(string.Concat(components[i].Name.ToString(), "/", components[i].Version.ToString()), i);
        }

        var roots = ReadPackageFolders(packageFolders);
        var targetCount = 0;
        var documentCount = 0;
        var matchedCount = 0;
        foreach (var library in libraries.EnumerateObject())
        {
            if (!componentIndexes.TryGetValue(library.Name, out var componentIndex)
                || library.Value.ValueKind != JsonValueKind.Object
                || !library.Value.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !type.ValueEquals("package"u8))
            {
                continue;
            }

            var relativePackagePath = library.Value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                ? path.GetString()
                : library.Name.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(relativePackagePath)) continue;

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

    private static string[] ReadPackageFolders(JsonElement packageFolders)
    {
        var roots = new string[packageFolders.GetPropertyCount()];
        var count = 0;
        foreach (var folder in packageFolders.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(folder.Name)) continue;
            roots[count++] = Path.GetFullPath(folder.Name);
        }

        return count == roots.Length ? roots : roots.AsSpan(0, count).ToArray();
    }

    private static string? FindPackageDirectory(ReadOnlySpan<string> roots, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath)) return null;
        var platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i < roots.Length; i++)
        {
            var root = Path.TrimEndingDirectorySeparator(roots[i]);
            var candidate = Path.GetFullPath(Path.Combine(root, platformPath));
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
        var declared = TryReadDeclaredLicensePath(packageDirectory);
        if (declared is not null) return [declared];

        var files = Directory.GetFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        for (var i = 0; i < files.Length; i++)
        {
            var stem = Path.GetFileNameWithoutExtension(files[i]);
            if (Array.BinarySearch(ConventionalLicenseNames, stem, StringComparer.OrdinalIgnoreCase) < 0) continue;
            files[count++] = files[i];
        }

        return count == files.Length ? files : files.AsSpan(0, count).ToArray();
    }

    private static string? TryReadDeclaredLicensePath(string packageDirectory)
    {
        var nuspecs = Directory.GetFiles(packageDirectory, "*.nuspec", SearchOption.TopDirectoryOnly);
        Array.Sort(nuspecs, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nuspecs.Length; i++)
        {
            try
            {
                using var reader = XmlReader.Create(nuspecs[i], new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
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
