namespace Ol.Core;

/// <summary>
/// Describes how a component is related to the root of the graph the input that supplied it declared.
/// </summary>
public enum DependencyType : byte
{
    /// <summary>No input that supplied the component stated a relationship for it.</summary>
    Unknown,

    /// <summary>
    /// The component is the subject of its input's graph rather than a dependency in it. Only an SBOM states this:
    /// a package-manager input enumerates dependencies, so listing a component is itself the determination that the
    /// component is a dependency of the scanned resolution.
    /// </summary>
    Root,

    /// <summary>The component is referenced by a root of its input's graph.</summary>
    Direct,

    /// <summary>The component is reachable from a root of its input's graph only through other components.</summary>
    Transitive,
}

internal static class DependencyTypes
{
    // Aggregates several observations of one graph into the relationship closest to a root that any of them proved.
    // Combining observations of different graphs is a different operation and belongs to DependencyInventoryCombiner.
    public static DependencyType Merge(DependencyType left, DependencyType right)
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
}
