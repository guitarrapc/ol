using System.Text;
using Ol.Core;
using Ol.Core.Reporting;

namespace Ol.Internals;

/// <summary>
/// Renders a root-to-component dependency path the same way in every report.
/// </summary>
/// <remarks>
/// SARIF, the check table and the scan report all answer the same question — which direct dependency
/// introduced this component — so they must spell the answer identically or a reviewer comparing two
/// projections of one run has to decide which of them to believe.
/// </remarks>
internal static class DependencyPathText
{
    /// <summary>Separates hops so a path stays one field in a tab-separated or Markdown table.</summary>
    public const string Separator = " > ";

    /// <summary>Names a component the way every path and location in the reports names it.</summary>
    public static string Identity(in ScanComponent component)
    {
        if (!component.Purl.IsEmpty) return component.Purl.ToString();
        var name = component.Name.ToString();
        return component.Version.IsEmpty ? name : $"{name}@{component.Version}";
    }

    /// <summary>Joins a resolved path into one field. Returns an empty string for an absent path.</summary>
    public static string Format(in DependencyInventory inventory, ReadOnlySpan<int> path)
    {
        if (path.Length == 0) return string.Empty;

        var builder = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            if (i != 0) builder.Append(Separator);
            builder.Append(Identity(inventory.Components[path[i]]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the path only when it names something the component itself does not.
    /// </summary>
    /// <remarks>
    /// A direct dependency is its own introducer and an unlinked component has no proven introducer at
    /// all. Printing a one-hop path in either case would repeat the row or imply a relationship the
    /// input never described, so both are reported as the absence of a path.
    /// </remarks>
    public static string Introducer(in DependencyInventory inventory, in DependencyRootPaths paths, in ScanComponent component, int preferredIndex = -1)
    {
        var componentIndex = FindComponentIndex(inventory.Components, component, preferredIndex);
        if (componentIndex < 0) return string.Empty;

        var path = paths.GetPath(componentIndex);
        return path.Length > 1 ? Format(inventory, path) : string.Empty;
    }

    /// <summary>
    /// Locates a reported component in the inventory it was derived from.
    /// </summary>
    /// <remarks>
    /// A report view is filtered and sorted, and a persisted report is re-read separately from its
    /// inventory, so a position in the view proves nothing about a position in the graph. Identity is
    /// the only thing both sides agree on; the hint just avoids the scan in the common aligned case.
    /// </remarks>
    public static int FindComponentIndex(
        ReadOnlySpan<ScanComponent> inventoryComponents,
        in ScanComponent component,
        int preferredIndex = -1)
    {
        if ((uint)preferredIndex < (uint)inventoryComponents.Length
            && HasSameIdentity(inventoryComponents[preferredIndex], component))
        {
            return preferredIndex;
        }

        for (var i = 0; i < inventoryComponents.Length; i++)
        {
            if (HasSameIdentity(inventoryComponents[i], component)) return i;
        }

        return -1;
    }

    private static bool HasSameIdentity(in ScanComponent left, in ScanComponent right)
        => left.Name.Equals(right.Name)
            && left.Version.Equals(right.Version)
            && left.Purl.Equals(right.Purl)
            && left.SourceId.Equals(right.SourceId)
            && string.Equals(left.Ecosystem, right.Ecosystem, StringComparison.Ordinal);
}
