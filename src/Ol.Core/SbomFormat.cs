namespace Ol.Core;

/// <summary>
/// Identifies a parsed SBOM format.
/// </summary>
/// <param name="Name">The stable format identifier.</param>
public readonly record struct SbomFormat(string Name)
{
    /// <summary>CycloneDX JSON.</summary>
    public static SbomFormat CycloneDxJson { get; } = new("CycloneDxJson");

    /// <summary>SPDX JSON.</summary>
    public static SbomFormat SpdxJson { get; } = new("SpdxJson");

    /// <inheritdoc />
    public override string ToString() => Name;
}
