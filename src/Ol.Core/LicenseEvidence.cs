namespace Ol.Core;

/// <summary>
/// Represents normalized license evidence retained for report consumers.
/// </summary>
/// <param name="Source">The evidence source.</param>
/// <param name="Kind">The source field or license value kind.</param>
/// <param name="Raw">The original license value.</param>
/// <param name="Normalized">The normalized SPDX expression, when valid.</param>
/// <param name="Status">The classification of this evidence.</param>
/// <param name="Warnings">Warnings associated with this evidence.</param>
public readonly record struct LicenseEvidence(
    string Source,
    string Kind,
    Utf8Slice Raw,
    string Normalized,
    LicenseStatus Status,
    string[] Warnings);
