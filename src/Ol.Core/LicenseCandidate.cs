namespace Ol.Core;

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
public readonly record struct LicenseCandidate(
    string Source,
    string Kind,
    Utf8Slice Raw,
    Utf8Slice Normalized,
    LicenseStatus Status,
    bool Deprecated,
    string[] Warnings);
