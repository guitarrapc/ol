namespace Ol.Core;

/// <summary>
/// Represents one scanned SBOM component.
/// </summary>
/// <param name="Name">The component name.</param>
/// <param name="Version">The component version.</param>
/// <param name="License">The display license value.</param>
/// <param name="Ecosystem">The detected package ecosystem.</param>
/// <param name="DependencyType">The dependency relationship type.</param>
/// <param name="Status">The license status.</param>
/// <param name="Purl">The package URL, when present.</param>
public readonly record struct ScanComponent(
    string Name,
    string Version,
    string License,
    string Ecosystem,
    DependencyType DependencyType,
    LicenseStatus Status,
    string Purl);