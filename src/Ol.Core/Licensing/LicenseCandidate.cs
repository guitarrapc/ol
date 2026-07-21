namespace Ol.Core.Licensing;

/// <summary>
/// Represents one license value extracted from an evidence source.
/// </summary>
/// <param name="Source">The evidence source.</param>
/// <param name="Kind">The source field or license value kind.</param>
/// <param name="Raw">The original license value.</param>
/// <param name="Normalized">The normalized SPDX expression, when valid.</param>
/// <param name="Status">The classification of this candidate.</param>
/// <param name="Deprecated">Whether the candidate uses a deprecated SPDX identifier.</param>
/// <param name="Warnings">Warnings associated with this candidate.</param>
/// <param name="Evidence">Typed provenance that substantiates this candidate.</param>
public readonly record struct LicenseCandidate(
    LicenseCandidateSource Source,
    LicenseCandidateKind Kind,
    Utf8Slice Raw,
    Utf8Slice Normalized,
    LicenseStatus Status,
    bool Deprecated,
    string[] Warnings,
    LicenseEvidence Evidence = default);

/// <summary>Identifies the evidence system that produced a license candidate.</summary>
public enum LicenseCandidateSource : byte
{
    None,
    Sbom,
    PackageRegistry,
    NpmRegistry,
    NuGetRegistry,
    CargoRegistry,
    GoModuleProxy,
    SourceRepository,
    GitHubLicenseApi,
    DependencyInput,
}

/// <summary>Identifies the field type that produced a license candidate.</summary>
public enum LicenseCandidateKind : byte
{
    None,
    License,
    Id,
    Name,
    Expression,
    Declared,
    Concluded,
    Fetch,
    Unavailable,
    Unsupported,
}

/// <summary>Renders stable candidate identifiers only at an output boundary.</summary>
public static class LicenseCandidateIdentifiers
{
    public static string ToDisplayString(this LicenseCandidateSource value) => value switch
    {
        LicenseCandidateSource.Sbom => "sbom",
        LicenseCandidateSource.PackageRegistry => "package-registry",
        LicenseCandidateSource.NpmRegistry => "npm-registry",
        LicenseCandidateSource.NuGetRegistry => "nuget-registry",
        LicenseCandidateSource.CargoRegistry => "cargo-registry",
        LicenseCandidateSource.GoModuleProxy => "go-module-proxy",
        LicenseCandidateSource.SourceRepository => "source-repository",
        LicenseCandidateSource.GitHubLicenseApi => "github-license-api",
        LicenseCandidateSource.DependencyInput => "dependency-input",
        _ => string.Empty,
    };

    public static string ToDisplayString(this LicenseCandidateKind value) => value switch
    {
        LicenseCandidateKind.License => "license",
        LicenseCandidateKind.Id => "id",
        LicenseCandidateKind.Name => "name",
        LicenseCandidateKind.Expression => "expression",
        LicenseCandidateKind.Declared => "declared",
        LicenseCandidateKind.Concluded => "concluded",
        LicenseCandidateKind.Fetch => "fetch",
        LicenseCandidateKind.Unavailable => "unavailable",
        LicenseCandidateKind.Unsupported => "unsupported",
        _ => string.Empty,
    };
}

/// <summary>Identifies the provenance family that substantiates a license candidate.</summary>
public enum LicenseEvidenceKind : byte
{
    /// <summary>No structured provenance is available.</summary>
    None,
    /// <summary>The claim came from an SBOM license field.</summary>
    Sbom,
    /// <summary>The claim came from a non-SBOM resolved dependency input.</summary>
    DependencyInput,
    /// <summary>The claim came from package registry metadata.</summary>
    PackageRegistry,
    /// <summary>The claim came from source repository inspection.</summary>
    SourceRepository,
}

/// <summary>Identifies the exact SBOM field from which a license claim was read.</summary>
public enum SbomLicenseField : byte
{
    /// <summary>No SBOM field applies.</summary>
    None,
    /// <summary>CycloneDX component <c>licenses</c>.</summary>
    CycloneDxLicenses,
    /// <summary>SPDX package <c>licenseDeclared</c>.</summary>
    SpdxLicenseDeclared,
    /// <summary>SPDX package <c>licenseConcluded</c>.</summary>
    SpdxLicenseConcluded,
}

/// <summary>Represents a license acknowledgement explicitly supplied by an input format.</summary>
public enum LicenseAcknowledgement : byte
{
    /// <summary>No acknowledgement was supplied.</summary>
    None,
    /// <summary>The license was declared by the package author or producer.</summary>
    Declared,
    /// <summary>The license was concluded by the document producer or analyzer.</summary>
    Concluded,
}

/// <summary>Contains source-specific provenance without repeating the candidate claim.</summary>
/// <param name="Kind">The provenance family.</param>
/// <param name="SbomField">The exact SBOM license field, when applicable.</param>
/// <param name="Acknowledgement">The explicit declared or concluded acknowledgement, when supplied.</param>
/// <param name="PackageRegistry">Package-registry provenance, when applicable.</param>
/// <param name="SourceRepository">Source-repository provenance, when applicable.</param>
/// <param name="DependencyInput">Resolved dependency-input provenance, when applicable.</param>
public readonly record struct LicenseEvidence(
    LicenseEvidenceKind Kind,
    SbomLicenseField SbomField = SbomLicenseField.None,
    LicenseAcknowledgement Acknowledgement = LicenseAcknowledgement.None,
    PackageRegistryEvidence? PackageRegistry = null,
    SourceRepositoryEvidence? SourceRepository = null,
    DependencyInputEvidence? DependencyInput = null);

/// <summary>Contains structured provenance for a non-SBOM dependency-input candidate.</summary>
/// <param name="Format">The stable dependency input format.</param>
/// <param name="Field">The format-native field that supplied the claim.</param>
public sealed record DependencyInputEvidence(string Format, string Field);

/// <summary>Contains structured provenance for one package-registry candidate.</summary>
public sealed record PackageRegistryEvidence(string CacheKeySha256, DateTimeOffset CollectedAt = default);

/// <summary>Contains structured provenance for one source-repository candidate.</summary>
public sealed record SourceRepositoryEvidence(
    string Repository,
    string Ref,
    int? HttpStatus,
    string CacheKeySha256,
    string LicensePath,
    string LicenseSha,
    string LicenseKey,
    string LicenseName,
    string LicenseUrl);
