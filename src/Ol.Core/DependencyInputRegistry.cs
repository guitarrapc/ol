using System.Text.Json;

namespace Ol.Core;

/// <summary>Parses one owned resolved dependency input into a normalized inventory.</summary>
/// <param name="source">The owned input buffer.</param>
/// <param name="offset">The UTF-8 offset after an optional BOM.</param>
/// <param name="spdxLicenseIndex">The SPDX lookup index.</param>
/// <param name="retainGraph">Whether occurrence and edge arrays are required by the caller.</param>
/// <returns>The normalized dependency inventory.</returns>
public delegate DependencyInventory DependencyInputParser(byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph);

/// <summary>Contains all data required to identify and parse one dependency input format.</summary>
/// <param name="Kind">The input family.</param>
/// <param name="Format">The public input format.</param>
/// <param name="MarkerName">A required top-level JSON property name.</param>
/// <param name="MarkerValue">The required marker value, or empty when property presence is sufficient.</param>
/// <param name="Parser">The format-owned parser.</param>
/// <param name="LegacySbomFormat">The legacy SBOM report format, when applicable.</param>
public readonly record struct DependencyInputHandler(
    ScanInputKind Kind,
    ScanInputFormat Format,
    ReadOnlyMemory<byte> MarkerName,
    ReadOnlyMemory<byte> MarkerValue,
    DependencyInputParser Parser,
    SbomFormat LegacySbomFormat = default);

/// <summary>Immutable registry of resolved dependency input handlers.</summary>
public sealed class DependencyInputRegistry
{
    private readonly DependencyInputHandler[] handlers;

    /// <summary>Gets the built-in resolved dependency input handlers.</summary>
    public static DependencyInputRegistry Default { get; } = new([
        new(ScanInputKind.Sbom, ScanInputFormat.CycloneDx, "bomFormat"u8.ToArray(), "CycloneDX"u8.ToArray(), SbomScanner.ParseCycloneDxInventory, SbomFormat.CycloneDxJson),
        new(ScanInputKind.Sbom, ScanInputFormat.Spdx, "spdxVersion"u8.ToArray(), Array.Empty<byte>(), SbomScanner.ParseSpdxInventory, SbomFormat.SpdxJson),
        new(ScanInputKind.PackageManager, ScanInputFormat.NuGetAssets, "targets"u8.ToArray(), Array.Empty<byte>(), NuGetAssetsScanner.Parse),
    ]);

    /// <summary>Initializes a registry from distinct format handlers.</summary>
    public DependencyInputRegistry(DependencyInputHandler[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = new DependencyInputHandler[handlers.Length];
        for (var i = 0; i < handlers.Length; i++)
        {
            var handler = handlers[i];
            if (string.IsNullOrEmpty(handler.Kind.Name)
                || string.IsNullOrEmpty(handler.Format.Name)
                || string.IsNullOrEmpty(handler.Format.Parser)
                || handler.MarkerName.Length == 0
                || handler.Parser is null)
            {
                throw new ArgumentException("Dependency input handlers require a kind, format, marker, and parser.", nameof(handlers));
            }

            for (var registeredIndex = 0; registeredIndex < i; registeredIndex++)
            {
                if (string.Equals(this.handlers[registeredIndex].Format.Name, handler.Format.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Duplicate dependency input format: {handler.Format.Name}", nameof(handlers));
                }
            }

            this.handlers[i] = handler with
            {
                MarkerName = handler.MarkerName.ToArray(),
                MarkerValue = handler.MarkerValue.ToArray(),
            };
        }
    }

    /// <summary>Finds a registered handler by public input format.</summary>
    public bool TryGetInputFormat(string name, out DependencyInputHandler handler)
    {
        for (var i = 0; i < handlers.Length; i++)
        {
            if (string.Equals(handlers[i].Format.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                handler = handlers[i];
                return true;
            }
        }

        handler = default;
        return false;
    }

    internal ReadOnlySpan<DependencyInputHandler> Handlers => handlers;
}

/// <summary>Detects and parses registered resolved dependency inputs.</summary>
public static class DependencyInputScanner
{
    /// <summary>Scans UTF-8 dependency input bytes, copying them into owned storage.</summary>
    public static DependencyInventory Scan(ReadOnlySpan<byte> inputUtf8, SpdxLicenseIndex spdxLicenseIndex, DependencyInputRegistry? inputs = null, ScanInputFormat expectedFormat = default)
        => Scan(inputUtf8.ToArray(), spdxLicenseIndex, inputs, expectedFormat);

    /// <summary>Scans an owned UTF-8 dependency input buffer without copying source text.</summary>
    public static DependencyInventory Scan(byte[] inputUtf8, SpdxLicenseIndex spdxLicenseIndex, DependencyInputRegistry? inputs = null, ScanInputFormat expectedFormat = default)
        => ScanCore(inputUtf8, spdxLicenseIndex, inputs ?? DependencyInputRegistry.Default, expectedFormat, retainGraph: true, out _);

    internal static DependencyInventory ScanCore(byte[] inputUtf8, SpdxLicenseIndex spdxLicenseIndex, DependencyInputRegistry inputs, ScanInputFormat expectedFormat, bool retainGraph, out DependencyInputHandler handler)
    {
        ArgumentNullException.ThrowIfNull(inputUtf8);
        var offset = HasUtf8Bom(inputUtf8) ? 3 : 0;
        var reader = new Utf8JsonReader(inputUtf8.AsSpan(offset), isFinalBlock: true, state: default);
        handler = DetectFormat(ref reader, inputs);
        if (!string.IsNullOrEmpty(expectedFormat.Name) && handler.Format != expectedFormat)
        {
            throw new InvalidOperationException($"Input format {expectedFormat.Name} does not match the detected {handler.Format.Name} format.");
        }

        var inventory = handler.Parser(inputUtf8, offset, spdxLicenseIndex, retainGraph);
        var descriptor = inventory.Input with { Kind = handler.Kind, Format = handler.Format };
        return inventory with { Input = descriptor };
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> inputUtf8)
        => inputUtf8.Length >= 3 && inputUtf8[0] == 0xEF && inputUtf8[1] == 0xBB && inputUtf8[2] == 0xBF;

    private static DependencyInputHandler DetectFormat(ref Utf8JsonReader reader, DependencyInputRegistry inputs)
    {
        var handlers = inputs.Handlers;
        var selected = -1;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            for (var i = 0; i < handlers.Length; i++)
            {
                var handler = handlers[i];
                if (!reader.ValueTextEquals(handler.MarkerName.Span))
                {
                    continue;
                }

                if (handler.MarkerValue.Length != 0)
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(handler.MarkerValue.Span))
                    {
                        continue;
                    }
                }

                if (selected >= 0 && selected != i)
                {
                    throw new JsonException("Unsupported or ambiguous dependency input format.");
                }

                selected = i;
            }
        }

        if (selected < 0)
        {
            throw new JsonException("Unsupported or ambiguous dependency input format.");
        }

        return handlers[selected];
    }
}
