using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class YarnClassicLockInputParser
{
    internal static bool Detect(ReadOnlySpan<byte> inputUtf8)
    {
        var position = 0;
        while (position < inputUtf8.Length)
        {
            var end = inputUtf8[position..].IndexOf((byte)'\n');
            end = end < 0 ? inputUtf8.Length : position + end;
            var line = inputUtf8[position..end];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
            if (line.SequenceEqual("# yarn lockfile v1"u8)) return true;
            if (!line.IsEmpty && line[0] != (byte)'#') return false;
            position = end == inputUtf8.Length ? end : end + 1;
        }

        return false;
    }

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex _, bool retainGraph)
        => YarnLockGraphParser.ParseClassic(source, offset, retainGraph);
}

internal static class YarnBerryLockInputParser
{
    internal static bool Detect(ReadOnlySpan<byte> inputUtf8)
    {
        var position = 0;
        while (position < inputUtf8.Length)
        {
            var end = inputUtf8[position..].IndexOf((byte)'\n');
            end = end < 0 ? inputUtf8.Length : position + end;
            var line = inputUtf8[position..end];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
            if (line.SequenceEqual("__metadata:"u8)) return true;
            if (!line.IsEmpty && line[0] is not ((byte)'#')) return false;
            position = end == inputUtf8.Length ? end : end + 1;
        }

        return false;
    }

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex _, bool retainGraph)
        => YarnLockGraphParser.ParseBerry(source, offset, retainGraph);
}

internal static class YarnLockGraphParser
{
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:npm/"u8;

    internal static DependencyInventory ParseClassic(byte[] source, int offset, bool retainGraph)
    {
        var nodes = ArrayPool<YarnNode>.Shared.Rent(16);
        var dependencies = ArrayPool<YarnDependency>.Shared.Rent(32);
        var nodeCount = 0;
        var dependencyCount = 0;
        try
        {
            var headerVersion = default(Utf8Slice);
            var currentNode = -1;
            var dependencyKind = YarnDependencyKind.None;
            var position = offset;
            while (position < source.Length)
            {
                var lineStart = position;
                var relativeEnd = source.AsSpan(position).IndexOf((byte)'\n');
                var lineEnd = relativeEnd < 0 ? source.Length : position + relativeEnd;
                position = relativeEnd < 0 ? source.Length : lineEnd + 1;
                if (lineEnd > lineStart && source[lineEnd - 1] == (byte)'\r') lineEnd--;
                var line = source.AsSpan(lineStart, lineEnd - lineStart);
                if (line.IsEmpty) continue;
                if (line[0] == (byte)'#')
                {
                    var marker = line.IndexOf("v1"u8);
                    if (line.SequenceEqual("# yarn lockfile v1"u8)) headerVersion = new Utf8Slice(source, lineStart + marker + 1, 1);
                    continue;
                }

                var indent = 0;
                while (indent < line.Length && line[indent] == (byte)' ') indent++;
                if (indent == 0)
                {
                    if (line[^1] != (byte)':') throw new JsonException("Yarn Classic lock entry must end with a colon.");
                    var descriptors = SliceTrimmed(source, lineStart, line.Length - 1);
                    EnsureCapacity(ref nodes, nodeCount);
                    currentNode = nodeCount;
                    nodes[nodeCount++] = new YarnNode(descriptors, default, GetDescriptorName(descriptors), default, false, dependencyCount, 0, default);
                    dependencyKind = YarnDependencyKind.None;
                    continue;
                }

                if (currentNode < 0) throw new JsonException("Yarn Classic lock field has no entry.");
                var content = line[indent..];
                if (indent == 2 && content.StartsWith("version "u8))
                {
                    nodes[currentNode] = nodes[currentNode] with { Version = Unquote(SliceTrimmed(source, lineStart + indent + 8, content.Length - 8)) };
                }
                else if (indent == 2 && content.SequenceEqual("dependencies:"u8)) dependencyKind = YarnDependencyKind.Normal;
                else if (indent == 2 && content.SequenceEqual("optionalDependencies:"u8)) dependencyKind = YarnDependencyKind.Optional;
                else if (indent == 2) dependencyKind = YarnDependencyKind.None;
                else if (indent == 4 && dependencyKind != YarnDependencyKind.None)
                {
                    var separator = FindClassicFieldSeparator(content);
                    if (separator <= 0) throw new JsonException("Yarn Classic dependency is malformed.");
                    var name = Unquote(new Utf8Slice(source, lineStart + indent, separator));
                    var range = Unquote(SliceTrimmed(source, lineStart + indent + separator + 1, content.Length - separator - 1));
                    AddDependency(currentNode, name, range, dependencyKind, ref nodes, ref dependencies, ref dependencyCount);
                }
            }

            if (headerVersion.IsEmpty || nodeCount == 0) throw new JsonException("Yarn Classic lockfile v1 header and entries are required.");
            for (var i = 0; i < nodeCount; i++)
            {
                var node = nodes[i];
                if (node.Version.IsEmpty) throw new JsonException("Yarn Classic lock entry requires a version.");
                nodes[i] = node with { Resolution = GetPrimaryDescriptor(node.Descriptors) };
            }

            return BuildInventory(nodes.AsSpan(0, nodeCount), dependencies.AsSpan(0, dependencyCount), headerVersion, false, retainGraph);
        }
        finally
        {
            ArrayPool<YarnNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<YarnDependency>.Shared.Return(dependencies, clearArray: true);
        }
    }

    internal static DependencyInventory ParseBerry(byte[] source, int offset, bool retainGraph)
    {
        var nodes = ArrayPool<YarnNode>.Shared.Rent(16);
        var dependencies = ArrayPool<YarnDependency>.Shared.Rent(32);
        var nodeCount = 0;
        var dependencyCount = 0;
        try
        {
            var specificationVersion = default(Utf8Slice);
            var currentNode = -1;
            var dependencyKind = YarnDependencyKind.None;
            var inMetadata = false;
            var reader = new Utf8YamlLineReader(source, offset);
            while (reader.Read(out var line))
            {
                if (line.IsSequence) throw new JsonException("Yarn Berry lock sequences are not supported.");
                if (line.Indent == 0)
                {
                    dependencyKind = YarnDependencyKind.None;
                    if (line.Key.Span.SequenceEqual("__metadata"u8))
                    {
                        inMetadata = true;
                        currentNode = -1;
                        continue;
                    }

                    inMetadata = false;
                    EnsureCapacity(ref nodes, nodeCount);
                    currentNode = nodeCount;
                    nodes[nodeCount++] = new YarnNode(line.Key, default, GetDescriptorName(line.Key), default, false, dependencyCount, 0, default);
                    continue;
                }

                if (inMetadata)
                {
                    if (line.Indent == 2 && line.Key.Span.SequenceEqual("version"u8)) specificationVersion = line.Value;
                    continue;
                }

                if (currentNode < 0) throw new JsonException("Yarn Berry lock field has no entry.");
                if (line.Indent == 2 && line.Key.Span.SequenceEqual("version"u8)) nodes[currentNode] = nodes[currentNode] with { Version = line.Value };
                else if (line.Indent == 2 && line.Key.Span.SequenceEqual("resolution"u8))
                {
                    var workspace = line.Value.Span.IndexOf("@workspace:"u8) >= 0;
                    nodes[currentNode] = nodes[currentNode] with { Resolution = line.Value, IsWorkspace = workspace, Variant = CreateVirtualVariant(line.Value) };
                }
                else if (line.Indent == 2 && line.Key.Span.SequenceEqual("dependencies"u8)) dependencyKind = YarnDependencyKind.Normal;
                else if (line.Indent == 2 && line.Key.Span.SequenceEqual("optionalDependencies"u8)) dependencyKind = YarnDependencyKind.Optional;
                else if (line.Indent == 2) dependencyKind = YarnDependencyKind.None;
                else if (line.Indent == 4 && dependencyKind != YarnDependencyKind.None)
                {
                    AddDependency(currentNode, line.Key, line.Value, dependencyKind, ref nodes, ref dependencies, ref dependencyCount);
                }
            }

            if (specificationVersion.IsEmpty || nodeCount == 0) throw new JsonException("Yarn Berry lock requires __metadata.version and entries.");
            if (!specificationVersion.Span.SequenceEqual("8"u8)) throw new JsonException("Yarn Berry lock metadata version must be 8.");
            for (var i = 0; i < nodeCount; i++)
            {
                if (nodes[i].Resolution.IsEmpty || nodes[i].Version.IsEmpty) throw new JsonException("Yarn Berry lock entry requires version and resolution.");
            }

            return BuildInventory(nodes.AsSpan(0, nodeCount), dependencies.AsSpan(0, dependencyCount), specificationVersion, true, retainGraph);
        }
        finally
        {
            ArrayPool<YarnNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<YarnDependency>.Shared.Return(dependencies, clearArray: true);
        }
    }

    private static DependencyInventory BuildInventory(ReadOnlySpan<YarnNode> nodes, ReadOnlySpan<YarnDependency> dependencies, Utf8Slice version, bool workspaces, bool retainGraph)
    {
        var contexts = ArrayPool<DependencyResolutionContext>.Shared.Rent(Math.Max(1, nodes.Length));
        var components = ArrayPool<ScanComponent>.Shared.Rent(nodes.Length);
        var occurrences = ArrayPool<DependencyOccurrence>.Shared.Rent(Math.Max(16, nodes.Length));
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(Math.Max(16, dependencies.Length));
        var variants = ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(Math.Max(16, nodes.Length));
        var componentByNode = ArrayPool<int>.Shared.Rent(nodes.Length);
        var depths = ArrayPool<int>.Shared.Rent(nodes.Length);
        var queue = ArrayPool<int>.Shared.Rent(Math.Max(16, nodes.Length));
        var occurrenceByNode = ArrayPool<int>.Shared.Rent(nodes.Length);
        var optionalReach = ArrayPool<byte>.Shared.Rent(nodes.Length);
        var contextCount = 0;
        var componentCount = 0;
        var occurrenceCount = 0;
        var edgeCount = 0;
        var variantCount = 0;
        try
        {
            componentByNode.AsSpan(0, nodes.Length).Fill(-1);
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                if (node.IsWorkspace) continue;
                componentByNode[i] = componentCount;
                components[componentCount++] = new ScanComponent(node.Name, node.Version, default, "npm", DependencyType.Unknown, LicenseStatus.Unknown, CreatePurl(node.Name, node.Version), node.Resolution, default, [], []);
            }

            if (workspaces)
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    if (!nodes[i].IsWorkspace) continue;
                    contexts[contextCount++] = new DependencyResolutionContext(GetWorkspaceOrigin(nodes[i].Resolution), default, default, default, default, default);
                }
            }
            else
            {
                contexts[contextCount++] = new DependencyResolutionContext(Utf8Slice.FromOwnedBytes("yarn.lock"u8.ToArray()), default, default, default, default, default);
            }

            if (contextCount == 0) throw new JsonException("Yarn Berry lock must contain at least one workspace entry.");
            for (var contextIndex = 0; contextIndex < contextCount; contextIndex++)
            {
                depths.AsSpan(0, nodes.Length).Fill(int.MinValue);
                optionalReach.AsSpan(0, nodes.Length).Clear();
                var head = 0;
                var tail = 0;
                if (workspaces)
                {
                    var workspaceIndex = FindWorkspaceByOrigin(nodes, contexts[contextIndex].ProjectOrigin);
                    depths[workspaceIndex] = 0;
                    EnsureCapacity(ref queue, tail);
                    queue[tail++] = workspaceIndex;
                }
                else
                {
                    for (var i = 0; i < nodes.Length; i++)
                    {
                        depths[i] = -1;
                        optionalReach[i] = HasOnlyOptionalIncoming(nodes, dependencies, i) ? (byte)1 : (byte)0;
                        EnsureCapacity(ref queue, tail);
                        queue[tail++] = i;
                    }
                }

                while (head < tail)
                {
                    var ownerIndex = queue[head++];
                    var owner = nodes[ownerIndex];
                    for (var dependencyIndex = owner.DependencyStart; dependencyIndex < owner.DependencyStart + owner.DependencyCount; dependencyIndex++)
                    {
                        var dependency = dependencies[dependencyIndex];
                        var targetIndex = FindTarget(nodes, dependency.Name, dependency.Range);
                        if (targetIndex < 0) continue;
                        var nextDepth = owner.IsWorkspace ? 0 : depths[ownerIndex] + 1;
                        var nextOptional = (byte)(optionalReach[ownerIndex] | (dependency.Kind == YarnDependencyKind.Optional ? 1 : 0));
                        if (depths[targetIndex] == int.MinValue || nextDepth < depths[targetIndex] || (nextOptional == 0 && optionalReach[targetIndex] != 0))
                        {
                            depths[targetIndex] = nextDepth;
                            optionalReach[targetIndex] = nextOptional;
                            EnsureCapacity(ref queue, tail);
                            queue[tail++] = targetIndex;
                        }
                    }
                }

                occurrenceByNode.AsSpan(0, nodes.Length).Fill(-1);
                for (var i = 0; i < nodes.Length; i++)
                {
                    var componentIndex = componentByNode[i];
                    var includeUnknown = workspaces && contextIndex == 0;
                    if (componentIndex < 0 || (!includeUnknown && depths[i] == int.MinValue)) continue;
                    occurrenceByNode[i] = occurrenceCount;
                    EnsureCapacity(ref occurrences, occurrenceCount);
                    occurrences[occurrenceCount++] = new DependencyOccurrence(contextIndex, componentIndex);
                    if (workspaces && depths[i] != int.MinValue)
                    {
                        var dependencyType = depths[i] == 0 ? DependencyType.Direct : DependencyType.Transitive;
                        components[componentIndex] = components[componentIndex] with { DependencyType = DependencyTypes.Merge(components[componentIndex].DependencyType, dependencyType) };
                    }

                    var variant = ComposeVariant(nodes[i].Variant, optionalReach[i] != 0);
                    if (!variant.IsEmpty)
                    {
                        EnsureCapacity(ref variants, variantCount);
                        variants[variantCount++] = new DependencyOccurrenceVariant(occurrenceCount - 1, variant);
                    }
                }

                for (var ownerIndex = 0; ownerIndex < nodes.Length; ownerIndex++)
                {
                    var owner = nodes[ownerIndex];
                    if (depths[ownerIndex] == int.MinValue) continue;
                    for (var dependencyIndex = owner.DependencyStart; dependencyIndex < owner.DependencyStart + owner.DependencyCount; dependencyIndex++)
                    {
                        var dependency = dependencies[dependencyIndex];
                        var targetIndex = FindTarget(nodes, dependency.Name, dependency.Range);
                        if (targetIndex < 0 || nodes[targetIndex].IsWorkspace || occurrenceByNode[targetIndex] < 0) continue;
                        var from = owner.IsWorkspace ? DependencyOccurrence.ContextRoot : occurrenceByNode[ownerIndex];
                        if (from < 0 && !owner.IsWorkspace) continue;
                        EnsureCapacity(ref edges, edgeCount);
                        edges[edgeCount++] = new DependencyEdge(contextIndex, from, occurrenceByNode[targetIndex]);
                    }
                }
            }

            return new DependencyInventory(new ScanInputDescriptor(default, default, string.Empty, string.Empty, version), contexts.AsSpan(0, contextCount).ToArray(), components.AsSpan(0, componentCount).ToArray(), retainGraph ? occurrences.AsSpan(0, occurrenceCount).ToArray() : [], retainGraph ? edges.AsSpan(0, edgeCount).ToArray() : [], retainGraph ? variants.AsSpan(0, variantCount).ToArray() : []);
        }
        finally
        {
            ArrayPool<DependencyResolutionContext>.Shared.Return(contexts, clearArray: true);
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyOccurrence>.Shared.Return(occurrences);
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            ArrayPool<DependencyOccurrenceVariant>.Shared.Return(variants, clearArray: true);
            ArrayPool<int>.Shared.Return(componentByNode);
            ArrayPool<int>.Shared.Return(depths);
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<int>.Shared.Return(occurrenceByNode);
            ArrayPool<byte>.Shared.Return(optionalReach);
        }
    }

    private static int FindWorkspaceByOrigin(ReadOnlySpan<YarnNode> nodes, Utf8Slice origin)
    {
        for (var i = 0; i < nodes.Length; i++) if (nodes[i].IsWorkspace && GetWorkspaceOrigin(nodes[i].Resolution).Equals(origin)) return i;
        throw new JsonException("Yarn workspace context could not be resolved.");
    }

    private static int FindTarget(ReadOnlySpan<YarnNode> nodes, Utf8Slice name, Utf8Slice range)
    {
        for (var i = 0; i < nodes.Length; i++) if (DescriptorListContains(nodes[i].Descriptors.Span, name.Span, range.Span)) return i;
        return -1;
    }

    private static bool HasOnlyOptionalIncoming(ReadOnlySpan<YarnNode> nodes, ReadOnlySpan<YarnDependency> dependencies, int targetIndex)
    {
        var foundOptional = false;
        for (var ownerIndex = 0; ownerIndex < nodes.Length; ownerIndex++)
        {
            var owner = nodes[ownerIndex];
            for (var dependencyIndex = owner.DependencyStart; dependencyIndex < owner.DependencyStart + owner.DependencyCount; dependencyIndex++)
            {
                var dependency = dependencies[dependencyIndex];
                if (FindTarget(nodes, dependency.Name, dependency.Range) != targetIndex) continue;
                if (dependency.Kind != YarnDependencyKind.Optional) return false;
                foundOptional = true;
            }
        }

        return foundOptional;
    }

    private static bool DescriptorListContains(ReadOnlySpan<byte> descriptors, ReadOnlySpan<byte> name, ReadOnlySpan<byte> range)
    {
        var start = 0;
        while (start < descriptors.Length)
        {
            var comma = descriptors[start..].IndexOf((byte)',');
            var end = comma < 0 ? descriptors.Length : start + comma;
            while (start < end && descriptors[start] == (byte)' ') start++;
            while (end > start && descriptors[end - 1] == (byte)' ') end--;
            if (end - start >= 2 && (descriptors[start] is (byte)'\'' or (byte)'"') && descriptors[end - 1] == descriptors[start])
            {
                start++;
                end--;
            }
            var value = descriptors[start..end];
            if (value.Length == name.Length + 1 + range.Length && value[..name.Length].SequenceEqual(name) && value[name.Length] == (byte)'@' && value[(name.Length + 1)..].SequenceEqual(range)) return true;
            if (comma < 0) break;
            start = end + 1;
        }

        return false;
    }

    private static void AddDependency(int ownerIndex, Utf8Slice name, Utf8Slice range, YarnDependencyKind kind, ref YarnNode[] nodes, ref YarnDependency[] dependencies, ref int dependencyCount)
    {
        EnsureCapacity(ref dependencies, dependencyCount);
        dependencies[dependencyCount++] = new YarnDependency(name, range, kind);
        var owner = nodes[ownerIndex];
        nodes[ownerIndex] = owner with { DependencyCount = owner.DependencyCount + 1 };
    }

    private static Utf8Slice GetDescriptorName(Utf8Slice descriptors)
    {
        var descriptor = GetPrimaryDescriptor(descriptors);
        var separator = descriptor.Span.LastIndexOf((byte)'@');
        if (separator <= 0) throw new JsonException("Yarn descriptor must contain a package name and range.");
        return descriptor.Slice(0, separator);
    }

    private static Utf8Slice GetPrimaryDescriptor(Utf8Slice descriptors)
    {
        var comma = descriptors.Span.IndexOf((byte)',');
        var start = 0;
        var end = comma < 0 ? descriptors.Length : comma;
        while (start < end && descriptors.Span[start] == (byte)' ') start++;
        while (end > start && descriptors.Span[end - 1] == (byte)' ') end--;
        if (end - start >= 2 && (descriptors.Span[start] is (byte)'\'' or (byte)'"') && descriptors.Span[end - 1] == descriptors.Span[start])
        {
            start++;
            end--;
        }

        return descriptors.Slice(start, end - start);
    }

    private static Utf8Slice GetWorkspaceOrigin(Utf8Slice resolution)
    {
        var marker = resolution.Span.IndexOf("@workspace:"u8);
        if (marker < 0) throw new JsonException("Yarn workspace resolution is malformed.");
        return resolution.Slice(marker + 11, resolution.Length - marker - 11);
    }

    private static Utf8Slice CreateVirtualVariant(Utf8Slice resolution)
    {
        var marker = resolution.Span.IndexOf("@virtual:"u8);
        if (marker < 0) return default;
        var start = marker + 9;
        var hash = resolution.Span[start..].IndexOf((byte)'#');
        if (hash <= 0) throw new JsonException("Yarn virtual resolution is malformed.");
        var value = resolution.Slice(start, hash);
        var bytes = new byte[8 + value.Length];
        "virtual="u8.CopyTo(bytes);
        value.Span.CopyTo(bytes.AsSpan(8));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice ComposeVariant(Utf8Slice variant, bool optional)
    {
        if (!optional) return variant;
        if (variant.IsEmpty) return Utf8Slice.FromOwnedBytes("optional"u8.ToArray());
        var bytes = new byte[9 + variant.Length];
        "optional;"u8.CopyTo(bytes);
        variant.Span.CopyTo(bytes.AsSpan(9));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreatePurl(Utf8Slice name, Utf8Slice version)
    {
        var nameLength = GetEncodedLength(name.Span, true);
        var versionLength = GetEncodedLength(version.Span, false);
        var bytes = new byte[PurlPrefix.Length + nameLength + 1 + versionLength];
        PurlPrefix.CopyTo(bytes);
        var index = PurlPrefix.Length;
        WriteEncoded(name.Span, true, bytes, ref index);
        bytes[index++] = (byte)'@';
        WriteEncoded(version.Span, false, bytes, ref index);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static int GetEncodedLength(ReadOnlySpan<byte> value, bool allowSlash)
    {
        var length = 0;
        for (var i = 0; i < value.Length; i++) length += IsPurlSafe(value[i], allowSlash) ? 1 : 3;
        return length;
    }

    private static void WriteEncoded(ReadOnlySpan<byte> value, bool allowSlash, Span<byte> destination, ref int index)
    {
        const string Hex = "0123456789ABCDEF";
        for (var i = 0; i < value.Length; i++)
        {
            var item = value[i];
            if (IsPurlSafe(item, allowSlash)) destination[index++] = item;
            else
            {
                destination[index++] = (byte)'%';
                destination[index++] = (byte)Hex[item >> 4];
                destination[index++] = (byte)Hex[item & 15];
            }
        }
    }

    private static bool IsPurlSafe(byte value, bool allowSlash)
        => value is >= (byte)'a' and <= (byte)'z'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~'
        || allowSlash && value == (byte)'/';

    private static int FindClassicFieldSeparator(ReadOnlySpan<byte> value)
    {
        for (var i = 0; i < value.Length; i++) if (value[i] == (byte)' ') return i;
        return -1;
    }

    private static Utf8Slice SliceTrimmed(byte[] source, int start, int length)
    {
        while (length > 0 && source[start] == (byte)' ') { start++; length--; }
        while (length > 0 && source[start + length - 1] == (byte)' ') length--;
        return length == 0 ? default : new Utf8Slice(source, start, length);
    }

    private static Utf8Slice Unquote(Utf8Slice value)
    {
        if (value.Length >= 2 && ((value.Span[0] == (byte)'"' && value.Span[^1] == (byte)'"') || (value.Span[0] == (byte)'\'' && value.Span[^1] == (byte)'\''))) return value.Slice(1, value.Length - 2);
        return value;
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length) return;
        var replacement = ArrayPool<T>.Shared.Rent(checked(values.Length * 2));
        values.AsSpan(0, count).CopyTo(replacement);
        ArrayPool<T>.Shared.Return(values, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        values = replacement;
    }

    private readonly record struct YarnNode(Utf8Slice Descriptors, Utf8Slice Resolution, Utf8Slice Name, Utf8Slice Version, bool IsWorkspace, int DependencyStart, int DependencyCount, Utf8Slice Variant);
    private readonly record struct YarnDependency(Utf8Slice Name, Utf8Slice Range, YarnDependencyKind Kind);
    private enum YarnDependencyKind : byte { None, Normal, Optional }
}
