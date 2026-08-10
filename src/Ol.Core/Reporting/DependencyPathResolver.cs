using System.Buffers;

namespace Ol.Core.Reporting;

/// <summary>
/// Finds the shortest path from a resolution-context root to a component.
/// </summary>
/// <remarks>
/// A policy violation is often introduced by a transitive dependency the user cannot edit directly.
/// Reporting the shortest root-to-component path names the direct dependency that has to be upgraded or
/// removed, which is the part the user can actually act on. Ol reads resolved graphs rather than
/// manifests, so this is a logical path, not a file position.
/// </remarks>
public static class DependencyPathResolver
{
    /// <summary>
    /// Returns the shortest root-to-component path as component indexes, starting at a direct dependency
    /// and ending at the requested component. Returns an empty span when no path exists.
    /// </summary>
    public static int[] FindShortestRootPath(in DependencyInventory inventory, int componentIndex)
        => BuildRootPaths(inventory).GetPath(componentIndex);

    /// <summary>
    /// Traverses the graph once and returns every component's shortest root path.
    /// </summary>
    /// <remarks>
    /// A report names the path for every unresolved component, not for one of them, and a per-component
    /// search would rescan the whole edge list each time. One breadth-first pass over an adjacency index
    /// answers all of them, which keeps a report with many findings proportional to the graph rather than
    /// to the graph times the findings.
    /// </remarks>
    public static DependencyRootPaths BuildRootPaths(in DependencyInventory inventory)
    {
        var occurrences = inventory.Occurrences;
        var edges = inventory.Edges;
        var componentCount = inventory.Components.Length;
        if (occurrences is null || edges is null || occurrences.Length == 0 || componentCount == 0)
        {
            return default;
        }

        var previous = new int[occurrences.Length];
        var reachedOccurrence = new int[componentCount];
        Array.Fill(previous, -1);
        Array.Fill(reachedOccurrence, -1);

        var visited = ArrayPool<bool>.Shared.Rent(occurrences.Length);
        var queue = ArrayPool<int>.Shared.Rent(occurrences.Length);
        var adjacencyOffsets = ArrayPool<int>.Shared.Rent(occurrences.Length + 1);
        var adjacencyTargets = ArrayPool<int>.Shared.Rent(edges.Length);
        try
        {
            visited.AsSpan(0, occurrences.Length).Clear();
            BuildAdjacency(edges, occurrences.Length, adjacencyOffsets, adjacencyTargets);

            var head = 0;
            var tail = 0;
            for (var i = 0; i < edges.Length; i++)
            {
                if (edges[i].FromOccurrenceIndex != DependencyOccurrence.ContextRoot) continue;
                var to = edges[i].ToOccurrenceIndex;
                if ((uint)to >= (uint)occurrences.Length || visited[to]) continue;
                visited[to] = true;
                queue[tail++] = to;
            }

            // An input without explicit root edges still classifies direct dependencies, so seed from those.
            if (tail == 0)
            {
                for (var i = 0; i < occurrences.Length; i++)
                {
                    var candidate = occurrences[i].ComponentIndex;
                    if ((uint)candidate >= (uint)componentCount) continue;
                    if (inventory.Components[candidate].DependencyType != DependencyType.Direct) continue;
                    if (visited[i]) continue;
                    visited[i] = true;
                    queue[tail++] = i;
                }
            }

            while (head != tail)
            {
                var occurrenceIndex = queue[head++];

                // Occurrences leave the queue in non-decreasing distance order, so the first one seen for a
                // component is on a shortest path and later occurrences of it cannot improve on that.
                var component = occurrences[occurrenceIndex].ComponentIndex;
                if ((uint)component < (uint)componentCount && reachedOccurrence[component] < 0)
                {
                    reachedOccurrence[component] = occurrenceIndex;
                }

                for (var i = adjacencyOffsets[occurrenceIndex]; i < adjacencyOffsets[occurrenceIndex + 1]; i++)
                {
                    var to = adjacencyTargets[i];
                    if (visited[to]) continue;
                    visited[to] = true;
                    previous[to] = occurrenceIndex;
                    queue[tail++] = to;
                }
            }

            return new DependencyRootPaths(occurrences, previous, reachedOccurrence);
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(visited);
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<int>.Shared.Return(adjacencyOffsets);
            ArrayPool<int>.Shared.Return(adjacencyTargets);
        }
    }

    /// <summary>Groups edges by their source occurrence so the traversal reads neighbours instead of scanning every edge.</summary>
    private static void BuildAdjacency(
        DependencyEdge[] edges,
        int occurrenceCount,
        int[] offsets,
        int[] targets)
    {
        offsets.AsSpan(0, occurrenceCount + 1).Clear();
        for (var i = 0; i < edges.Length; i++)
        {
            var from = edges[i].FromOccurrenceIndex;
            if ((uint)from >= (uint)occurrenceCount) continue;
            if ((uint)edges[i].ToOccurrenceIndex >= (uint)occurrenceCount) continue;
            offsets[from + 1]++;
        }

        for (var i = 0; i < occurrenceCount; i++)
        {
            offsets[i + 1] += offsets[i];
        }

        var cursor = ArrayPool<int>.Shared.Rent(occurrenceCount);
        try
        {
            offsets.AsSpan(0, occurrenceCount).CopyTo(cursor);
            for (var i = 0; i < edges.Length; i++)
            {
                var from = edges[i].FromOccurrenceIndex;
                var to = edges[i].ToOccurrenceIndex;
                if ((uint)from >= (uint)occurrenceCount || (uint)to >= (uint)occurrenceCount) continue;
                targets[cursor[from]++] = to;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(cursor);
        }
    }
}

/// <summary>Holds one traversal's shortest root paths for every component in an inventory.</summary>
public readonly struct DependencyRootPaths
{
    private readonly DependencyOccurrence[]? occurrences;
    private readonly int[]? previousOccurrence;
    private readonly int[]? reachedOccurrenceByComponent;

    internal DependencyRootPaths(DependencyOccurrence[] occurrences, int[] previousOccurrence, int[] reachedOccurrenceByComponent)
    {
        this.occurrences = occurrences;
        this.previousOccurrence = previousOccurrence;
        this.reachedOccurrenceByComponent = reachedOccurrenceByComponent;
    }

    /// <summary>
    /// Returns the shortest root-to-component path as component indexes, starting at a direct dependency
    /// and ending at the requested component. Returns an empty array when no path exists.
    /// </summary>
    public int[] GetPath(int componentIndex)
    {
        var reached = this.reachedOccurrenceByComponent;
        var previous = this.previousOccurrence;
        var source = this.occurrences;
        if (reached is null || previous is null || source is null) return [];
        if ((uint)componentIndex >= (uint)reached.Length) return [];

        var occurrenceIndex = reached[componentIndex];
        if (occurrenceIndex < 0) return [];

        var length = 0;
        for (var cursor = occurrenceIndex; cursor >= 0; cursor = previous[cursor]) length++;

        var path = new int[length];
        var position = length - 1;
        for (var cursor = occurrenceIndex; cursor >= 0; cursor = previous[cursor])
        {
            path[position--] = source[cursor].ComponentIndex;
        }

        return path;
    }
}
