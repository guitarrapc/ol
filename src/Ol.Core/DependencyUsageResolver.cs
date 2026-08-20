using System.Buffers;

namespace Ol.Core;

/// <summary>Aggregates occurrence-level development usage into one verdict per inventory component.</summary>
public static class DependencyUsageResolver
{
    // Merge codes ordered so that a plain max over a component's occurrences yields the policy verdict:
    // Runtime defeats Development, and a component no input classified stays Unknown.
    //
    // An occurrence whose input determined no usage casts no code at all. It records no observation about
    // reachability — its input kind has no vocabulary for one — so counting it as a competing claim let an
    // input that cannot speak overrule one that did. That is not the same as two inputs disagreeing, which
    // Runtime still wins. The case it decided in practice was an SBOM folded onto a lockfile row: the SBOM
    // adds an occurrence so its graph has an endpoint, and that occurrence silently cancelled the
    // classification the lockfile made. It did so unevenly, too — a package installed at two paths is two
    // rows and the SBOM attaches to the first, so one copy kept its classification and the other did not.
    private const byte None = 0;
    private const byte DevelopmentCode = 1;
    private const byte RuntimeCode = 2;

    /// <summary>
    /// Writes the aggregated <see cref="DependencyUsage"/> for each inventory component into
    /// <paramref name="componentUsages"/>. A component is <see cref="DependencyUsage.Development"/> only when at least
    /// one input classified it and every classification is development-only; a runtime classification downgrades it,
    /// while an occurrence from an input that classifies nothing abstains. A component no input classified is
    /// <see cref="DependencyUsage.Unknown"/>.
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
                // A development occurrence always lies inside a determined range, so the check only decides
                // whether an occurrence that is not development was classified at all.
                if (!isDevelopment && !IsDetermined(occurrenceIndex, ranges))
                {
                    continue;
                }

                var code = isDevelopment ? DevelopmentCode : RuntimeCode;
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
