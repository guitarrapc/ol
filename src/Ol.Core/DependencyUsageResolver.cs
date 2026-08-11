using System.Buffers;

namespace Ol.Core;

/// <summary>Aggregates occurrence-level development usage into one verdict per inventory component.</summary>
public static class DependencyUsageResolver
{
    // Merge codes ordered so that a plain max over a component's occurrences yields the policy verdict:
    // Runtime and Unknown both defeat Development, and a component with no occurrence stays Unknown.
    private const byte None = 0;
    private const byte DevelopmentCode = 1;
    private const byte UnknownCode = 2;
    private const byte RuntimeCode = 3;

    /// <summary>
    /// Writes the aggregated <see cref="DependencyUsage"/> for each inventory component into
    /// <paramref name="componentUsages"/>. A component is <see cref="DependencyUsage.Development"/> only when it has at
    /// least one occurrence and every occurrence is development-only; any runtime or unknown occurrence downgrades it.
    /// </summary>
    /// <param name="inventory">The inventory whose occurrences carry usage information.</param>
    /// <param name="componentUsages">Destination sized to <c>inventory.Components.Length</c>.</param>
    public static void Resolve(in DependencyInventory inventory, Span<DependencyUsage> componentUsages)
    {
        var componentCount = inventory.Components.Length;
        if (componentUsages.Length < componentCount)
        {
            throw new ArgumentException("The destination must have room for every component.", nameof(componentUsages));
        }

        componentUsages[..componentCount].Fill(DependencyUsage.Unknown);
        var ranges = inventory.UsageDeterminedRanges;
        if (ranges is null || componentCount == 0)
        {
            return;
        }

        var occurrences = inventory.Occurrences;
        var developmentOccurrences = inventory.DevelopmentOccurrences;
        var rented = ArrayPool<byte>.Shared.Rent(componentCount);
        try
        {
            var codes = rented.AsSpan(0, componentCount);
            codes.Clear();

            // Occurrences are visited in ascending index order and DevelopmentOccurrences is ascending, so a cursor
            // that only moves forward decides development membership in one pass instead of a search per occurrence.
            var developmentCursor = 0;
            for (var occurrenceIndex = 0; occurrenceIndex < occurrences.Length; occurrenceIndex++)
            {
                // Advance before the component check so the cursor stays aligned even when an occurrence is skipped.
                if (developmentOccurrences is not null)
                {
                    while (developmentCursor < developmentOccurrences.Length && developmentOccurrences[developmentCursor] < occurrenceIndex)
                    {
                        developmentCursor++;
                    }
                }

                var componentIndex = occurrences[occurrenceIndex].ComponentIndex;
                if ((uint)componentIndex >= (uint)componentCount)
                {
                    continue;
                }

                var isDevelopment = developmentOccurrences is not null
                    && developmentCursor < developmentOccurrences.Length
                    && developmentOccurrences[developmentCursor] == occurrenceIndex;
                var code = isDevelopment ? DevelopmentCode : IsDetermined(occurrenceIndex, ranges) ? RuntimeCode : UnknownCode;
                if (codes[componentIndex] < code)
                {
                    codes[componentIndex] = code;
                }
            }

            for (var i = 0; i < componentCount; i++)
            {
                componentUsages[i] = codes[i] switch
                {
                    DevelopmentCode => DependencyUsage.Development,
                    RuntimeCode => DependencyUsage.Runtime,
                    _ => DependencyUsage.Unknown,
                };
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsDetermined(int occurrenceIndex, DependencyUsageRange[] ranges)
    {
        for (var i = 0; i < ranges.Length; i++)
        {
            if ((uint)(occurrenceIndex - ranges[i].StartOccurrenceIndex) < (uint)ranges[i].Length)
            {
                return true;
            }
        }

        return false;
    }
}
