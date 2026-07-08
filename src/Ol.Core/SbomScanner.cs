using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core;

/// <summary>
/// Scans SBOM JSON documents into license reports.
/// </summary>
public static class SbomScanner
{
    /// <summary>
    /// Scans an SBOM from UTF-8 JSON bytes.
    /// </summary>
    /// <param name="sbomUtf8">The SBOM JSON bytes.</param>
    /// <param name="spdxLicenseIndex">The SPDX license lookup index.</param>
    /// <returns>The scan report.</returns>
    public static ScanReport Scan(ReadOnlySpan<byte> sbomUtf8, SpdxLicenseIndex spdxLicenseIndex)
    {
        var reader = new Utf8JsonReader(sbomUtf8, isFinalBlock: true, state: default);
        var format = DetectFormat(ref reader);

        return format switch
        {
            SbomFormat.CycloneDxJson => ScanCycloneDx(sbomUtf8, spdxLicenseIndex),
            SbomFormat.SpdxJson => ScanSpdx(sbomUtf8, spdxLicenseIndex),
            _ => throw new JsonException("Unsupported SBOM JSON format."),
        };
    }

    private static SbomFormat DetectFormat(ref Utf8JsonReader reader)
    {
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("bomFormat"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("CycloneDX"u8))
                {
                    return SbomFormat.CycloneDxJson;
                }
            }

            if (reader.ValueTextEquals("spdxVersion"u8))
            {
                return SbomFormat.SpdxJson;
            }
        }

        throw new JsonException("Unsupported SBOM JSON format.");
    }

    private static ScanReport ScanCycloneDx(ReadOnlySpan<byte> sbomUtf8, SpdxLicenseIndex spdxLicenseIndex)
    {
        var reader = new Utf8JsonReader(sbomUtf8, isFinalBlock: true, state: default);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var componentCount = 0;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("components"u8))
                {
                    continue;
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    continue;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    var component = ReadCycloneDxComponent(ref reader, spdxLicenseIndex);
                    if (componentCount == components.Length)
                    {
                        var expanded = ArrayPool<ScanComponent>.Shared.Rent(components.Length * 2);
                        components.AsSpan(0, componentCount).CopyTo(expanded);
                        ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
                        components = expanded;
                    }

                    components[componentCount] = component;
                    componentCount++;
                }
            }

            var result = new ScanComponent[componentCount];
            components.AsSpan(0, componentCount).CopyTo(result);
            return new ScanReport(SbomFormat.CycloneDxJson, result);
        }
        finally
        {
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
        }
    }

    private static ScanReport ScanSpdx(ReadOnlySpan<byte> sbomUtf8, SpdxLicenseIndex spdxLicenseIndex)
    {
        var reader = new Utf8JsonReader(sbomUtf8, isFinalBlock: true, state: default);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var componentCount = 0;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("packages"u8))
                {
                    continue;
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    continue;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    var component = ReadSpdxPackage(ref reader, spdxLicenseIndex);
                    if (componentCount == components.Length)
                    {
                        var expanded = ArrayPool<ScanComponent>.Shared.Rent(components.Length * 2);
                        components.AsSpan(0, componentCount).CopyTo(expanded);
                        ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
                        components = expanded;
                    }

                    components[componentCount] = component;
                    componentCount++;
                }
            }

            var result = new ScanComponent[componentCount];
            components.AsSpan(0, componentCount).CopyTo(result);
            return new ScanReport(SbomFormat.SpdxJson, result);
        }
        finally
        {
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
        }
    }

    private static ScanComponent ReadCycloneDxComponent(ref Utf8JsonReader reader, SpdxLicenseIndex spdxLicenseIndex)
    {
        var depth = reader.CurrentDepth;
        var name = string.Empty;
        var version = string.Empty;
        var purl = string.Empty;
        var license = string.Empty;
        var status = LicenseStatus.Unknown;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("name"u8))
            {
                name = ReadString(ref reader);
                continue;
            }

            if (reader.ValueTextEquals("version"u8))
            {
                version = ReadString(ref reader);
                continue;
            }

            if (reader.ValueTextEquals("purl"u8))
            {
                purl = ReadString(ref reader);
                continue;
            }

            if (reader.ValueTextEquals("licenses"u8))
            {
                (license, status) = ReadCycloneDxLicenses(ref reader, spdxLicenseIndex);
                continue;
            }

            reader.Read();
            reader.Skip();
        }

        if (license.Length == 0)
        {
            license = "-";
        }

        return new ScanComponent(name, version, license, GetEcosystem(purl), DependencyType.Unknown, status, purl);
    }

    private static ScanComponent ReadSpdxPackage(ref Utf8JsonReader reader, SpdxLicenseIndex spdxLicenseIndex)
    {
        var depth = reader.CurrentDepth;
        var name = string.Empty;
        var version = string.Empty;
        var purl = string.Empty;
        var declared = string.Empty;
        var concluded = string.Empty;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("name"u8))
            {
                name = ReadString(ref reader);
                continue;
            }

            if (reader.ValueTextEquals("versionInfo"u8))
            {
                version = ReadString(ref reader);
                continue;
            }

            if (reader.ValueTextEquals("licenseDeclared"u8))
            {
                declared = ReadString(ref reader);
                continue;
            }

            if (reader.ValueTextEquals("licenseConcluded"u8))
            {
                concluded = ReadString(ref reader);
                continue;
            }

            if (reader.ValueTextEquals("externalRefs"u8))
            {
                purl = ReadSpdxPurl(ref reader);
                continue;
            }

            reader.Read();
            reader.Skip();
        }

        var (license, status) = ReconcileLicenses(declared, concluded, spdxLicenseIndex);
        return new ScanComponent(name, version, license, GetEcosystem(purl), DependencyType.Unknown, status, purl);
    }

    private static string ReadSpdxPurl(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return string.Empty;
        }

        var purl = string.Empty;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var depth = reader.CurrentDepth;
            var referenceType = string.Empty;
            var referenceLocator = string.Empty;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("referenceType"u8))
                {
                    referenceType = ReadString(ref reader);
                    continue;
                }

                if (reader.ValueTextEquals("referenceLocator"u8))
                {
                    referenceLocator = ReadString(ref reader);
                    continue;
                }

                reader.Read();
                reader.Skip();
            }

            if (string.Equals(referenceType, "purl", StringComparison.OrdinalIgnoreCase))
            {
                purl = referenceLocator;
            }
        }

        return purl;
    }

    private static (string License, LicenseStatus Status) ReadCycloneDxLicenses(ref Utf8JsonReader reader, SpdxLicenseIndex spdxLicenseIndex)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return ("-", LicenseStatus.Unknown);
        }

        var validCount = 0;
        var ambiguousCount = 0;
        var firstValue = string.Empty;
        var secondValue = string.Empty;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var candidate = ReadCycloneDxLicenseCandidate(ref reader);
            if (IsUnknown(candidate))
            {
                continue;
            }

            if (spdxLicenseIndex.TryNormalizeLicenseId(candidate, out var normalized))
            {
                validCount++;
                if (validCount == 1)
                {
                    firstValue = normalized;
                }
                else if (!string.Equals(firstValue, normalized, StringComparison.Ordinal))
                {
                    secondValue = normalized;
                }
            }
            else
            {
                ambiguousCount++;
                if (firstValue.Length == 0)
                {
                    firstValue = candidate;
                }
            }
        }

        if (validCount == 1)
        {
            return (firstValue, LicenseStatus.Matched);
        }

        if (validCount > 1)
        {
            return (secondValue.Length == 0 ? firstValue : string.Concat(firstValue, ", ", secondValue, " (?)"), LicenseStatus.Ambiguous);
        }

        if (ambiguousCount > 0)
        {
            return (string.Concat(firstValue, " (?)"), LicenseStatus.Ambiguous);
        }

        return ("-", LicenseStatus.Unknown);
    }

    private static (string License, LicenseStatus Status) ReconcileLicenses(string first, string second, SpdxLicenseIndex spdxLicenseIndex)
    {
        var firstStatus = ClassifyLicense(first, spdxLicenseIndex, out var firstValue);
        var secondStatus = ClassifyLicense(second, spdxLicenseIndex, out var secondValue);

        if (firstStatus == LicenseStatus.Matched && secondStatus == LicenseStatus.Matched)
        {
            if (string.Equals(firstValue, secondValue, StringComparison.Ordinal))
            {
                return (firstValue, LicenseStatus.Matched);
            }

            return (string.Concat(firstValue, ", ", secondValue, " (?)"), LicenseStatus.Conflict);
        }

        if (firstStatus == LicenseStatus.Matched)
        {
            return (firstValue, LicenseStatus.Matched);
        }

        if (secondStatus == LicenseStatus.Matched)
        {
            return (secondValue, LicenseStatus.Matched);
        }

        if (firstStatus == LicenseStatus.Ambiguous)
        {
            return (string.Concat(firstValue, " (?)"), LicenseStatus.Ambiguous);
        }

        if (secondStatus == LicenseStatus.Ambiguous)
        {
            return (string.Concat(secondValue, " (?)"), LicenseStatus.Ambiguous);
        }

        return ("-", LicenseStatus.Unknown);
    }

    private static LicenseStatus ClassifyLicense(string value, SpdxLicenseIndex spdxLicenseIndex, out string normalized)
    {
        normalized = string.Empty;
        if (IsUnknown(value))
        {
            return LicenseStatus.Unknown;
        }

        if (spdxLicenseIndex.TryNormalizeLicenseId(value, out normalized))
        {
            return LicenseStatus.Matched;
        }

        normalized = value;
        return LicenseStatus.Ambiguous;
    }

    private static string ReadCycloneDxLicenseCandidate(ref Utf8JsonReader reader)
    {
        var depth = reader.CurrentDepth;
        var candidate = string.Empty;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("id"u8) || reader.ValueTextEquals("expression"u8) || reader.ValueTextEquals("name"u8))
            {
                candidate = ReadString(ref reader);
                continue;
            }

            reader.Read();
        }

        return candidate;
    }

    private static string ReadString(ref Utf8JsonReader reader)
    {
        reader.Read();
        return reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnknown(string value)
    {
        return value.Length == 0
            || string.Equals(value, "NOASSERTION", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "UNKNOWN", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetEcosystem(string purl)
    {
        return purl.AsSpan().StartsWith("pkg:npm/", StringComparison.Ordinal) ? "npm" : "-";
    }
}
