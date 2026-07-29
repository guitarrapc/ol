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
    {
        var occurrences = inventory.Occurrences;
        var edges = inventory.Edges;
        if (occurrences is null || edges is null || occurrences.Length == 0 || componentIndex < 0) return [];

        // Adjacency is rebuilt per query rather than cached: violations are few, and a persistent index
        // would add allocation to every scan for a path only the SARIF output asks for.
        var previous = new int[occurrences.Length];
        var visited = new bool[occurrences.Length];
        Array.Fill(previous, -1);

        var queue = new Queue<int>();
        for (var i = 0; i < edges.Length; i++)
        {
            if (edges[i].FromOccurrenceIndex != DependencyOccurrence.ContextRoot) continue;
            var to = edges[i].ToOccurrenceIndex;
            if ((uint)to >= (uint)occurrences.Length || visited[to]) continue;
            visited[to] = true;
            queue.Enqueue(to);
        }

        // An input without explicit root edges still classifies direct dependencies, so seed from those.
        if (queue.Count == 0)
        {
            for (var i = 0; i < occurrences.Length; i++)
            {
                var candidate = occurrences[i].ComponentIndex;
                if ((uint)candidate >= (uint)inventory.Components.Length) continue;
                if (inventory.Components[candidate].DependencyType != DependencyType.Direct) continue;
                if (visited[i]) continue;
                visited[i] = true;
                queue.Enqueue(i);
            }
        }

        while (queue.Count != 0)
        {
            var occurrenceIndex = queue.Dequeue();
            if (occurrences[occurrenceIndex].ComponentIndex == componentIndex)
            {
                return BuildPath(inventory, previous, occurrenceIndex);
            }

            for (var i = 0; i < edges.Length; i++)
            {
                if (edges[i].FromOccurrenceIndex != occurrenceIndex) continue;
                var to = edges[i].ToOccurrenceIndex;
                if ((uint)to >= (uint)occurrences.Length || visited[to]) continue;
                visited[to] = true;
                previous[to] = occurrenceIndex;
                queue.Enqueue(to);
            }
        }

        return [];
    }

    private static int[] BuildPath(in DependencyInventory inventory, int[] previous, int occurrenceIndex)
    {
        var length = 0;
        for (var cursor = occurrenceIndex; cursor >= 0; cursor = previous[cursor]) length++;

        var path = new int[length];
        var position = length - 1;
        for (var cursor = occurrenceIndex; cursor >= 0; cursor = previous[cursor])
        {
            path[position--] = inventory.Occurrences[cursor].ComponentIndex;
        }

        return path;
    }
}
