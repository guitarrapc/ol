using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class NpmPackageLockInputParser
{
    private static readonly LicenseEvidence PackageLicenseEvidence = new(
        LicenseEvidenceKind.DependencyInput,
        DependencyInput: new DependencyInputEvidence("npm-package-lock", "packages[].license"));
    private static ReadOnlySpan<byte> NodeModules => "node_modules"u8;
    private static ReadOnlySpan<byte> NodeModulesPrefix => "node_modules/"u8;
    private static ReadOnlySpan<byte> NestedNodeModules => "/node_modules/"u8;
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:npm/"u8;

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph)
    {
        var nodes = ArrayPool<PackageNode>.Shared.Rent(16);
        var dependencies = ArrayPool<NodeDependency>.Shared.Rent(32);
        var restrictions = ArrayPool<Utf8Slice>.Shared.Rent(16);
        var contexts = ArrayPool<DependencyResolutionContext>.Shared.Rent(4);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var occurrences = ArrayPool<DependencyOccurrence>.Shared.Rent(32);
        var occurrenceVariants = ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(8);
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(32);
        int[]? nodeIndexes = null;
        int[]? componentByNode = null;
        int[]? depths = null;
        int[]? queue = null;
        int[]? occurrenceByNode = null;
        var nodeCount = 0;
        var dependencyCount = 0;
        var restrictionCount = 0;
        var contextCount = 0;
        var componentCount = 0;
        var occurrenceCount = 0;
        var occurrenceVariantCount = 0;
        var edgeCount = 0;
        try
        {
            ReadRoot(
                source,
                offset,
                ref nodes,
                ref nodeCount,
                ref dependencies,
                ref dependencyCount,
                ref restrictions,
                ref restrictionCount,
                out var specificationVersion,
                out var rootName);

            var indexCapacity = GetIndexCapacity(nodeCount);
            nodeIndexes = ArrayPool<int>.Shared.Rent(indexCapacity);
            nodeIndexes.AsSpan(0, indexCapacity).Fill(-1);
            var rootNodeIndex = -1;
            for (var i = 0; i < nodeCount; i++)
            {
                if (!AddNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, i))
                {
                    throw new JsonException("npm package-lock.json contains a duplicate package path.");
                }

                if (nodes[i].Path.IsEmpty)
                {
                    rootNodeIndex = i;
                }
            }

            if (rootNodeIndex < 0)
            {
                throw new JsonException("npm package-lock.json packages must contain the root package entry.");
            }

            componentByNode = ArrayPool<int>.Shared.Rent(Math.Max(nodeCount, 1));
            componentByNode.AsSpan(0, nodeCount).Fill(-1);
            for (var i = 0; i < nodeCount; i++)
            {
                var node = nodes[i];
                if (!node.IsRegistryPackage)
                {
                    continue;
                }

                if (node.Name.IsEmpty || node.Version.IsEmpty)
                {
                    throw new JsonException("npm package entries must contain a package name and version.");
                }

                EnsureCapacity(ref components, componentCount);
                componentByNode[i] = componentCount;
                components[componentCount++] = CreateComponent(node, spdxLicenseIndex);
            }

            EnsureCapacity(ref contexts, contextCount);
            var rootNode = nodes[rootNodeIndex];
            var rootOrigin = !rootNode.Name.IsEmpty
                ? rootNode.Name
                : !rootName.IsEmpty
                    ? rootName
                    : Utf8Slice.FromOwnedBytes("."u8.ToArray());
            contexts[contextCount++] = new DependencyResolutionContext(rootOrigin, default, default, default, default, default);
            for (var i = 0; i < nodeCount; i++)
            {
                var node = nodes[i];
                if (i == rootNodeIndex || node.Link || node.IsRegistryPackage)
                {
                    continue;
                }

                EnsureCapacity(ref contexts, contextCount);
                contexts[contextCount++] = new DependencyResolutionContext(node.Path, default, default, default, default, default);
            }

            depths = ArrayPool<int>.Shared.Rent(Math.Max(nodeCount, 1));
            queue = ArrayPool<int>.Shared.Rent(Math.Max(nodeCount, 1));
            occurrenceByNode = ArrayPool<int>.Shared.Rent(Math.Max(nodeCount, 1));
            var contextRootNode = rootNodeIndex;
            for (var contextIndex = 0; contextIndex < contextCount; contextIndex++)
            {
                if (contextIndex > 0)
                {
                    var contextOrigin = contexts[contextIndex].ProjectOrigin;
                    if (!TryGetNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, contextOrigin.Span, out contextRootNode))
                    {
                        throw new JsonException("npm workspace context does not match a package entry.");
                    }
                }

                Traverse(
                    nodes.AsSpan(0, nodeCount),
                    dependencies.AsSpan(0, dependencyCount),
                    nodeIndexes,
                    indexCapacity,
                    contextRootNode,
                    depths.AsSpan(0, nodeCount),
                    queue.AsSpan(0, nodeCount));

                occurrenceByNode.AsSpan(0, nodeCount).Fill(-1);
                var includeUnknown = contextIndex == 0;
                for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
                {
                    var componentIndex = componentByNode[nodeIndex];
                    if (componentIndex < 0 || (!includeUnknown && depths[nodeIndex] == int.MinValue))
                    {
                        continue;
                    }

                    var dependencyType = depths[nodeIndex] switch
                    {
                        0 => DependencyType.Direct,
                        > 0 => DependencyType.Transitive,
                        _ => DependencyType.Unknown,
                    };
                    var component = components[componentIndex];
                    components[componentIndex] = component with { DependencyType = DependencyTypes.Merge(component.DependencyType, dependencyType) };
                    EnsureCapacity(ref occurrences, occurrenceCount);
                    occurrenceByNode[nodeIndex] = occurrenceCount;
                    occurrences[occurrenceCount++] = new DependencyOccurrence(contextIndex, componentIndex);
                    if (!nodes[nodeIndex].Variant.IsEmpty)
                    {
                        EnsureCapacity(ref occurrenceVariants, occurrenceVariantCount);
                        occurrenceVariants[occurrenceVariantCount++] = new DependencyOccurrenceVariant(occurrenceCount - 1, nodes[nodeIndex].Variant);
                    }
                }

                ProjectEdges(
                    nodes.AsSpan(0, nodeCount),
                    dependencies.AsSpan(0, dependencyCount),
                    nodeIndexes,
                    indexCapacity,
                    depths.AsSpan(0, nodeCount),
                    occurrenceByNode.AsSpan(0, nodeCount),
                    contextIndex,
                    ref edges,
                    ref edgeCount);
            }

            return new DependencyInventory(
                new ScanInputDescriptor(default, default, string.Empty, string.Empty, specificationVersion),
                contexts.AsSpan(0, contextCount).ToArray(),
                components.AsSpan(0, componentCount).ToArray(),
                retainGraph ? occurrences.AsSpan(0, occurrenceCount).ToArray() : [],
                retainGraph ? edges.AsSpan(0, edgeCount).ToArray() : [],
                retainGraph ? occurrenceVariants.AsSpan(0, occurrenceVariantCount).ToArray() : []);
        }
        finally
        {
            ArrayPool<PackageNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<NodeDependency>.Shared.Return(dependencies, clearArray: true);
            ArrayPool<Utf8Slice>.Shared.Return(restrictions, clearArray: true);
            ArrayPool<DependencyResolutionContext>.Shared.Return(contexts, clearArray: true);
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyOccurrence>.Shared.Return(occurrences);
            ArrayPool<DependencyOccurrenceVariant>.Shared.Return(occurrenceVariants, clearArray: true);
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            if (nodeIndexes is not null) ArrayPool<int>.Shared.Return(nodeIndexes);
            if (componentByNode is not null) ArrayPool<int>.Shared.Return(componentByNode);
            if (depths is not null) ArrayPool<int>.Shared.Return(depths);
            if (queue is not null) ArrayPool<int>.Shared.Return(queue);
            if (occurrenceByNode is not null) ArrayPool<int>.Shared.Return(occurrenceByNode);
        }
    }

    private static void ReadRoot(
        byte[] source,
        int offset,
        ref PackageNode[] nodes,
        ref int nodeCount,
        ref NodeDependency[] dependencies,
        ref int dependencyCount,
        ref Utf8Slice[] restrictions,
        ref int restrictionCount,
        out Utf8Slice specificationVersion,
        out Utf8Slice rootName)
    {
        specificationVersion = default;
        rootName = default;
        var foundPackages = false;
        var reader = CreateReader(source, offset);
        RequireToken(ref reader, JsonTokenType.StartObject, "npm package-lock.json root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "npm package-lock.json contains an invalid root property.");
            if (reader.ValueTextEquals("lockfileVersion"u8))
            {
                reader.Read();
                specificationVersion = CreateNumberSlice(ref reader, source, offset);
                if (!specificationVersion.Span.SequenceEqual("2"u8) && !specificationVersion.Span.SequenceEqual("3"u8))
                {
                    throw new JsonException("npm package-lock.json lockfileVersion must be 2 or 3.");
                }
            }
            else if (reader.ValueTextEquals("name"u8))
            {
                rootName = ReadString(ref reader, source, offset);
            }
            else if (reader.ValueTextEquals("packages"u8))
            {
                if (foundPackages)
                {
                    throw new JsonException("npm package-lock.json cannot contain duplicate packages objects.");
                }

                reader.Read();
                ReadPackages(
                    ref reader,
                    source,
                    offset,
                    ref nodes,
                    ref nodeCount,
                    ref dependencies,
                    ref dependencyCount,
                    ref restrictions,
                    ref restrictionCount);
                foundPackages = true;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (specificationVersion.IsEmpty || !foundPackages)
        {
            throw new JsonException("npm package-lock.json is missing lockfileVersion or packages.");
        }
    }

    private static void ReadPackages(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        ref PackageNode[] nodes,
        ref int nodeCount,
        ref NodeDependency[] dependencies,
        ref int dependencyCount,
        ref Utf8Slice[] restrictions,
        ref int restrictionCount)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "npm packages must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "npm package path is invalid.");
            var path = CreateValueSlice(ref reader, source, offset);
            reader.Read();
            RequireCurrentToken(ref reader, JsonTokenType.StartObject, "npm package entry must be an object.");
            var dependencyStart = dependencyCount;
            var osStart = restrictionCount;
            var osCount = 0;
            var cpuStart = 0;
            var cpuCount = 0;
            var name = default(Utf8Slice);
            var version = default(Utf8Slice);
            var license = default(Utf8Slice);
            var resolved = default(Utf8Slice);
            var flags = NodeFlags.None;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "npm package property is invalid.");
                if (reader.ValueTextEquals("name"u8))
                {
                    name = ReadString(ref reader, source, offset);
                }
                else if (reader.ValueTextEquals("version"u8))
                {
                    version = ReadString(ref reader, source, offset);
                }
                else if (reader.ValueTextEquals("license"u8))
                {
                    license = ReadString(ref reader, source, offset);
                }
                else if (reader.ValueTextEquals("resolved"u8))
                {
                    resolved = ReadString(ref reader, source, offset);
                }
                else if (reader.ValueTextEquals("link"u8))
                {
                    if (ReadBoolean(ref reader)) flags |= NodeFlags.Link;
                }
                else if (reader.ValueTextEquals("dev"u8))
                {
                    if (ReadBoolean(ref reader)) flags |= NodeFlags.Dev;
                }
                else if (reader.ValueTextEquals("optional"u8))
                {
                    if (ReadBoolean(ref reader)) flags |= NodeFlags.Optional;
                }
                else if (reader.ValueTextEquals("devOptional"u8))
                {
                    if (ReadBoolean(ref reader)) flags |= NodeFlags.DevOptional;
                }
                else if (reader.ValueTextEquals("peer"u8))
                {
                    if (ReadBoolean(ref reader)) flags |= NodeFlags.Peer;
                }
                else if (reader.ValueTextEquals("dependencies"u8)
                    || reader.ValueTextEquals("optionalDependencies"u8)
                    || reader.ValueTextEquals("devDependencies"u8)
                    || reader.ValueTextEquals("peerDependencies"u8))
                {
                    reader.Read();
                    ReadDependencies(ref reader, source, offset, dependencyStart, ref dependencies, ref dependencyCount);
                }
                else if (reader.ValueTextEquals("os"u8))
                {
                    reader.Read();
                    osStart = restrictionCount;
                    ReadRestrictions(ref reader, source, offset, ref restrictions, ref restrictionCount);
                    osCount = restrictionCount - osStart;
                }
                else if (reader.ValueTextEquals("cpu"u8))
                {
                    reader.Read();
                    cpuStart = restrictionCount;
                    ReadRestrictions(ref reader, source, offset, ref restrictions, ref restrictionCount);
                    cpuCount = restrictionCount - cpuStart;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            var derivedName = default(Utf8Slice);
            var isRegistryPackage = (flags & NodeFlags.Link) == 0 && TryDerivePackageName(path, out derivedName);
            if (name.IsEmpty && isRegistryPackage)
            {
                name = derivedName;
            }

            EnsureCapacity(ref nodes, nodeCount);
            nodes[nodeCount++] = new PackageNode(
                path,
                name,
                version,
                license,
                resolved,
                CreateVariant(flags, restrictions.AsSpan(osStart, osCount), restrictions.AsSpan(cpuStart, cpuCount)),
                (flags & NodeFlags.Link) != 0,
                isRegistryPackage,
                dependencyStart,
                dependencyCount - dependencyStart);
        }
    }

    private static void ReadDependencies(ref Utf8JsonReader reader, byte[] source, int offset, int dependencyStart, ref NodeDependency[] dependencies, ref int dependencyCount)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "npm dependency map must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "npm dependency name is invalid.");
            var name = CreateValueSlice(ref reader, source, offset);
            var duplicate = false;
            for (var i = dependencyStart; i < dependencyCount; i++)
            {
                if (dependencies[i].Name.Equals(name))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                EnsureCapacity(ref dependencies, dependencyCount);
                dependencies[dependencyCount++] = new NodeDependency(name);
            }

            reader.Read();
            reader.Skip();
        }
    }

    private static void ReadRestrictions(ref Utf8JsonReader reader, byte[] source, int offset, ref Utf8Slice[] restrictions, ref int restrictionCount)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartArray, "npm os and cpu restrictions must be arrays.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("npm os and cpu restrictions must contain strings.");
            }

            EnsureCapacity(ref restrictions, restrictionCount);
            restrictions[restrictionCount++] = CreateValueSlice(ref reader, source, offset);
        }
    }

    private static void Traverse(
        ReadOnlySpan<PackageNode> nodes,
        ReadOnlySpan<NodeDependency> dependencies,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        int rootNodeIndex,
        Span<int> depths,
        Span<int> queue)
    {
        depths.Fill(int.MinValue);
        depths[rootNodeIndex] = -1;
        var head = 0;
        var tail = 0;
        queue[tail++] = rootNodeIndex;
        while (head < tail)
        {
            var nodeIndex = queue[head++];
            var node = nodes[nodeIndex];
            for (var i = node.DependencyStart; i < node.DependencyStart + node.DependencyCount; i++)
            {
                if (!TryResolveDependency(nodes, nodeIndexes, indexCapacity, node.Path.Span, dependencies[i].Name.Span, out var targetIndex))
                {
                    continue;
                }

                targetIndex = FollowLink(nodes, nodeIndexes, indexCapacity, targetIndex);
                if (depths[targetIndex] != int.MinValue)
                {
                    continue;
                }

                depths[targetIndex] = depths[nodeIndex] + 1;
                queue[tail++] = targetIndex;
            }
        }
    }

    private static void ProjectEdges(
        ReadOnlySpan<PackageNode> nodes,
        ReadOnlySpan<NodeDependency> dependencies,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        ReadOnlySpan<int> depths,
        ReadOnlySpan<int> occurrenceByNode,
        int contextIndex,
        ref DependencyEdge[] edges,
        ref int edgeCount)
    {
        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            var fromOccurrence = occurrenceByNode[nodeIndex];
            if (fromOccurrence < 0)
            {
                continue;
            }

            if (depths[nodeIndex] == 0)
            {
                EnsureCapacity(ref edges, edgeCount);
                edges[edgeCount++] = new DependencyEdge(contextIndex, DependencyOccurrence.ContextRoot, fromOccurrence);
            }

            var node = nodes[nodeIndex];
            for (var i = node.DependencyStart; i < node.DependencyStart + node.DependencyCount; i++)
            {
                if (!TryResolveDependency(nodes, nodeIndexes, indexCapacity, node.Path.Span, dependencies[i].Name.Span, out var targetIndex))
                {
                    continue;
                }

                targetIndex = FollowLink(nodes, nodeIndexes, indexCapacity, targetIndex);
                var toOccurrence = occurrenceByNode[targetIndex];
                if (toOccurrence < 0)
                {
                    continue;
                }

                EnsureCapacity(ref edges, edgeCount);
                edges[edgeCount++] = new DependencyEdge(contextIndex, fromOccurrence, toOccurrence);
            }
        }
    }

    private static int FollowLink(ReadOnlySpan<PackageNode> nodes, ReadOnlySpan<int> indexes, int capacity, int nodeIndex)
    {
        for (var remaining = nodes.Length; remaining > 0 && nodes[nodeIndex].Link; remaining--)
        {
            if (nodes[nodeIndex].Resolved.IsEmpty || !TryGetNodeIndex(nodes, indexes, capacity, nodes[nodeIndex].Resolved.Span, out var targetIndex))
            {
                break;
            }

            nodeIndex = targetIndex;
        }

        return nodeIndex;
    }

    private static bool TryResolveDependency(
        ReadOnlySpan<PackageNode> nodes,
        ReadOnlySpan<int> indexes,
        int capacity,
        ReadOnlySpan<byte> fromPath,
        ReadOnlySpan<byte> dependencyName,
        out int nodeIndex)
    {
        var current = fromPath;
        while (true)
        {
            if (!current.SequenceEqual(NodeModules)
                && TryGetNodeIndex(nodes, indexes, capacity, current, dependencyName, out nodeIndex))
            {
                return true;
            }

            if (current.IsEmpty)
            {
                break;
            }

            var separator = current.LastIndexOf((byte)'/');
            current = separator < 0 ? default : current[..separator];
        }

        nodeIndex = -1;
        return false;
    }

    private static ScanComponent CreateComponent(PackageNode node, SpdxLicenseIndex spdxLicenseIndex)
    {
        var candidate = node.License.IsEmpty
            ? default
            : LicenseCandidateFactory.Create(
                LicenseCandidateSource.DependencyInput,
                LicenseCandidateKind.License,
                node.License,
                spdxLicenseIndex,
                PackageLicenseEvidence);
        var (license, status) = candidate.Status switch
        {
            LicenseStatus.Matched => (candidate.Normalized, LicenseStatus.Matched),
            LicenseStatus.Invalid => (LicenseText.WithUncertainty(candidate.Raw), LicenseStatus.Invalid),
            LicenseStatus.Ambiguous => (LicenseText.WithUncertainty(candidate.Raw), LicenseStatus.Ambiguous),
            _ => (default(Utf8Slice), LicenseStatus.Unknown),
        };
        return new ScanComponent(
            node.Name,
            node.Version,
            license,
            "npm",
            DependencyType.Unknown,
            status,
            CreatePurl(node.Name, node.Version),
            node.Path,
            candidate,
            [],
            candidate.Warnings.ToStrings());
    }

    private static Utf8Slice CreateVariant(NodeFlags flags, ReadOnlySpan<Utf8Slice> os, ReadOnlySpan<Utf8Slice> cpu)
    {
        flags &= NodeFlags.Dev | NodeFlags.Optional | NodeFlags.DevOptional | NodeFlags.Peer;
        var length = 0;
        var partCount = 0;
        AddPartLength((flags & NodeFlags.Dev) != 0, 3, ref length, ref partCount);
        AddPartLength((flags & NodeFlags.Optional) != 0, 8, ref length, ref partCount);
        AddPartLength((flags & NodeFlags.DevOptional) != 0, 12, ref length, ref partCount);
        AddPartLength((flags & NodeFlags.Peer) != 0, 4, ref length, ref partCount);
        if (!os.IsEmpty) AddListLength(os, 3, ref length, ref partCount);
        if (!cpu.IsEmpty) AddListLength(cpu, 4, ref length, ref partCount);
        if (length == 0)
        {
            return default;
        }

        var bytes = new byte[length];
        var index = 0;
        var writtenParts = 0;
        WritePart((flags & NodeFlags.Dev) != 0, "dev"u8, bytes, ref index, ref writtenParts);
        WritePart((flags & NodeFlags.Optional) != 0, "optional"u8, bytes, ref index, ref writtenParts);
        WritePart((flags & NodeFlags.DevOptional) != 0, "dev-optional"u8, bytes, ref index, ref writtenParts);
        WritePart((flags & NodeFlags.Peer) != 0, "peer"u8, bytes, ref index, ref writtenParts);
        if (!os.IsEmpty) WriteList("os="u8, os, bytes, ref index, ref writtenParts);
        if (!cpu.IsEmpty) WriteList("cpu="u8, cpu, bytes, ref index, ref writtenParts);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static void AddPartLength(bool present, int valueLength, ref int length, ref int partCount)
    {
        if (!present) return;
        length = checked(length + valueLength + (partCount == 0 ? 0 : 1));
        partCount++;
    }

    private static void AddListLength(ReadOnlySpan<Utf8Slice> values, int prefixLength, ref int length, ref int partCount)
    {
        var valueLength = prefixLength + values.Length - 1;
        for (var i = 0; i < values.Length; i++) valueLength = checked(valueLength + values[i].Length);
        AddPartLength(true, valueLength, ref length, ref partCount);
    }

    private static void WritePart(bool present, ReadOnlySpan<byte> value, Span<byte> destination, ref int index, ref int partCount)
    {
        if (!present) return;
        if (partCount++ > 0) destination[index++] = (byte)';';
        value.CopyTo(destination[index..]);
        index += value.Length;
    }

    private static void WriteList(ReadOnlySpan<byte> prefix, ReadOnlySpan<Utf8Slice> values, Span<byte> destination, ref int index, ref int partCount)
    {
        if (partCount++ > 0) destination[index++] = (byte)';';
        prefix.CopyTo(destination[index..]);
        index += prefix.Length;
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) destination[index++] = (byte)',';
            values[i].Span.CopyTo(destination[index..]);
            index += values[i].Length;
        }
    }

    private static Utf8Slice CreatePurl(Utf8Slice name, Utf8Slice version)
    {
        var nameLength = GetEncodedLength(name.Span, allowSlash: true);
        var versionLength = GetEncodedLength(version.Span, allowSlash: false);
        var bytes = new byte[PurlPrefix.Length + nameLength + 1 + versionLength];
        PurlPrefix.CopyTo(bytes);
        var index = PurlPrefix.Length;
        WriteEncoded(name.Span, allowSlash: true, bytes, ref index);
        bytes[index++] = (byte)'@';
        WriteEncoded(version.Span, allowSlash: false, bytes, ref index);
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
            if (IsPurlSafe(item, allowSlash))
            {
                destination[index++] = item;
            }
            else
            {
                destination[index++] = (byte)'%';
                destination[index++] = (byte)Hex[item >> 4];
                destination[index++] = (byte)Hex[item & 0x0F];
            }
        }
    }

    private static bool IsPurlSafe(byte value, bool allowSlash)
        => value is >= (byte)'a' and <= (byte)'z'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~'
        || allowSlash && value == (byte)'/';

    private static bool TryDerivePackageName(Utf8Slice path, out Utf8Slice name)
    {
        var bytes = path.Span;
        var markerIndex = bytes.LastIndexOf(NodeModulesPrefix);
        if (markerIndex < 0)
        {
            markerIndex = bytes.LastIndexOf(NestedNodeModules);
            if (markerIndex >= 0) markerIndex++;
        }

        if (markerIndex < 0)
        {
            name = default;
            return false;
        }

        var nameOffset = markerIndex + NodeModulesPrefix.Length;
        var value = bytes[nameOffset..];
        if (value.IsEmpty || (value[0] == (byte)'@' ? value.IndexOf((byte)'/') <= 1 || value.LastIndexOf((byte)'/') != value.IndexOf((byte)'/') : value.Contains((byte)'/')))
        {
            throw new JsonException("npm package path contains an invalid package name.");
        }

        name = path.Slice(nameOffset, value.Length);
        return true;
    }

    private static int GetIndexCapacity(int count)
    {
        var capacity = 2;
        while (capacity < count * 2) capacity *= 2;
        return capacity;
    }

    private static bool AddNodeIndex(ReadOnlySpan<PackageNode> nodes, Span<int> indexes, int capacity, int nodeIndex)
    {
        var slot = (int)(Hash(nodes[nodeIndex].Path.Span) & (uint)(capacity - 1));
        while (indexes[slot] >= 0)
        {
            if (nodes[indexes[slot]].Path.Equals(nodes[nodeIndex].Path)) return false;
            slot = (slot + 1) & (capacity - 1);
        }

        indexes[slot] = nodeIndex;
        return true;
    }

    private static bool TryGetNodeIndex(ReadOnlySpan<PackageNode> nodes, ReadOnlySpan<int> indexes, int capacity, ReadOnlySpan<byte> path, out int nodeIndex)
    {
        var slot = (int)(Hash(path) & (uint)(capacity - 1));
        while ((nodeIndex = indexes[slot]) >= 0)
        {
            if (nodes[nodeIndex].Path.Span.SequenceEqual(path)) return true;
            slot = (slot + 1) & (capacity - 1);
        }

        return false;
    }

    private static bool TryGetNodeIndex(ReadOnlySpan<PackageNode> nodes, ReadOnlySpan<int> indexes, int capacity, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> name, out int nodeIndex)
    {
        var separator = prefix.IsEmpty ? NodeModulesPrefix : NestedNodeModules;
        var hash = Hash(prefix);
        hash = Hash(separator, hash);
        hash = Hash(name, hash);
        var slot = (int)(hash & (uint)(capacity - 1));
        var expectedLength = prefix.Length + separator.Length + name.Length;
        while ((nodeIndex = indexes[slot]) >= 0)
        {
            var candidate = nodes[nodeIndex].Path.Span;
            if (candidate.Length == expectedLength
                && candidate[..prefix.Length].SequenceEqual(prefix)
                && candidate.Slice(prefix.Length, separator.Length).SequenceEqual(separator)
                && candidate[(prefix.Length + separator.Length)..].SequenceEqual(name))
            {
                return true;
            }

            slot = (slot + 1) & (capacity - 1);
        }

        return false;
    }

    private static uint Hash(ReadOnlySpan<byte> value, uint hash = 2166136261)
    {
        for (var i = 0; i < value.Length; i++) hash = (hash ^ value[i]) * 16777619;
        return hash;
    }

    private static bool ReadBoolean(ref Utf8JsonReader reader)
    {
        reader.Read();
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            _ => throw new JsonException("npm package flags must be booleans."),
        };
    }

    private static Utf8Slice ReadString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("npm package fields must use their documented JSON types.");
        return CreateValueSlice(ref reader, source, offset);
    }

    private static Utf8Slice CreateValueSlice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped) return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static Utf8Slice CreateNumberSlice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        if (reader.TokenType != JsonTokenType.Number || reader.HasValueSequence) throw new JsonException("npm lockfileVersion must be a number.");
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex), reader.ValueSpan.Length);
    }

    private static Utf8JsonReader CreateReader(byte[] source, int offset)
        => new(source.AsSpan(offset), isFinalBlock: true, state: default);

    private static void RequireToken(ref Utf8JsonReader reader, JsonTokenType expected, string message)
    {
        if (!reader.Read() || reader.TokenType != expected) throw new JsonException(message);
    }

    private static void RequireCurrentToken(ref Utf8JsonReader reader, JsonTokenType expected, string message)
    {
        if (reader.TokenType != expected) throw new JsonException(message);
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length) return;
        var expanded = ArrayPool<T>.Shared.Rent(values.Length * 2);
        values.AsSpan(0, count).CopyTo(expanded);
        ArrayPool<T>.Shared.Return(values, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        values = expanded;
    }

    private readonly record struct PackageNode(
        Utf8Slice Path,
        Utf8Slice Name,
        Utf8Slice Version,
        Utf8Slice License,
        Utf8Slice Resolved,
        Utf8Slice Variant,
        bool Link,
        bool IsRegistryPackage,
        int DependencyStart,
        int DependencyCount);

    private readonly record struct NodeDependency(Utf8Slice Name);

    [Flags]
    private enum NodeFlags : byte
    {
        None = 0,
        Link = 1 << 0,
        Dev = 1 << 1,
        Optional = 1 << 2,
        DevOptional = 1 << 3,
        Peer = 1 << 4,
    }
}
