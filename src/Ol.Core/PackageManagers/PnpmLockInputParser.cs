using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class PnpmLockInputParser
{
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:npm/"u8;

    internal static bool Detect(ReadOnlySpan<byte> inputUtf8)
    {
        var hasVersion = false;
        var hasImporters = false;
        var position = 0;
        while (position < inputUtf8.Length)
        {
            var start = position;
            var newline = inputUtf8[position..].IndexOf((byte)'\n');
            var end = newline < 0 ? inputUtf8.Length : position + newline;
            position = newline < 0 ? inputUtf8.Length : end + 1;
            if (end > start && inputUtf8[end - 1] == (byte)'\r') end--;
            var line = inputUtf8[start..end];
            if (line.IsEmpty || line[0] is (byte)' ' or (byte)'\t' or (byte)'#') continue;
            if (line.StartsWith("lockfileVersion:"u8)) hasVersion = true;
            else if (line.StartsWith("importers:"u8)) hasImporters = true;
            if (hasVersion && hasImporters) return true;
        }

        return false;
    }

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex _, bool retainGraph)
    {
        var nodes = ArrayPool<ResolverNode>.Shared.Rent(16);
        var dependencies = ArrayPool<ResolverDependency>.Shared.Rent(32);
        var packageMetadata = ArrayPool<PackageMetadata>.Shared.Rent(16);
        var restrictions = ArrayPool<Utf8Slice>.Shared.Rent(16);
        var contexts = ArrayPool<DependencyResolutionContext>.Shared.Rent(4);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var occurrences = ArrayPool<DependencyOccurrence>.Shared.Rent(32);
        var occurrenceVariants = ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(8);
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(32);
        int[]? snapshotIndexes = null;
        int[]? componentByNode = null;
        int[]? depths = null;
        int[]? queue = null;
        int[]? occurrenceByNode = null;
        byte[]? reachKinds = null;
        var nodeCount = 0;
        var dependencyCount = 0;
        var packageMetadataCount = 0;
        var restrictionCount = 0;
        var contextCount = 0;
        var componentCount = 0;
        var occurrenceCount = 0;
        var occurrenceVariantCount = 0;
        var edgeCount = 0;
        try
        {
            ReadLockfile(
                source,
                offset,
                ref nodes,
                ref nodeCount,
                ref dependencies,
                ref dependencyCount,
                ref packageMetadata,
                ref packageMetadataCount,
                ref restrictions,
                ref restrictionCount,
                out var specificationVersion,
                out var importerCount);

            if (importerCount == 0)
            {
                throw new JsonException("pnpm lockfile must contain at least one importer.");
            }

            var snapshotCount = nodeCount - importerCount;
            if (snapshotCount <= 0)
            {
                throw new JsonException("pnpm lockfile must contain snapshots.");
            }

            var snapshotIndexCapacity = GetIndexCapacity(snapshotCount);
            snapshotIndexes = ArrayPool<int>.Shared.Rent(snapshotIndexCapacity);
            snapshotIndexes.AsSpan(0, snapshotIndexCapacity).Fill(-1);
            componentByNode = ArrayPool<int>.Shared.Rent(nodeCount);
            componentByNode.AsSpan(0, nodeCount).Fill(-1);
            for (var nodeIndex = importerCount; nodeIndex < nodeCount; nodeIndex++)
            {
                if (!AddSnapshotIndex(nodes.AsSpan(0, nodeCount), snapshotIndexes, snapshotIndexCapacity, nodeIndex))
                {
                    throw new JsonException("pnpm snapshots cannot repeat a package identity.");
                }

                var node = nodes[nodeIndex];
                SplitSnapshotIdentity(node.Identity, out var name, out var version, out var peerSuffix, out var packageIdentity);
                var metadataIndex = FindPackageMetadata(packageMetadata.AsSpan(0, packageMetadataCount), packageIdentity);
                var metadata = metadataIndex < 0 ? default : packageMetadata[metadataIndex];
                var baseVariant = CreateBaseVariant(
                    peerSuffix,
                    metadataIndex < 0 ? default : restrictions.AsSpan(metadata.OsStart, metadata.OsCount),
                    metadataIndex < 0 ? default : restrictions.AsSpan(metadata.CpuStart, metadata.CpuCount));
                nodes[nodeIndex] = node with { BaseVariant = baseVariant };
                EnsureCapacity(ref components, componentCount);
                componentByNode[nodeIndex] = componentCount;
                components[componentCount++] = new ScanComponent(
                    name,
                    version,
                    default,
                    "npm",
                    DependencyType.Unknown,
                    LicenseStatus.Unknown,
                    CreatePurl(name, version),
                    node.Identity,
                    default,
                    [],
                    []);
            }

            for (var importerIndex = 0; importerIndex < importerCount; importerIndex++)
            {
                EnsureCapacity(ref contexts, contextCount);
                contexts[contextCount++] = new DependencyResolutionContext(nodes[importerIndex].Identity, default, default, default, default, default);
            }

            depths = ArrayPool<int>.Shared.Rent(nodeCount);
            queue = ArrayPool<int>.Shared.Rent(checked(nodeCount * 4));
            occurrenceByNode = ArrayPool<int>.Shared.Rent(nodeCount);
            reachKinds = ArrayPool<byte>.Shared.Rent(nodeCount);
            for (var contextIndex = 0; contextIndex < importerCount; contextIndex++)
            {
                Traverse(
                    nodes.AsSpan(0, nodeCount),
                    dependencies.AsSpan(0, dependencyCount),
                    snapshotIndexes,
                    snapshotIndexCapacity,
                    importerCount,
                    contextIndex,
                    depths.AsSpan(0, nodeCount),
                    reachKinds.AsSpan(0, nodeCount),
                    queue.AsSpan(0, nodeCount * 4));

                occurrenceByNode.AsSpan(0, nodeCount).Fill(-1);
                var includeUnknown = contextIndex == 0;
                for (var nodeIndex = importerCount; nodeIndex < nodeCount; nodeIndex++)
                {
                    if (!includeUnknown && depths[nodeIndex] == int.MinValue) continue;
                    var componentIndex = componentByNode[nodeIndex];
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
                    var variant = ComposeOccurrenceVariant(nodes[nodeIndex].BaseVariant, (ReachKind)reachKinds[nodeIndex]);
                    if (!variant.IsEmpty)
                    {
                        EnsureCapacity(ref occurrenceVariants, occurrenceVariantCount);
                        occurrenceVariants[occurrenceVariantCount++] = new DependencyOccurrenceVariant(occurrenceCount - 1, variant);
                    }
                }

                ProjectEdges(
                    nodes.AsSpan(0, nodeCount),
                    dependencies.AsSpan(0, dependencyCount),
                    snapshotIndexes,
                    snapshotIndexCapacity,
                    importerCount,
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
            ArrayPool<ResolverNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<ResolverDependency>.Shared.Return(dependencies, clearArray: true);
            ArrayPool<PackageMetadata>.Shared.Return(packageMetadata, clearArray: true);
            ArrayPool<Utf8Slice>.Shared.Return(restrictions, clearArray: true);
            ArrayPool<DependencyResolutionContext>.Shared.Return(contexts, clearArray: true);
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyOccurrence>.Shared.Return(occurrences);
            ArrayPool<DependencyOccurrenceVariant>.Shared.Return(occurrenceVariants, clearArray: true);
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            if (snapshotIndexes is not null) ArrayPool<int>.Shared.Return(snapshotIndexes);
            if (componentByNode is not null) ArrayPool<int>.Shared.Return(componentByNode);
            if (depths is not null) ArrayPool<int>.Shared.Return(depths);
            if (queue is not null) ArrayPool<int>.Shared.Return(queue);
            if (occurrenceByNode is not null) ArrayPool<int>.Shared.Return(occurrenceByNode);
            if (reachKinds is not null) ArrayPool<byte>.Shared.Return(reachKinds);
        }
    }

    private static void ReadLockfile(
        byte[] source,
        int offset,
        ref ResolverNode[] nodes,
        ref int nodeCount,
        ref ResolverDependency[] dependencies,
        ref int dependencyCount,
        ref PackageMetadata[] packageMetadata,
        ref int packageMetadataCount,
        ref Utf8Slice[] restrictions,
        ref int restrictionCount,
        out Utf8Slice specificationVersion,
        out int importerCount)
    {
        specificationVersion = default;
        importerCount = 0;
        var section = PnpmSection.None;
        var currentNode = -1;
        var currentDependency = -1;
        var dependencyKind = DependencyKind.Normal;
        var currentPackage = -1;
        var restrictionKind = RestrictionKind.None;
        var foundPackages = false;
        var foundSnapshots = false;
        var reader = new Utf8YamlLineReader(source, offset);
        while (reader.Read(out var line))
        {
            if (line.IsSequence)
            {
                if (section == PnpmSection.Packages && line.Indent == 6 && currentPackage >= 0 && restrictionKind != RestrictionKind.None)
                {
                    AddRestriction(line.Value, restrictionKind, currentPackage, ref packageMetadata, ref restrictions, ref restrictionCount);
                    continue;
                }

                throw new JsonException("pnpm lockfile contains an unexpected YAML sequence.");
            }

            if (line.Indent == 0)
            {
                currentNode = -1;
                currentDependency = -1;
                currentPackage = -1;
                restrictionKind = RestrictionKind.None;
                if (line.Key.Span.SequenceEqual("lockfileVersion"u8))
                {
                    specificationVersion = line.Value;
                    if (!specificationVersion.Span.SequenceEqual("9.0"u8))
                    {
                        throw new JsonException("pnpm lockfileVersion must be 9.0.");
                    }

                    section = PnpmSection.None;
                }
                else if (line.Key.Span.SequenceEqual("importers"u8))
                {
                    section = PnpmSection.Importers;
                }
                else if (line.Key.Span.SequenceEqual("packages"u8))
                {
                    section = PnpmSection.Packages;
                    foundPackages = true;
                }
                else if (line.Key.Span.SequenceEqual("snapshots"u8))
                {
                    section = PnpmSection.Snapshots;
                    foundSnapshots = true;
                }
                else
                {
                    section = PnpmSection.None;
                }

                continue;
            }

            switch (section)
            {
                case PnpmSection.Importers:
                    if (line.Indent == 2)
                    {
                        EnsureCapacity(ref nodes, nodeCount);
                        currentNode = nodeCount;
                        nodes[nodeCount++] = new ResolverNode(line.Key, NodeKind.Importer, dependencyCount, 0, default);
                        importerCount++;
                        currentDependency = -1;
                    }
                    else if (line.Indent == 4 && currentNode >= 0)
                    {
                        dependencyKind = ParseDependencyKind(line.Key);
                        currentDependency = -1;
                    }
                    else if (line.Indent == 6 && currentNode >= 0 && dependencyKind != DependencyKind.None)
                    {
                        currentDependency = AddDependency(line.Key, line.HasValue ? line.Value : default, dependencyKind, currentNode, ref nodes, ref dependencies, ref dependencyCount);
                    }
                    else if (line.Indent == 8 && currentDependency >= 0 && line.Key.Span.SequenceEqual("version"u8))
                    {
                        dependencies[currentDependency] = dependencies[currentDependency] with { Resolution = line.Value };
                    }

                    break;
                case PnpmSection.Packages:
                    if (line.Indent == 2)
                    {
                        EnsureCapacity(ref packageMetadata, packageMetadataCount);
                        currentPackage = packageMetadataCount;
                        packageMetadata[packageMetadataCount++] = new PackageMetadata(line.Key, restrictionCount, 0, restrictionCount, 0);
                        restrictionKind = RestrictionKind.None;
                    }
                    else if (line.Indent == 4 && currentPackage >= 0 && (line.Key.Span.SequenceEqual("os"u8) || line.Key.Span.SequenceEqual("cpu"u8)))
                    {
                        restrictionKind = line.Key.Span.SequenceEqual("os"u8) ? RestrictionKind.Os : RestrictionKind.Cpu;
                        if (line.HasValue)
                        {
                            ReadInlineRestrictions(line.Value, restrictionKind, currentPackage, ref packageMetadata, ref restrictions, ref restrictionCount);
                            restrictionKind = RestrictionKind.None;
                        }
                    }
                    else if (line.Indent <= 4)
                    {
                        restrictionKind = RestrictionKind.None;
                    }

                    break;
                case PnpmSection.Snapshots:
                    if (line.Indent == 2)
                    {
                        EnsureCapacity(ref nodes, nodeCount);
                        currentNode = nodeCount;
                        nodes[nodeCount++] = new ResolverNode(line.Key, NodeKind.Package, dependencyCount, 0, default);
                        currentDependency = -1;
                    }
                    else if (line.Indent == 4 && currentNode >= 0)
                    {
                        dependencyKind = ParseDependencyKind(line.Key);
                        currentDependency = -1;
                    }
                    else if (line.Indent == 6 && currentNode >= 0 && dependencyKind != DependencyKind.None)
                    {
                        AddDependency(line.Key, line.Value, dependencyKind, currentNode, ref nodes, ref dependencies, ref dependencyCount);
                    }

                    break;
            }
        }

        if (specificationVersion.IsEmpty || !foundPackages || !foundSnapshots)
        {
            throw new JsonException("pnpm lockfile 9.0 requires lockfileVersion, packages, and snapshots.");
        }
    }

    private static int AddDependency(
        Utf8Slice name,
        Utf8Slice resolution,
        DependencyKind kind,
        int nodeIndex,
        ref ResolverNode[] nodes,
        ref ResolverDependency[] dependencies,
        ref int dependencyCount)
    {
        EnsureCapacity(ref dependencies, dependencyCount);
        var dependencyIndex = dependencyCount;
        dependencies[dependencyCount++] = new ResolverDependency(name, resolution, kind);
        var node = nodes[nodeIndex];
        nodes[nodeIndex] = node with { DependencyCount = node.DependencyCount + 1 };
        return dependencyIndex;
    }

    private static DependencyKind ParseDependencyKind(Utf8Slice key)
        => key.Span.SequenceEqual("dependencies"u8) ? DependencyKind.Normal
        : key.Span.SequenceEqual("optionalDependencies"u8) ? DependencyKind.Optional
        : key.Span.SequenceEqual("devDependencies"u8) ? DependencyKind.Dev
        : DependencyKind.None;

    private static void Traverse(
        ReadOnlySpan<ResolverNode> nodes,
        ReadOnlySpan<ResolverDependency> dependencies,
        ReadOnlySpan<int> snapshotIndexes,
        int snapshotIndexCapacity,
        int importerCount,
        int rootNodeIndex,
        Span<int> depths,
        Span<byte> reachKinds,
        Span<int> queue)
    {
        depths.Fill(int.MinValue);
        reachKinds.Clear();
        depths[rootNodeIndex] = -1;
        reachKinds[rootNodeIndex] = (byte)ReachKind.Production;
        var head = 0;
        var tail = 0;
        queue[tail++] = rootNodeIndex;
        while (head < tail)
        {
            var nodeIndex = queue[head++];
            var node = nodes[nodeIndex];
            for (var i = node.DependencyStart; i < node.DependencyStart + node.DependencyCount; i++)
            {
                var dependency = dependencies[i];
                if (!TryResolveNode(nodes, snapshotIndexes, snapshotIndexCapacity, importerCount, node.Identity, dependency, out var targetIndex)) continue;
                var nextDepth = depths[nodeIndex] + 1;
                var nextReach = GetReachKind((ReachKind)reachKinds[nodeIndex], dependency.Kind);
                var changed = false;
                if (depths[targetIndex] == int.MinValue || nextDepth < depths[targetIndex])
                {
                    depths[targetIndex] = nextDepth;
                    changed = true;
                }

                var mergedReach = (ReachKind)reachKinds[targetIndex] | nextReach;
                if ((byte)mergedReach != reachKinds[targetIndex])
                {
                    reachKinds[targetIndex] = (byte)mergedReach;
                    changed = true;
                }

                if (changed)
                {
                    if (tail >= queue.Length) throw new JsonException("pnpm dependency graph exceeded the bounded traversal queue.");
                    queue[tail++] = targetIndex;
                }
            }
        }
    }

    private static ReachKind GetReachKind(ReachKind source, DependencyKind dependency)
    {
        if (dependency == DependencyKind.Dev) return ReachKind.Dev;
        if (dependency == DependencyKind.Optional) return ReachKind.Optional | (source & ReachKind.Dev);
        return source;
    }

    private static void ProjectEdges(
        ReadOnlySpan<ResolverNode> nodes,
        ReadOnlySpan<ResolverDependency> dependencies,
        ReadOnlySpan<int> snapshotIndexes,
        int snapshotIndexCapacity,
        int importerCount,
        ReadOnlySpan<int> depths,
        ReadOnlySpan<int> occurrenceByNode,
        int contextIndex,
        ref DependencyEdge[] edges,
        ref int edgeCount)
    {
        for (var nodeIndex = importerCount; nodeIndex < nodes.Length; nodeIndex++)
        {
            var fromOccurrence = occurrenceByNode[nodeIndex];
            if (fromOccurrence < 0) continue;
            if (depths[nodeIndex] == 0)
            {
                EnsureCapacity(ref edges, edgeCount);
                edges[edgeCount++] = new DependencyEdge(contextIndex, DependencyOccurrence.ContextRoot, fromOccurrence);
            }

            var node = nodes[nodeIndex];
            for (var i = node.DependencyStart; i < node.DependencyStart + node.DependencyCount; i++)
            {
                if (!TryResolveNode(nodes, snapshotIndexes, snapshotIndexCapacity, importerCount, node.Identity, dependencies[i], out var targetIndex)) continue;
                var toOccurrence = occurrenceByNode[targetIndex];
                if (toOccurrence < 0 || targetIndex < importerCount) continue;
                EnsureCapacity(ref edges, edgeCount);
                edges[edgeCount++] = new DependencyEdge(contextIndex, fromOccurrence, toOccurrence);
            }
        }
    }

    private static bool TryResolveNode(
        ReadOnlySpan<ResolverNode> nodes,
        ReadOnlySpan<int> snapshotIndexes,
        int snapshotIndexCapacity,
        int importerCount,
        Utf8Slice owner,
        ResolverDependency dependency,
        out int nodeIndex)
    {
        var resolution = dependency.Resolution.Span;
        if (resolution.StartsWith("link:"u8))
        {
            return TryResolveImporter(nodes[..importerCount], owner.Span, resolution["link:"u8.Length..], out nodeIndex);
        }

        if (resolution.StartsWith("workspace:"u8) || resolution.StartsWith("file:"u8) || resolution.StartsWith("portal:"u8))
        {
            nodeIndex = -1;
            return false;
        }

        return TryGetSnapshotIndex(nodes, snapshotIndexes, snapshotIndexCapacity, dependency.Name.Span, resolution, out nodeIndex);
    }

    private static bool TryResolveImporter(ReadOnlySpan<ResolverNode> importers, ReadOnlySpan<byte> owner, ReadOnlySpan<byte> target, out int nodeIndex)
    {
        for (var i = 0; i < importers.Length; i++)
        {
            if (PathEquals(importers[i].Identity.Span, owner, target))
            {
                nodeIndex = i;
                return true;
            }
        }

        nodeIndex = -1;
        return false;
    }

    private static bool PathEquals(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> owner, ReadOnlySpan<byte> target)
    {
        if (!target.StartsWith("./"u8) && !target.StartsWith("../"u8)) return candidate.SequenceEqual(target);
        var basePath = owner;
        if (target.StartsWith("./"u8)) target = target[2..];
        while (target.StartsWith("../"u8))
        {
            target = target[3..];
            var separator = basePath.LastIndexOf((byte)'/');
            basePath = separator < 0 ? default : basePath[..separator];
        }

        if (basePath.SequenceEqual("."u8)) basePath = default;
        if (basePath.IsEmpty) return candidate.SequenceEqual(target);
        return candidate.Length == basePath.Length + 1 + target.Length
            && candidate[..basePath.Length].SequenceEqual(basePath)
            && candidate[basePath.Length] == (byte)'/'
            && candidate[(basePath.Length + 1)..].SequenceEqual(target);
    }

    private static void SplitSnapshotIdentity(Utf8Slice identity, out Utf8Slice name, out Utf8Slice version, out Utf8Slice peerSuffix, out Utf8Slice packageIdentity)
    {
        var value = identity.Span;
        var peerStart = value.IndexOf((byte)'(');
        var baseLength = peerStart < 0 ? value.Length : peerStart;
        var separator = value[..baseLength].LastIndexOf((byte)'@');
        if (separator <= 0 || separator == baseLength - 1)
        {
            throw new JsonException("pnpm snapshot identity must contain a package name and version.");
        }

        name = identity.Slice(0, separator);
        version = identity.Slice(separator + 1, baseLength - separator - 1);
        peerSuffix = peerStart < 0 ? default : identity.Slice(peerStart, value.Length - peerStart);
        packageIdentity = identity.Slice(0, baseLength);
    }

    private static int FindPackageMetadata(ReadOnlySpan<PackageMetadata> metadata, Utf8Slice identity)
    {
        for (var i = 0; i < metadata.Length; i++)
        {
            if (metadata[i].Identity.Equals(identity)) return i;
        }

        return -1;
    }

    private static Utf8Slice CreateBaseVariant(Utf8Slice peerSuffix, ReadOnlySpan<Utf8Slice> os, ReadOnlySpan<Utf8Slice> cpu)
    {
        var peerLength = GetPeerValueLength(peerSuffix.Span);
        var length = 0;
        var parts = 0;
        if (peerLength > 0) AddPartLength(5 + peerLength, ref length, ref parts);
        if (!os.IsEmpty) AddListLength(os, 3, ref length, ref parts);
        if (!cpu.IsEmpty) AddListLength(cpu, 4, ref length, ref parts);
        if (length == 0) return default;
        var bytes = new byte[length];
        var index = 0;
        var written = 0;
        if (peerLength > 0)
        {
            WriteSeparator(bytes, ref index, ref written);
            "peer="u8.CopyTo(bytes.AsSpan(index));
            index += 5;
            WritePeerValue(peerSuffix.Span, bytes, ref index);
        }

        if (!os.IsEmpty) WriteList("os="u8, os, bytes, ref index, ref written);
        if (!cpu.IsEmpty) WriteList("cpu="u8, cpu, bytes, ref index, ref written);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice ComposeOccurrenceVariant(Utf8Slice baseVariant, ReachKind reach)
    {
        var strictlyOptional = (reach & ReachKind.Production) == 0 && (reach & ReachKind.Optional) != 0;
        var strictlyDev = (reach & ReachKind.Production) == 0 && (reach & ReachKind.Dev) != 0;
        if (!strictlyOptional && !strictlyDev) return baseVariant;
        var prefixLength = (strictlyOptional ? 8 : 0) + (strictlyDev ? 3 : 0) + (strictlyOptional && strictlyDev ? 1 : 0);
        var length = prefixLength + (baseVariant.IsEmpty ? 0 : 1 + baseVariant.Length);
        var bytes = new byte[length];
        var index = 0;
        if (strictlyOptional)
        {
            "optional"u8.CopyTo(bytes);
            index = 8;
        }

        if (strictlyDev)
        {
            if (index > 0) bytes[index++] = (byte)';';
            "dev"u8.CopyTo(bytes.AsSpan(index));
            index += 3;
        }

        if (!baseVariant.IsEmpty)
        {
            bytes[index++] = (byte)';';
            baseVariant.Span.CopyTo(bytes.AsSpan(index));
        }

        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static int GetPeerValueLength(ReadOnlySpan<byte> suffix)
    {
        if (suffix.IsEmpty) return 0;
        var length = 0;
        var depth = 0;
        var groupCount = 0;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (suffix[i] == (byte)'(')
            {
                if (depth++ == 0)
                {
                    if (groupCount++ > 0) length++;
                }
                else length++;
            }
            else if (suffix[i] == (byte)')')
            {
                if (--depth > 0) length++;
            }
            else length++;
        }

        if (depth != 0) throw new JsonException("pnpm peer snapshot suffix is malformed.");
        return length;
    }

    private static void WritePeerValue(ReadOnlySpan<byte> suffix, Span<byte> destination, ref int index)
    {
        var depth = 0;
        var groupCount = 0;
        for (var i = 0; i < suffix.Length; i++)
        {
            var value = suffix[i];
            if (value == (byte)'(')
            {
                if (depth++ == 0)
                {
                    if (groupCount++ > 0) destination[index++] = (byte)',';
                }
                else destination[index++] = value;
            }
            else if (value == (byte)')')
            {
                if (--depth > 0) destination[index++] = value;
            }
            else destination[index++] = value;
        }
    }

    private static void ReadInlineRestrictions(Utf8Slice value, RestrictionKind kind, int packageIndex, ref PackageMetadata[] metadata, ref Utf8Slice[] restrictions, ref int restrictionCount)
    {
        var bytes = value.Span;
        if (bytes.Length < 2 || bytes[0] != (byte)'[' || bytes[^1] != (byte)']') throw new JsonException("pnpm os and cpu restrictions must be YAML sequences.");
        var start = 1;
        for (var i = 1; i <= bytes.Length - 1; i++)
        {
            if (i != bytes.Length - 1 && bytes[i] != (byte)',') continue;
            var itemStart = start;
            var itemEnd = i;
            while (itemStart < itemEnd && bytes[itemStart] == (byte)' ') itemStart++;
            while (itemEnd > itemStart && bytes[itemEnd - 1] == (byte)' ') itemEnd--;
            if (itemEnd > itemStart && (bytes[itemStart] is (byte)'\'' or (byte)'"') && bytes[itemEnd - 1] == bytes[itemStart])
            {
                itemStart++;
                itemEnd--;
            }

            if (itemEnd > itemStart) AddRestriction(value.Slice(itemStart, itemEnd - itemStart), kind, packageIndex, ref metadata, ref restrictions, ref restrictionCount);
            start = i + 1;
        }
    }

    private static void AddRestriction(Utf8Slice value, RestrictionKind kind, int packageIndex, ref PackageMetadata[] metadata, ref Utf8Slice[] restrictions, ref int restrictionCount)
    {
        EnsureCapacity(ref restrictions, restrictionCount);
        var valueIndex = restrictionCount;
        restrictions[restrictionCount++] = value;
        var item = metadata[packageIndex];
        metadata[packageIndex] = kind == RestrictionKind.Os
            ? item with { OsStart = item.OsCount == 0 ? valueIndex : item.OsStart, OsCount = item.OsCount + 1 }
            : item with { CpuStart = item.CpuCount == 0 ? valueIndex : item.CpuStart, CpuCount = item.CpuCount + 1 };
    }

    private static void AddPartLength(int valueLength, ref int length, ref int parts)
    {
        length = checked(length + valueLength + (parts == 0 ? 0 : 1));
        parts++;
    }

    private static void AddListLength(ReadOnlySpan<Utf8Slice> values, int prefixLength, ref int length, ref int parts)
    {
        var valueLength = prefixLength + values.Length - 1;
        for (var i = 0; i < values.Length; i++) valueLength = checked(valueLength + values[i].Length);
        AddPartLength(valueLength, ref length, ref parts);
    }

    private static void WriteList(ReadOnlySpan<byte> prefix, ReadOnlySpan<Utf8Slice> values, Span<byte> destination, ref int index, ref int parts)
    {
        WriteSeparator(destination, ref index, ref parts);
        prefix.CopyTo(destination[index..]);
        index += prefix.Length;
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) destination[index++] = (byte)',';
            values[i].Span.CopyTo(destination[index..]);
            index += values[i].Length;
        }
    }

    private static void WriteSeparator(Span<byte> destination, ref int index, ref int parts)
    {
        if (parts++ > 0) destination[index++] = (byte)';';
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

    private static int GetIndexCapacity(int count)
    {
        var capacity = 2;
        while (capacity < count * 2) capacity *= 2;
        return capacity;
    }

    private static bool AddSnapshotIndex(ReadOnlySpan<ResolverNode> nodes, Span<int> indexes, int capacity, int nodeIndex)
    {
        var slot = (int)(Hash(nodes[nodeIndex].Identity.Span) & (uint)(capacity - 1));
        while (indexes[slot] >= 0)
        {
            if (nodes[indexes[slot]].Identity.Equals(nodes[nodeIndex].Identity)) return false;
            slot = (slot + 1) & (capacity - 1);
        }

        indexes[slot] = nodeIndex;
        return true;
    }

    private static bool TryGetSnapshotIndex(ReadOnlySpan<ResolverNode> nodes, ReadOnlySpan<int> indexes, int capacity, ReadOnlySpan<byte> name, ReadOnlySpan<byte> resolution, out int nodeIndex)
    {
        var hash = Hash(name);
        hash = Hash("@"u8, hash);
        hash = Hash(resolution, hash);
        var slot = (int)(hash & (uint)(capacity - 1));
        var expectedLength = name.Length + 1 + resolution.Length;
        while ((nodeIndex = indexes[slot]) >= 0)
        {
            var identity = nodes[nodeIndex].Identity.Span;
            if (identity.Length == expectedLength
                && identity[..name.Length].SequenceEqual(name)
                && identity[name.Length] == (byte)'@'
                && identity[(name.Length + 1)..].SequenceEqual(resolution)) return true;
            slot = (slot + 1) & (capacity - 1);
        }

        return false;
    }

    private static uint Hash(ReadOnlySpan<byte> value, uint hash = 2166136261)
    {
        for (var i = 0; i < value.Length; i++) hash = (hash ^ value[i]) * 16777619;
        return hash;
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length) return;
        var expanded = ArrayPool<T>.Shared.Rent(values.Length * 2);
        values.AsSpan(0, count).CopyTo(expanded);
        ArrayPool<T>.Shared.Return(values, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        values = expanded;
    }

    private readonly record struct ResolverNode(Utf8Slice Identity, NodeKind Kind, int DependencyStart, int DependencyCount, Utf8Slice BaseVariant);
    private readonly record struct ResolverDependency(Utf8Slice Name, Utf8Slice Resolution, DependencyKind Kind);
    private readonly record struct PackageMetadata(Utf8Slice Identity, int OsStart, int OsCount, int CpuStart, int CpuCount);

    private enum PnpmSection : byte { None, Importers, Packages, Snapshots }
    private enum NodeKind : byte { Importer, Package }
    private enum RestrictionKind : byte { None, Os, Cpu }
    private enum DependencyKind : byte { None, Normal, Optional, Dev }
    [Flags]
    private enum ReachKind : byte { None = 0, Production = 1, Dev = 2, Optional = 4 }
}
