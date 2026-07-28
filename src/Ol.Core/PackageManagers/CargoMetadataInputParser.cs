using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class CargoMetadataInputParser
{
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:cargo/"u8;
    private static ReadOnlySpan<byte> CratesIoGitIndex => "registry+https://github.com/rust-lang/crates.io-index"u8;
    private static ReadOnlySpan<byte> CratesIoSparseIndex => "sparse+https://index.crates.io/"u8;
    private static readonly LicenseEvidence PackageLicenseEvidence = new(
        LicenseEvidenceKind.DependencyInput,
        DependencyInput: new DependencyInputEvidence("cargo-metadata", "packages[].license"));

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph)
    {
        var nodes = ArrayPool<PackageNode>.Shared.Rent(16);
        var dependencies = ArrayPool<CargoDependency>.Shared.Rent(32);
        var dependencyKinds = ArrayPool<DependencyKind>.Shared.Rent(32);
        var features = ArrayPool<Utf8Slice>.Shared.Rent(32);
        var workspaceIndexes = ArrayPool<int>.Shared.Rent(4);
        var contexts = ArrayPool<DependencyResolutionContext>.Shared.Rent(4);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var occurrences = ArrayPool<DependencyOccurrence>.Shared.Rent(32);
        var occurrenceVariants = ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(16);
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(32);
        var uniqueKinds = ArrayPool<Utf8Slice>.Shared.Rent(4);
        var uniqueTargets = ArrayPool<Utf8Slice>.Shared.Rent(4);
        int[]? nodeIndexes = null;
        int[]? componentByNode = null;
        int[]? depths = null;
        int[]? queue = null;
        int[]? occurrenceByNode = null;
        int[]? firstIncoming = null;
        int[]? nextIncoming = null;
        var nodeCount = 0;
        var dependencyCount = 0;
        var dependencyKindCount = 0;
        var featureCount = 0;
        var workspaceCount = 0;
        var contextCount = 0;
        var componentCount = 0;
        var occurrenceCount = 0;
        var occurrenceVariantCount = 0;
        var edgeCount = 0;
        try
        {
            ReadPackages(source, offset, ref nodes, ref nodeCount);
            if (nodeCount == 0) throw new JsonException("Cargo metadata must contain packages.");

            var indexCapacity = GetIndexCapacity(nodeCount);
            nodeIndexes = ArrayPool<int>.Shared.Rent(indexCapacity);
            nodeIndexes.AsSpan(0, indexCapacity).Fill(-1);
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                if (!AddNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, nodeIndex))
                {
                    throw new JsonException("Cargo metadata packages cannot repeat a package id.");
                }
            }

            ReadWorkspaceMembers(
                source,
                offset,
                ref nodes,
                nodeCount,
                nodeIndexes,
                indexCapacity,
                ref workspaceIndexes,
                ref workspaceCount);
            if (workspaceCount == 0) throw new JsonException("Cargo metadata must contain workspace members.");

            ReadResolve(
                source,
                offset,
                ref nodes,
                nodeCount,
                nodeIndexes,
                indexCapacity,
                ref dependencies,
                ref dependencyCount,
                ref dependencyKinds,
                ref dependencyKindCount,
                ref features,
                ref featureCount,
                out var specificationVersion);

            componentByNode = ArrayPool<int>.Shared.Rent(nodeCount);
            componentByNode.AsSpan(0, nodeCount).Fill(-1);
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                var node = nodes[nodeIndex];
                if (node.IsWorkspace) continue;
                if (node.Name.IsEmpty || node.Version.IsEmpty)
                {
                    throw new JsonException("Cargo metadata packages must contain a name and version.");
                }

                EnsureCapacity(ref components, componentCount);
                componentByNode[nodeIndex] = componentCount;
                components[componentCount++] = CreateComponent(node, spdxLicenseIndex);
            }

            for (var workspaceOffset = 0; workspaceOffset < workspaceCount; workspaceOffset++)
            {
                var node = nodes[workspaceIndexes[workspaceOffset]];
                if (!node.HasResolveNode) throw new JsonException("Cargo workspace members must have resolve nodes.");
                EnsureCapacity(ref contexts, contextCount);
                contexts[contextCount++] = new DependencyResolutionContext(
                    node.Name,
                    default,
                    default,
                    default,
                    default,
                    CreateFeatureVariant(features.AsSpan(node.FeatureStart, node.FeatureCount)));
            }

            if (!retainGraph)
            {
                return new DependencyInventory(
                    new ScanInputDescriptor(default, default, string.Empty, string.Empty, specificationVersion),
                    contexts.AsSpan(0, contextCount).ToArray(),
                    components.AsSpan(0, componentCount).ToArray(),
                    [],
                    [],
                    []);
            }

            depths = ArrayPool<int>.Shared.Rent(nodeCount);
            queue = ArrayPool<int>.Shared.Rent(nodeCount);
            occurrenceByNode = ArrayPool<int>.Shared.Rent(nodeCount);
            firstIncoming = ArrayPool<int>.Shared.Rent(nodeCount);
            firstIncoming.AsSpan(0, nodeCount).Fill(-1);
            nextIncoming = ArrayPool<int>.Shared.Rent(Math.Max(dependencyCount, 1));
            for (var dependencyIndex = dependencyCount - 1; dependencyIndex >= 0; dependencyIndex--)
            {
                var targetIndex = dependencies[dependencyIndex].TargetIndex;
                nextIncoming[dependencyIndex] = firstIncoming[targetIndex];
                firstIncoming[targetIndex] = dependencyIndex;
            }

            for (var contextIndex = 0; contextIndex < workspaceCount; contextIndex++)
            {
                var rootNodeIndex = workspaceIndexes[contextIndex];
                Traverse(
                    nodes.AsSpan(0, nodeCount),
                    dependencies.AsSpan(0, dependencyCount),
                    rootNodeIndex,
                    depths.AsSpan(0, nodeCount),
                    queue.AsSpan(0, nodeCount));

                occurrenceByNode.AsSpan(0, nodeCount).Fill(-1);
                var includeUnknown = contextIndex == 0;
                for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
                {
                    var componentIndex = componentByNode[nodeIndex];
                    if (componentIndex < 0 || (!includeUnknown && depths[nodeIndex] == int.MinValue)) continue;

                    var dependencyType = depths[nodeIndex] switch
                    {
                        0 => DependencyType.Direct,
                        > 0 => DependencyType.Transitive,
                        _ => DependencyType.Unknown,
                    };
                    var component = components[componentIndex];
                    components[componentIndex] = component with { DependencyType = DependencyTypes.Merge(component.DependencyType, dependencyType) };
                    EnsureCapacity(ref occurrences, occurrenceCount);
                    var occurrenceIndex = occurrenceCount;
                    occurrenceByNode[nodeIndex] = occurrenceIndex;
                    occurrences[occurrenceCount++] = new DependencyOccurrence(contextIndex, componentIndex);

                    var kindCount = 0;
                    var targetCount = 0;
                    CollectIncomingConditions(
                        dependencies.AsSpan(0, dependencyCount),
                        dependencyKinds.AsSpan(0, dependencyKindCount),
                        firstIncoming[nodeIndex],
                        nextIncoming.AsSpan(0, dependencyCount),
                        depths.AsSpan(0, nodeCount),
                        ref uniqueKinds,
                        ref kindCount,
                        ref uniqueTargets,
                        ref targetCount);
                    var variant = CreateOccurrenceVariant(
                        nodes[nodeIndex],
                        features.AsSpan(nodes[nodeIndex].FeatureStart, nodes[nodeIndex].FeatureCount),
                        uniqueKinds.AsSpan(0, kindCount),
                        uniqueTargets.AsSpan(0, targetCount));
                    EnsureCapacity(ref occurrenceVariants, occurrenceVariantCount);
                    occurrenceVariants[occurrenceVariantCount++] = new DependencyOccurrenceVariant(occurrenceIndex, variant);
                }

                ProjectEdges(
                    nodes.AsSpan(0, nodeCount),
                    dependencies.AsSpan(0, dependencyCount),
                    depths.AsSpan(0, nodeCount),
                    occurrenceByNode.AsSpan(0, nodeCount),
                    rootNodeIndex,
                    contextIndex,
                    ref edges,
                    ref edgeCount);
            }

            return new DependencyInventory(
                new ScanInputDescriptor(default, default, string.Empty, string.Empty, specificationVersion),
                contexts.AsSpan(0, contextCount).ToArray(),
                components.AsSpan(0, componentCount).ToArray(),
                occurrences.AsSpan(0, occurrenceCount).ToArray(),
                edges.AsSpan(0, edgeCount).ToArray(),
                occurrenceVariants.AsSpan(0, occurrenceVariantCount).ToArray());
        }
        finally
        {
            ArrayPool<PackageNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<CargoDependency>.Shared.Return(dependencies);
            ArrayPool<DependencyKind>.Shared.Return(dependencyKinds, clearArray: true);
            ArrayPool<Utf8Slice>.Shared.Return(features, clearArray: true);
            ArrayPool<int>.Shared.Return(workspaceIndexes);
            ArrayPool<DependencyResolutionContext>.Shared.Return(contexts, clearArray: true);
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyOccurrence>.Shared.Return(occurrences);
            ArrayPool<DependencyOccurrenceVariant>.Shared.Return(occurrenceVariants, clearArray: true);
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            ArrayPool<Utf8Slice>.Shared.Return(uniqueKinds, clearArray: true);
            ArrayPool<Utf8Slice>.Shared.Return(uniqueTargets, clearArray: true);
            if (nodeIndexes is not null) ArrayPool<int>.Shared.Return(nodeIndexes);
            if (componentByNode is not null) ArrayPool<int>.Shared.Return(componentByNode);
            if (depths is not null) ArrayPool<int>.Shared.Return(depths);
            if (queue is not null) ArrayPool<int>.Shared.Return(queue);
            if (occurrenceByNode is not null) ArrayPool<int>.Shared.Return(occurrenceByNode);
            if (firstIncoming is not null) ArrayPool<int>.Shared.Return(firstIncoming);
            if (nextIncoming is not null) ArrayPool<int>.Shared.Return(nextIncoming);
        }
    }

    private static void ReadPackages(byte[] source, int offset, ref PackageNode[] nodes, ref int nodeCount)
    {
        var reader = CreateReader(source, offset);
        RequireToken(ref reader, JsonTokenType.StartObject, "Cargo metadata root must be an object.");
        var found = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Cargo metadata contains an invalid root property.");
            var isPackages = reader.ValueTextEquals("packages"u8);
            RequireRead(ref reader, "Cargo metadata root properties must have values.");
            if (!isPackages)
            {
                SkipCurrent(ref reader);
                continue;
            }

            if (found) throw new JsonException("Cargo metadata packages cannot be repeated.");
            found = true;
            RequireCurrentToken(ref reader, JsonTokenType.StartArray, "Cargo metadata packages must be an array.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                EnsureCapacity(ref nodes, nodeCount);
                nodes[nodeCount++] = ReadPackage(ref reader, source, offset);
            }
        }

        if (!found) throw new JsonException("Cargo metadata must contain packages.");
    }

    private static PackageNode ReadPackage(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Cargo metadata package entries must be objects.");
        Utf8Slice id = default;
        Utf8Slice name = default;
        Utf8Slice version = default;
        Utf8Slice license = default;
        Utf8Slice packageSource = default;
        Utf8Slice repository = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Cargo metadata package entries contain an invalid property.");
            if (reader.ValueTextEquals("id"u8)) id = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("name"u8)) name = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("version"u8)) version = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("license"u8)) license = ReadNullableString(ref reader, source, offset);
            else if (reader.ValueTextEquals("source"u8)) packageSource = ReadNullableString(ref reader, source, offset);
            else if (reader.ValueTextEquals("repository"u8)) repository = ReadNullableString(ref reader, source, offset);
            else
            {
                RequireRead(ref reader, "Cargo metadata package properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        if (id.IsEmpty || name.IsEmpty || version.IsEmpty)
        {
            throw new JsonException("Cargo metadata packages must contain id, name, and version.");
        }

        return new PackageNode(id, name, version, license, packageSource, repository, GetSourceKind(packageSource), false, false, 0, 0, 0, 0);
    }

    private static void ReadWorkspaceMembers(
        byte[] source,
        int offset,
        ref PackageNode[] nodes,
        int nodeCount,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        ref int[] workspaceIndexes,
        ref int workspaceCount)
    {
        var reader = CreateReader(source, offset);
        RequireToken(ref reader, JsonTokenType.StartObject, "Cargo metadata root must be an object.");
        var found = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Cargo metadata contains an invalid root property.");
            var isMembers = reader.ValueTextEquals("workspace_members"u8);
            RequireRead(ref reader, "Cargo metadata root properties must have values.");
            if (!isMembers)
            {
                SkipCurrent(ref reader);
                continue;
            }

            if (found) throw new JsonException("Cargo metadata workspace_members cannot be repeated.");
            found = true;
            RequireCurrentToken(ref reader, JsonTokenType.StartArray, "Cargo metadata workspace_members must be an array.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                RequireCurrentToken(ref reader, JsonTokenType.String, "Cargo workspace member ids must be strings.");
                var id = CreateValueSlice(ref reader, source, offset);
                if (!TryGetNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, id.Span, out var nodeIndex))
                {
                    throw new JsonException("Cargo workspace member ids must match packages.");
                }

                if (nodes[nodeIndex].IsWorkspace) throw new JsonException("Cargo workspace members cannot be repeated.");
                nodes[nodeIndex] = nodes[nodeIndex] with { IsWorkspace = true };
                EnsureCapacity(ref workspaceIndexes, workspaceCount);
                workspaceIndexes[workspaceCount++] = nodeIndex;
            }
        }

        if (!found) throw new JsonException("Cargo metadata must contain workspace_members.");
    }

    private static void ReadResolve(
        byte[] source,
        int offset,
        ref PackageNode[] nodes,
        int nodeCount,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        ref CargoDependency[] dependencies,
        ref int dependencyCount,
        ref DependencyKind[] dependencyKinds,
        ref int dependencyKindCount,
        ref Utf8Slice[] features,
        ref int featureCount,
        out Utf8Slice specificationVersion)
    {
        specificationVersion = default;
        var foundResolve = false;
        var foundVersion = false;
        var reader = CreateReader(source, offset);
        RequireToken(ref reader, JsonTokenType.StartObject, "Cargo metadata root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Cargo metadata contains an invalid root property.");
            if (reader.ValueTextEquals("resolve"u8))
            {
                if (foundResolve) throw new JsonException("Cargo metadata resolve cannot be repeated.");
                foundResolve = true;
                RequireRead(ref reader, "Cargo metadata resolve must have a value.");
                ReadResolveObject(ref reader, source, offset, ref nodes, nodeCount, nodeIndexes, indexCapacity, ref dependencies, ref dependencyCount, ref dependencyKinds, ref dependencyKindCount, ref features, ref featureCount);
            }
            else if (reader.ValueTextEquals("version"u8))
            {
                if (foundVersion) throw new JsonException("Cargo metadata version cannot be repeated.");
                foundVersion = true;
                RequireRead(ref reader, "Cargo metadata version must have a value.");
                if (reader.TokenType != JsonTokenType.Number || reader.HasValueSequence || !reader.ValueSpan.SequenceEqual("1"u8))
                {
                    throw new JsonException("Only Cargo metadata format version 1 is supported.");
                }

                specificationVersion = CreateNumberSlice(ref reader, source, offset);
            }
            else
            {
                RequireRead(ref reader, "Cargo metadata root properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        if (!foundResolve || !foundVersion) throw new JsonException("Cargo metadata must contain resolve and version 1.");
    }

    private static void ReadResolveObject(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        ref PackageNode[] nodes,
        int nodeCount,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        ref CargoDependency[] dependencies,
        ref int dependencyCount,
        ref DependencyKind[] dependencyKinds,
        ref int dependencyKindCount,
        ref Utf8Slice[] features,
        ref int featureCount)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Cargo metadata resolve must be an object; metadata produced with --no-deps is not resolved.");
        var foundNodes = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Cargo metadata resolve contains an invalid property.");
            var isNodes = reader.ValueTextEquals("nodes"u8);
            RequireRead(ref reader, "Cargo metadata resolve properties must have values.");
            if (!isNodes)
            {
                SkipCurrent(ref reader);
                continue;
            }

            if (foundNodes) throw new JsonException("Cargo metadata resolve nodes cannot be repeated.");
            foundNodes = true;
            RequireCurrentToken(ref reader, JsonTokenType.StartArray, "Cargo metadata resolve nodes must be an array.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                ReadResolveNode(ref reader, source, offset, ref nodes, nodeCount, nodeIndexes, indexCapacity, ref dependencies, ref dependencyCount, ref dependencyKinds, ref dependencyKindCount, ref features, ref featureCount);
            }
        }

        if (!foundNodes) throw new JsonException("Cargo metadata resolve must contain nodes.");
    }

    private static void ReadResolveNode(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        ref PackageNode[] nodes,
        int nodeCount,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        ref CargoDependency[] dependencies,
        ref int dependencyCount,
        ref DependencyKind[] dependencyKinds,
        ref int dependencyKindCount,
        ref Utf8Slice[] features,
        ref int featureCount)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Cargo resolve node entries must be objects.");
        Utf8Slice id = default;
        var dependencyStart = dependencyCount;
        var featureStart = featureCount;
        var foundDeps = false;
        var foundFeatures = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Cargo resolve nodes contain an invalid property.");
            if (reader.ValueTextEquals("id"u8)) id = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("deps"u8))
            {
                if (foundDeps) throw new JsonException("Cargo resolve node deps cannot be repeated.");
                foundDeps = true;
                RequireRead(ref reader, "Cargo resolve node deps must have a value.");
                RequireCurrentToken(ref reader, JsonTokenType.StartArray, "Cargo resolve node deps must be an array.");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    EnsureCapacity(ref dependencies, dependencyCount);
                    dependencies[dependencyCount++] = ReadDependency(ref reader, source, offset, nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, ref dependencyKinds, ref dependencyKindCount);
                }
            }
            else if (reader.ValueTextEquals("features"u8))
            {
                if (foundFeatures) throw new JsonException("Cargo resolve node features cannot be repeated.");
                foundFeatures = true;
                RequireRead(ref reader, "Cargo resolve node features must have a value.");
                RequireCurrentToken(ref reader, JsonTokenType.StartArray, "Cargo resolve node features must be an array.");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    RequireCurrentToken(ref reader, JsonTokenType.String, "Cargo resolved features must be strings.");
                    EnsureCapacity(ref features, featureCount);
                    features[featureCount++] = CreateValueSlice(ref reader, source, offset);
                }
            }
            else
            {
                RequireRead(ref reader, "Cargo resolve node properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        if (id.IsEmpty || !foundDeps || !foundFeatures)
        {
            throw new JsonException("Cargo resolve nodes must contain id, deps, and features.");
        }

        if (!TryGetNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, id.Span, out var nodeIndex))
        {
            throw new JsonException("Cargo resolve node ids must match packages.");
        }

        if (nodes[nodeIndex].HasResolveNode) throw new JsonException("Cargo resolve nodes cannot repeat a package id.");
        for (var dependencyIndex = dependencyStart; dependencyIndex < dependencyCount; dependencyIndex++)
        {
            dependencies[dependencyIndex] = dependencies[dependencyIndex] with { OwnerIndex = nodeIndex };
        }

        nodes[nodeIndex] = nodes[nodeIndex] with
        {
            HasResolveNode = true,
            DependencyStart = dependencyStart,
            DependencyCount = dependencyCount - dependencyStart,
            FeatureStart = featureStart,
            FeatureCount = featureCount - featureStart,
        };
    }

    private static CargoDependency ReadDependency(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        ReadOnlySpan<PackageNode> nodes,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        ref DependencyKind[] dependencyKinds,
        ref int dependencyKindCount)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Cargo dependency entries must be objects.");
        Utf8Slice packageId = default;
        var kindStart = dependencyKindCount;
        var foundKinds = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Cargo dependency entries contain an invalid property.");
            if (reader.ValueTextEquals("pkg"u8)) packageId = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("dep_kinds"u8))
            {
                if (foundKinds) throw new JsonException("Cargo dependency dep_kinds cannot be repeated.");
                foundKinds = true;
                RequireRead(ref reader, "Cargo dependency dep_kinds must have a value.");
                RequireCurrentToken(ref reader, JsonTokenType.StartArray, "Cargo dependency dep_kinds must be an array.");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    EnsureCapacity(ref dependencyKinds, dependencyKindCount);
                    dependencyKinds[dependencyKindCount++] = ReadDependencyKind(ref reader, source, offset);
                }
            }
            else
            {
                RequireRead(ref reader, "Cargo dependency properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        if (packageId.IsEmpty || !foundKinds) throw new JsonException("Cargo dependencies must contain pkg and dep_kinds.");
        if (!TryGetNodeIndex(nodes, nodeIndexes, indexCapacity, packageId.Span, out var targetIndex))
        {
            throw new JsonException("Cargo dependency package ids must match packages.");
        }

        return new CargoDependency(-1, targetIndex, kindStart, dependencyKindCount - kindStart);
    }

    private static DependencyKind ReadDependencyKind(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Cargo dependency kind entries must be objects.");
        Utf8Slice kind = default;
        Utf8Slice target = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Cargo dependency kinds contain an invalid property.");
            if (reader.ValueTextEquals("kind"u8)) kind = ReadNullableString(ref reader, source, offset);
            else if (reader.ValueTextEquals("target"u8)) target = ReadNullableString(ref reader, source, offset);
            else
            {
                RequireRead(ref reader, "Cargo dependency kind properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        return new DependencyKind(kind, target);
    }

    private static void Traverse(ReadOnlySpan<PackageNode> nodes, ReadOnlySpan<CargoDependency> dependencies, int rootNodeIndex, Span<int> depths, Span<int> queue)
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
            for (var dependencyIndex = node.DependencyStart; dependencyIndex < node.DependencyStart + node.DependencyCount; dependencyIndex++)
            {
                var targetIndex = dependencies[dependencyIndex].TargetIndex;
                if (depths[targetIndex] != int.MinValue) continue;
                depths[targetIndex] = depths[nodeIndex] + 1;
                queue[tail++] = targetIndex;
            }
        }
    }

    private static void ProjectEdges(
        ReadOnlySpan<PackageNode> nodes,
        ReadOnlySpan<CargoDependency> dependencies,
        ReadOnlySpan<int> depths,
        ReadOnlySpan<int> occurrenceByNode,
        int rootNodeIndex,
        int contextIndex,
        ref DependencyEdge[] edges,
        ref int edgeCount)
    {
        for (var ownerIndex = 0; ownerIndex < nodes.Length; ownerIndex++)
        {
            if (depths[ownerIndex] == int.MinValue) continue;
            var fromOccurrence = ownerIndex == rootNodeIndex ? DependencyOccurrence.ContextRoot : occurrenceByNode[ownerIndex];
            if (fromOccurrence < 0 && ownerIndex != rootNodeIndex) continue;
            var owner = nodes[ownerIndex];
            for (var dependencyIndex = owner.DependencyStart; dependencyIndex < owner.DependencyStart + owner.DependencyCount; dependencyIndex++)
            {
                var toOccurrence = occurrenceByNode[dependencies[dependencyIndex].TargetIndex];
                if (toOccurrence < 0) continue;
                EnsureCapacity(ref edges, edgeCount);
                edges[edgeCount++] = new DependencyEdge(contextIndex, fromOccurrence, toOccurrence);
            }
        }
    }

    private static void CollectIncomingConditions(
        ReadOnlySpan<CargoDependency> dependencies,
        ReadOnlySpan<DependencyKind> dependencyKinds,
        int firstDependency,
        ReadOnlySpan<int> nextIncoming,
        ReadOnlySpan<int> depths,
        ref Utf8Slice[] uniqueKinds,
        ref int kindCount,
        ref Utf8Slice[] uniqueTargets,
        ref int targetCount)
    {
        for (var dependencyIndex = firstDependency; dependencyIndex >= 0; dependencyIndex = nextIncoming[dependencyIndex])
        {
            var dependency = dependencies[dependencyIndex];
            if (depths[dependency.OwnerIndex] == int.MinValue) continue;
            for (var kindIndex = dependency.KindStart; kindIndex < dependency.KindStart + dependency.KindCount; kindIndex++)
            {
                var dependencyKind = dependencyKinds[kindIndex];
                AddUnique(dependencyKind.Kind, ref uniqueKinds, ref kindCount);
                AddUnique(dependencyKind.Target, ref uniqueTargets, ref targetCount);
            }
        }
    }

    private static void AddUnique(Utf8Slice value, ref Utf8Slice[] values, ref int count)
    {
        if (value.IsEmpty) return;
        for (var index = 0; index < count; index++)
        {
            if (values[index].Equals(value)) return;
        }

        EnsureCapacity(ref values, count);
        values[count++] = value;
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
            "cargo",
            DependencyType.Unknown,
            status,
            node.SourceKind == PackageSourceKind.CratesIo ? CreatePurl(node.Name, node.Version) : default,
            node.Id,
            candidate,
            [],
            candidate.Warnings.ToStrings(),
            node.Repository);
    }

    private static Utf8Slice CreateFeatureVariant(ReadOnlySpan<Utf8Slice> features)
        => features.IsEmpty ? default : CreateVariant(default, features, default, default, contextOnly: true);

    private static Utf8Slice CreateOccurrenceVariant(PackageNode node, ReadOnlySpan<Utf8Slice> features, ReadOnlySpan<Utf8Slice> kinds, ReadOnlySpan<Utf8Slice> targets)
        => CreateVariant(GetSourceName(node.SourceKind), features, kinds, targets, contextOnly: false);

    private static Utf8Slice CreateVariant(ReadOnlySpan<byte> sourceName, ReadOnlySpan<Utf8Slice> features, ReadOnlySpan<Utf8Slice> kinds, ReadOnlySpan<Utf8Slice> targets, bool contextOnly)
    {
        var length = contextOnly ? 0 : "source="u8.Length + sourceName.Length;
        var partCount = contextOnly ? 0 : 1;
        AddListLength(features, "features="u8.Length, ref length, ref partCount);
        AddListLength(kinds, "kind="u8.Length, ref length, ref partCount);
        AddListLength(targets, "target="u8.Length, ref length, ref partCount);
        if (length == 0) return default;
        var bytes = new byte[length];
        var index = 0;
        var writtenParts = 0;
        if (!contextOnly) WritePart("source="u8, sourceName, bytes, ref index, ref writtenParts);
        WriteList("features="u8, features, bytes, ref index, ref writtenParts);
        WriteList("kind="u8, kinds, bytes, ref index, ref writtenParts);
        WriteList("target="u8, targets, bytes, ref index, ref writtenParts);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static void AddListLength(ReadOnlySpan<Utf8Slice> values, int prefixLength, ref int length, ref int partCount)
    {
        if (values.IsEmpty) return;
        if (partCount++ > 0) length++;
        length = checked(length + prefixLength + values.Length - 1);
        for (var index = 0; index < values.Length; index++) length = checked(length + values[index].Length);
    }

    private static void WritePart(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> value, Span<byte> destination, ref int index, ref int partCount)
    {
        if (partCount++ > 0) destination[index++] = (byte)';';
        prefix.CopyTo(destination[index..]);
        index += prefix.Length;
        value.CopyTo(destination[index..]);
        index += value.Length;
    }

    private static void WriteList(ReadOnlySpan<byte> prefix, ReadOnlySpan<Utf8Slice> values, Span<byte> destination, ref int index, ref int partCount)
    {
        if (values.IsEmpty) return;
        if (partCount++ > 0) destination[index++] = (byte)';';
        prefix.CopyTo(destination[index..]);
        index += prefix.Length;
        for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
        {
            if (valueIndex > 0) destination[index++] = (byte)',';
            values[valueIndex].Span.CopyTo(destination[index..]);
            index += values[valueIndex].Length;
        }
    }

    private static ReadOnlySpan<byte> GetSourceName(PackageSourceKind sourceKind) => sourceKind switch
    {
        PackageSourceKind.CratesIo or PackageSourceKind.Registry => "registry"u8,
        PackageSourceKind.Git => "git"u8,
        PackageSourceKind.Path => "path"u8,
        _ => "other"u8,
    };

    private static PackageSourceKind GetSourceKind(Utf8Slice source)
    {
        if (source.IsEmpty) return PackageSourceKind.Path;
        if (source.Span.SequenceEqual(CratesIoGitIndex) || source.Span.SequenceEqual(CratesIoSparseIndex)) return PackageSourceKind.CratesIo;
        if (source.Span.StartsWith("registry+"u8) || source.Span.StartsWith("sparse+"u8)) return PackageSourceKind.Registry;
        if (source.Span.StartsWith("git+"u8)) return PackageSourceKind.Git;
        return PackageSourceKind.Other;
    }

    private static Utf8Slice CreatePurl(Utf8Slice name, Utf8Slice version)
    {
        var nameLength = GetEncodedLength(name.Span);
        var versionLength = GetEncodedLength(version.Span);
        var bytes = new byte[PurlPrefix.Length + nameLength + 1 + versionLength];
        PurlPrefix.CopyTo(bytes);
        var index = PurlPrefix.Length;
        WriteEncoded(name.Span, bytes, ref index);
        bytes[index++] = (byte)'@';
        WriteEncoded(version.Span, bytes, ref index);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static int GetEncodedLength(ReadOnlySpan<byte> value)
    {
        var length = 0;
        for (var index = 0; index < value.Length; index++) length += IsPurlSafe(value[index]) ? 1 : 3;
        return length;
    }

    private static void WriteEncoded(ReadOnlySpan<byte> value, Span<byte> destination, ref int index)
    {
        const string Hex = "0123456789ABCDEF";
        for (var valueIndex = 0; valueIndex < value.Length; valueIndex++)
        {
            var item = value[valueIndex];
            if (IsPurlSafe(item)) destination[index++] = item;
            else
            {
                destination[index++] = (byte)'%';
                destination[index++] = (byte)Hex[item >> 4];
                destination[index++] = (byte)Hex[item & 0x0F];
            }
        }
    }

    private static bool IsPurlSafe(byte value)
        => value is >= (byte)'a' and <= (byte)'z'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    private static int GetIndexCapacity(int count)
    {
        var capacity = 2;
        while (capacity < count * 2) capacity *= 2;
        return capacity;
    }

    private static bool AddNodeIndex(ReadOnlySpan<PackageNode> nodes, Span<int> indexes, int capacity, int nodeIndex)
    {
        var slot = (int)(Hash(nodes[nodeIndex].Id.Span) & (uint)(capacity - 1));
        while (indexes[slot] >= 0)
        {
            if (nodes[indexes[slot]].Id.Equals(nodes[nodeIndex].Id)) return false;
            slot = (slot + 1) & (capacity - 1);
        }

        indexes[slot] = nodeIndex;
        return true;
    }

    private static bool TryGetNodeIndex(ReadOnlySpan<PackageNode> nodes, ReadOnlySpan<int> indexes, int capacity, ReadOnlySpan<byte> id, out int nodeIndex)
    {
        var slot = (int)(Hash(id) & (uint)(capacity - 1));
        while ((nodeIndex = indexes[slot]) >= 0)
        {
            if (nodes[nodeIndex].Id.Span.SequenceEqual(id)) return true;
            slot = (slot + 1) & (capacity - 1);
        }

        return false;
    }

    private static uint Hash(ReadOnlySpan<byte> value)
    {
        var hash = 2166136261u;
        for (var index = 0; index < value.Length; index++) hash = (hash ^ value[index]) * 16777619;
        return hash;
    }

    private static Utf8Slice ReadString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireRead(ref reader, "Cargo metadata string fields must have values.");
        RequireCurrentToken(ref reader, JsonTokenType.String, "Cargo metadata fields must use their documented JSON types.");
        return CreateValueSlice(ref reader, source, offset);
    }

    private static Utf8Slice ReadNullableString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireRead(ref reader, "Cargo metadata nullable string fields must have values.");
        if (reader.TokenType == JsonTokenType.Null) return default;
        RequireCurrentToken(ref reader, JsonTokenType.String, "Cargo metadata fields must use their documented JSON types.");
        return CreateValueSlice(ref reader, source, offset);
    }

    private static Utf8Slice CreateValueSlice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped) return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static Utf8Slice CreateNumberSlice(ref Utf8JsonReader reader, byte[] source, int offset)
        => new(source, checked(offset + (int)reader.TokenStartIndex), reader.ValueSpan.Length);

    private static Utf8JsonReader CreateReader(byte[] source, int offset)
        => new(source.AsSpan(offset), isFinalBlock: true, state: default);

    private static void SkipCurrent(ref Utf8JsonReader reader)
    {
        if (!reader.TrySkip()) throw new JsonException("Cargo metadata contains an incomplete JSON value.");
    }

    private static void RequireToken(ref Utf8JsonReader reader, JsonTokenType expected, string message)
    {
        if (!reader.Read() || reader.TokenType != expected) throw new JsonException(message);
    }

    private static void RequireRead(ref Utf8JsonReader reader, string message)
    {
        if (!reader.Read()) throw new JsonException(message);
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
        Utf8Slice Id,
        Utf8Slice Name,
        Utf8Slice Version,
        Utf8Slice License,
        Utf8Slice Source,
        Utf8Slice Repository,
        PackageSourceKind SourceKind,
        bool IsWorkspace,
        bool HasResolveNode,
        int DependencyStart,
        int DependencyCount,
        int FeatureStart,
        int FeatureCount);

    private readonly record struct CargoDependency(int OwnerIndex, int TargetIndex, int KindStart, int KindCount);

    private readonly record struct DependencyKind(Utf8Slice Kind, Utf8Slice Target);

    private enum PackageSourceKind : byte
    {
        Path,
        CratesIo,
        Registry,
        Git,
        Other,
    }
}
