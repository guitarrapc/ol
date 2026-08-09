using System.Buffers;
using Ol.Core;
using Ol.Core.Licensing;

namespace Ol.Internals;

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
        var usageRangeCount = 0;
        var developmentOccurrenceCount = 0;
        var hasSbom = false;
        var hasPackageManager = false;
        for (var i = 0; i < inventories.Length; i++)
        {
            contextCount = checked(contextCount + inventories[i].Contexts.Length);
            componentCapacity = checked(componentCapacity + inventories[i].Components.Length);
            occurrenceCount = checked(occurrenceCount + inventories[i].Occurrences.Length);
            edgeCount = checked(edgeCount + inventories[i].Edges.Length);
            occurrenceVariantCount = checked(occurrenceVariantCount + (inventories[i].OccurrenceVariants?.Length ?? 0));
            usageRangeCount = checked(usageRangeCount + (inventories[i].UsageDeterminedRanges?.Length ?? 0));
            developmentOccurrenceCount = checked(developmentOccurrenceCount + (inventories[i].DevelopmentOccurrences?.Length ?? 0));
            if (handlers[i].Kind == ScanInputKind.Sbom) hasSbom = true;
            else hasPackageManager = true;
        }

        var contexts = new DependencyResolutionContext[contextCount];
        var occurrences = new DependencyOccurrence[occurrenceCount];
        var edges = new DependencyEdge[edgeCount];
        var occurrenceVariants = new DependencyOccurrenceVariant[occurrenceVariantCount];
        // Only inputs that determined usage contribute ranges or development occurrences; if none did, both stay null so
        // the combined inventory carries no usage storage.
        var usageRanges = usageRangeCount == 0 ? null : new DependencyUsageRange[usageRangeCount];
        var developmentOccurrences = developmentOccurrenceCount == 0 ? null : new int[developmentOccurrenceCount];
        var components = ArrayPool<ScanComponent>.Shared.Rent(Math.Max(componentCapacity, 1));
        var componentRemap = ArrayPool<int>.Shared.Rent(Math.Max(componentCapacity, 1));
        var componentIndexes = new Dictionary<ComponentKey, int>(componentCapacity, ComponentKeyComparer.Instance);
        // The identity index is only needed to fold an SBOM into package-manager rows, so a collection of one kind
        // pays nothing for it.
        var foldsSbom = hasSbom && hasPackageManager;
        var identityNext = foldsSbom ? ArrayPool<int>.Shared.Rent(Math.Max(componentCapacity, 1)) : null;
        var identityComparisons = foldsSbom ? ArrayPool<DependencyComponentIdentityComparison>.Shared.Rent(Math.Max(componentCapacity, 1)) : null;
        try
        {
            // Identity is assigned in two passes rather than one so that the result does not depend on the order the
            // inputs were named. Package-manager inputs are the finer observation and own the resulting rows; the SBOM
            // is a purl-keyed projection of the same resolution and is folded into them afterwards.
            var combinedComponentCount = AssignPackageManagerComponents(inventories, handlers, components, componentRemap, componentIndexes);
            if (foldsSbom)
            {
                identityNext.AsSpan(0, Math.Max(componentCapacity, 1)).Fill(NotIndexed);
                var identities = new Dictionary<PurlIdentityKey, IdentityChain>(combinedComponentCount, PurlIdentityKeyComparer.Instance);
                BuildIdentityChains(inventories, handlers, components, componentRemap, identities, identityNext!, identityComparisons!);
                combinedComponentCount = FoldSbomComponents(
                    inventories,
                    handlers,
                    components,
                    componentRemap,
                    combinedComponentCount,
                    identities,
                    identityNext!,
                    identityComparisons!);
            }
            else
            {
                combinedComponentCount = AppendSbomComponents(inventories, handlers, components, componentRemap, combinedComponentCount, componentIndexes);
            }

            var contextOffset = 0;
            var componentOffset = 0;
            var occurrenceOffset = 0;
            var edgeOffset = 0;
            var occurrenceVariantOffset = 0;
            var usageRangeOffset = 0;
            var developmentOccurrenceOffset = 0;
            for (var inventoryIndex = 0; inventoryIndex < inventories.Length; inventoryIndex++)
            {
                var inventory = inventories[inventoryIndex];
                inventory.Contexts.CopyTo(contexts, contextOffset);

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

                var inventoryUsageRanges = inventory.UsageDeterminedRanges;
                if (inventoryUsageRanges is not null && usageRanges is not null)
                {
                    for (var i = 0; i < inventoryUsageRanges.Length; i++)
                    {
                        var range = inventoryUsageRanges[i];
                        usageRanges[usageRangeOffset + i] = new DependencyUsageRange(range.StartOccurrenceIndex + occurrenceOffset, range.Length);
                    }

                    usageRangeOffset += inventoryUsageRanges.Length;
                }

                var inventoryDevelopmentOccurrences = inventory.DevelopmentOccurrences;
                if (inventoryDevelopmentOccurrences is not null && developmentOccurrences is not null)
                {
                    for (var i = 0; i < inventoryDevelopmentOccurrences.Length; i++)
                    {
                        developmentOccurrences[developmentOccurrenceOffset + i] = inventoryDevelopmentOccurrences[i] + occurrenceOffset;
                    }

                    developmentOccurrenceOffset += inventoryDevelopmentOccurrences.Length;
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
                occurrenceVariants,
                usageRanges,
                developmentOccurrences);
        }
        finally
        {
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<int>.Shared.Return(componentRemap);
            if (identityNext is not null) ArrayPool<int>.Shared.Return(identityNext);
            if (identityComparisons is not null) ArrayPool<DependencyComponentIdentityComparison>.Shared.Return(identityComparisons);
        }
    }

    /// <summary>Marks a combined component that no identity chain has claimed yet.</summary>
    private const int NotIndexed = -2;

    private static int AssignPackageManagerComponents(
        ReadOnlySpan<DependencyInventory> inventories,
        ReadOnlySpan<DependencyInputHandler> handlers,
        ScanComponent[] components,
        int[] componentRemap,
        Dictionary<ComponentKey, int> componentIndexes)
        => AssignComponents(inventories, handlers, components, componentRemap, componentIndexes, combinedComponentCount: 0, assignSbom: false);

    private static int AppendSbomComponents(
        ReadOnlySpan<DependencyInventory> inventories,
        ReadOnlySpan<DependencyInputHandler> handlers,
        ScanComponent[] components,
        int[] componentRemap,
        int combinedComponentCount,
        Dictionary<ComponentKey, int> componentIndexes)
        => AssignComponents(inventories, handlers, components, componentRemap, componentIndexes, combinedComponentCount, assignSbom: true);

    // Within one input kind, identity stays exactly what each registered format declares: its purl comparison and,
    // where the format tracks distinct installations, its source identifier. Two lockfiles describe two installations,
    // so a shared purl across formats is two observations rather than one.
    private static int AssignComponents(
        ReadOnlySpan<DependencyInventory> inventories,
        ReadOnlySpan<DependencyInputHandler> handlers,
        ScanComponent[] components,
        int[] componentRemap,
        Dictionary<ComponentKey, int> componentIndexes,
        int combinedComponentCount,
        bool assignSbom)
    {
        var componentOffset = 0;
        for (var inventoryIndex = 0; inventoryIndex < inventories.Length; inventoryIndex++)
        {
            var inventory = inventories[inventoryIndex];
            if (handlers[inventoryIndex].Kind == ScanInputKind.Sbom != assignSbom)
            {
                componentOffset += inventory.Components.Length;
                continue;
            }

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

            componentOffset += inventory.Components.Length;
        }

        return combinedComponentCount;
    }

    private static void BuildIdentityChains(
        ReadOnlySpan<DependencyInventory> inventories,
        ReadOnlySpan<DependencyInputHandler> handlers,
        ScanComponent[] components,
        int[] componentRemap,
        Dictionary<PurlIdentityKey, IdentityChain> identities,
        int[] identityNext,
        DependencyComponentIdentityComparison[] identityComparisons)
    {
        var componentOffset = 0;
        for (var inventoryIndex = 0; inventoryIndex < inventories.Length; inventoryIndex++)
        {
            var inventory = inventories[inventoryIndex];
            if (handlers[inventoryIndex].Kind == ScanInputKind.Sbom)
            {
                componentOffset += inventory.Components.Length;
                continue;
            }

            for (var i = 0; i < inventory.Components.Length; i++)
            {
                if (inventory.Components[i].Purl.IsEmpty) continue;
                var combinedIndex = componentRemap[componentOffset + i];
                if (identityNext[combinedIndex] != NotIndexed) continue;
                identityNext[combinedIndex] = -1;
                identityComparisons[combinedIndex] = handlers[inventoryIndex].ComponentIdentityComparison;
                var key = new PurlIdentityKey(components[combinedIndex].Purl);
                if (identities.TryGetValue(key, out var chain))
                {
                    identityNext[chain.Tail] = combinedIndex;
                    identities[key] = chain with { Tail = combinedIndex };
                }
                else
                {
                    identities.Add(key, new IdentityChain(combinedIndex, combinedIndex));
                }
            }

            componentOffset += inventory.Components.Length;
        }
    }

    private static int FoldSbomComponents(
        ReadOnlySpan<DependencyInventory> inventories,
        ReadOnlySpan<DependencyInputHandler> handlers,
        ScanComponent[] components,
        int[] componentRemap,
        int combinedComponentCount,
        Dictionary<PurlIdentityKey, IdentityChain> identities,
        int[] identityNext,
        DependencyComponentIdentityComparison[] identityComparisons)
    {
        var componentOffset = 0;
        for (var inventoryIndex = 0; inventoryIndex < inventories.Length; inventoryIndex++)
        {
            var inventory = inventories[inventoryIndex];
            if (handlers[inventoryIndex].Kind != ScanInputKind.Sbom)
            {
                componentOffset += inventory.Components.Length;
                continue;
            }

            for (var i = 0; i < inventory.Components.Length; i++)
            {
                var component = inventory.Components[i];
                var head = component.Purl.IsEmpty
                    ? -1
                    : FindIdentity(component.Purl, components, identities, identityNext, identityComparisons);
                if (head < 0)
                {
                    // No package-manager input answers for this purl, so the SBOM keeps its own row and its own supply.
                    components[combinedComponentCount] = component;
                    componentRemap[componentOffset + i] = combinedComponentCount;
                    combinedComponentCount++;
                    continue;
                }

                // An SBOM does not distinguish the installed copies a package manager tracks separately, so its
                // declaration answers for every one of them. The occurrence attaches to the first row in input order,
                // which is the only endpoint the SBOM's own graph can point at without inventing a distinction.
                for (var target = head; target >= 0; target = identityNext[target])
                {
                    if (!MatchesIdentity(component.Purl, components[target].Purl, identityComparisons[target])) continue;
                    components[target] = Absorb(components[target], component);
                }

                componentRemap[componentOffset + i] = head;
            }

            componentOffset += inventory.Components.Length;
        }

        return combinedComponentCount;
    }

    private static int FindIdentity(
        Utf8Slice purl,
        ScanComponent[] components,
        Dictionary<PurlIdentityKey, IdentityChain> identities,
        int[] identityNext,
        DependencyComponentIdentityComparison[] identityComparisons)
    {
        if (!identities.TryGetValue(new PurlIdentityKey(purl), out var chain)) return -1;
        for (var index = chain.Head; index >= 0; index = identityNext[index])
        {
            if (MatchesIdentity(purl, components[index].Purl, identityComparisons[index])) return index;
        }

        return -1;
    }

    // Buckets fold ASCII casing so that one lookup reaches every candidate, and each candidate is then confirmed with
    // the owning format's own rule. A case-insensitive ecosystem such as NuGet accepts the difference; a case-sensitive
    // one still rejects it.
    private static bool MatchesIdentity(Utf8Slice left, Utf8Slice right, DependencyComponentIdentityComparison comparison)
    {
        var leftIdentity = PurlIdentityKey.Identity(left.Span);
        var rightIdentity = PurlIdentityKey.Identity(right.Span);
        if (leftIdentity.Length != rightIdentity.Length) return false;
        if (comparison != DependencyComponentIdentityComparison.AsciiIgnoreCase)
        {
            return leftIdentity.SequenceEqual(rightIdentity);
        }

        for (var i = 0; i < leftIdentity.Length; i++)
        {
            if (ToLowerAscii(leftIdentity[i]) != ToLowerAscii(rightIdentity[i])) return false;
        }

        return true;
    }

    // Folding keeps the receiving row's identity and adds what the SBOM contributes: its license candidates, its
    // supplying input kind, the stronger dependency relationship, and a repository URL the receiver lacks. Nothing the
    // receiver already states is replaced, so the fold can only add evidence.
    private static ScanComponent Absorb(ScanComponent target, ScanComponent source)
    {
        var merged = target with
        {
            DependencyType = MergeDependencyType(target.DependencyType, source.DependencyType),
            SuppliedBy = target.SuppliedBy | source.SuppliedBy,
            RepositoryUrl = target.RepositoryUrl.IsEmpty ? source.RepositoryUrl : target.RepositoryUrl,
        };

        for (var i = 0; i < source.CandidateCount; i++)
        {
            merged = LicenseReconciler.AddCandidate(merged, source.GetCandidate(i));
        }

        return merged;
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

    private static byte ToLowerAscii(byte value)
        => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ((byte)'a' - (byte)'A')) : value;

    private readonly record struct IdentityChain(int Head, int Tail);

    /// <summary>
    /// A package URL reduced to the part that names the package: everything before the first qualifier or subpath.
    /// Ol and SBOM generators disagree about which qualifiers to emit for the same artifact, so comparing whole purls
    /// would miss matches the ecosystem itself considers the same package.
    /// </summary>
    private readonly record struct PurlIdentityKey(Utf8Slice Purl)
    {
        public static ReadOnlySpan<byte> Identity(ReadOnlySpan<byte> purl)
        {
            var end = purl.IndexOfAny((byte)'?', (byte)'#');
            return end < 0 ? purl : purl[..end];
        }
    }

    private sealed class PurlIdentityKeyComparer : IEqualityComparer<PurlIdentityKey>
    {
        public static PurlIdentityKeyComparer Instance { get; } = new();

        public bool Equals(PurlIdentityKey left, PurlIdentityKey right)
        {
            var leftIdentity = PurlIdentityKey.Identity(left.Purl.Span);
            var rightIdentity = PurlIdentityKey.Identity(right.Purl.Span);
            if (leftIdentity.Length != rightIdentity.Length) return false;
            for (var i = 0; i < leftIdentity.Length; i++)
            {
                if (ToLowerAscii(leftIdentity[i]) != ToLowerAscii(rightIdentity[i])) return false;
            }

            return true;
        }

        public int GetHashCode(PurlIdentityKey value)
        {
            var hash = new HashCode();
            var identity = PurlIdentityKey.Identity(value.Purl.Span);
            for (var i = 0; i < identity.Length; i++)
            {
                hash.Add(ToLowerAscii(identity[i]));
            }

            return hash.ToHashCode();
        }
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
    }
}
