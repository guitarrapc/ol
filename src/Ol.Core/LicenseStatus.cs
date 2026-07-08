namespace Ol.Core;

/// <summary>
/// Describes the license classification result for a component.
/// </summary>
public enum LicenseStatus
{
    /// <summary>Available evidence yields a single valid license expression.</summary>
    Matched,

    /// <summary>Available evidence yields multiple different valid license expressions.</summary>
    Conflict,

    /// <summary>No usable license information is available.</summary>
    Unknown,

    /// <summary>License text exists but cannot be normalized without guessing.</summary>
    Ambiguous,

    /// <summary>A claimed SPDX expression is invalid.</summary>
    Invalid,

    /// <summary>Evidence could not be collected or processed.</summary>
    Error,
}