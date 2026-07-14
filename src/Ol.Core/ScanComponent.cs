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
/// <param name="SourceId">The source SBOM component identifier, when present.</param>
/// <param name="LicenseCandidates">The extracted license candidates.</param>
/// <param name="Evidence">The normalized evidence records.</param>
/// <param name="Warnings">Warnings associated with this component.</param>
public readonly record struct ScanComponent(
    Utf8Slice Name,
    Utf8Slice Version,
    string License,
    string Ecosystem,
    DependencyType DependencyType,
    LicenseStatus Status,
    Utf8Slice Purl,
    Utf8Slice SourceId,
    LicenseCandidate[] LicenseCandidates,
    LicenseEvidence[] Evidence,
    string[] Warnings);
