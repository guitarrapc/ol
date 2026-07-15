using Ol.Core.Licensing;

namespace Ol.Core;

/// <summary>
/// Represents one scanned SBOM component.
/// </summary>
/// <param name="Name">The component name.</param>
/// <param name="Version">The component version.</param>
/// <param name="License">The UTF-8 display license value.</param>
/// <param name="Ecosystem">The detected package ecosystem.</param>
/// <param name="DependencyType">The dependency relationship type.</param>
/// <param name="Status">The license status.</param>
/// <param name="Purl">The package URL, when present.</param>
/// <param name="SourceId">The source SBOM component identifier, when present.</param>
/// <param name="PrimaryCandidate">The first extracted license candidate, when present.</param>
/// <param name="AdditionalCandidates">The additional extracted candidates, when present.</param>
/// <param name="Warnings">Warnings associated with this component.</param>
/// <param name="RepositoryUrl">The source repository URL supplied by the SBOM, when present.</param>
public readonly record struct ScanComponent(
    Utf8Slice Name,
    Utf8Slice Version,
    Utf8Slice License,
    string Ecosystem,
    DependencyType DependencyType,
    LicenseStatus Status,
    Utf8Slice Purl,
    Utf8Slice SourceId,
    LicenseCandidate PrimaryCandidate,
    LicenseCandidate[] AdditionalCandidates,
    string[] Warnings,
    Utf8Slice RepositoryUrl = default)
{
    /// <summary>Gets the number of retained license candidates.</summary>
    public int CandidateCount => PrimaryCandidate.Source is null ? 0 : AdditionalCandidates.Length + 1;

    /// <summary>Gets a retained candidate by index without materializing a combined array.</summary>
    public LicenseCandidate GetCandidate(int index)
        => index == 0 && PrimaryCandidate.Source is not null ? PrimaryCandidate
        : (uint)(index - 1) < (uint)AdditionalCandidates.Length ? AdditionalCandidates[index - 1]
        : throw new ArgumentOutOfRangeException(nameof(index));
}
