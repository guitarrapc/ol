namespace Ol.Core;

/// <summary>
/// Represents the result of scanning an SBOM.
/// </summary>
/// <param name="Format">The detected SBOM format.</param>
/// <param name="Components">The scanned components.</param>
public readonly record struct ScanReport(SbomFormat Format, ScanComponent[] Components);
