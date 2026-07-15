namespace Ol.Core;

/// <summary>Identifies the kind of resolved dependency input supplied to a scan.</summary>
/// <param name="Name">The stable input-kind identifier.</param>
public readonly record struct ScanInputKind(string Name)
{
    /// <summary>An SBOM dependency input.</summary>
    public static ScanInputKind Sbom { get; } = new("sbom");

    /// <summary>A package-manager resolved dependency input.</summary>
    public static ScanInputKind PackageManager { get; } = new("package-manager");

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Identifies one registered resolved dependency input format.</summary>
/// <param name="Name">The stable CLI and report identifier.</param>
/// <param name="Parser">The stable parser identity.</param>
/// <param name="DisplayName">The human-readable format name.</param>
public readonly record struct ScanInputFormat(string Name, string Parser, string DisplayName)
{
    /// <summary>CycloneDX JSON.</summary>
    public static ScanInputFormat CycloneDx { get; } = new("cyclonedx", "cyclonedx-json", "CycloneDX");

    /// <summary>SPDX JSON.</summary>
    public static ScanInputFormat Spdx { get; } = new("spdx", "spdx-json", "SPDX");

    /// <summary>NuGet project.assets.json.</summary>
    public static ScanInputFormat NuGetAssets { get; } = new("nuget-assets", "nuget-assets-json", "NuGet assets");

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Describes the source and parser used to produce one dependency inventory.</summary>
/// <param name="Kind">The input kind.</param>
/// <param name="Format">The registered input format.</param>
/// <param name="SourceReference">The non-sensitive logical source reference.</param>
/// <param name="SourceSha256">The lowercase SHA-256 of the source input.</param>
/// <param name="SpecificationVersion">The source format specification version, when present.</param>
public readonly record struct ScanInputDescriptor(
    ScanInputKind Kind,
    ScanInputFormat Format,
    string SourceReference,
    string SourceSha256,
    Utf8Slice SpecificationVersion);

/// <summary>Identifies the resolver conditions for one dependency graph.</summary>
/// <param name="ProjectOrigin">The project or workspace that owns the graph.</param>
/// <param name="Target">The target framework or language target.</param>
/// <param name="Runtime">The runtime or resolver runtime identifier.</param>
/// <param name="Platform">The operating system or platform.</param>
/// <param name="Architecture">The target architecture.</param>
/// <param name="Variant">The resolver-specific configuration or variant.</param>
public readonly record struct DependencyResolutionContext(
    Utf8Slice ProjectOrigin,
    Utf8Slice Target,
    Utf8Slice Runtime,
    Utf8Slice Platform,
    Utf8Slice Architecture,
    Utf8Slice Variant);

/// <summary>Represents one package occurrence in one resolution context.</summary>
/// <param name="ContextIndex">The index of the owning resolution context.</param>
/// <param name="Name">The normalized package name.</param>
/// <param name="Version">The resolved package version.</param>
/// <param name="Ecosystem">The package ecosystem.</param>
/// <param name="Purl">The versioned package URL, when representable.</param>
/// <param name="SourceId">The resolver-native occurrence identifier, when present.</param>
/// <param name="DependencyType">The relationship to the root in this context.</param>
/// <param name="LicenseCandidates">The license claims supplied by the dependency input.</param>
/// <param name="Warnings">The input warnings associated with this occurrence.</param>
/// <param name="RepositoryUrl">The repository URL supplied by the dependency input.</param>
public readonly record struct DependencyOccurrence(
    int ContextIndex,
    Utf8Slice Name,
    Utf8Slice Version,
    string Ecosystem,
    Utf8Slice Purl,
    Utf8Slice SourceId,
    DependencyType DependencyType,
    LicenseCandidate[] LicenseCandidates,
    string[] Warnings,
    Utf8Slice RepositoryUrl = default);

/// <summary>Represents one directed dependency edge between package occurrences.</summary>
/// <param name="ContextIndex">The index of the owning resolution context.</param>
/// <param name="FromOccurrenceIndex">The dependent occurrence index.</param>
/// <param name="ToOccurrenceIndex">The dependency occurrence index.</param>
public readonly record struct DependencyEdge(int ContextIndex, int FromOccurrenceIndex, int ToOccurrenceIndex);

/// <summary>Contains a complete resolved dependency inventory before external enrichment.</summary>
/// <param name="Input">The dependency input descriptor.</param>
/// <param name="Contexts">The distinct resolution contexts.</param>
/// <param name="Occurrences">The package occurrences in deterministic input order.</param>
/// <param name="Edges">The dependency edges in deterministic input order.</param>
public readonly record struct DependencyInventory(
    ScanInputDescriptor Input,
    DependencyResolutionContext[] Contexts,
    DependencyOccurrence[] Occurrences,
    DependencyEdge[] Edges);

/// <summary>Contains a dependency inventory and its reconciled component results.</summary>
/// <param name="Inventory">The complete dependency inventory.</param>
/// <param name="Components">The reconciled component results in occurrence order.</param>
public readonly record struct ScanResult(DependencyInventory Inventory, ScanComponent[] Components);
