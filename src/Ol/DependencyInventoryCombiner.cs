using System.Buffers;
using Ol.Core;

internal static class DependencyInventoryCombiner
{
    public static DependencyInventory Combine(ReadOnlySpan<DependencyInventory> inventories, ReadOnlySpan<DependencyInputHandler> handlers, ScanInputDescriptor input)
    {
        if (inventories.Length == 0 || handlers.Length != inventories.Length)
        {
            throw new ArgumentException("Each dependency inventory requires its registered input handler.", nameof(inventories));
        }

        var contextCount = 0;
        var componentCapacity = 0;
        var occurrenceCount = 0;
        var edgeCount = 0;
        var occurrenceVariantCount = 0;
        for (var i = 0; i < inventories.Length; i++)
        {
            contextCount = checked(contextCount + inventories[i].Contexts.Length);
            componentCapacity = checked(componentCapacity + inventories[i].Components.Length);
            occurrenceCount = checked(occurrenceCount + inventories[i].Occurrences.Length);
            edgeCount = checked(edgeCount + inventories[i].Edges.Length);
            occurrenceVariantCount = checked(occurrenceVariantCount + (inventories[i].OccurrenceVariants?.Length ?? 0));
        }

        var contexts = new DependencyResolutionContext[contextCount];
        var occurrences = new DependencyOccurrence[occurrenceCount];
        var edges = new DependencyEdge[edgeCount];
        var occurrenceVariants = new DependencyOccurrenceVariant[occurrenceVariantCount];
        var components = ArrayPool<ScanComponent>.Shared.Rent(Math.Max(componentCapacity, 1));
        var componentRemap = ArrayPool<int>.Shared.Rent(Math.Max(componentCapacity, 1));
        var componentIndexes = new Dictionary<ComponentKey, int>(componentCapacity, ComponentKeyComparer.Instance);
        var contextOffset = 0;
        var componentOffset = 0;
        var combinedComponentCount = 0;
        var occurrenceOffset = 0;
        var edgeOffset = 0;
        var occurrenceVariantOffset = 0;
        try
        {
            for (var inventoryIndex = 0; inventoryIndex < inventories.Length; inventoryIndex++)
            {
                var inventory = inventories[inventoryIndex];
                inventory.Contexts.CopyTo(contexts, contextOffset);

                for (var i = 0; i < inventory.Components.Length; i++)
                {
                    var component = inventory.Components[i];
                    var key = new ComponentKey(
                        handlers[inventoryIndex].Format.Name,
                        component.Purl,
                        component.SourceId,
                        handlers[inventoryIndex].ComponentIdentityComparison,
                        component.Purl.IsEmpty ? componentOffset + i + 1 : 0);
                    if (!componentIndexes.TryGetValue(key, out var combinedIndex))
                    {
                        combinedIndex = combinedComponentCount++;
                        componentIndexes.Add(key, combinedIndex);
                        components[combinedIndex] = component;
                    }
                    else
                    {
                        var combined = components[combinedIndex];
                        components[combinedIndex] = combined with { DependencyType = MergeDependencyType(combined.DependencyType, component.DependencyType) };
                    }

                    componentRemap[componentOffset + i] = combinedIndex;
                }

                for (var i = 0; i < inventory.Occurrences.Length; i++)
                {
                    var occurrence = inventory.Occurrences[i];
                    occurrences[occurrenceOffset + i] = new DependencyOccurrence(
                        occurrence.ContextIndex < 0 ? occurrence.ContextIndex : occurrence.ContextIndex + contextOffset,
                        componentRemap[componentOffset + occurrence.ComponentIndex]);
                }

                var inventoryOccurrenceVariants = inventory.OccurrenceVariants;
                if (inventoryOccurrenceVariants is not null)
                {
                    for (var i = 0; i < inventoryOccurrenceVariants.Length; i++)
                    {
                        var variant = inventoryOccurrenceVariants[i];
                        occurrenceVariants[occurrenceVariantOffset + i] = new DependencyOccurrenceVariant(variant.OccurrenceIndex + occurrenceOffset, variant.Value);
                    }

                    occurrenceVariantOffset += inventoryOccurrenceVariants.Length;
                }

                for (var i = 0; i < inventory.Edges.Length; i++)
                {
                    var edge = inventory.Edges[i];
                    edges[edgeOffset + i] = new DependencyEdge(
                        edge.ContextIndex < 0 ? edge.ContextIndex : edge.ContextIndex + contextOffset,
                        edge.FromOccurrenceIndex < 0 ? edge.FromOccurrenceIndex : edge.FromOccurrenceIndex + occurrenceOffset,
                        edge.ToOccurrenceIndex + occurrenceOffset);
                }

                contextOffset += inventory.Contexts.Length;
                componentOffset += inventory.Components.Length;
                occurrenceOffset += inventory.Occurrences.Length;
                edgeOffset += inventory.Edges.Length;
            }

            return new DependencyInventory(
                input,
                contexts,
                components.AsSpan(0, combinedComponentCount).ToArray(),
                occurrences,
                edges,
                occurrenceVariants);
        }
        finally
        {
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<int>.Shared.Return(componentRemap);
        }
    }

    private static DependencyType MergeDependencyType(DependencyType left, DependencyType right)
    {
        if (left == DependencyType.Root || right == DependencyType.Root)
        {
            return DependencyType.Root;
        }

        if (left == DependencyType.Direct || right == DependencyType.Direct)
        {
            return DependencyType.Direct;
        }

        return left == DependencyType.Transitive || right == DependencyType.Transitive
            ? DependencyType.Transitive
            : DependencyType.Unknown;
    }

    private readonly record struct ComponentKey(
        string Format,
        Utf8Slice Purl,
        Utf8Slice SourceId,
        DependencyComponentIdentityComparison Comparison,
        int UniqueIndex);

    private sealed class ComponentKeyComparer : IEqualityComparer<ComponentKey>
    {
        public static ComponentKeyComparer Instance { get; } = new();

        public bool Equals(ComponentKey left, ComponentKey right)
        {
            if (left.UniqueIndex != 0 || right.UniqueIndex != 0)
            {
                return left.UniqueIndex == right.UniqueIndex;
            }

            if (left.Comparison != right.Comparison || !string.Equals(left.Format, right.Format, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var leftValue = left.Purl.Span;
            var rightValue = right.Purl.Span;
            if (leftValue.Length != rightValue.Length)
            {
                return false;
            }

            var purlEquals = left.Comparison is DependencyComponentIdentityComparison.Ordinal or DependencyComponentIdentityComparison.OrdinalWithSourceId
                ? leftValue.SequenceEqual(rightValue)
                : AsciiEqualsIgnoreCase(leftValue, rightValue);
            if (!purlEquals)
            {
                return false;
            }

            return left.Comparison != DependencyComponentIdentityComparison.OrdinalWithSourceId
                || left.SourceId.Span.SequenceEqual(right.SourceId.Span);
        }

        private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            for (var i = 0; i < left.Length; i++)
            {
                if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(ComponentKey value)
        {
            if (value.UniqueIndex != 0)
            {
                return value.UniqueIndex;
            }

            var hash = new HashCode();
            hash.Add(value.Format, StringComparer.OrdinalIgnoreCase);
            var bytes = value.Purl.Span;
            for (var i = 0; i < bytes.Length; i++)
            {
                hash.Add(value.Comparison == DependencyComponentIdentityComparison.AsciiIgnoreCase ? ToLowerAscii(bytes[i]) : bytes[i]);
            }

            if (value.Comparison == DependencyComponentIdentityComparison.OrdinalWithSourceId)
            {
                hash.Add((byte)0);
                var sourceId = value.SourceId.Span;
                for (var i = 0; i < sourceId.Length; i++)
                {
                    hash.Add(sourceId[i]);
                }
            }

            return hash.ToHashCode();
        }

        private static byte ToLowerAscii(byte value)
            => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ((byte)'a' - (byte)'A')) : value;
    }
}
