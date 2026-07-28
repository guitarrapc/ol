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
    LicenseCandidateWarnings Warnings,
    LicenseEvidence Evidence = default);

/// <summary>Identifies warning codes retained by a license candidate without string storage.</summary>
[Flags]
public enum LicenseCandidateWarnings : ushort
{
    None = 0,
    DeprecatedSpdxIdentifier = 1 << 0,
    PackageMetadataFetchFailed = 1 << 1,
    UnsupportedPackageMetadata = 1 << 2,
    SourceRepositoryCacheInvalid = 1 << 3,
    SourceRepositoryCacheWriteFailed = 1 << 4,
    SourceRepositoryFetchFailed = 1 << 5,
    SourceRepositoryUnavailable = 1 << 6,
    UnsupportedSourceRepository = 1 << 7,
}

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

/// <summary>Provides stable UTF-8 candidate identifiers without string allocation.</summary>
public static class LicenseCandidateIdentifiers
{
    public static LicenseCandidateWarnings ParseWarning(string value) => value switch
    {
        "deprecated_spdx_identifier" => LicenseCandidateWarnings.DeprecatedSpdxIdentifier,
        "package_metadata_fetch_failed" => LicenseCandidateWarnings.PackageMetadataFetchFailed,
        "unsupported_package_metadata" => LicenseCandidateWarnings.UnsupportedPackageMetadata,
        "source_repository_cache_invalid" => LicenseCandidateWarnings.SourceRepositoryCacheInvalid,
        "source_repository_cache_write_failed" => LicenseCandidateWarnings.SourceRepositoryCacheWriteFailed,
        "source_repository_fetch_failed" => LicenseCandidateWarnings.SourceRepositoryFetchFailed,
        "source_repository_unavailable" => LicenseCandidateWarnings.SourceRepositoryUnavailable,
        "unsupported_source_repository" => LicenseCandidateWarnings.UnsupportedSourceRepository,
        _ => LicenseCandidateWarnings.None,
    };

    public static LicenseCandidateWarnings ParseWarnings(ReadOnlySpan<string> values)
    {
        var result = LicenseCandidateWarnings.None;
        for (var i = 0; i < values.Length; i++) result |= ParseWarning(values[i]);
        return result;
    }

    public static string[] ToStrings(this LicenseCandidateWarnings value)
    {
        if (value == LicenseCandidateWarnings.None) return [];
        var result = new string[System.Numerics.BitOperations.PopCount((uint)value)];
        var index = 0;
        if ((value & LicenseCandidateWarnings.DeprecatedSpdxIdentifier) != 0) result[index++] = "deprecated_spdx_identifier";
        if ((value & LicenseCandidateWarnings.PackageMetadataFetchFailed) != 0) result[index++] = "package_metadata_fetch_failed";
        if ((value & LicenseCandidateWarnings.SourceRepositoryCacheInvalid) != 0) result[index++] = "source_repository_cache_invalid";
        if ((value & LicenseCandidateWarnings.SourceRepositoryCacheWriteFailed) != 0) result[index++] = "source_repository_cache_write_failed";
        if ((value & LicenseCandidateWarnings.SourceRepositoryFetchFailed) != 0) result[index++] = "source_repository_fetch_failed";
        if ((value & LicenseCandidateWarnings.SourceRepositoryUnavailable) != 0) result[index++] = "source_repository_unavailable";
        if ((value & LicenseCandidateWarnings.UnsupportedPackageMetadata) != 0) result[index++] = "unsupported_package_metadata";
        if ((value & LicenseCandidateWarnings.UnsupportedSourceRepository) != 0) result[index] = "unsupported_source_repository";
        return result;
    }

    public static ReadOnlySpan<byte> ToUtf8(this LicenseCandidateSource value) => value switch
    {
        LicenseCandidateSource.Sbom => "sbom"u8,
        LicenseCandidateSource.PackageRegistry => "package-registry"u8,
        LicenseCandidateSource.NpmRegistry => "npm-registry"u8,
        LicenseCandidateSource.NuGetRegistry => "nuget-registry"u8,
        LicenseCandidateSource.CargoRegistry => "cargo-registry"u8,
        LicenseCandidateSource.GoModuleProxy => "go-module-proxy"u8,
        LicenseCandidateSource.SourceRepository => "source-repository"u8,
        LicenseCandidateSource.GitHubLicenseApi => "github-license-api"u8,
        LicenseCandidateSource.DependencyInput => "dependency-input"u8,
        _ => default,
    };

    public static ReadOnlySpan<byte> ToUtf8(this LicenseCandidateKind value) => value switch
    {
        LicenseCandidateKind.License => "license"u8,
        LicenseCandidateKind.Id => "id"u8,
        LicenseCandidateKind.Name => "name"u8,
        LicenseCandidateKind.Expression => "expression"u8,
        LicenseCandidateKind.Declared => "declared"u8,
        LicenseCandidateKind.Concluded => "concluded"u8,
        LicenseCandidateKind.Fetch => "fetch"u8,
        LicenseCandidateKind.Unavailable => "unavailable"u8,
        LicenseCandidateKind.Unsupported => "unsupported"u8,
        _ => default,
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
