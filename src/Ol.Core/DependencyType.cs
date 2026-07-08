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