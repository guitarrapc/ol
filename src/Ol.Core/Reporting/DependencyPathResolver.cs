using System.Buffers;
using System.Numerics;

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
    /// Traverses the graph once and returns every component's shortest root path.
    /// </summary>
    /// <remarks>
    /// A report names the path for every unresolved component, not for one of them, and a per-component
    /// search would rescan the whole edge list each time. One breadth-first pass over an adjacency index
    /// answers all of them, which keeps a report with many findings proportional to the graph rather than
    /// to the graph times the findings.
    /// </remarks>
    public static DependencyRootPaths BuildRootPaths(scoped in DependencyInventory inventory)
    {
        var occurrences = inventory.Occurrences;
        var edges = inventory.Edges;
        var componentCount = inventory.Components.Length;
        if (occurrences is null || edges is null || occurrences.Length == 0 || componentCount == 0)
        {
            return default;
        }

        var identityCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(componentCount * 2, 4));
        var previous = ArrayPool<int>.Shared.Rent(occurrences.Length);
        var reachedOccurrence = ArrayPool<int>.Shared.Rent(componentCount);
        var identityIndex = ArrayPool<int>.Shared.Rent(identityCapacity);
        previous.AsSpan(0, occurrences.Length).Fill(-1);
        reachedOccurrence.AsSpan(0, componentCount).Fill(-1);
        BuildIdentityIndex(inventory.Components, identityIndex, identityCapacity);

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

            return new DependencyRootPaths(occurrences, previous, reachedOccurrence, inventory.Components, identityIndex, identityCapacity, componentCount);
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(visited);
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<int>.Shared.Return(adjacencyOffsets);
            ArrayPool<int>.Shared.Return(adjacencyTargets);
        }
    }

    /// <summary>
    /// Indexes components by the identity a report names them with.
    /// </summary>
    /// <remarks>
    /// The lookup this serves used to scan the inventory once per reported finding, falling back from a
    /// positional hint that a sorted view almost never satisfies — and every scan sorts before it renders,
    /// so the fallback was the normal case rather than the exceptional one. That made a report cost the
    /// findings times the graph. Empty slots hold <c>-1</c> rather than encoding positions one-based,
    /// which would leave position zero indistinguishable from an empty slot. The first component holding
    /// an identity wins, which is the component the scan it replaces used to return.
    /// </remarks>
    private static void BuildIdentityIndex(ScanComponent[] components, int[] table, int capacity)
    {
        var mask = capacity - 1;
        table.AsSpan(0, capacity).Fill(-1);
        for (var i = 0; i < components.Length; i++)
        {
            var slot = DependencyRootPaths.GetIdentityHash(components[i]) & mask;
            while (table[slot] >= 0 && !DependencyRootPaths.HasSameIdentity(components[table[slot]], components[i]))
            {
                slot = (slot + 1) & mask;
            }

            if (table[slot] < 0)
            {
                table[slot] = i;
            }
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

/// <summary>
/// Holds one traversal's shortest root paths for every component in an inventory, and the index that
/// locates a reported component within it.
/// </summary>
/// <remarks>
/// <para>
/// Both answer the same question — which direct dependency introduced this reported component — and both
/// are built once for a whole report, so they are produced and carried together. A default instance
/// describes an inventory with no graph, where no path exists to name and the lookup has nothing to find.
/// </para>
/// </remarks>
public ref struct DependencyRootPaths
{
    private DependencyOccurrence[]? occurrences;
    private int[]? previousOccurrence;
    private int[]? reachedOccurrenceByComponent;
    private ScanComponent[]? components;
    private int[]? identityIndex;
    private readonly int identityCapacity;
    private readonly int componentCount;

    internal DependencyRootPaths(
        DependencyOccurrence[] occurrences,
        int[] previousOccurrence,
        int[] reachedOccurrenceByComponent,
        ScanComponent[] components,
        int[] identityIndex,
        int identityCapacity,
        int componentCount)
    {
        this.occurrences = occurrences;
        this.previousOccurrence = previousOccurrence;
        this.reachedOccurrenceByComponent = reachedOccurrenceByComponent;
        this.components = components;
        this.identityIndex = identityIndex;
        this.identityCapacity = identityCapacity;
        this.componentCount = componentCount;
    }

    /// <summary>Returns the rentals and invalidates every later access. Tolerates repeated calls.</summary>
    public void Dispose()
    {
        var previous = previousOccurrence;
        var reached = reachedOccurrenceByComponent;
        var identity = identityIndex;
        occurrences = null;
        components = null;
        previousOccurrence = null;
        reachedOccurrenceByComponent = null;
        identityIndex = null;
        if (previous is not null) ArrayPool<int>.Shared.Return(previous);
        if (reached is not null) ArrayPool<int>.Shared.Return(reached);
        if (identity is not null) ArrayPool<int>.Shared.Return(identity);
    }

    /// <summary>A built instance whose rentals have already gone back to the pool.</summary>
    private readonly bool IsDisposed => identityCapacity != 0 && identityIndex is null;

    private readonly void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(DependencyRootPaths));
        }
    }

    /// <summary>
    /// Locates a reported component in the inventory these paths were built from.
    /// </summary>
    /// <param name="component">The reported component.</param>
    /// <param name="preferredIndex">A position to try first, for the case where the view is still aligned.</param>
    /// <returns>The inventory position, or <c>-1</c> when this inventory does not hold the component.</returns>
    /// <remarks>
    /// A report view is filtered and sorted, and a persisted report is re-read separately from its
    /// inventory, so a position in the view proves nothing about a position in the graph. Identity is the
    /// only thing both sides agree on.
    /// </remarks>
    public readonly int FindComponentIndex(in ScanComponent component, int preferredIndex = -1)
    {
        ThrowIfDisposed();
        var source = components;
        var table = identityIndex;
        if (source is null || table is null)
        {
            return -1;
        }

        if ((uint)preferredIndex < (uint)source.Length && HasSameIdentity(source[preferredIndex], component))
        {
            return preferredIndex;
        }

        var mask = identityCapacity - 1;
        var slot = GetIdentityHash(component) & mask;
        while (table[slot] >= 0)
        {
            if (HasSameIdentity(source[table[slot]], component))
            {
                return table[slot];
            }

            slot = (slot + 1) & mask;
        }

        return -1;
    }

    /// <summary>Hashes the fields <see cref="HasSameIdentity"/> compares, reading UTF-8 without decoding it.</summary>
    internal static int GetIdentityHash(in ScanComponent component)
    {
        var hash = new HashCode();
        hash.AddBytes(component.Name.Span);
        hash.AddBytes(component.Version.Span);
        hash.AddBytes(component.Purl.Span);
        hash.AddBytes(component.SourceId.Span);
        hash.Add(component.Ecosystem, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>Reports whether two components describe the same package to a reader.</summary>
    internal static bool HasSameIdentity(in ScanComponent left, in ScanComponent right)
        => left.Name.Equals(right.Name)
            && left.Version.Equals(right.Version)
            && left.Purl.Equals(right.Purl)
            && left.SourceId.Equals(right.SourceId)
            && string.Equals(left.Ecosystem, right.Ecosystem, StringComparison.Ordinal);

    /// <summary>
    /// Returns the shortest root-to-component path as component indexes, starting at a direct dependency
    /// and ending at the requested component. Returns an empty array when no path exists.
    /// </summary>
    public readonly int[] GetPath(int componentIndex)
    {
        ThrowIfDisposed();
        var reached = this.reachedOccurrenceByComponent;
        var previous = this.previousOccurrence;
        var source = this.occurrences;
        if (reached is null || previous is null || source is null) return [];

        // Bounded by the component count rather than the buffer, which the pool may hand back oversized.
        if ((uint)componentIndex >= (uint)componentCount) return [];

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
