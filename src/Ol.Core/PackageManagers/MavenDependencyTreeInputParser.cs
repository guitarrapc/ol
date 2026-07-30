using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class MavenDependencyTreeInputParser
{
    private const byte GroupIdField = 1 << 0;
    private const byte ArtifactIdField = 1 << 1;
    private const byte VersionField = 1 << 2;
    private const byte TypeField = 1 << 3;
    private const byte ScopeField = 1 << 4;
    private const byte ClassifierField = 1 << 5;
    private const byte OptionalField = 1 << 6;
    private const byte RequiredFields = GroupIdField | ArtifactIdField | VersionField | TypeField | ScopeField | ClassifierField | OptionalField;
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:maven/"u8;

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph)
    {
        var nodes = ArrayPool<MavenNode>.Shared.Rent(16);
        var nodeCount = 0;
        try
        {
            var reader = new Utf8JsonReader(source.AsSpan(offset), new JsonReaderOptions { MaxDepth = 64 });
            RequireToken(ref reader, JsonTokenType.StartObject, "Maven dependency tree must be a JSON object.");
            ReadNode(ref reader, source, offset, parentIndex: -1, depth: 0, ref nodes, ref nodeCount);
            if (reader.Read()) throw new JsonException("Maven dependency tree must contain one root object.");
            return CreateInventory(nodes.AsSpan(0, nodeCount), retainGraph);
        }
        finally
        {
            ArrayPool<MavenNode>.Shared.Return(nodes, clearArray: true);
        }
    }

    private static int ReadNode(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        int parentIndex,
        int depth,
        ref MavenNode[] nodes,
        ref int nodeCount)
    {
        EnsureCapacity(ref nodes, nodeCount);
        var nodeIndex = nodeCount++;
        var node = new MavenNode { ParentIndex = parentIndex, Depth = depth };
        byte fields = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (fields != RequiredFields
                    || node.GroupId.IsEmpty
                    || node.ArtifactId.IsEmpty
                    || node.Version.IsEmpty
                    || node.Type.IsEmpty)
                {
                    throw new JsonException("Maven dependency tree nodes require non-empty groupId, artifactId, version, and type plus scope, classifier, and optional strings.");
                }

                ValidateCoordinate(node);
                nodes[nodeIndex] = node;
                return nodeIndex;
            }

            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Maven dependency tree nodes must contain JSON properties.");
            if (reader.ValueTextEquals("groupId"u8))
            {
                RequireUnique(ref fields, GroupIdField);
                node.GroupId = ReadString(ref reader, source, offset);
            }
            else if (reader.ValueTextEquals("artifactId"u8))
            {
                RequireUnique(ref fields, ArtifactIdField);
                node.ArtifactId = ReadString(ref reader, source, offset);
            }
            else if (reader.ValueTextEquals("version"u8))
            {
                RequireUnique(ref fields, VersionField);
                node.Version = ReadString(ref reader, source, offset);
            }
            else if (reader.ValueTextEquals("type"u8))
            {
                RequireUnique(ref fields, TypeField);
                node.Type = ReadString(ref reader, source, offset);
            }
            else if (reader.ValueTextEquals("scope"u8))
            {
                RequireUnique(ref fields, ScopeField);
                node.Scope = ReadString(ref reader, source, offset);
            }
            else if (reader.ValueTextEquals("classifier"u8))
            {
                RequireUnique(ref fields, ClassifierField);
                node.Classifier = ReadString(ref reader, source, offset);
            }
            else if (reader.ValueTextEquals("optional"u8))
            {
                RequireUnique(ref fields, OptionalField);
                var optional = ReadString(ref reader, source, offset);
                if (optional.Span.SequenceEqual("true"u8)) node.Optional = true;
                else if (!optional.Span.SequenceEqual("false"u8)) throw new JsonException("Maven dependency tree optional values must be \"true\" or \"false\".");
            }
            else if (reader.ValueTextEquals("children"u8))
            {
                RequireToken(ref reader, JsonTokenType.StartArray, "Maven dependency tree children must be an array.");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Maven dependency tree children must be objects.");
                    ReadNode(ref reader, source, offset, nodeIndex, depth + 1, ref nodes, ref nodeCount);
                }

                RequireCurrentToken(ref reader, JsonTokenType.EndArray, "Maven dependency tree children array is incomplete.");
            }
            else
            {
                RequireRead(ref reader, "Maven dependency tree property must have a value.");
                SkipCurrent(ref reader);
            }
        }

        throw new JsonException("Maven dependency tree node is incomplete.");
    }

    private static DependencyInventory CreateInventory(ReadOnlySpan<MavenNode> nodes, bool retainGraph)
    {
        if (nodes.IsEmpty) throw new JsonException("Maven dependency tree must contain a root project.");
        var componentCapacity = Math.Max(1, nodes.Length - 1);
        var components = ArrayPool<ScanComponent>.Shared.Rent(componentCapacity);
        var componentByNode = ArrayPool<int>.Shared.Rent(nodes.Length);
        var componentIndexes = ArrayPool<int>.Shared.Rent(GetIndexCapacity(nodes.Length));
        DependencyOccurrence[]? occurrences = null;
        DependencyOccurrenceVariant[]? variants = null;
        DependencyEdge[]? edges = null;
        int[]? developmentOccurrences = null;
        var componentCount = 0;
        var variantCount = 0;
        var developmentOccurrenceCount = 0;
        try
        {
            var indexCapacity = GetIndexCapacity(nodes.Length);
            componentIndexes.AsSpan(0, indexCapacity).Fill(-1);
            componentByNode[0] = DependencyOccurrence.ContextRoot;
            for (var nodeIndex = 1; nodeIndex < nodes.Length; nodeIndex++)
            {
                var node = nodes[nodeIndex];
                var slot = (int)(HashIdentity(node) & (uint)(indexCapacity - 1));
                while (componentIndexes[slot] >= 0 && !IdentityEquals(nodes[componentIndexes[slot]], node))
                {
                    slot = (slot + 1) & (indexCapacity - 1);
                }

                if (componentIndexes[slot] >= 0)
                {
                    var componentIndex = componentByNode[componentIndexes[slot]];
                    componentByNode[nodeIndex] = componentIndex;
                    if (node.Depth == 1 && components[componentIndex].DependencyType != DependencyType.Direct)
                    {
                        components[componentIndex] = components[componentIndex] with { DependencyType = DependencyType.Direct };
                    }
                }
                else
                {
                    componentIndexes[slot] = nodeIndex;
                    componentByNode[nodeIndex] = componentCount;
                    components[componentCount] = new ScanComponent(
                        node.ArtifactId,
                        node.Version,
                        default,
                        "maven",
                        node.Depth == 1 ? DependencyType.Direct : DependencyType.Transitive,
                        LicenseStatus.Unknown,
                        CreatePurl(node),
                        CreateSourceId(node),
                        default,
                        [],
                        []);
                    componentCount++;
                }
            }

            if (retainGraph)
            {
                occurrences = ArrayPool<DependencyOccurrence>.Shared.Rent(componentCapacity);
                variants = ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(componentCapacity);
                edges = ArrayPool<DependencyEdge>.Shared.Rent(componentCapacity);
                developmentOccurrences = ArrayPool<int>.Shared.Rent(componentCapacity);
                for (var nodeIndex = 1; nodeIndex < nodes.Length; nodeIndex++)
                {
                    var occurrenceIndex = nodeIndex - 1;
                    occurrences[occurrenceIndex] = new DependencyOccurrence(0, componentByNode[nodeIndex]);

                    // Maven resolves one effective scope per tree position; `test` is the only scope that is never
                    // part of a production build. `provided`/`system`/`optional` stay runtime (conservative).
                    if (nodes[nodeIndex].Scope.Span.SequenceEqual("test"u8))
                    {
                        developmentOccurrences[developmentOccurrenceCount++] = occurrenceIndex;
                    }

                    var variant = CreateVariant(nodes[nodeIndex].Scope, nodes[nodeIndex].Optional);
                    if (!variant.IsEmpty)
                    {
                        variants[variantCount++] = new DependencyOccurrenceVariant(occurrenceIndex, variant);
                    }

                    var parent = nodes[nodeIndex].ParentIndex;
                    var from = parent == 0 ? DependencyOccurrence.ContextRoot : parent - 1;
                    edges[occurrenceIndex] = new DependencyEdge(0, from, occurrenceIndex);
                }
            }

            var root = nodes[0];
            var occurrenceCount = nodes.Length - 1;
            return new DependencyInventory(
                new ScanInputDescriptor(default, default, string.Empty, string.Empty, default),
                [new DependencyResolutionContext(CreateProjectOrigin(root.GroupId, root.ArtifactId), default, default, default, default, default)],
                components.AsSpan(0, componentCount).ToArray(),
                retainGraph ? occurrences!.AsSpan(0, occurrenceCount).ToArray() : [],
                retainGraph ? edges!.AsSpan(0, occurrenceCount).ToArray() : [],
                retainGraph && variantCount != 0 ? variants!.AsSpan(0, variantCount).ToArray() : [],
                retainGraph && occurrenceCount > 0 ? [new DependencyUsageRange(0, occurrenceCount)] : null,
                retainGraph && developmentOccurrenceCount != 0 ? developmentOccurrences!.AsSpan(0, developmentOccurrenceCount).ToArray() : null);
        }
        finally
        {
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            if (occurrences is not null) ArrayPool<DependencyOccurrence>.Shared.Return(occurrences);
            if (variants is not null) ArrayPool<DependencyOccurrenceVariant>.Shared.Return(variants, clearArray: true);
            if (edges is not null) ArrayPool<DependencyEdge>.Shared.Return(edges);
            if (developmentOccurrences is not null) ArrayPool<int>.Shared.Return(developmentOccurrences);
            ArrayPool<int>.Shared.Return(componentByNode);
            ArrayPool<int>.Shared.Return(componentIndexes);
        }
    }

    private static Utf8Slice CreateProjectOrigin(Utf8Slice groupId, Utf8Slice artifactId)
    {
        var bytes = new byte[checked(groupId.Length + 1 + artifactId.Length)];
        groupId.Span.CopyTo(bytes);
        bytes[groupId.Length] = (byte)':';
        artifactId.Span.CopyTo(bytes.AsSpan(groupId.Length + 1));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateSourceId(MavenNode node)
    {
        var length = checked(node.GroupId.Length + node.ArtifactId.Length + node.Type.Length + node.Classifier.Length + node.Version.Length + 4);
        var bytes = new byte[length];
        var index = 0;
        WritePart(node.GroupId.Span, bytes, ref index);
        bytes[index++] = (byte)':';
        WritePart(node.ArtifactId.Span, bytes, ref index);
        bytes[index++] = (byte)':';
        WritePart(node.Type.Span, bytes, ref index);
        bytes[index++] = (byte)':';
        WritePart(node.Classifier.Span, bytes, ref index);
        bytes[index++] = (byte)':';
        WritePart(node.Version.Span, bytes, ref index);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreatePurl(MavenNode node)
    {
        var groupLength = GetEncodedLength(node.GroupId.Span);
        var artifactLength = GetEncodedLength(node.ArtifactId.Span);
        var versionLength = GetEncodedLength(node.Version.Span);
        var hasClassifier = !node.Classifier.IsEmpty;
        var hasType = !node.Type.Span.SequenceEqual("jar"u8);
        var qualifierLength = (hasClassifier ? "?classifier=".Length + GetEncodedLength(node.Classifier.Span) : 0)
            + (hasType ? 1 + "type=".Length + GetEncodedLength(node.Type.Span) : 0);
        var bytes = new byte[checked(PurlPrefix.Length + groupLength + 1 + artifactLength + 1 + versionLength + qualifierLength)];
        PurlPrefix.CopyTo(bytes);
        var index = PurlPrefix.Length;
        WriteEncoded(node.GroupId.Span, bytes, ref index);
        bytes[index++] = (byte)'/';
        WriteEncoded(node.ArtifactId.Span, bytes, ref index);
        bytes[index++] = (byte)'@';
        WriteEncoded(node.Version.Span, bytes, ref index);
        if (hasClassifier)
        {
            "?classifier="u8.CopyTo(bytes.AsSpan(index));
            index += "?classifier=".Length;
            WriteEncoded(node.Classifier.Span, bytes, ref index);
        }

        if (hasType)
        {
            bytes[index++] = hasClassifier ? (byte)'&' : (byte)'?';
            "type="u8.CopyTo(bytes.AsSpan(index));
            index += "type=".Length;
            WriteEncoded(node.Type.Span, bytes, ref index);
        }

        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateVariant(Utf8Slice scope, bool optional)
    {
        if (scope.IsEmpty && !optional) return default;
        var length = (scope.IsEmpty ? 0 : "scope=".Length + scope.Length) + (optional ? (scope.IsEmpty ? 0 : 1) + "optional".Length : 0);
        var bytes = new byte[length];
        var index = 0;
        if (!scope.IsEmpty)
        {
            "scope="u8.CopyTo(bytes);
            index = "scope=".Length;
            scope.Span.CopyTo(bytes.AsSpan(index));
            index += scope.Length;
        }

        if (optional)
        {
            if (index != 0) bytes[index++] = (byte)';';
            "optional"u8.CopyTo(bytes.AsSpan(index));
        }

        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static void ValidateCoordinate(MavenNode node)
    {
        if (node.GroupId.Span.Contains((byte)':')
            || node.ArtifactId.Span.Contains((byte)':')
            || node.Version.Span.Contains((byte)':')
            || node.Type.Span.Contains((byte)':')
            || node.Classifier.Span.Contains((byte)':')
            || node.Scope.Span.Contains((byte)';'))
        {
            throw new JsonException("Maven dependency tree coordinates contain unsupported separators.");
        }
    }

    private static bool IdentityEquals(MavenNode left, MavenNode right)
        => left.GroupId.Equals(right.GroupId)
        && left.ArtifactId.Equals(right.ArtifactId)
        && left.Version.Equals(right.Version)
        && left.Type.Equals(right.Type)
        && left.Classifier.Equals(right.Classifier);

    private static uint HashIdentity(MavenNode node)
    {
        var hash = 2166136261u;
        Hash(node.GroupId.Span, ref hash);
        Hash(node.ArtifactId.Span, ref hash);
        Hash(node.Version.Span, ref hash);
        Hash(node.Type.Span, ref hash);
        Hash(node.Classifier.Span, ref hash);
        return hash;
    }

    private static void Hash(ReadOnlySpan<byte> value, ref uint hash)
    {
        for (var index = 0; index < value.Length; index++) hash = (hash ^ value[index]) * 16777619;
        hash = (hash ^ 0xff) * 16777619;
    }

    private static int GetIndexCapacity(int count)
    {
        if (count > 1 << 29) throw new JsonException("Maven dependency tree contains too many nodes.");
        var capacity = 2;
        while (capacity < count * 2) capacity *= 2;
        return capacity;
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
            if (IsPurlSafe(item))
            {
                destination[index++] = item;
            }
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

    private static void WritePart(ReadOnlySpan<byte> value, Span<byte> destination, ref int index)
    {
        value.CopyTo(destination[index..]);
        index += value.Length;
    }

    private static Utf8Slice ReadString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireRead(ref reader, "Maven dependency tree string field must have a value.");
        RequireCurrentToken(ref reader, JsonTokenType.String, "Maven dependency tree fields must use their documented JSON types.");
        if (reader.HasValueSequence || reader.ValueIsEscaped) return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static void RequireUnique(ref byte fields, byte field)
    {
        if ((fields & field) != 0) throw new JsonException("Maven dependency tree nodes cannot repeat coordinate properties.");
        fields |= field;
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

    private static void SkipCurrent(ref Utf8JsonReader reader)
    {
        if (!reader.TrySkip()) throw new JsonException("Maven dependency tree contains an incomplete JSON value.");
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length) return;
        var expanded = ArrayPool<T>.Shared.Rent(values.Length * 2);
        values.AsSpan(0, count).CopyTo(expanded);
        ArrayPool<T>.Shared.Return(values, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        values = expanded;
    }

    private struct MavenNode
    {
        public Utf8Slice GroupId;
        public Utf8Slice ArtifactId;
        public Utf8Slice Version;
        public Utf8Slice Type;
        public Utf8Slice Scope;
        public Utf8Slice Classifier;
        public int ParentIndex;
        public int Depth;
        public bool Optional;
    }
}
