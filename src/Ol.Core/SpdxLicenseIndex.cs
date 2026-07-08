using System.Collections.Frozen;

namespace Ol.Core;

/// <summary>
/// Provides SPDX license and exception identifier lookup for scan classification.
/// </summary>
public sealed class SpdxLicenseIndex
{
    private readonly FrozenDictionary<string, string> licenses;
    private readonly FrozenDictionary<string, string> exceptions;

    /// <summary>
    /// Initializes a new SPDX lookup index.
    /// </summary>
    /// <param name="licenses">Known SPDX license identifiers.</param>
    /// <param name="exceptions">Known SPDX exception identifiers.</param>
    public SpdxLicenseIndex(string[] licenses, string[] exceptions)
    {
        this.licenses = CreateLookup(licenses);
        this.exceptions = CreateLookup(exceptions);
    }

    /// <summary>
    /// Attempts to normalize a license identifier to official SPDX casing.
    /// </summary>
    /// <param name="licenseId">The candidate license identifier.</param>
    /// <param name="normalized">The normalized identifier when the lookup succeeds.</param>
    /// <returns><see langword="true" /> when the license identifier is known.</returns>
    public bool TryNormalizeLicenseId(string licenseId, out string normalized)
    {
        return licenses.TryGetValue(licenseId, out normalized!);
    }

    /// <summary>
    /// Attempts to normalize an exception identifier to official SPDX casing.
    /// </summary>
    /// <param name="exceptionId">The candidate exception identifier.</param>
    /// <param name="normalized">The normalized identifier when the lookup succeeds.</param>
    /// <returns><see langword="true" /> when the exception identifier is known.</returns>
    public bool TryNormalizeExceptionId(string exceptionId, out string normalized)
    {
        return exceptions.TryGetValue(exceptionId, out normalized!);
    }

    private static FrozenDictionary<string, string> CreateLookup(string[] identifiers)
    {
        var dictionary = new Dictionary<string, string>(identifiers.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < identifiers.Length; i++)
        {
            var identifier = identifiers[i];
            dictionary[identifier] = identifier;
        }

        return dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
