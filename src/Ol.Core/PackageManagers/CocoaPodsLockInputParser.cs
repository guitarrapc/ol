using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class CocoaPodsLockInputParser
{
    private static readonly Utf8Slice ProjectOrigin = Utf8Slice.FromOwnedBytes("Podfile.lock"u8.ToArray());
    private static readonly Utf8Slice PrivateSpecRepoVariant = Utf8Slice.FromOwnedBytes("source=private-spec-repo"u8.ToArray());
    private static readonly Utf8Slice ExternalSourceVariant = Utf8Slice.FromOwnedBytes("source=external"u8.ToArray());
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:cocoapods/"u8;

    internal static bool Detect(ReadOnlySpan<byte> inputUtf8)
    {
        var pods = false;
        var dependencies = false;
        var cocoapods = false;
        var position = 0;
        while (position < inputUtf8.Length)
        {
            var newline = inputUtf8[position..].IndexOf((byte)'\n');
            var end = newline < 0 ? inputUtf8.Length : position + newline;
            var line = inputUtf8[position..end];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
            if (!line.IsEmpty && line[0] != (byte)' ')
            {
                pods |= line.SequenceEqual("PODS:"u8);
                dependencies |= line.SequenceEqual("DEPENDENCIES:"u8);
                cocoapods |= line.StartsWith("COCOAPODS:"u8);
            }
            position = newline < 0 ? inputUtf8.Length : end + 1;
        }
        return pods && dependencies && cocoapods;
    }

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph)
    {
        var nodes = ArrayPool<PodNode>.Shared.Rent(16);
        var dependencies = ArrayPool<PodDependency>.Shared.Rent(32);
        var directNames = ArrayPool<Utf8Slice>.Shared.Rent(16);
        var repoPods = ArrayPool<RepoPod>.Shared.Rent(16);
        var externalNames = ArrayPool<Utf8Slice>.Shared.Rent(8);
        var nodeIndexCapacity = 32;
        var nodeIndexes = ArrayPool<int>.Shared.Rent(nodeIndexCapacity);
        nodeIndexes.AsSpan(0, nodeIndexCapacity).Fill(-1);
        var nodeCount = 0;
        var dependencyCount = 0;
        var directCount = 0;
        var repoPodCount = 0;
        var externalCount = 0;
        try
        {
            var reader = new Utf8YamlLineReader(source, offset);
            var section = PodSection.None;
            var currentNode = -1;
            var currentRepoPublic = false;
            var cocoapodsVersion = default(Utf8Slice);
            var foundPods = false;
            var foundDirect = false;
            while (reader.Read(out var line))
            {
                if (line.Indent == 0 && !line.IsSequence)
                {
                    section = GetSection(line.Key.Span);
                    currentNode = -1;
                    if (section == PodSection.Pods) foundPods = true;
                    else if (section == PodSection.Direct) foundDirect = true;
                    else if (section == PodSection.Version)
                    {
                        if (!cocoapodsVersion.IsEmpty || !line.HasValue || line.Value.IsEmpty)
                        {
                            throw new JsonException("Podfile.lock requires one non-empty COCOAPODS version.");
                        }
                        cocoapodsVersion = line.Value;
                    }
                    continue;
                }

                switch (section)
                {
                    case PodSection.Pods when line.IsSequence && line.Indent == 2:
                        ParsePodCoordinate(RemoveTrailingColon(line.Value), requireVersion: true, out var podName, out var version);
                        var rootName = GetRootName(podName);
                        ValidatePodName(rootName.Span);
                        currentNode = FindNode(nodes.AsSpan(0, nodeCount), nodeIndexes, nodeIndexCapacity, rootName);
                        if (currentNode < 0)
                        {
                            if (nodeCount * 2 >= nodeIndexCapacity)
                            {
                                GrowNodeIndex(nodes.AsSpan(0, nodeCount), ref nodeIndexes, ref nodeIndexCapacity);
                            }
                            EnsureCapacity(ref nodes, nodeCount);
                            currentNode = nodeCount++;
                            nodes[currentNode] = new PodNode(rootName, version);
                            AddNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, nodeIndexCapacity, currentNode);
                        }
                        else if (!nodes[currentNode].Version.Equals(version))
                        {
                            throw new JsonException("Podfile.lock cannot resolve one root pod to multiple versions.");
                        }
                        break;
                    case PodSection.Pods when line.IsSequence && line.Indent == 4:
                        if (currentNode < 0) throw new JsonException("Podfile.lock pod dependency has no owning pod.");
                        ParsePodCoordinate(line.Value, requireVersion: false, out var dependencyName, out _);
                        EnsureCapacity(ref dependencies, dependencyCount);
                        dependencies[dependencyCount++] = new PodDependency(currentNode, GetRootName(dependencyName));
                        break;
                    case PodSection.Direct when line.IsSequence && line.Indent == 2:
                        ParsePodCoordinate(line.Value, requireVersion: false, out var directName, out _);
                        EnsureCapacity(ref directNames, directCount);
                        directNames[directCount++] = GetRootName(directName);
                        break;
                    case PodSection.SpecRepos when !line.IsSequence && line.Indent == 2:
                        currentRepoPublic = IsPublicRepo(line.Key.Span);
                        break;
                    case PodSection.SpecRepos when line.IsSequence && line.Indent == 4:
                        EnsureCapacity(ref repoPods, repoPodCount);
                        repoPods[repoPodCount++] = new RepoPod(GetRootName(line.Value), currentRepoPublic);
                        break;
                    case PodSection.External when !line.IsSequence && line.Indent == 2:
                        EnsureCapacity(ref externalNames, externalCount);
                        externalNames[externalCount++] = GetRootName(line.Key);
                        break;
                }
            }

            if (!foundPods || !foundDirect || cocoapodsVersion.IsEmpty)
            {
                throw new JsonException("Podfile.lock requires PODS, DEPENDENCIES, and COCOAPODS sections.");
            }
            return CreateInventory(
                nodes.AsSpan(0, nodeCount),
                dependencies.AsSpan(0, dependencyCount),
                directNames.AsSpan(0, directCount),
                repoPods.AsSpan(0, repoPodCount),
                externalNames.AsSpan(0, externalCount),
                nodeIndexes,
                nodeIndexCapacity,
                cocoapodsVersion,
                retainGraph);
        }
        finally
        {
            ArrayPool<PodNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<PodDependency>.Shared.Return(dependencies, clearArray: true);
            ArrayPool<Utf8Slice>.Shared.Return(directNames, clearArray: true);
            ArrayPool<RepoPod>.Shared.Return(repoPods, clearArray: true);
            ArrayPool<Utf8Slice>.Shared.Return(externalNames, clearArray: true);
            ArrayPool<int>.Shared.Return(nodeIndexes);
        }
    }

    private static DependencyInventory CreateInventory(
        ReadOnlySpan<PodNode> nodes,
        ReadOnlySpan<PodDependency> dependencies,
        ReadOnlySpan<Utf8Slice> directNames,
        ReadOnlySpan<RepoPod> repoPods,
        ReadOnlySpan<Utf8Slice> externalNames,
        int[] nodeIndexes,
        int nodeIndexCapacity,
        Utf8Slice cocoapodsVersion,
        bool retainGraph)
    {
        var depths = ArrayPool<int>.Shared.Rent(Math.Max(nodes.Length, 1));
        var queue = ArrayPool<int>.Shared.Rent(Math.Max(nodes.Length, 1));
        try
        {
            depths.AsSpan(0, nodes.Length).Fill(int.MinValue);
            var head = 0;
            var tail = 0;
            for (var directIndex = 0; directIndex < directNames.Length; directIndex++)
            {
                var target = FindNode(nodes, nodeIndexes, nodeIndexCapacity, directNames[directIndex]);
                if (target < 0 || depths[target] == 0) continue;
                depths[target] = 0;
                queue[tail++] = target;
            }
            while (head < tail)
            {
                var owner = queue[head++];
                for (var dependencyIndex = 0; dependencyIndex < dependencies.Length; dependencyIndex++)
                {
                    if (dependencies[dependencyIndex].OwnerIndex != owner) continue;
                    var target = FindNode(nodes, nodeIndexes, nodeIndexCapacity, dependencies[dependencyIndex].Name);
                    if (target < 0) continue;
                    var depth = depths[owner] + 1;
                    if (depths[target] != int.MinValue && depths[target] <= depth) continue;
                    depths[target] = depth;
                    queue[tail++] = target;
                }
            }

            var components = new ScanComponent[nodes.Length];
            var occurrences = retainGraph ? new DependencyOccurrence[nodes.Length] : [];
            var variantBuffer = retainGraph ? ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(Math.Max(nodes.Length, 1)) : null;
            var sources = ArrayPool<PodSource>.Shared.Rent(Math.Max(nodes.Length, 1));
            var variantCount = 0;
            try
            {
                ClassifySources(nodes, repoPods, externalNames, nodeIndexes, nodeIndexCapacity, sources.AsSpan(0, nodes.Length));
                for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                {
                    var node = nodes[nodeIndex];
                    var source = sources[nodeIndex];
                    var purl = source == PodSource.Public ? CreatePurl(node.Name, node.Version) : default;
                    components[nodeIndex] = new ScanComponent(
                        node.Name,
                        node.Version,
                        default,
                        purl.IsEmpty ? "-" : "cocoapods",
                        depths[nodeIndex] switch { 0 => DependencyType.Direct, > 0 => DependencyType.Transitive, _ => DependencyType.Unknown },
                        LicenseStatus.Unknown,
                        purl,
                        CreateSourceId(node.Name, node.Version),
                        default,
                        []);
                    if (!retainGraph) continue;
                    occurrences[nodeIndex] = new DependencyOccurrence(0, nodeIndex);
                    var variant = source switch
                    {
                        PodSource.Private => PrivateSpecRepoVariant,
                        PodSource.External => ExternalSourceVariant,
                        _ => default,
                    };
                    if (!variant.IsEmpty) variantBuffer![variantCount++] = new DependencyOccurrenceVariant(nodeIndex, variant);
                }

                var edges = retainGraph ? ProjectEdges(nodes, dependencies, directNames, nodeIndexes, nodeIndexCapacity) : [];
                return new DependencyInventory(
                    new ScanInputDescriptor(default, default, string.Empty, string.Empty, cocoapodsVersion),
                    [new DependencyResolutionContext(ProjectOrigin, default, default, default, default, CreateVersionVariant(cocoapodsVersion))],
                    components,
                    occurrences,
                    edges,
                    retainGraph && variantCount != 0 ? variantBuffer!.AsSpan(0, variantCount).ToArray() : []);
            }
            finally
            {
                if (variantBuffer is not null) ArrayPool<DependencyOccurrenceVariant>.Shared.Return(variantBuffer, clearArray: true);
                ArrayPool<PodSource>.Shared.Return(sources);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(depths);
            ArrayPool<int>.Shared.Return(queue);
        }
    }

    private static DependencyEdge[] ProjectEdges(
        ReadOnlySpan<PodNode> nodes,
        ReadOnlySpan<PodDependency> dependencies,
        ReadOnlySpan<Utf8Slice> directNames,
        int[] nodeIndexes,
        int nodeIndexCapacity)
    {
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(Math.Max(16, directNames.Length + dependencies.Length));
        var edgeIndexCapacity = 2;
        var edgeCapacityTarget = directNames.Length + dependencies.Length;
        if (edgeCapacityTarget > 1 << 29) throw new JsonException("Podfile.lock contains too many dependency edges.");
        while (edgeIndexCapacity < edgeCapacityTarget * 2) edgeIndexCapacity *= 2;
        var edgeIndexes = ArrayPool<int>.Shared.Rent(edgeIndexCapacity);
        edgeIndexes.AsSpan(0, edgeIndexCapacity).Fill(-1);
        var edgeCount = 0;
        try
        {
            for (var directIndex = 0; directIndex < directNames.Length; directIndex++)
            {
                var target = FindNode(nodes, nodeIndexes, nodeIndexCapacity, directNames[directIndex]);
                if (target >= 0) AddUniqueEdge(edges, edgeIndexes, edgeIndexCapacity, ref edgeCount, new DependencyEdge(0, DependencyOccurrence.ContextRoot, target));
            }
            for (var dependencyIndex = 0; dependencyIndex < dependencies.Length; dependencyIndex++)
            {
                var dependency = dependencies[dependencyIndex];
                var target = FindNode(nodes, nodeIndexes, nodeIndexCapacity, dependency.Name);
                if (target >= 0 && target != dependency.OwnerIndex)
                {
                    AddUniqueEdge(edges, edgeIndexes, edgeIndexCapacity, ref edgeCount, new DependencyEdge(0, dependency.OwnerIndex, target));
                }
            }
            return edges.AsSpan(0, edgeCount).ToArray();
        }
        finally
        {
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            ArrayPool<int>.Shared.Return(edgeIndexes);
        }
    }

    private static void AddUniqueEdge(DependencyEdge[] edges, int[] indexes, int capacity, ref int count, DependencyEdge edge)
    {
        var slot = (int)(HashEdge(edge) & (uint)(capacity - 1));
        while (indexes[slot] >= 0)
        {
            if (edges[indexes[slot]] == edge) return;
            slot = (slot + 1) & (capacity - 1);
        }
        indexes[slot] = count;
        edges[count++] = edge;
    }

    private static void ClassifySources(
        ReadOnlySpan<PodNode> nodes,
        ReadOnlySpan<RepoPod> repoPods,
        ReadOnlySpan<Utf8Slice> externalNames,
        int[] nodeIndexes,
        int nodeIndexCapacity,
        Span<PodSource> sources)
    {
        sources.Clear();
        for (var index = 0; index < repoPods.Length; index++)
        {
            var nodeIndex = FindNode(nodes, nodeIndexes, nodeIndexCapacity, repoPods[index].Name);
            if (nodeIndex < 0) continue;
            var source = repoPods[index].Public ? PodSource.Public : PodSource.Private;
            if (sources[nodeIndex] != PodSource.Unknown && sources[nodeIndex] != source)
            {
                throw new JsonException("Podfile.lock maps one pod to ambiguous spec repositories.");
            }
            sources[nodeIndex] = source;
        }
        for (var index = 0; index < externalNames.Length; index++)
        {
            var nodeIndex = FindNode(nodes, nodeIndexes, nodeIndexCapacity, externalNames[index]);
            if (nodeIndex >= 0) sources[nodeIndex] = PodSource.External;
        }
    }

    private static void ParsePodCoordinate(Utf8Slice value, bool requireVersion, out Utf8Slice name, out Utf8Slice version)
    {
        var bytes = value.Span;
        var open = bytes.IndexOf(" ("u8);
        if (open < 0)
        {
            if (requireVersion || bytes.IsEmpty) throw new JsonException("Podfile.lock pod entries require a name and resolved version.");
            name = value;
            version = default;
            return;
        }
        name = value.Slice(0, open);
        var close = bytes[(open + 2)..].IndexOf((byte)')');
        if (close < 0) throw new JsonException("Podfile.lock pod coordinate is incomplete.");
        var constraint = value.Slice(open + 2, close);
        if (requireVersion)
        {
            if (constraint.IsEmpty || constraint.Span.IndexOfAny(" ~><="u8) >= 0)
            {
                throw new JsonException("Podfile.lock PODS entries require exact resolved versions.");
            }
            version = constraint;
        }
        else version = default;
    }

    private static Utf8Slice RemoveTrailingColon(Utf8Slice value)
        => !value.IsEmpty && value.Span[^1] == (byte)':' ? value.Slice(0, value.Length - 1) : value;

    private static Utf8Slice GetRootName(Utf8Slice name)
    {
        var slash = name.Span.IndexOf((byte)'/');
        return slash < 0 ? name : name.Slice(0, slash);
    }

    private static int FindNode(ReadOnlySpan<PodNode> nodes, int[] indexes, int capacity, Utf8Slice name)
    {
        var slot = (int)(HashName(name.Span) & (uint)(capacity - 1));
        while (indexes[slot] >= 0)
        {
            if (nodes[indexes[slot]].Name.Equals(name)) return indexes[slot];
            slot = (slot + 1) & (capacity - 1);
        }
        return -1;
    }

    private static void AddNodeIndex(ReadOnlySpan<PodNode> nodes, int[] indexes, int capacity, int nodeIndex)
    {
        var slot = (int)(HashName(nodes[nodeIndex].Name.Span) & (uint)(capacity - 1));
        while (indexes[slot] >= 0) slot = (slot + 1) & (capacity - 1);
        indexes[slot] = nodeIndex;
    }

    private static void GrowNodeIndex(ReadOnlySpan<PodNode> nodes, ref int[] indexes, ref int capacity)
    {
        var expandedCapacity = checked(capacity * 2);
        var expanded = ArrayPool<int>.Shared.Rent(expandedCapacity);
        expanded.AsSpan(0, expandedCapacity).Fill(-1);
        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++) AddNodeIndex(nodes, expanded, expandedCapacity, nodeIndex);
        ArrayPool<int>.Shared.Return(indexes);
        indexes = expanded;
        capacity = expandedCapacity;
    }

    private static uint HashName(ReadOnlySpan<byte> value)
    {
        var hash = 2166136261u;
        for (var index = 0; index < value.Length; index++) hash = (hash ^ value[index]) * 16777619;
        return hash;
    }

    private static uint HashEdge(DependencyEdge edge)
    {
        var hash = 2166136261u;
        hash = (hash ^ (uint)edge.FromOccurrenceIndex) * 16777619;
        return (hash ^ (uint)edge.ToOccurrenceIndex) * 16777619;
    }

    private static bool IsPublicRepo(ReadOnlySpan<byte> value)
        => value.SequenceEqual("trunk"u8)
        || value.SequenceEqual("https://cdn.cocoapods.org/"u8)
        || value.SequenceEqual("https://github.com/CocoaPods/Specs.git"u8);

    private static void ValidatePodName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty || name[0] == (byte)'.') throw new JsonException("Podfile.lock contains an invalid pod name.");
        for (var index = 0; index < name.Length; index++)
        {
            if (name[index] <= 0x20 || name[index] == (byte)'+') throw new JsonException("Podfile.lock contains an invalid pod name.");
        }
    }

    private static Utf8Slice CreatePurl(Utf8Slice name, Utf8Slice version)
    {
        var bytes = new byte[checked(PurlPrefix.Length + GetEncodedLength(name.Span) + 1 + GetEncodedLength(version.Span))];
        PurlPrefix.CopyTo(bytes);
        var index = PurlPrefix.Length;
        WriteEncoded(name.Span, bytes, ref index);
        bytes[index++] = (byte)'@';
        WriteEncoded(version.Span, bytes, ref index);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateSourceId(Utf8Slice name, Utf8Slice version)
    {
        var bytes = new byte[checked(name.Length + 1 + version.Length)];
        name.Span.CopyTo(bytes);
        bytes[name.Length] = (byte)'@';
        version.Span.CopyTo(bytes.AsSpan(name.Length + 1));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateVersionVariant(Utf8Slice version)
    {
        var bytes = new byte[checked("cocoapods=".Length + version.Length)];
        "cocoapods="u8.CopyTo(bytes);
        version.Span.CopyTo(bytes.AsSpan("cocoapods=".Length));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static int GetEncodedLength(ReadOnlySpan<byte> value)
    {
        var length = 0;
        for (var index = 0; index < value.Length; index++) length = checked(length + (IsPurlSafe(value[index]) ? 1 : 3));
        return length;
    }

    private static void WriteEncoded(ReadOnlySpan<byte> value, Span<byte> destination, ref int index)
    {
        ReadOnlySpan<byte> hex = "0123456789ABCDEF"u8;
        for (var valueIndex = 0; valueIndex < value.Length; valueIndex++)
        {
            var item = value[valueIndex];
            if (IsPurlSafe(item)) destination[index++] = item;
            else
            {
                destination[index++] = (byte)'%';
                destination[index++] = hex[item >> 4];
                destination[index++] = hex[item & 0x0f];
            }
        }
    }

    private static bool IsPurlSafe(byte value)
        => value is >= (byte)'a' and <= (byte)'z'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    private static PodSection GetSection(ReadOnlySpan<byte> key)
        => key.SequenceEqual("PODS"u8) ? PodSection.Pods
        : key.SequenceEqual("DEPENDENCIES"u8) ? PodSection.Direct
        : key.SequenceEqual("SPEC REPOS"u8) ? PodSection.SpecRepos
        : key.SequenceEqual("EXTERNAL SOURCES"u8) ? PodSection.External
        : key.SequenceEqual("COCOAPODS"u8) ? PodSection.Version
        : PodSection.None;

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length) return;
        var expanded = ArrayPool<T>.Shared.Rent(values.Length * 2);
        values.AsSpan(0, count).CopyTo(expanded);
        ArrayPool<T>.Shared.Return(values, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        values = expanded;
    }

    private readonly record struct PodNode(Utf8Slice Name, Utf8Slice Version);
    private readonly record struct PodDependency(int OwnerIndex, Utf8Slice Name);
    private readonly record struct RepoPod(Utf8Slice Name, bool Public);
    private enum PodSource : byte { Unknown, Public, Private, External }
    private enum PodSection : byte { None, Pods, Direct, SpecRepos, External, Version }
}
