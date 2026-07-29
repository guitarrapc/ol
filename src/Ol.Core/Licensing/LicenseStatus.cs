namespace Ol.Core.Licensing;

/// <summary>
/// Describes the license classification result for a component.
/// </summary>
/// <remarks>
/// Values are explicit and <see cref="Unknown"/> is zero on purpose. A default-constructed candidate or
/// component must never read as resolved: an input that declares no license has to surface as unresolved,
/// not as an empty <see cref="Matched"/> expression that a policy check would then be unable to explain.
/// Report and baseline documents persist these as name tokens, so the numbers are free to be pinned here.
/// </remarks>
public enum LicenseStatus
{
    /// <summary>No usable license information is available. The safe default for an unset status.</summary>
    Unknown = 0,

    /// <summary>Available evidence yields a single valid license expression.</summary>
    Matched = 1,

    /// <summary>Available evidence yields multiple different valid license expressions.</summary>
    Conflict = 2,

    /// <summary>License text exists but cannot be normalized without guessing.</summary>
    Ambiguous = 3,

    /// <summary>A claimed SPDX expression is invalid.</summary>
    Invalid = 4,

    /// <summary>Evidence could not be collected or processed.</summary>
    Error = 5,
}

/// <summary>Provides stable UTF-8 license status identifiers without string allocation.</summary>
public static class LicenseStatusIdentifiers
{
    /// <summary>Parses a persisted status token.</summary>
    public static bool TryParse(ReadOnlySpan<byte> value, out LicenseStatus status)
    {
        switch (value)
        {
            case var v when v.SequenceEqual("matched"u8): status = LicenseStatus.Matched; return true;
            case var v when v.SequenceEqual("conflict"u8): status = LicenseStatus.Conflict; return true;
            case var v when v.SequenceEqual("unknown"u8): status = LicenseStatus.Unknown; return true;
            case var v when v.SequenceEqual("ambiguous"u8): status = LicenseStatus.Ambiguous; return true;
            case var v when v.SequenceEqual("invalid"u8): status = LicenseStatus.Invalid; return true;
            case var v when v.SequenceEqual("error"u8): status = LicenseStatus.Error; return true;
            default: status = LicenseStatus.Unknown; return false;
        }
    }

    public static ReadOnlySpan<byte> ToUtf8(this LicenseStatus value) => value switch
    {
        LicenseStatus.Matched => "matched"u8,
        LicenseStatus.Conflict => "conflict"u8,
        LicenseStatus.Unknown => "unknown"u8,
        LicenseStatus.Ambiguous => "ambiguous"u8,
        LicenseStatus.Invalid => "invalid"u8,
        LicenseStatus.Error => "error"u8,
        _ => default,
    };
}
