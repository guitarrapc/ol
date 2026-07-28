namespace Ol.Core;

/// <summary>
/// Describes how a component is related to the SBOM root component.
/// </summary>
public enum DependencyType
{
    /// <summary>The dependency relationship could not be determined from the SBOM.</summary>
    Unknown,

    /// <summary>The component is the SBOM root component.</summary>
    Root,

    /// <summary>The component is directly referenced by the SBOM root component.</summary>
    Direct,

    /// <summary>The component is reachable transitively from the SBOM root component.</summary>
    Transitive,
}

internal static class DependencyTypes
{
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
