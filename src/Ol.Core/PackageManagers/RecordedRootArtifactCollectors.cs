using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Ol.Core.Spdx;

namespace Ol.Core.PackageManagers;

/// <summary>Collects root license documents from npm's installed package paths.</summary>
public static class NpmRestoreArtifactCollector
{
    /// <summary>Uses each npm component source id as a path relative to the package-lock directory.</summary>
    public static PackageArtifactCollection Collect(
        string packageLockPath,
        ScanComponent[] components,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageLockPath);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(spdxLicenseIndex);
        var root = Path.GetDirectoryName(Path.GetFullPath(packageLockPath));
        if (root is null) return new PackageArtifactCollection(components, default);

        var summary = default(PackageArtifactCollectionSummary);
        for (var index = 0; index < components.Length; index++)
        {
            ref readonly var component = ref components[index];
            if (!string.Equals(component.Ecosystem, "npm", StringComparison.OrdinalIgnoreCase)
                || !IsInstalledPackagePath(component.SourceId.Span)) continue;
            var directory = PackageArtifactDocumentCollector.TryResolveContainedDirectory(root, component.SourceId.ToString());
            if (directory is null) continue;
            summary = PackageArtifactDocumentCollector.Add(
                summary,
                PackageArtifactDocumentCollector.CollectConventional(directory, components, index, matcher, spdxLicenseIndex));
        }

        return new PackageArtifactCollection(components, summary);
    }

    private static bool IsInstalledPackagePath(ReadOnlySpan<byte> path)
        => path.StartsWith("node_modules/"u8) || path.IndexOf("/node_modules/"u8) >= 0;
}

/// <summary>Collects license documents from Cargo package roots recorded by <c>manifest_path</c>.</summary>
public static class CargoRestoreArtifactCollector
{
    /// <summary>Reads Cargo metadata without invoking Cargo or reconstructing registry-cache paths.</summary>
    public static PackageArtifactCollection Collect(
        string metadataPath,
        ScanComponent[] components,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
        => JsonRecordedRootCollector.Collect(metadataPath, components, matcher, spdxLicenseIndex, RecordedRootFormat.Cargo);
}

/// <summary>Collects license documents from installed Python metadata roots recorded by pip inspect.</summary>
public static class PipRestoreArtifactCollector
{
    /// <summary>Reads declared <c>license_file</c> entries below <c>metadata_location</c>.</summary>
    public static PackageArtifactCollection Collect(
        string inspectPath,
        ScanComponent[] components,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
        => JsonRecordedRootCollector.Collect(inspectPath, components, matcher, spdxLicenseIndex, RecordedRootFormat.Pip);
}

/// <summary>Collects root license documents from Go module directories recorded by <c>go list -m -json all</c>.</summary>
public static class GoRestoreArtifactCollector
{
    /// <summary>Reads the selected-module JSON stream without deriving paths from GOMODCACHE.</summary>
    public static PackageArtifactCollection Collect(
        string moduleListPath,
        ScanComponent[] components,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
        => JsonRecordedRootCollector.Collect(moduleListPath, components, matcher, spdxLicenseIndex, RecordedRootFormat.Go);
}

internal enum RecordedRootFormat : byte
{
    Cargo,
    Pip,
    Go,
}

internal static class JsonRecordedRootCollector
{
    internal static PackageArtifactCollection Collect(
        string inputPath,
        ScanComponent[] components,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex,
        RecordedRootFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(spdxLicenseIndex);
        if (!CacheFile.TryRentContent(inputPath, out var source, out var length) || length == 0)
        {
            return new PackageArtifactCollection(components, default);
        }

        var ecosystem = format switch
        {
            RecordedRootFormat.Cargo => "cargo",
            RecordedRootFormat.Pip => "pypi",
            _ => "golang",
        };
        var table = PackageArtifactComponentIndex.Rent(components, ecosystem, out var tableLength);
        try
        {
            var offset = HasUtf8Bom(source.AsSpan(0, length)) ? 3 : 0;
            var summary = format switch
            {
                RecordedRootFormat.Cargo => CollectCargo(source, offset, length - offset, components, table.AsSpan(0, tableLength), matcher, spdxLicenseIndex),
                RecordedRootFormat.Pip => CollectPip(source, offset, length - offset, components, table.AsSpan(0, tableLength), matcher, spdxLicenseIndex),
                _ => CollectGo(source, offset, length - offset, components, table.AsSpan(0, tableLength), matcher, spdxLicenseIndex),
            };
            return new PackageArtifactCollection(components, summary);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(table);
            CacheFile.Return(source);
        }
    }

    private static PackageArtifactCollectionSummary CollectCargo(
        byte[] source,
        int offset,
        int length,
        ScanComponent[] components,
        ReadOnlySpan<int> table,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        var summary = default(PackageArtifactCollectionSummary);
        var reader = new Utf8JsonReader(source.AsSpan(offset, length));
        RequireRead(ref reader, JsonTokenType.StartObject, "Cargo metadata root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Require(ref reader, JsonTokenType.PropertyName, "Cargo metadata contains an invalid root property.");
            var packages = reader.ValueTextEquals("packages"u8);
            RequireRead(ref reader, packages ? JsonTokenType.StartArray : null, "Cargo metadata property has an invalid value.");
            if (!packages)
            {
                reader.Skip();
                continue;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                Require(ref reader, JsonTokenType.StartObject, "Cargo package must be an object.");
                Utf8Slice id = default;
                string? manifestPath = null;
                Utf8Slice licenseFile = default;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    Require(ref reader, JsonTokenType.PropertyName, "Cargo package property is invalid.");
                    if (reader.ValueTextEquals("id"u8)) id = ReadString(ref reader, source, offset);
                    else if (reader.ValueTextEquals("manifest_path"u8)) manifestPath = ReadPathString(ref reader);
                    else if (reader.ValueTextEquals("license_file"u8)) licenseFile = ReadNullableString(ref reader, source, offset);
                    else
                    {
                        RequireRead(ref reader, null, "Cargo package property has no value.");
                        reader.Skip();
                    }
                }

                var componentIndex = PackageArtifactComponentIndex.Find(components, table, id.Span);
                if (componentIndex < 0 || string.IsNullOrWhiteSpace(manifestPath)) continue;
                var directory = Path.GetDirectoryName(manifestPath);
                if (!IsRecordedDirectory(directory)) continue;
                var collected = licenseFile.IsEmpty
                    ? PackageArtifactDocumentCollector.CollectConventional(directory, components, componentIndex, matcher, spdxLicenseIndex)
                    : PackageArtifactDocumentCollector.CollectDeclared(directory, licenseFile.Span, null, components, componentIndex, matcher, spdxLicenseIndex);
                summary = PackageArtifactDocumentCollector.Add(summary, collected);
            }
        }

        return summary;
    }

    private static PackageArtifactCollectionSummary CollectPip(
        byte[] source,
        int offset,
        int length,
        ScanComponent[] components,
        ReadOnlySpan<int> table,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        var summary = default(PackageArtifactCollectionSummary);
        var licenseFiles = ArrayPool<Utf8Slice>.Shared.Rent(4);
        try
        {
            var reader = new Utf8JsonReader(source.AsSpan(offset, length));
            RequireRead(ref reader, JsonTokenType.StartObject, "pip inspect root must be an object.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                Require(ref reader, JsonTokenType.PropertyName, "pip inspect contains an invalid root property.");
                var installed = reader.ValueTextEquals("installed"u8);
                RequireRead(ref reader, installed ? JsonTokenType.StartArray : null, "pip inspect property has an invalid value.");
                if (!installed)
                {
                    reader.Skip();
                    continue;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    Require(ref reader, JsonTokenType.StartObject, "pip inspect installed entry must be an object.");
                    Utf8Slice name = default;
                    Utf8Slice version = default;
                    string? metadataLocation = null;
                    var licenseFileCount = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        Require(ref reader, JsonTokenType.PropertyName, "pip inspect installed property is invalid.");
                        if (reader.ValueTextEquals("metadata_location"u8)) metadataLocation = ReadPathString(ref reader);
                        else if (reader.ValueTextEquals("metadata"u8))
                        {
                            RequireRead(ref reader, JsonTokenType.StartObject, "pip metadata must be an object.");
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                Require(ref reader, JsonTokenType.PropertyName, "pip metadata property is invalid.");
                                if (reader.ValueTextEquals("name"u8)) name = ReadString(ref reader, source, offset);
                                else if (reader.ValueTextEquals("version"u8)) version = ReadString(ref reader, source, offset);
                                else if (reader.ValueTextEquals("license_file"u8))
                                {
                                    RequireRead(ref reader, null, "pip license_file has no value.");
                                    if (reader.TokenType == JsonTokenType.String)
                                    {
                                        EnsureCapacity(ref licenseFiles, licenseFileCount);
                                        licenseFiles[licenseFileCount++] = CreateValueSlice(ref reader, source, offset);
                                    }
                                    else if (reader.TokenType == JsonTokenType.StartArray)
                                    {
                                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                                        {
                                            Require(ref reader, JsonTokenType.String, "pip license_file entries must be strings.");
                                            EnsureCapacity(ref licenseFiles, licenseFileCount);
                                            licenseFiles[licenseFileCount++] = CreateValueSlice(ref reader, source, offset);
                                        }
                                    }
                                    else reader.Skip();
                                }
                                else
                                {
                                    RequireRead(ref reader, null, "pip metadata property has no value.");
                                    reader.Skip();
                                }
                            }
                        }
                        else
                        {
                            RequireRead(ref reader, null, "pip installed property has no value.");
                            reader.Skip();
                        }
                    }

                    var componentIndex = PackageArtifactComponentIndex.FindNormalizedPython(components, table, name.Span, version.Span);
                    if (componentIndex >= 0 && !string.IsNullOrWhiteSpace(metadataLocation))
                    {
                        var directory = metadataLocation;
                        if (IsRecordedDirectory(directory))
                        {
                            if (licenseFileCount == 0)
                            {
                                summary = PackageArtifactDocumentCollector.Add(
                                    summary,
                                    PackageArtifactDocumentCollector.CollectConventional(directory, components, componentIndex, matcher, spdxLicenseIndex));
                            }
                            else
                            {
                                var targetAdded = false;
                                for (var index = 0; index < licenseFileCount; index++)
                                {
                                    var collected = PackageArtifactDocumentCollector.CollectDeclared(
                                        directory,
                                        licenseFiles[index].Span,
                                        "licenses",
                                        components,
                                        componentIndex,
                                        matcher,
                                        spdxLicenseIndex);
                                    if (collected.DocumentCount == 0)
                                    {
                                        collected = PackageArtifactDocumentCollector.CollectDeclared(
                                            directory,
                                            licenseFiles[index].Span,
                                            null,
                                            components,
                                            componentIndex,
                                            matcher,
                                            spdxLicenseIndex);
                                    }

                                    if (targetAdded) collected = collected with { TargetCount = 0 };
                                    else targetAdded = true;
                                    summary = PackageArtifactDocumentCollector.Add(summary, collected);
                                }
                            }
                        }
                    }

                    licenseFiles.AsSpan(0, licenseFileCount).Clear();
                }
            }
        }
        finally
        {
            ArrayPool<Utf8Slice>.Shared.Return(licenseFiles, clearArray: true);
        }

        return summary;
    }

    private static PackageArtifactCollectionSummary CollectGo(
        byte[] source,
        int offset,
        int length,
        ScanComponent[] components,
        ReadOnlySpan<int> table,
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        var summary = default(PackageArtifactCollectionSummary);
        var reader = new Utf8JsonReader(source.AsSpan(offset, length), new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            Require(ref reader, JsonTokenType.StartObject, "Go module list must contain objects.");
            Utf8Slice path = default;
            Utf8Slice version = default;
            string? directory = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                Require(ref reader, JsonTokenType.PropertyName, "Go module property is invalid.");
                if (reader.ValueTextEquals("Path"u8)) path = ReadString(ref reader, source, offset);
                else if (reader.ValueTextEquals("Version"u8)) version = ReadString(ref reader, source, offset);
                else if (reader.ValueTextEquals("Dir"u8)) directory = ReadPathString(ref reader);
                else
                {
                    RequireRead(ref reader, null, "Go module property has no value.");
                    reader.Skip();
                }
            }

            if (path.IsEmpty || version.IsEmpty || string.IsNullOrWhiteSpace(directory)) continue;
            var componentIndex = FindGoComponent(components, table, path.Span, version.Span);
            if (componentIndex < 0) continue;
            var packageDirectory = directory;
            if (!IsRecordedDirectory(packageDirectory)) continue;
            summary = PackageArtifactDocumentCollector.Add(
                summary,
                PackageArtifactDocumentCollector.CollectConventional(packageDirectory, components, componentIndex, matcher, spdxLicenseIndex));
        }

        return summary;
    }

    private static int FindGoComponent(ReadOnlySpan<ScanComponent> components, ReadOnlySpan<int> table, ReadOnlySpan<byte> path, ReadOnlySpan<byte> version)
    {
        var hash = Fnv1a.Hash(path);
        hash = Fnv1a.Hash("@"u8, hash);
        hash = Fnv1a.Hash(version, hash);
        var mask = table.Length - 1;
        var slot = (int)(hash & (uint)mask);
        while (true)
        {
            var stored = table[slot];
            if (stored == 0) return -1;
            var index = stored - 1;
            var id = components[index].SourceId.Span;
            if (id.Length == path.Length + 1 + version.Length
                && id[..path.Length].SequenceEqual(path)
                && id[path.Length] == (byte)'@'
                && id[(path.Length + 1)..].SequenceEqual(version)) return index;
            slot = (slot + 1) & mask;
        }
    }

    private static Utf8Slice ReadString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireRead(ref reader, JsonTokenType.String, "JSON field must be a string.");
        return CreateValueSlice(ref reader, source, offset);
    }

    private static Utf8Slice ReadNullableString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireRead(ref reader, null, "JSON field has no value.");
        if (reader.TokenType == JsonTokenType.Null) return default;
        Require(ref reader, JsonTokenType.String, "JSON field must be a string or null.");
        return CreateValueSlice(ref reader, source, offset);
    }

    private static string ReadPathString(ref Utf8JsonReader reader)
    {
        RequireRead(ref reader, JsonTokenType.String, "JSON path field must be a string.");
        return reader.GetString() ?? string.Empty;
    }

    private static Utf8Slice CreateValueSlice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped) return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static bool IsRecordedDirectory([NotNullWhen(true)] string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Directory.Exists(path);

    private static void EnsureCapacity(ref Utf8Slice[] values, int count)
    {
        if (count < values.Length) return;
        var replacement = ArrayPool<Utf8Slice>.Shared.Rent(values.Length * 2);
        values.AsSpan(0, count).CopyTo(replacement);
        ArrayPool<Utf8Slice>.Shared.Return(values, clearArray: true);
        values = replacement;
    }

    private static void RequireRead(ref Utf8JsonReader reader, JsonTokenType? expected, string message)
    {
        if (!reader.Read() || (expected.HasValue && reader.TokenType != expected.Value)) throw new JsonException(message);
    }

    private static void Require(ref Utf8JsonReader reader, JsonTokenType expected, string message)
    {
        if (reader.TokenType != expected) throw new JsonException(message);
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> value)
        => value.Length >= 3 && value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf;
}
