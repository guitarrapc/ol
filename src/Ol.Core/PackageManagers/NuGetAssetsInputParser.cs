using System.Buffers;
using System.Text.Json;

namespace Ol.Core;

internal static class NuGetAssetsInputParser
{
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:nuget/"u8;

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex _, bool retainGraph)
    {
        var directDependencies = ArrayPool<DirectDependency>.Shared.Rent(8);
        var contexts = ArrayPool<DependencyResolutionContext>.Shared.Rent(4);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var occurrences = ArrayPool<DependencyOccurrence>.Shared.Rent(16);
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(32);
        var packageLibraries = ArrayPool<Utf8Slice>.Shared.Rent(16);
        var directCount = 0;
        var contextCount = 0;
        var componentCount = 0;
        var occurrenceCount = 0;
        var edgeCount = 0;
        var packageLibraryCount = 0;
        try
        {
            ReadProject(source, offset, ref directDependencies, ref directCount, out var specificationVersion, out var projectOrigin);
            ReadPackageLibraries(source, offset, ref packageLibraries, ref packageLibraryCount);
            ReadTargets(
                source,
                offset,
                packageLibraries.AsSpan(0, packageLibraryCount),
                directDependencies.AsSpan(0, directCount),
                projectOrigin,
                ref contexts,
                ref contextCount,
                ref components,
                ref componentCount,
                ref occurrences,
                ref occurrenceCount,
                ref edges,
                ref edgeCount);

            if (contextCount == 0)
            {
                throw new JsonException("NuGet project.assets.json must contain at least one target object.");
            }

            var ownedContexts = contexts.AsSpan(0, contextCount).ToArray();
            var ownedComponents = components.AsSpan(0, componentCount).ToArray();
            var ownedOccurrences = retainGraph ? occurrences.AsSpan(0, occurrenceCount).ToArray() : [];
            var ownedEdges = retainGraph ? edges.AsSpan(0, edgeCount).ToArray() : [];
            return new DependencyInventory(
                new ScanInputDescriptor(default, default, string.Empty, string.Empty, specificationVersion),
                ownedContexts,
                ownedComponents,
                ownedOccurrences,
                ownedEdges);
        }
        finally
        {
            ArrayPool<DirectDependency>.Shared.Return(directDependencies, clearArray: true);
            ArrayPool<DependencyResolutionContext>.Shared.Return(contexts, clearArray: true);
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyOccurrence>.Shared.Return(occurrences);
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            ArrayPool<Utf8Slice>.Shared.Return(packageLibraries, clearArray: true);
        }
    }

    private static void ReadProject(
        byte[] source,
        int offset,
        ref DirectDependency[] directDependencies,
        ref int directCount,
        out Utf8Slice specificationVersion,
        out Utf8Slice projectOrigin)
    {
        specificationVersion = default;
        projectOrigin = default;
        var reader = CreateReader(source, offset);
        RequireToken(ref reader, JsonTokenType.StartObject, "NuGet project.assets.json root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("NuGet project.assets.json contains an invalid root property.");
            }

            if (reader.ValueTextEquals("version"u8))
            {
                reader.Read();
                specificationVersion = CreateScalarSlice(ref reader, source, offset);
                if (!specificationVersion.Span.SequenceEqual("3"u8) && !specificationVersion.Span.SequenceEqual("4"u8))
                {
                    throw new JsonException("NuGet project.assets.json version must be 3 or 4.");
                }
            }
            else if (reader.ValueTextEquals("project"u8))
            {
                reader.Read();
                ReadProjectObject(ref reader, source, offset, ref directDependencies, ref directCount, ref projectOrigin);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (specificationVersion.IsEmpty || projectOrigin.IsEmpty)
        {
            throw new JsonException("NuGet project.assets.json is missing version or project restore metadata.");
        }
    }

    private static void ReadProjectObject(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        ref DirectDependency[] directDependencies,
        ref int directCount,
        ref Utf8Slice projectOrigin)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet project must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("NuGet project contains an invalid property.");
            }

            if (reader.ValueTextEquals("restore"u8))
            {
                reader.Read();
                ReadRestore(ref reader, source, offset, ref projectOrigin);
            }
            else if (reader.ValueTextEquals("frameworks"u8))
            {
                reader.Read();
                ReadFrameworks(ref reader, source, offset, ref directDependencies, ref directCount);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }

    private static void ReadRestore(ref Utf8JsonReader reader, byte[] source, int offset, ref Utf8Slice projectOrigin)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet project restore must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("NuGet project restore contains an invalid property.");
            }

            if (reader.ValueTextEquals("projectPath"u8))
            {
                projectOrigin = ReadString(ref reader, source, offset);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }

    private static void ReadFrameworks(ref Utf8JsonReader reader, byte[] source, int offset, ref DirectDependency[] directDependencies, ref int directCount)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet project frameworks must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet project framework name is invalid.");
            var framework = CreateValueSlice(ref reader, source, offset);
            reader.Read();
            RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet project framework must be an object.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet project framework property is invalid.");
                if (!reader.ValueTextEquals("dependencies"u8))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                reader.Read();
                RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet project framework dependencies must be an object.");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet project dependency name is invalid.");
                    EnsureCapacity(ref directDependencies, directCount);
                    directDependencies[directCount++] = new DirectDependency(framework, CreateValueSlice(ref reader, source, offset));
                    reader.Read();
                    reader.Skip();
                }
            }
        }
    }

    private static void ReadPackageLibraries(byte[] source, int offset, ref Utf8Slice[] packages, ref int packageCount)
    {
        var reader = CreateReader(source, offset);
        RequireToken(ref reader, JsonTokenType.StartObject, "NuGet project.assets.json root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet project.assets.json contains an invalid root property.");
            if (!reader.ValueTextEquals("libraries"u8))
            {
                reader.Read();
                reader.Skip();
                continue;
            }

            reader.Read();
            RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet libraries must be an object.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet library identity is invalid.");
                var identity = CreateValueSlice(ref reader, source, offset);
                reader.Read();
                RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet library must be an object.");
                var isPackage = false;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet library property is invalid.");
                    if (reader.ValueTextEquals("type"u8))
                    {
                        reader.Read();
                        isPackage = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("package"u8);
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                if (isPackage)
                {
                    EnsureCapacity(ref packages, packageCount);
                    packages[packageCount++] = identity;
                }
            }

            Array.Sort(packages, 0, packageCount, NuGetIdentityComparer.Instance);
            return;
        }

        throw new JsonException("NuGet project.assets.json is missing libraries.");
    }

    private static void ReadTargets(
        byte[] source,
        int offset,
        ReadOnlySpan<Utf8Slice> packageLibraries,
        ReadOnlySpan<DirectDependency> directDependencies,
        Utf8Slice projectOrigin,
        ref DependencyResolutionContext[] contexts,
        ref int contextCount,
        ref ScanComponent[] components,
        ref int componentCount,
        ref DependencyOccurrence[] occurrences,
        ref int occurrenceCount,
        ref DependencyEdge[] edges,
        ref int edgeCount)
    {
        var reader = CreateReader(source, offset);
        RequireToken(ref reader, JsonTokenType.StartObject, "NuGet project.assets.json root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet project.assets.json contains an invalid root property.");
            if (!reader.ValueTextEquals("targets"u8))
            {
                reader.Read();
                reader.Skip();
                continue;
            }

            reader.Read();
            RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet targets must be an object.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet target name is invalid.");
                var targetIdentity = CreateValueSlice(ref reader, source, offset);
                SplitTarget(targetIdentity, out var target, out var runtime);
                EnsureCapacity(ref contexts, contextCount);
                var contextIndex = contextCount;
                contexts[contextCount++] = new DependencyResolutionContext(projectOrigin, target, runtime, default, default, default);
                reader.Read();
                ReadTarget(
                    ref reader,
                    source,
                    offset,
                    packageLibraries,
                    directDependencies,
                    contextIndex,
                    target,
                    ref components,
                    ref componentCount,
                    ref occurrences,
                    ref occurrenceCount,
                    ref edges,
                    ref edgeCount);
            }

            return;
        }

        throw new JsonException("NuGet project.assets.json is missing targets.");
    }

    private static void ReadTarget(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        ReadOnlySpan<Utf8Slice> packageLibraries,
        ReadOnlySpan<DirectDependency> directDependencies,
        int contextIndex,
        Utf8Slice target,
        ref ScanComponent[] components,
        ref int componentCount,
        ref DependencyOccurrence[] occurrences,
        ref int occurrenceCount,
        ref DependencyEdge[] edges,
        ref int edgeCount)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet target must be an object.");
        var nodes = ArrayPool<TargetNode>.Shared.Rent(16);
        var dependencies = ArrayPool<NodeDependency>.Shared.Rent(32);
        var nodeCount = 0;
        var dependencyCount = 0;
        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet target library identity is invalid.");
                var identity = CreateValueSlice(ref reader, source, offset);
                SplitLibraryIdentity(identity, out var name, out var version);
                reader.Read();
                RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet target library must be an object.");
                var dependencyStart = dependencyCount;
                var kind = TargetNodeKind.Unknown;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet target library property is invalid.");
                    if (reader.ValueTextEquals("type"u8))
                    {
                        reader.Read();
                        kind = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("package"u8)
                            ? TargetNodeKind.Package
                            : reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("project"u8)
                                ? TargetNodeKind.Project
                                : TargetNodeKind.Unknown;
                    }
                    else if (reader.ValueTextEquals("dependencies"u8))
                    {
                        reader.Read();
                        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "NuGet target library dependencies must be an object.");
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "NuGet target dependency name is invalid.");
                            EnsureCapacity(ref dependencies, dependencyCount);
                            dependencies[dependencyCount++] = new NodeDependency(CreateValueSlice(ref reader, source, offset));
                            reader.Read();
                            reader.Skip();
                        }
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                if (kind == TargetNodeKind.Package && !ContainsPackageLibrary(packageLibraries, identity))
                {
                    kind = TargetNodeKind.Unknown;
                }

                EnsureCapacity(ref nodes, nodeCount);
                nodes[nodeCount++] = new TargetNode(identity, name, version, kind, dependencyStart, dependencyCount - dependencyStart);
            }

            ProjectTarget(
                nodes.AsSpan(0, nodeCount),
                dependencies.AsSpan(0, dependencyCount),
                directDependencies,
                contextIndex,
                target,
                ref components,
                ref componentCount,
                ref occurrences,
                ref occurrenceCount,
                ref edges,
                ref edgeCount);
        }
        finally
        {
            ArrayPool<TargetNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<NodeDependency>.Shared.Return(dependencies, clearArray: true);
        }
    }

    private static void ProjectTarget(
        ReadOnlySpan<TargetNode> nodes,
        ReadOnlySpan<NodeDependency> dependencies,
        ReadOnlySpan<DirectDependency> directDependencies,
        int contextIndex,
        Utf8Slice target,
        ref ScanComponent[] components,
        ref int componentCount,
        ref DependencyOccurrence[] occurrences,
        ref int occurrenceCount,
        ref DependencyEdge[] edges,
        ref int edgeCount)
    {
        var nodeIndexCapacity = GetNodeIndexCapacity(nodes.Length);
        var nodeIndexes = ArrayPool<int>.Shared.Rent(nodeIndexCapacity);
        nodeIndexes.AsSpan(0, nodeIndexCapacity).Fill(-1);
        for (var i = 0; i < nodes.Length; i++)
        {
            AddNodeIndex(nodes, nodeIndexes, nodeIndexCapacity, i);
        }

        var depths = ArrayPool<int>.Shared.Rent(Math.Max(nodes.Length, 1));
        var queue = ArrayPool<int>.Shared.Rent(Math.Max(nodes.Length, 1));
        var occurrenceByNode = ArrayPool<int>.Shared.Rent(Math.Max(nodes.Length, 1));
        try
        {
            depths.AsSpan(0, nodes.Length).Fill(-1);
            occurrenceByNode.AsSpan(0, nodes.Length).Fill(-1);
            var queueHead = 0;
            var queueTail = 0;
            for (var i = 0; i < directDependencies.Length; i++)
            {
                var direct = directDependencies[i];
                if (!NuGetIdentityComparer.Instance.Equals(direct.Framework, target) || !TryGetNodeIndex(nodes, nodeIndexes, nodeIndexCapacity, direct.Name, out var nodeIndex) || depths[nodeIndex] >= 0)
                {
                    continue;
                }

                depths[nodeIndex] = 0;
                queue[queueTail++] = nodeIndex;
            }

            while (queueHead < queueTail)
            {
                var nodeIndex = queue[queueHead++];
                var node = nodes[nodeIndex];
                for (var dependencyIndex = node.DependencyStart; dependencyIndex < node.DependencyStart + node.DependencyCount; dependencyIndex++)
                {
                    if (!TryGetNodeIndex(nodes, nodeIndexes, nodeIndexCapacity, dependencies[dependencyIndex].Name, out var targetNodeIndex) || depths[targetNodeIndex] >= 0)
                    {
                        continue;
                    }

                    depths[targetNodeIndex] = depths[nodeIndex] + 1;
                    queue[queueTail++] = targetNodeIndex;
                }
            }

            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                if (node.Kind != TargetNodeKind.Package)
                {
                    continue;
                }

                var dependencyType = depths[i] switch
                {
                    0 => DependencyType.Direct,
                    > 0 => DependencyType.Transitive,
                    _ => DependencyType.Unknown,
                };
                EnsureCapacity(ref components, componentCount);
                EnsureCapacity(ref occurrences, occurrenceCount);
                occurrenceByNode[i] = occurrenceCount;
                components[componentCount] = new ScanComponent(node.Name, node.Version, default, "nuget", dependencyType, LicenseStatus.Unknown, CreatePurl(node.Name, node.Version), node.Identity, default, [], []);
                occurrences[occurrenceCount++] = new DependencyOccurrence(contextIndex, componentCount++);
            }

            for (var i = 0; i < nodes.Length; i++)
            {
                var fromOccurrence = occurrenceByNode[i];
                if (fromOccurrence < 0)
                {
                    continue;
                }

                if (depths[i] == 0)
                {
                    EnsureCapacity(ref edges, edgeCount);
                    edges[edgeCount++] = new DependencyEdge(contextIndex, DependencyOccurrence.ContextRoot, fromOccurrence);
                }

                var node = nodes[i];
                for (var dependencyIndex = node.DependencyStart; dependencyIndex < node.DependencyStart + node.DependencyCount; dependencyIndex++)
                {
                    if (!TryGetNodeIndex(nodes, nodeIndexes, nodeIndexCapacity, dependencies[dependencyIndex].Name, out var targetNodeIndex))
                    {
                        continue;
                    }

                    var toOccurrence = occurrenceByNode[targetNodeIndex];
                    if (toOccurrence < 0)
                    {
                        continue;
                    }

                    EnsureCapacity(ref edges, edgeCount);
                    edges[edgeCount++] = new DependencyEdge(contextIndex, fromOccurrence, toOccurrence);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(depths);
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<int>.Shared.Return(occurrenceByNode);
            ArrayPool<int>.Shared.Return(nodeIndexes);
        }
    }

    private static bool ContainsPackageLibrary(ReadOnlySpan<Utf8Slice> packages, Utf8Slice identity)
    {
        var low = 0;
        var high = packages.Length - 1;
        while (low <= high)
        {
            var middle = (int)((uint)(low + high) >> 1);
            var comparison = NuGetIdentityComparer.Instance.Compare(packages[middle], identity);
            if (comparison == 0)
            {
                return true;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return false;
    }

    private static int GetNodeIndexCapacity(int nodeCount)
    {
        var capacity = 2;
        while (capacity < nodeCount * 2)
        {
            capacity *= 2;
        }

        return capacity;
    }

    private static void AddNodeIndex(ReadOnlySpan<TargetNode> nodes, Span<int> indexes, int capacity, int nodeIndex)
    {
        var slot = NuGetIdentityComparer.Instance.GetHashCode(nodes[nodeIndex].Name) & (capacity - 1);
        while (indexes[slot] >= 0)
        {
            if (NuGetIdentityComparer.Instance.Equals(nodes[indexes[slot]].Name, nodes[nodeIndex].Name))
            {
                return;
            }

            slot = (slot + 1) & (capacity - 1);
        }

        indexes[slot] = nodeIndex;
    }

    private static bool TryGetNodeIndex(ReadOnlySpan<TargetNode> nodes, ReadOnlySpan<int> indexes, int capacity, Utf8Slice name, out int nodeIndex)
    {
        var slot = NuGetIdentityComparer.Instance.GetHashCode(name) & (capacity - 1);
        while ((nodeIndex = indexes[slot]) >= 0)
        {
            if (NuGetIdentityComparer.Instance.Equals(nodes[nodeIndex].Name, name))
            {
                return true;
            }

            slot = (slot + 1) & (capacity - 1);
        }

        nodeIndex = -1;
        return false;
    }

    private static Utf8Slice CreatePurl(Utf8Slice name, Utf8Slice version)
    {
        var prefix = PurlPrefix;
        var bytes = new byte[prefix.Length + name.Length + 1 + version.Length];
        prefix.CopyTo(bytes);
        name.Span.CopyTo(bytes.AsSpan(prefix.Length));
        bytes[prefix.Length + name.Length] = (byte)'@';
        version.Span.CopyTo(bytes.AsSpan(prefix.Length + name.Length + 1));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static void SplitTarget(Utf8Slice identity, out Utf8Slice target, out Utf8Slice runtime)
    {
        var separator = identity.Span.IndexOf((byte)'/');
        target = separator < 0 ? identity : Slice(identity, 0, separator);
        runtime = separator < 0 ? default : Slice(identity, separator + 1, identity.Length - separator - 1);
    }

    private static void SplitLibraryIdentity(Utf8Slice identity, out Utf8Slice name, out Utf8Slice version)
    {
        var separator = identity.Span.LastIndexOf((byte)'/');
        if (separator <= 0 || separator == identity.Length - 1)
        {
            throw new JsonException("NuGet target library identity must contain a name and version.");
        }

        name = Slice(identity, 0, separator);
        version = Slice(identity, separator + 1, identity.Length - separator - 1);
    }

    private static Utf8Slice Slice(Utf8Slice value, int start, int length)
        => value.Slice(start, length);

    private static Utf8JsonReader CreateReader(byte[] source, int offset)
        => new(source.AsSpan(offset), isFinalBlock: true, state: default);

    private static Utf8Slice ReadString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected a string value in NuGet project.assets.json.");
        }

        return CreateValueSlice(ref reader, source, offset);
    }

    private static Utf8Slice CreateValueSlice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped)
        {
            return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        }

        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static Utf8Slice CreateScalarSlice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return CreateValueSlice(ref reader, source, offset);
        }

        if (reader.TokenType != JsonTokenType.Number || reader.HasValueSequence)
        {
            throw new JsonException("NuGet project.assets.json version must be a number or string.");
        }

        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex), reader.ValueSpan.Length);
    }

    private static void RequireToken(ref Utf8JsonReader reader, JsonTokenType expected, string message)
    {
        if (!reader.Read() || reader.TokenType != expected)
        {
            throw new JsonException(message);
        }
    }

    private static void RequireCurrentToken(ref Utf8JsonReader reader, JsonTokenType expected, string message)
    {
        if (reader.TokenType != expected)
        {
            throw new JsonException(message);
        }
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length)
        {
            return;
        }

        var expanded = ArrayPool<T>.Shared.Rent(values.Length * 2);
        values.AsSpan(0, count).CopyTo(expanded);
        ArrayPool<T>.Shared.Return(values, clearArray: true);
        values = expanded;
    }

    private readonly record struct DirectDependency(Utf8Slice Framework, Utf8Slice Name);
    private readonly record struct NodeDependency(Utf8Slice Name);
    private readonly record struct TargetNode(Utf8Slice Identity, Utf8Slice Name, Utf8Slice Version, TargetNodeKind Kind, int DependencyStart, int DependencyCount);

    private enum TargetNodeKind : byte
    {
        Unknown,
        Package,
        Project,
    }

    private sealed class NuGetIdentityComparer : IEqualityComparer<Utf8Slice>, IComparer<Utf8Slice>
    {
        internal static NuGetIdentityComparer Instance { get; } = new();

        public bool Equals(Utf8Slice left, Utf8Slice right)
        {
            var leftValue = left.Span;
            var rightValue = right.Span;
            if (leftValue.Length != rightValue.Length)
            {
                return false;
            }

            for (var i = 0; i < leftValue.Length; i++)
            {
                if (ToLowerAscii(leftValue[i]) != ToLowerAscii(rightValue[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(Utf8Slice value)
        {
            var hash = new HashCode();
            foreach (var item in value.Span)
            {
                hash.Add(ToLowerAscii(item));
            }

            return hash.ToHashCode();
        }

        public int Compare(Utf8Slice left, Utf8Slice right)
        {
            var leftValue = left.Span;
            var rightValue = right.Span;
            var length = Math.Min(leftValue.Length, rightValue.Length);
            for (var i = 0; i < length; i++)
            {
                var comparison = ToLowerAscii(leftValue[i]).CompareTo(ToLowerAscii(rightValue[i]));
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return leftValue.Length.CompareTo(rightValue.Length);
        }

        private static byte ToLowerAscii(byte value)
            => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ('a' - 'A')) : value;
    }
}
