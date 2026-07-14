namespace Ol.Core;

/// <summary>
/// Parses an owned SBOM source buffer for one registered format.
/// </summary>
/// <param name="source">The owned source buffer.</param>
/// <param name="offset">The UTF-8 JSON offset after an optional BOM.</param>
/// <param name="spdxLicenseIndex">The SPDX lookup index.</param>
/// <returns>The parsed report.</returns>
public delegate ScanReport SbomFormatParser(byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex);

/// <summary>
/// Data required to identify and parse one SBOM JSON format.
/// </summary>
/// <param name="Format">The report format identifier.</param>
/// <param name="MarkerName">A required top-level JSON property name.</param>
/// <param name="MarkerValue">The required marker value, or empty when property presence is sufficient.</param>
/// <param name="Parser">The format-owned parser.</param>
public readonly record struct SbomFormatHandler(SbomFormat Format, ReadOnlyMemory<byte> MarkerName, ReadOnlyMemory<byte> MarkerValue, SbomFormatParser Parser);

/// <summary>
/// Immutable registry of SBOM JSON format handlers.
/// </summary>
public sealed class SbomFormatRegistry
{
    private readonly SbomFormatHandler[] handlers;

    /// <summary>
    /// Gets the built-in SBOM format handlers.
    /// </summary>
    public static SbomFormatRegistry Default { get; } = new([
        new(SbomFormat.CycloneDxJson, "bomFormat"u8.ToArray(), "CycloneDX"u8.ToArray(), SbomScanner.ScanCycloneDx),
        new(SbomFormat.SpdxJson, "spdxVersion"u8.ToArray(), Array.Empty<byte>(), SbomScanner.ScanSpdx),
    ]);

    /// <summary>
    /// Initializes a format registry.
    /// </summary>
    /// <param name="handlers">The format handlers to register.</param>
    public SbomFormatRegistry(SbomFormatHandler[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = new SbomFormatHandler[handlers.Length];
        for (var i = 0; i < this.handlers.Length; i++)
        {
            var handler = handlers[i];
            if (handler.Format.Name.Length == 0 || handler.MarkerName.Length == 0 || handler.Parser is null)
            {
                throw new ArgumentException("SBOM format handlers require a format, marker name, and parser.", nameof(handlers));
            }

            this.handlers[i] = handler with { MarkerName = handler.MarkerName.ToArray(), MarkerValue = handler.MarkerValue.ToArray() };
        }
    }

    internal ReadOnlySpan<SbomFormatHandler> Handlers => handlers;
}
