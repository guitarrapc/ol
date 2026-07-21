using Ol.Core.PackageManagers;
using Ol.Core.Sbom;
using Ol.Core.Spdx;
using System.Buffers;
using System.Text.Json;

namespace Ol.Core;

/// <summary>Parses one owned resolved dependency input into a normalized inventory.</summary>
/// <param name="source">The owned input buffer.</param>
/// <param name="offset">The UTF-8 offset after an optional BOM.</param>
/// <param name="spdxLicenseIndex">The SPDX lookup index.</param>
/// <param name="retainGraph">Whether occurrence and edge arrays are required by the caller.</param>
/// <returns>The normalized dependency inventory.</returns>
public delegate DependencyInventory DependencyInputParser(byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph);

/// <summary>Detects one non-JSON dependency input format without materializing source text.</summary>
/// <param name="inputUtf8">The dependency input after an optional UTF-8 BOM.</param>
public delegate bool DependencyInputDetector(ReadOnlySpan<byte> inputUtf8);

/// <summary>Defines how a top-level JSON property confirms one dependency input format.</summary>
public enum DependencyInputMarkerValueKind : byte
{
    /// <summary>The property value must be a JSON string.</summary>
    String,

    /// <summary>The property value must equal the registered UTF-8 string.</summary>
    StringEquals,

    /// <summary>The property value must equal the registered JSON number token.</summary>
    NumberEquals,

    /// <summary>The property value must be a JSON number.</summary>
    Number,

    /// <summary>The property value must be a JSON object.</summary>
    Object,

    /// <summary>The property value must be a JSON array.</summary>
    Array,
}

/// <summary>One required top-level JSON marker in a dependency input signature.</summary>
/// <param name="Name">The top-level property name.</param>
/// <param name="ValueKind">The required JSON value match.</param>
/// <param name="Value">The required UTF-8 value for an equality match, or empty.</param>
public readonly record struct DependencyInputMarker(
    ReadOnlyMemory<byte> Name,
    DependencyInputMarkerValueKind ValueKind,
    ReadOnlyMemory<byte> Value = default);

/// <summary>All markers that must match to identify one dependency input format.</summary>
/// <param name="RequiredMarkers">The required top-level markers.</param>
public readonly record struct DependencyInputSignature(ReadOnlyMemory<DependencyInputMarker> RequiredMarkers);

/// <summary>Defines how package identities from repeated inputs are compared.</summary>
public enum DependencyComponentIdentityComparison : byte
{
    /// <summary>Compare canonical package URLs by ordinal UTF-8 bytes.</summary>
    Ordinal,

    /// <summary>Compare package URLs with ASCII case folding.</summary>
    AsciiIgnoreCase,

    /// <summary>Compare canonical package URLs and resolver-native source identifiers by ordinal UTF-8 bytes.</summary>
    OrdinalWithSourceId,
}

/// <summary>Contains all data required to identify and parse one dependency input format.</summary>
/// <param name="Kind">The input family.</param>
/// <param name="Format">The public input format.</param>
/// <param name="Signature">The deterministic content signature.</param>
/// <param name="Parser">The format-owned parser.</param>
/// <param name="DirectoryFileNames">Exact file names discovered recursively for a directory input.</param>
/// <param name="ComponentIdentityComparison">The package identity comparison used when combining repeated inputs of this format.</param>
/// <param name="Detector">The format-owned detector for non-JSON input, or null for a JSON signature.</param>
public readonly record struct DependencyInputHandler(
    ScanInputKind Kind,
    ScanInputFormat Format,
    DependencyInputSignature Signature,
    DependencyInputParser Parser,
    ReadOnlyMemory<string> DirectoryFileNames = default,
    DependencyComponentIdentityComparison ComponentIdentityComparison = DependencyComponentIdentityComparison.Ordinal,
    DependencyInputDetector? Detector = null);

/// <summary>
/// Immutable registry of resolved dependency input handlers.
/// </summary>
public sealed class DependencyInputRegistry
{
    private readonly DependencyInputHandler[] handlers;

    /// <summary>
    /// Gets the built-in resolved dependency input handlers.
    /// </summary>
    public static DependencyInputRegistry Default { get; } = new([
        // CycloneDX - SBOM
        new(ScanInputKind.Sbom, ScanInputFormat.CycloneDx, new(new DependencyInputMarker[] { new("bomFormat"u8.ToArray(), DependencyInputMarkerValueKind.StringEquals, "CycloneDX"u8.ToArray()) }), SbomInputParser.ParseCycloneDxInventory),
        // SPDX - SBOM
        new(ScanInputKind.Sbom, ScanInputFormat.Spdx, new(new DependencyInputMarker[] { new("spdxVersion"u8.ToArray(), DependencyInputMarkerValueKind.String) }), SbomInputParser.ParseSpdxInventory),
        // NuGet - Package Manager
        new(ScanInputKind.PackageManager, ScanInputFormat.NuGetAssets, new(new DependencyInputMarker[] {
            new("version"u8.ToArray(), DependencyInputMarkerValueKind.Number),
            new("targets"u8.ToArray(), DependencyInputMarkerValueKind.Object),
            new("libraries"u8.ToArray(), DependencyInputMarkerValueKind.Object),
            new("project"u8.ToArray(), DependencyInputMarkerValueKind.Object),
        }), NuGetAssetsInputParser.Parse, new[] { "project.assets.json" }, DependencyComponentIdentityComparison.AsciiIgnoreCase),
        // NPM - Package Manager
        new(ScanInputKind.PackageManager, ScanInputFormat.NpmPackageLock, new(new DependencyInputMarker[] {
            new("lockfileVersion"u8.ToArray(), DependencyInputMarkerValueKind.Number),
            new("packages"u8.ToArray(), DependencyInputMarkerValueKind.Object),
        }), NpmPackageLockInputParser.Parse, new[] { "package-lock.json" }, DependencyComponentIdentityComparison.OrdinalWithSourceId),
        // PNPM - Package Manager
        new(ScanInputKind.PackageManager, ScanInputFormat.PnpmLock, default, PnpmLockInputParser.Parse, new[] { "pnpm-lock.yaml" }, DependencyComponentIdentityComparison.OrdinalWithSourceId, PnpmLockInputParser.Detect),
        // Yarn - Package Manager
        new(ScanInputKind.PackageManager, ScanInputFormat.YarnClassicLock, default, YarnClassicLockInputParser.Parse, new[] { "yarn.lock" }, DependencyComponentIdentityComparison.OrdinalWithSourceId, YarnClassicLockInputParser.Detect),
        // Yarn - Package Manager
        new(ScanInputKind.PackageManager, ScanInputFormat.YarnBerryLock, default, YarnBerryLockInputParser.Parse, new[] { "yarn.lock" }, DependencyComponentIdentityComparison.OrdinalWithSourceId, YarnBerryLockInputParser.Detect),
        // Cargo - Package Manager
        new(ScanInputKind.PackageManager, ScanInputFormat.CargoMetadata, new(new DependencyInputMarker[] {
            new("packages"u8.ToArray(), DependencyInputMarkerValueKind.Array),
            new("workspace_members"u8.ToArray(), DependencyInputMarkerValueKind.Array),
            new("resolve"u8.ToArray(), DependencyInputMarkerValueKind.Object),
            new("target_directory"u8.ToArray(), DependencyInputMarkerValueKind.String),
            new("version"u8.ToArray(), DependencyInputMarkerValueKind.NumberEquals, "1"u8.ToArray()),
            new("workspace_root"u8.ToArray(), DependencyInputMarkerValueKind.String),
        }), CargoMetadataInputParser.Parse, new[] { "cargo-metadata.json" }, DependencyComponentIdentityComparison.OrdinalWithSourceId),
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
                || handler.Signature.RequiredMarkers.Length > 64
                || (handler.Signature.RequiredMarkers.Length == 0 && handler.Detector is null)
                || (handler.Signature.RequiredMarkers.Length != 0 && handler.Detector is not null)
                || handler.Parser is null)
            {
                throw new ArgumentException("Dependency input handlers require a kind, format, signature, and parser.", nameof(handlers));
            }

            for (var registeredIndex = 0; registeredIndex < i; registeredIndex++)
            {
                if (string.Equals(this.handlers[registeredIndex].Format.Name, handler.Format.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Duplicate dependency input format: {handler.Format.Name}", nameof(handlers));
                }
            }

            var markers = handler.Signature.RequiredMarkers.Span;
            var ownedMarkers = new DependencyInputMarker[markers.Length];
            for (var markerIndex = 0; markerIndex < markers.Length; markerIndex++)
            {
                var marker = markers[markerIndex];
                var requiresValue = marker.ValueKind is DependencyInputMarkerValueKind.StringEquals or DependencyInputMarkerValueKind.NumberEquals;
                if (marker.Name.Length == 0 || requiresValue != (marker.Value.Length != 0))
                {
                    throw new ArgumentException("Dependency input signature markers require a name and a value only for equality matches.", nameof(handlers));
                }

                for (var registeredMarkerIndex = 0; registeredMarkerIndex < markerIndex; registeredMarkerIndex++)
                {
                    if (ownedMarkers[registeredMarkerIndex].Name.Span.SequenceEqual(marker.Name.Span))
                    {
                        throw new ArgumentException("Dependency input signatures cannot repeat a marker name.", nameof(handlers));
                    }
                }

                ownedMarkers[markerIndex] = marker with { Name = marker.Name.ToArray(), Value = marker.Value.ToArray() };
            }

            var directoryFileNames = handler.DirectoryFileNames.Span;
            var ownedDirectoryFileNames = new string[directoryFileNames.Length];
            for (var fileIndex = 0; fileIndex < directoryFileNames.Length; fileIndex++)
            {
                var fileName = directoryFileNames[fileIndex];
                if (string.IsNullOrWhiteSpace(fileName)
                    || fileName.Contains(Path.DirectorySeparatorChar)
                    || fileName.Contains(Path.AltDirectorySeparatorChar)
                    || fileName.Contains('*')
                    || fileName.Contains('?'))
                {
                    throw new ArgumentException("Dependency input directory file names must be exact file names.", nameof(handlers));
                }

                for (var registeredFileIndex = 0; registeredFileIndex < fileIndex; registeredFileIndex++)
                {
                    if (string.Equals(ownedDirectoryFileNames[registeredFileIndex], fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("Dependency input handlers cannot repeat a directory file name.", nameof(handlers));
                    }
                }

                ownedDirectoryFileNames[fileIndex] = fileName;
            }

            this.handlers[i] = handler with
            {
                Signature = new DependencyInputSignature(ownedMarkers),
                DirectoryFileNames = ownedDirectoryFileNames,
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

    /// <summary>Gets the registered handlers in deterministic registration order.</summary>
    public ReadOnlySpan<DependencyInputHandler> RegisteredHandlers => handlers;
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
        handler = DetectFormat(inputUtf8.AsSpan(offset), inputs);
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

    private static DependencyInputHandler DetectFormat(ReadOnlySpan<byte> inputUtf8, DependencyInputRegistry inputs)
    {
        var handlers = inputs.Handlers;
        var selected = -1;
        for (var handlerIndex = 0; handlerIndex < handlers.Length; handlerIndex++)
        {
            var detector = handlers[handlerIndex].Detector;
            if (detector is null || !detector(inputUtf8))
            {
                continue;
            }

            if (selected >= 0)
            {
                throw new InvalidOperationException("Ambiguous dependency input format: multiple registered format signatures matched.");
            }

            selected = handlerIndex;
        }

        if (!LooksLikeJsonObject(inputUtf8))
        {
            if (selected < 0)
            {
                throw new InvalidOperationException("Unsupported dependency input format: no registered format signature matched.");
            }

            return handlers[selected];
        }

        const int MaxStackHandlers = 16;
        ulong[]? rentedMatched = null;
        int[]? rentedPending = null;
        Span<ulong> matched = handlers.Length <= MaxStackHandlers
            ? stackalloc ulong[MaxStackHandlers]
            : (rentedMatched = ArrayPool<ulong>.Shared.Rent(handlers.Length));
        Span<int> pending = handlers.Length <= MaxStackHandlers
            ? stackalloc int[MaxStackHandlers]
            : (rentedPending = ArrayPool<int>.Shared.Rent(handlers.Length));
        matched = matched[..handlers.Length];
        pending = pending[..handlers.Length];
        matched.Clear();
        try
        {
            var reader = new Utf8JsonReader(inputUtf8, isFinalBlock: true, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                pending.Fill(-1);
                var hasPending = false;
                var propertyLength = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
                for (var handlerIndex = 0; handlerIndex < handlers.Length; handlerIndex++)
                {
                    var markers = handlers[handlerIndex].Signature.RequiredMarkers.Span;
                    for (var markerIndex = 0; markerIndex < markers.Length; markerIndex++)
                    {
                        var marker = markers[markerIndex];
                        if ((matched[handlerIndex] & (1UL << markerIndex)) == 0
                            && (reader.ValueIsEscaped || propertyLength == marker.Name.Length)
                            && reader.ValueTextEquals(marker.Name.Span))
                        {
                            pending[handlerIndex] = markerIndex;
                            hasPending = true;
                            break;
                        }
                    }
                }

                if (!hasPending || !reader.Read())
                {
                    continue;
                }

                for (var handlerIndex = 0; handlerIndex < handlers.Length; handlerIndex++)
                {
                    var markerIndex = pending[handlerIndex];
                    if (markerIndex < 0)
                    {
                        continue;
                    }

                    var marker = handlers[handlerIndex].Signature.RequiredMarkers.Span[markerIndex];
                    var matches = marker.ValueKind switch
                    {
                        DependencyInputMarkerValueKind.String => reader.TokenType == JsonTokenType.String,
                        DependencyInputMarkerValueKind.StringEquals => reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(marker.Value.Span),
                        DependencyInputMarkerValueKind.NumberEquals => reader.TokenType == JsonTokenType.Number && !reader.HasValueSequence && reader.ValueSpan.SequenceEqual(marker.Value.Span),
                        DependencyInputMarkerValueKind.Number => reader.TokenType == JsonTokenType.Number,
                        DependencyInputMarkerValueKind.Object => reader.TokenType == JsonTokenType.StartObject,
                        DependencyInputMarkerValueKind.Array => reader.TokenType == JsonTokenType.StartArray,
                        _ => false,
                    };
                    if (matches)
                    {
                        matched[handlerIndex] |= 1UL << markerIndex;
                    }
                }
            }

            for (var handlerIndex = 0; handlerIndex < handlers.Length; handlerIndex++)
            {
                var markerCount = handlers[handlerIndex].Signature.RequiredMarkers.Length;
                if (markerCount == 0)
                {
                    continue;
                }

                var required = markerCount == 64 ? ulong.MaxValue : (1UL << markerCount) - 1;
                if (matched[handlerIndex] != required)
                {
                    continue;
                }

                if (selected >= 0)
                {
                    throw new JsonException("Ambiguous dependency input format: multiple registered format signatures matched.");
                }

                selected = handlerIndex;
            }

            if (selected < 0)
            {
                throw new JsonException("Unsupported dependency input format: no registered format signature matched.");
            }

            return handlers[selected];
        }
        finally
        {
            if (rentedMatched is not null)
            {
                ArrayPool<ulong>.Shared.Return(rentedMatched);
            }

            if (rentedPending is not null)
            {
                ArrayPool<int>.Shared.Return(rentedPending);
            }
        }
    }

    private static bool LooksLikeJsonObject(ReadOnlySpan<byte> inputUtf8)
    {
        for (var i = 0; i < inputUtf8.Length; i++)
        {
            var value = inputUtf8[i];
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return value == (byte)'{';
        }

        return false;
    }
}
