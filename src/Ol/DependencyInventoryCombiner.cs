using System.Buffers;
using Ol.Core;

internal static class DependencyInventoryCombiner
{
    public static DependencyInventory CombineNuGetAssets(ReadOnlySpan<DependencyInventory> inventories, ScanInputDescriptor input)
    {
        if (inventories.Length == 0)
        {
            throw new ArgumentException("At least one NuGet assets inventory is required.", nameof(inventories));
        }

        var contextCount = 0;
        var componentCapacity = 0;
        var occurrenceCount = 0;
        var edgeCount = 0;
        for (var i = 0; i < inventories.Length; i++)
        {
            contextCount = checked(contextCount + inventories[i].Contexts.Length);
            componentCapacity = checked(componentCapacity + inventories[i].Components.Length);
            occurrenceCount = checked(occurrenceCount + inventories[i].Occurrences.Length);
            edgeCount = checked(edgeCount + inventories[i].Edges.Length);
        }

        var contexts = new DependencyResolutionContext[contextCount];
        var occurrences = new DependencyOccurrence[occurrenceCount];
        var edges = new DependencyEdge[edgeCount];
        var components = ArrayPool<ScanComponent>.Shared.Rent(Math.Max(componentCapacity, 1));
        var componentRemap = ArrayPool<int>.Shared.Rent(Math.Max(componentCapacity, 1));
        var componentIndexes = new Dictionary<Utf8Slice, int>(componentCapacity, Utf8AsciiIgnoreCaseComparer.Instance);
        var contextOffset = 0;
        var componentOffset = 0;
        var combinedComponentCount = 0;
        var occurrenceOffset = 0;
        var edgeOffset = 0;
        try
        {
            for (var inventoryIndex = 0; inventoryIndex < inventories.Length; inventoryIndex++)
            {
                var inventory = inventories[inventoryIndex];
                inventory.Contexts.CopyTo(contexts, contextOffset);

                for (var i = 0; i < inventory.Components.Length; i++)
                {
                    var component = inventory.Components[i];
                    if (!componentIndexes.TryGetValue(component.Purl, out var combinedIndex))
                    {
                        combinedIndex = combinedComponentCount++;
                        componentIndexes.Add(component.Purl, combinedIndex);
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

                for (var i = 0; i < inventory.Edges.Length; i++)
                {
                    var edge = inventory.Edges[i];
                    edges[edgeOffset + i] = new DependencyEdge(
                        edge.ContextIndex + contextOffset,
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
                edges);
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

    private sealed class Utf8AsciiIgnoreCaseComparer : IEqualityComparer<Utf8Slice>
    {
        public static Utf8AsciiIgnoreCaseComparer Instance { get; } = new();

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
            var bytes = value.Span;
            for (var i = 0; i < bytes.Length; i++)
            {
                hash.Add(ToLowerAscii(bytes[i]));
            }

            return hash.ToHashCode();
        }

        private static byte ToLowerAscii(byte value)
            => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ((byte)'a' - (byte)'A')) : value;
    }
}
