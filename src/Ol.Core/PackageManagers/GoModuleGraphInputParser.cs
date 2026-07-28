using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class GoModuleGraphInputParser
{
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:golang/"u8;
    private static readonly Utf8Slice IndirectVariant = Utf8Slice.FromOwnedBytes("indirect"u8.ToArray());
    private static readonly Utf8Slice LocalReplacementVariant = Utf8Slice.FromOwnedBytes("replace=local"u8.ToArray());
    private static readonly Utf8Slice RetractedVariant = Utf8Slice.FromOwnedBytes("retracted"u8.ToArray());

    internal static DependencyInventory Parse(byte[][] sources, SpdxLicenseIndex _, bool retainGraph)
    {
        if (sources.Length != 2) throw new JsonException("Go module graph input requires go-list-modules.json and go-mod-graph.txt.");
        var nodes = ArrayPool<GoModuleNode>.Shared.Rent(16);
        var graphEdges = ArrayPool<GoGraphEdge>.Shared.Rent(32);
        var contexts = ArrayPool<DependencyResolutionContext>.Shared.Rent(4);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var occurrences = ArrayPool<DependencyOccurrence>.Shared.Rent(32);
        var occurrenceVariants = ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(16);
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(32);
        int[]? nodeIndexes = null;
        int[]? mainIndexes = null;
        int[]? componentByNode = null;
        int[]? depths = null;
        int[]? queue = null;
        int[]? occurrenceByNode = null;
        int[]? firstOutgoing = null;
        int[]? nextOutgoing = null;
        var nodeCount = 0;
        var graphEdgeCount = 0;
        var mainCount = 0;
        var contextCount = 0;
        var componentCount = 0;
        var occurrenceCount = 0;
        var occurrenceVariantCount = 0;
        var edgeCount = 0;
        try
        {
            ReadSelectedModules(sources[0], ref nodes, ref nodeCount);
            if (nodeCount == 0) throw new JsonException("go list -m -json all output must contain modules.");

            var indexCapacity = GetIndexCapacity(nodeCount);
            nodeIndexes = ArrayPool<int>.Shared.Rent(indexCapacity);
            nodeIndexes.AsSpan(0, indexCapacity).Fill(-1);
            mainIndexes = ArrayPool<int>.Shared.Rent(Math.Max(nodeCount, 1));
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                if (!AddNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, nodeIndex))
                {
                    throw new JsonException("go list selected modules cannot repeat a module identity.");
                }

                if (nodes[nodeIndex].Main) mainIndexes[mainCount++] = nodeIndex;
            }

            if (mainCount == 0) throw new JsonException("go list selected modules must contain at least one main module.");
            ReadGraph(sources[1], nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, ref graphEdges, ref graphEdgeCount);

            componentByNode = ArrayPool<int>.Shared.Rent(nodeCount);
            componentByNode.AsSpan(0, nodeCount).Fill(-1);
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                var node = nodes[nodeIndex];
                if (node.Main) continue;
                EnsureCapacity(ref components, componentCount);
                componentByNode[nodeIndex] = componentCount;
                components[componentCount++] = CreateComponent(node);
            }

            for (var mainOffset = 0; mainOffset < mainCount; mainOffset++)
            {
                EnsureCapacity(ref contexts, contextCount);
                contexts[contextCount++] = new DependencyResolutionContext(nodes[mainIndexes[mainOffset]].Path, default, default, default, default, default);
            }

            if (!retainGraph)
            {
                return new DependencyInventory(
                    new ScanInputDescriptor(default, default, string.Empty, string.Empty, default),
                    contexts.AsSpan(0, contextCount).ToArray(),
                    components.AsSpan(0, componentCount).ToArray(),
                    [],
                    [],
                    []);
            }

            firstOutgoing = ArrayPool<int>.Shared.Rent(nodeCount);
            firstOutgoing.AsSpan(0, nodeCount).Fill(-1);
            nextOutgoing = ArrayPool<int>.Shared.Rent(Math.Max(graphEdgeCount, 1));
            for (var graphEdgeIndex = graphEdgeCount - 1; graphEdgeIndex >= 0; graphEdgeIndex--)
            {
                var ownerIndex = graphEdges[graphEdgeIndex].OwnerIndex;
                nextOutgoing[graphEdgeIndex] = firstOutgoing[ownerIndex];
                firstOutgoing[ownerIndex] = graphEdgeIndex;
            }

            depths = ArrayPool<int>.Shared.Rent(nodeCount);
            queue = ArrayPool<int>.Shared.Rent(nodeCount);
            occurrenceByNode = ArrayPool<int>.Shared.Rent(nodeCount);
            for (var contextIndex = 0; contextIndex < mainCount; contextIndex++)
            {
                var rootNodeIndex = mainIndexes[contextIndex];
                Traverse(
                    graphEdges.AsSpan(0, graphEdgeCount),
                    firstOutgoing.AsSpan(0, nodeCount),
                    nextOutgoing.AsSpan(0, graphEdgeCount),
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
                    if (!nodes[nodeIndex].Variant.IsEmpty)
                    {
                        EnsureCapacity(ref occurrenceVariants, occurrenceVariantCount);
                        occurrenceVariants[occurrenceVariantCount++] = new DependencyOccurrenceVariant(occurrenceIndex, nodes[nodeIndex].Variant);
                    }
                }

                ProjectEdges(
                    nodes.AsSpan(0, nodeCount),
                    graphEdges.AsSpan(0, graphEdgeCount),
                    depths.AsSpan(0, nodeCount),
                    occurrenceByNode.AsSpan(0, nodeCount),
                    rootNodeIndex,
                    contextIndex,
                    ref edges,
                    ref edgeCount);
            }

            return new DependencyInventory(
                new ScanInputDescriptor(default, default, string.Empty, string.Empty, default),
                contexts.AsSpan(0, contextCount).ToArray(),
                components.AsSpan(0, componentCount).ToArray(),
                occurrences.AsSpan(0, occurrenceCount).ToArray(),
                edges.AsSpan(0, edgeCount).ToArray(),
                occurrenceVariantCount == 0 ? [] : occurrenceVariants.AsSpan(0, occurrenceVariantCount).ToArray());
        }
        finally
        {
            ArrayPool<GoModuleNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<GoGraphEdge>.Shared.Return(graphEdges);
            ArrayPool<DependencyResolutionContext>.Shared.Return(contexts, clearArray: true);
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyOccurrence>.Shared.Return(occurrences);
            ArrayPool<DependencyOccurrenceVariant>.Shared.Return(occurrenceVariants, clearArray: true);
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            if (nodeIndexes is not null) ArrayPool<int>.Shared.Return(nodeIndexes);
            if (mainIndexes is not null) ArrayPool<int>.Shared.Return(mainIndexes);
            if (componentByNode is not null) ArrayPool<int>.Shared.Return(componentByNode);
            if (depths is not null) ArrayPool<int>.Shared.Return(depths);
            if (queue is not null) ArrayPool<int>.Shared.Return(queue);
            if (occurrenceByNode is not null) ArrayPool<int>.Shared.Return(occurrenceByNode);
            if (firstOutgoing is not null) ArrayPool<int>.Shared.Return(firstOutgoing);
            if (nextOutgoing is not null) ArrayPool<int>.Shared.Return(nextOutgoing);
        }
    }

    private static void ReadSelectedModules(byte[] source, ref GoModuleNode[] nodes, ref int nodeCount)
    {
        var offset = HasUtf8Bom(source) ? 3 : 0;
        var reader = new Utf8JsonReader(
            source.AsSpan(offset),
            new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.StartObject || reader.CurrentDepth != 0)
            {
                throw new JsonException("go list -m -json all must be a sequence of module objects.");
            }

            EnsureCapacity(ref nodes, nodeCount);
            nodes[nodeCount++] = ReadModule(ref reader, source, offset);
        }
    }

    private static GoModuleNode ReadModule(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        Utf8Slice path = default;
        Utf8Slice version = default;
        Utf8Slice replacementPath = default;
        Utf8Slice replacementVersion = default;
        var main = false;
        var indirect = false;
        var hasReplacement = false;
        var retracted = false;
        var hasError = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Go selected module contains an invalid property.");
            if (reader.ValueTextEquals("Path"u8)) path = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("Version"u8)) version = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("Main"u8)) main = ReadBoolean(ref reader);
            else if (reader.ValueTextEquals("Indirect"u8)) indirect = ReadBoolean(ref reader);
            else if (reader.ValueTextEquals("Replace"u8))
            {
                RequireRead(ref reader, "Go module replacement must have a value.");
                RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Go module replacement must be an object.");
                ReadReplacement(ref reader, source, offset, out replacementPath, out replacementVersion);
                hasReplacement = true;
            }
            else if (reader.ValueTextEquals("Retracted"u8))
            {
                RequireRead(ref reader, "Go module retraction must have a value.");
                RequireCurrentToken(ref reader, JsonTokenType.StartArray, "Go module retraction must be an array.");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    RequireCurrentToken(ref reader, JsonTokenType.String, "Go module retraction entries must be strings.");
                    retracted = true;
                }
            }
            else if (reader.ValueTextEquals("Error"u8))
            {
                RequireRead(ref reader, "Go module error must have a value.");
                hasError = reader.TokenType != JsonTokenType.Null;
                SkipCurrent(ref reader);
            }
            else
            {
                RequireRead(ref reader, "Go selected module property must have a value.");
                SkipCurrent(ref reader);
            }
        }

        if (path.IsEmpty || hasError || main == !version.IsEmpty)
        {
            throw new JsonException("Go selected modules require a path, main modules without versions, and dependencies with versions and no errors.");
        }

        if (hasReplacement && replacementPath.IsEmpty)
        {
            throw new JsonException("Go module replacements require a replacement path.");
        }

        var identity = main ? path : CreateIdentity(path, version);
        var variant = CreateVariant(indirect, hasReplacement, replacementPath, replacementVersion, retracted);
        return new GoModuleNode(identity, path, version, replacementPath, replacementVersion, variant, main, hasReplacement);
    }

    private static void ReadReplacement(ref Utf8JsonReader reader, byte[] source, int offset, out Utf8Slice path, out Utf8Slice version)
    {
        path = default;
        version = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Go module replacement contains an invalid property.");
            if (reader.ValueTextEquals("Path"u8)) path = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("Version"u8)) version = ReadString(ref reader, source, offset);
            else
            {
                RequireRead(ref reader, "Go module replacement property must have a value.");
                SkipCurrent(ref reader);
            }
        }
    }

    private static void ReadGraph(
        byte[] source,
        ReadOnlySpan<GoModuleNode> nodes,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        ref GoGraphEdge[] graphEdges,
        ref int graphEdgeCount)
    {
        var position = HasUtf8Bom(source) ? 3 : 0;
        while (position < source.Length)
        {
            var lineStart = position;
            var relativeEnd = source.AsSpan(position).IndexOf((byte)'\n');
            var lineEnd = relativeEnd < 0 ? source.Length : position + relativeEnd;
            position = relativeEnd < 0 ? source.Length : lineEnd + 1;
            if (lineEnd > lineStart && source[lineEnd - 1] == (byte)'\r') lineEnd--;
            var line = source.AsSpan(lineStart, lineEnd - lineStart);
            if (line.IsEmpty) continue;
            var separator = line.IndexOf((byte)' ');
            if (separator <= 0 || separator == line.Length - 1 || line[(separator + 1)..].Contains((byte)' ') || line.Contains((byte)'\t'))
            {
                throw new JsonException("go mod graph lines must contain exactly two space-separated module identities.");
            }

            var owner = line[..separator];
            var target = line[(separator + 1)..];
            if (!IsModuleIdentity(owner) || !IsModuleIdentity(target))
            {
                throw new JsonException("go mod graph contains an invalid module identity.");
            }

            if (!TryGetNodeIndex(nodes, nodeIndexes, indexCapacity, owner, out var ownerIndex)
                || !TryGetNodeIndex(nodes, nodeIndexes, indexCapacity, target, out var targetIndex))
            {
                continue;
            }

            EnsureCapacity(ref graphEdges, graphEdgeCount);
            graphEdges[graphEdgeCount++] = new GoGraphEdge(ownerIndex, targetIndex);
        }
    }

    private static bool IsModuleIdentity(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] <= (byte)' ' || value[index] == 0x7F) return false;
        }

        var at = value.IndexOf((byte)'@');
        return at < 0 || at == value.LastIndexOf((byte)'@') && at > 0 && at < value.Length - 1;
    }

    private static void Traverse(
        ReadOnlySpan<GoGraphEdge> graphEdges,
        ReadOnlySpan<int> firstOutgoing,
        ReadOnlySpan<int> nextOutgoing,
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
            var ownerIndex = queue[head++];
            for (var graphEdgeIndex = firstOutgoing[ownerIndex]; graphEdgeIndex >= 0; graphEdgeIndex = nextOutgoing[graphEdgeIndex])
            {
                var targetIndex = graphEdges[graphEdgeIndex].TargetIndex;
                if (depths[targetIndex] != int.MinValue) continue;
                depths[targetIndex] = depths[ownerIndex] + 1;
                queue[tail++] = targetIndex;
            }
        }
    }

    private static void ProjectEdges(
        ReadOnlySpan<GoModuleNode> nodes,
        ReadOnlySpan<GoGraphEdge> graphEdges,
        ReadOnlySpan<int> depths,
        ReadOnlySpan<int> occurrenceByNode,
        int rootNodeIndex,
        int contextIndex,
        ref DependencyEdge[] edges,
        ref int edgeCount)
    {
        for (var graphEdgeIndex = 0; graphEdgeIndex < graphEdges.Length; graphEdgeIndex++)
        {
            var graphEdge = graphEdges[graphEdgeIndex];
            if (depths[graphEdge.OwnerIndex] == int.MinValue) continue;
            var toOccurrence = occurrenceByNode[graphEdge.TargetIndex];
            if (toOccurrence < 0) continue;
            var fromOccurrence = graphEdge.OwnerIndex == rootNodeIndex
                ? DependencyOccurrence.ContextRoot
                : occurrenceByNode[graphEdge.OwnerIndex];
            if ((fromOccurrence < 0 && graphEdge.OwnerIndex != rootNodeIndex)
                || (nodes[graphEdge.OwnerIndex].Main && graphEdge.OwnerIndex != rootNodeIndex)) continue;
            EnsureCapacity(ref edges, edgeCount);
            edges[edgeCount++] = new DependencyEdge(contextIndex, fromOccurrence, toOccurrence);
        }
    }

    private static ScanComponent CreateComponent(GoModuleNode node)
    {
        var purl = node.HasReplacement
            ? node.ReplacementVersion.IsEmpty ? default : CreatePurl(node.ReplacementPath, node.ReplacementVersion)
            : CreatePurl(node.Path, node.Version);
        return new ScanComponent(
            node.Path,
            node.Version,
            default,
            "golang",
            DependencyType.Unknown,
            LicenseStatus.Unknown,
            purl,
            node.Identity,
            default,
            [],
            []);
    }

    private static Utf8Slice CreateVariant(bool indirect, bool hasReplacement, Utf8Slice replacementPath, Utf8Slice replacementVersion, bool retracted)
    {
        var partCount = (indirect ? 1 : 0) + (hasReplacement ? 1 : 0) + (retracted ? 1 : 0);
        if (partCount == 0) return default;
        if (partCount == 1)
        {
            if (indirect) return IndirectVariant;
            if (retracted) return RetractedVariant;
            if (replacementVersion.IsEmpty) return LocalReplacementVariant;
        }

        var replacementLength = !hasReplacement ? 0 : replacementVersion.IsEmpty
            ? "replace=local"u8.Length
            : "replace="u8.Length + replacementPath.Length + 1 + replacementVersion.Length;
        var length = (indirect ? "indirect"u8.Length : 0)
            + replacementLength
            + (retracted ? "retracted"u8.Length : 0)
            + partCount - 1;
        var bytes = new byte[length];
        var index = 0;
        var written = 0;
        WritePart(indirect, "indirect"u8, bytes, ref index, ref written);
        if (hasReplacement)
        {
            if (written++ > 0) bytes[index++] = (byte)';';
            "replace="u8.CopyTo(bytes.AsSpan(index));
            index += "replace="u8.Length;
            if (replacementVersion.IsEmpty)
            {
                "local"u8.CopyTo(bytes.AsSpan(index));
                index += "local"u8.Length;
            }
            else
            {
                replacementPath.Span.CopyTo(bytes.AsSpan(index));
                index += replacementPath.Length;
                bytes[index++] = (byte)'@';
                replacementVersion.Span.CopyTo(bytes.AsSpan(index));
                index += replacementVersion.Length;
            }
        }

        WritePart(retracted, "retracted"u8, bytes, ref index, ref written);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static void WritePart(bool present, ReadOnlySpan<byte> value, Span<byte> destination, ref int index, ref int written)
    {
        if (!present) return;
        if (written++ > 0) destination[index++] = (byte)';';
        value.CopyTo(destination[index..]);
        index += value.Length;
    }

    private static Utf8Slice CreateIdentity(Utf8Slice path, Utf8Slice version)
    {
        var bytes = new byte[path.Length + 1 + version.Length];
        path.Span.CopyTo(bytes);
        bytes[path.Length] = (byte)'@';
        version.Span.CopyTo(bytes.AsSpan(path.Length + 1));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreatePurl(Utf8Slice path, Utf8Slice version)
    {
        var pathLength = GetEncodedLength(path.Span, allowSlash: true);
        var versionLength = GetEncodedLength(version.Span, allowSlash: false);
        var bytes = new byte[PurlPrefix.Length + pathLength + 1 + versionLength];
        PurlPrefix.CopyTo(bytes);
        var index = PurlPrefix.Length;
        WriteEncoded(path.Span, allowSlash: true, bytes, ref index);
        bytes[index++] = (byte)'@';
        WriteEncoded(version.Span, allowSlash: false, bytes, ref index);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static int GetEncodedLength(ReadOnlySpan<byte> value, bool allowSlash)
    {
        var length = 0;
        for (var index = 0; index < value.Length; index++) length += IsPurlSafe(value[index], allowSlash) ? 1 : 3;
        return length;
    }

    private static void WriteEncoded(ReadOnlySpan<byte> value, bool allowSlash, Span<byte> destination, ref int index)
    {
        const string Hex = "0123456789ABCDEF";
        for (var valueIndex = 0; valueIndex < value.Length; valueIndex++)
        {
            var item = value[valueIndex];
            if (IsPurlSafe(item, allowSlash)) destination[index++] = item;
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

    private static int GetIndexCapacity(int count)
    {
        var capacity = 2;
        while (capacity < count * 2) capacity *= 2;
        return capacity;
    }

    private static bool AddNodeIndex(ReadOnlySpan<GoModuleNode> nodes, Span<int> indexes, int capacity, int nodeIndex)
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

    private static bool TryGetNodeIndex(ReadOnlySpan<GoModuleNode> nodes, ReadOnlySpan<int> indexes, int capacity, ReadOnlySpan<byte> identity, out int nodeIndex)
    {
        var slot = (int)(Hash(identity) & (uint)(capacity - 1));
        while ((nodeIndex = indexes[slot]) >= 0)
        {
            if (nodes[nodeIndex].Identity.Span.SequenceEqual(identity)) return true;
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
        RequireRead(ref reader, "Go module string field must have a value.");
        RequireCurrentToken(ref reader, JsonTokenType.String, "Go module fields must use their documented JSON types.");
        if (reader.HasValueSequence || reader.ValueIsEscaped) return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static bool ReadBoolean(ref Utf8JsonReader reader)
    {
        RequireRead(ref reader, "Go module boolean field must have a value.");
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            _ => throw new JsonException("Go module fields must use their documented JSON types."),
        };
    }

    private static void SkipCurrent(ref Utf8JsonReader reader)
    {
        if (!reader.TrySkip()) throw new JsonException("Go module metadata contains an incomplete JSON value.");
    }

    private static void RequireRead(ref Utf8JsonReader reader, string message)
    {
        if (!reader.Read()) throw new JsonException(message);
    }

    private static void RequireCurrentToken(ref Utf8JsonReader reader, JsonTokenType expected, string message)
    {
        if (reader.TokenType != expected) throw new JsonException(message);
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> source)
        => source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF;

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length) return;
        var expanded = ArrayPool<T>.Shared.Rent(values.Length * 2);
        values.AsSpan(0, count).CopyTo(expanded);
        ArrayPool<T>.Shared.Return(values, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        values = expanded;
    }

    private readonly record struct GoModuleNode(
        Utf8Slice Identity,
        Utf8Slice Path,
        Utf8Slice Version,
        Utf8Slice ReplacementPath,
        Utf8Slice ReplacementVersion,
        Utf8Slice Variant,
        bool Main,
        bool HasReplacement);

    private readonly record struct GoGraphEdge(int OwnerIndex, int TargetIndex);
}
