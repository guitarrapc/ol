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
        sbomUtf8 = SkipUtf8Bom(sbomUtf8);
        var reader = new Utf8JsonReader(sbomUtf8, isFinalBlock: true, state: default);
        var format = DetectFormat(ref reader);

        return format switch
        {
            SbomFormat.CycloneDxJson => ScanCycloneDx(sbomUtf8, spdxLicenseIndex),
            SbomFormat.SpdxJson => ScanSpdx(sbomUtf8, spdxLicenseIndex),
            _ => throw new JsonException("Unsupported SBOM JSON format."),
        };
    }

    private static ReadOnlySpan<byte> SkipUtf8Bom(ReadOnlySpan<byte> sbomUtf8)
    {
        return sbomUtf8.Length >= 3 && sbomUtf8[0] == 0xEF && sbomUtf8[1] == 0xBB && sbomUtf8[2] == 0xBF
            ? sbomUtf8[3..]
            : sbomUtf8;
    }

    private static SbomFormat DetectFormat(ref Utf8JsonReader reader)
    {
        var isCycloneDx = false;
        var isSpdx = false;
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
                    isCycloneDx = true;
                }
            }

            if (reader.ValueTextEquals("spdxVersion"u8))
            {
                isSpdx = true;
            }
        }

        if (isCycloneDx == isSpdx)
        {
            throw new JsonException("Unsupported or ambiguous SBOM JSON format.");
        }

        if (isCycloneDx)
        {
            return SbomFormat.CycloneDxJson;
        }

        if (isSpdx)
        {
            return SbomFormat.SpdxJson;
        }

        throw new JsonException("Unsupported SBOM JSON format.");
    }

    private static ScanReport ScanCycloneDx(ReadOnlySpan<byte> sbomUtf8, SpdxLicenseIndex spdxLicenseIndex)
    {
        var reader = new Utf8JsonReader(sbomUtf8, isFinalBlock: true, state: default);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var dependencyRefs = ArrayPool<DependencyEdge>.Shared.Rent(16);
        var componentCount = 0;
        var dependencyCount = 0;
        var hasRoot = false;
        var rootRef = string.Empty;
        var specVersion = string.Empty;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("metadata"u8))
                {
                    var rootComponent = ReadCycloneDxMetadataComponent(ref reader, spdxLicenseIndex);
                    if (rootComponent.SourceId.Length != 0)
                    {
                        hasRoot = true;
                        rootRef = rootComponent.SourceId;
                        EnsureComponentCapacity(ref components, componentCount);
                        components[componentCount] = rootComponent with { DependencyType = DependencyType.Root };
                        componentCount++;
                    }

                    continue;
                }

                if (reader.ValueTextEquals("specVersion"u8))
                {
                    specVersion = ReadString(ref reader);
                    continue;
                }

                if (reader.ValueTextEquals("dependencies"u8))
                {
                    ReadCycloneDxDependencies(ref reader, ref dependencyRefs, ref dependencyCount);
                    continue;
                }

                if (!reader.ValueTextEquals("components"u8))
                {
                    reader.Read();
                    reader.Skip();
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
                    EnsureComponentCapacity(ref components, componentCount);
                    components[componentCount] = component;
                    componentCount++;
                }
            }

            var result = new ScanComponent[componentCount];
            components.AsSpan(0, componentCount).CopyTo(result);
            if (hasRoot)
            {
                ApplyDependencyTypes(result, rootRef, dependencyRefs.AsSpan(0, dependencyCount));
            }

            return new ScanReport(SbomFormat.CycloneDxJson, specVersion, result);
        }
        finally
        {
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyEdge>.Shared.Return(dependencyRefs, clearArray: true);
        }
    }

    private static ScanReport ScanSpdx(ReadOnlySpan<byte> sbomUtf8, SpdxLicenseIndex spdxLicenseIndex)
    {
        var reader = new Utf8JsonReader(sbomUtf8, isFinalBlock: true, state: default);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var dependencyRefs = ArrayPool<DependencyEdge>.Shared.Rent(16);
        var componentCount = 0;
        var dependencyCount = 0;
        var rootRef = string.Empty;
        var specVersion = string.Empty;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("relationships"u8))
                {
                    ReadSpdxRelationships(ref reader, ref dependencyRefs, ref dependencyCount, ref rootRef);
                    continue;
                }

                if (reader.ValueTextEquals("spdxVersion"u8))
                {
                    specVersion = ReadString(ref reader);
                    continue;
                }

                if (!reader.ValueTextEquals("packages"u8))
                {
                    reader.Read();
                    reader.Skip();
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
            if (rootRef.Length != 0)
            {
                ApplyDependencyTypes(result, rootRef, dependencyRefs.AsSpan(0, dependencyCount));
            }

            return new ScanReport(SbomFormat.SpdxJson, specVersion, result);
        }
        finally
        {
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyEdge>.Shared.Return(dependencyRefs, clearArray: true);
        }
    }

    private static ScanComponent ReadCycloneDxComponent(ref Utf8JsonReader reader, SpdxLicenseIndex spdxLicenseIndex)
    {
        var depth = reader.CurrentDepth;
        var sourceId = string.Empty;
        var name = string.Empty;
        var version = string.Empty;
        var purl = string.Empty;
        var license = string.Empty;
        var status = LicenseStatus.Unknown;
        var candidates = Array.Empty<LicenseCandidate>();

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

            if (reader.ValueTextEquals("bom-ref"u8))
            {
                sourceId = ReadString(ref reader);
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
                (license, status, candidates) = ReadCycloneDxLicenses(ref reader, spdxLicenseIndex);
                continue;
            }

            reader.Read();
            reader.Skip();
        }

        if (license.Length == 0)
        {
            license = "-";
        }

        return CreateScanComponent(name, version, license, GetEcosystem(purl), DependencyType.Unknown, status, purl, sourceId, candidates);
    }

    private static ScanComponent ReadCycloneDxMetadataComponent(ref Utf8JsonReader reader, SpdxLicenseIndex spdxLicenseIndex)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return default;
        }

        var depth = reader.CurrentDepth;
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

            if (reader.ValueTextEquals("component"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    return ReadCycloneDxComponent(ref reader, spdxLicenseIndex);
                }

                reader.Skip();
                continue;
            }

            reader.Read();
            reader.Skip();
        }

        return default;
    }

    private static ScanComponent ReadSpdxPackage(ref Utf8JsonReader reader, SpdxLicenseIndex spdxLicenseIndex)
    {
        var depth = reader.CurrentDepth;
        var sourceId = string.Empty;
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

            if (reader.ValueTextEquals("SPDXID"u8))
            {
                sourceId = ReadString(ref reader);
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
        var candidates = new[]
        {
            CreateLicenseCandidate("sbom", "declared", declared, spdxLicenseIndex),
            CreateLicenseCandidate("sbom", "concluded", concluded, spdxLicenseIndex),
        };
        return CreateScanComponent(name, version, license, GetEcosystem(purl), DependencyType.Unknown, status, purl, sourceId, candidates);
    }

    private static void ReadSpdxRelationships(ref Utf8JsonReader reader, ref DependencyEdge[] dependencies, ref int dependencyCount, ref string rootRef)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var depth = reader.CurrentDepth;
            var element = string.Empty;
            var type = string.Empty;
            var related = string.Empty;
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

                if (reader.ValueTextEquals("spdxElementId"u8))
                {
                    element = ReadString(ref reader);
                    continue;
                }

                if (reader.ValueTextEquals("relationshipType"u8))
                {
                    type = ReadString(ref reader);
                    continue;
                }

                if (reader.ValueTextEquals("relatedSpdxElement"u8))
                {
                    related = ReadString(ref reader);
                    continue;
                }

                reader.Read();
                reader.Skip();
            }

            if (string.Equals(type, "DESCRIBES", StringComparison.OrdinalIgnoreCase))
            {
                rootRef = related;
            }
            else if (string.Equals(type, "DEPENDS_ON", StringComparison.OrdinalIgnoreCase))
            {
                AddDependencyEdge(ref dependencies, ref dependencyCount, element, related);
            }
            else if (string.Equals(type, "DEPENDENCY_OF", StringComparison.OrdinalIgnoreCase))
            {
                AddDependencyEdge(ref dependencies, ref dependencyCount, related, element);
            }
        }
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

    private static (string License, LicenseStatus Status, LicenseCandidate[] Candidates) ReadCycloneDxLicenses(ref Utf8JsonReader reader, SpdxLicenseIndex spdxLicenseIndex)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return ("-", LicenseStatus.Unknown, []);
        }

        var candidateBuffer = ArrayPool<LicenseCandidate>.Shared.Rent(4);
        var candidateCount = 0;
        var validCount = 0;
        var ambiguousCount = 0;
        var invalidCount = 0;
        var firstValue = string.Empty;
        var secondValue = string.Empty;

        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                var (kind, raw) = ReadCycloneDxLicenseCandidate(ref reader);
                if (candidateCount == candidateBuffer.Length)
                {
                    var expanded = ArrayPool<LicenseCandidate>.Shared.Rent(candidateBuffer.Length * 2);
                    candidateBuffer.AsSpan(0, candidateCount).CopyTo(expanded);
                    ArrayPool<LicenseCandidate>.Shared.Return(candidateBuffer, clearArray: true);
                    candidateBuffer = expanded;
                }

                var candidate = CreateLicenseCandidate("sbom", kind, raw, spdxLicenseIndex);
                candidateBuffer[candidateCount] = candidate;
                candidateCount++;
                if (candidate.Status == LicenseStatus.Unknown)
                {
                    continue;
                }

                if (candidate.Status == LicenseStatus.Matched)
                {
                    validCount++;
                    if (validCount == 1)
                    {
                        firstValue = candidate.Normalized;
                    }
                    else if (!string.Equals(firstValue, candidate.Normalized, StringComparison.Ordinal))
                    {
                        secondValue = candidate.Normalized;
                    }
                }
                else if (candidate.Status == LicenseStatus.Invalid)
                {
                    invalidCount++;
                    if (firstValue.Length == 0)
                    {
                        firstValue = candidate.Raw;
                    }
                }
                else
                {
                    ambiguousCount++;
                    if (firstValue.Length == 0)
                    {
                        firstValue = candidate.Raw;
                    }
                }
            }

            var candidates = candidateBuffer.AsSpan(0, candidateCount).ToArray();
            if (validCount == 1)
            {
                return (firstValue, LicenseStatus.Matched, candidates);
            }

            if (validCount > 1)
            {
                return (secondValue.Length == 0 ? firstValue : string.Concat(firstValue, ", ", secondValue, " (?)"), LicenseStatus.Ambiguous, candidates);
            }

            if (invalidCount > 0)
            {
                return (string.Concat(firstValue, " (?)"), LicenseStatus.Invalid, candidates);
            }

            if (ambiguousCount > 0)
            {
                return (string.Concat(firstValue, " (?)"), LicenseStatus.Ambiguous, candidates);
            }

            return ("-", LicenseStatus.Unknown, candidates);
        }
        finally
        {
            ArrayPool<LicenseCandidate>.Shared.Return(candidateBuffer, clearArray: true);
        }
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

        if (firstStatus == LicenseStatus.Invalid)
        {
            return (string.Concat(firstValue, " (?)"), LicenseStatus.Invalid);
        }

        if (secondStatus == LicenseStatus.Invalid)
        {
            return (string.Concat(secondValue, " (?)"), LicenseStatus.Invalid);
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

    private static ScanComponent CreateScanComponent(
        string name,
        string version,
        string license,
        string ecosystem,
        DependencyType dependencyType,
        LicenseStatus status,
        string purl,
        string sourceId,
        LicenseCandidate[] candidates)
    {
        var evidence = new LicenseEvidence[candidates.Length];
        var hasDeprecatedWarning = false;
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            evidence[i] = new LicenseEvidence(candidate.Source, candidate.Kind, candidate.Raw, candidate.Normalized, candidate.Status, candidate.Warnings);
            hasDeprecatedWarning |= candidate.Deprecated;
        }

        return new ScanComponent(
            name,
            version,
            license,
            ecosystem,
            dependencyType,
            status,
            purl,
            sourceId,
            candidates,
            evidence,
            hasDeprecatedWarning ? ["deprecated_spdx_identifier"] : []);
    }

    private static LicenseCandidate CreateLicenseCandidate(string source, string kind, string raw, SpdxLicenseIndex spdxLicenseIndex)
    {
        var status = ClassifyLicense(raw, spdxLicenseIndex, out var normalized, out var deprecated);
        return new LicenseCandidate(
            source,
            kind,
            raw,
            normalized,
            status,
            deprecated,
            deprecated ? ["deprecated_spdx_identifier"] : []);
    }

    private static LicenseStatus ClassifyLicense(string value, SpdxLicenseIndex spdxLicenseIndex, out string normalized)
    {
        return ClassifyLicense(value, spdxLicenseIndex, out normalized, out _);
    }

    private static LicenseStatus ClassifyLicense(string value, SpdxLicenseIndex spdxLicenseIndex, out string normalized, out bool deprecated)
    {
        normalized = string.Empty;
        deprecated = false;
        if (IsUnknown(value))
        {
            return LicenseStatus.Unknown;
        }

        if (SpdxExpression.TryNormalize(value, spdxLicenseIndex, out normalized, out deprecated))
        {
            return LicenseStatus.Matched;
        }

        normalized = value;
        if (LooksLikeSpdxExpression(value))
        {
            return LicenseStatus.Invalid;
        }

        return LicenseStatus.Ambiguous;
    }

    private static bool LooksLikeSpdxExpression(string value)
    {
        return value.Contains(" AND ", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" OR ", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" WITH ", StringComparison.OrdinalIgnoreCase)
            || value.Contains('(')
            || value.Contains(')');
    }

    private static (string Kind, string Raw) ReadCycloneDxLicenseCandidate(ref Utf8JsonReader reader)
    {
        var depth = reader.CurrentDepth;
        var kind = "unknown";
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
                kind = reader.ValueTextEquals("id"u8) ? "id" : reader.ValueTextEquals("expression"u8) ? "expression" : "name";
                candidate = ReadString(ref reader);
                continue;
            }

            reader.Read();
        }

        return (kind, candidate);
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
        var purlSpan = purl.AsSpan();
        if (!purlSpan.StartsWith("pkg:", StringComparison.Ordinal))
        {
            return "-";
        }

        var typeStart = "pkg:".Length;
        var slash = purlSpan[typeStart..].IndexOf('/');
        if (slash < 0)
        {
            return "-";
        }

        return purlSpan.Slice(typeStart, slash).ToString().ToLowerInvariant();
    }

    private static void EnsureComponentCapacity(ref ScanComponent[] components, int componentCount)
    {
        if (componentCount != components.Length)
        {
            return;
        }

        var expanded = ArrayPool<ScanComponent>.Shared.Rent(components.Length * 2);
        components.AsSpan(0, componentCount).CopyTo(expanded);
        ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
        components = expanded;
    }

    private static void ReadCycloneDxDependencies(ref Utf8JsonReader reader, ref DependencyEdge[] dependencies, ref int dependencyCount)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var depth = reader.CurrentDepth;
            var parentRef = string.Empty;
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

                if (reader.ValueTextEquals("ref"u8))
                {
                    parentRef = ReadString(ref reader);
                    continue;
                }

                if (reader.ValueTextEquals("dependsOn"u8))
                {
                    ReadCycloneDxDependsOn(ref reader, parentRef, ref dependencies, ref dependencyCount);
                    continue;
                }

                reader.Read();
                reader.Skip();
            }
        }
    }

    private static void ReadCycloneDxDependsOn(ref Utf8JsonReader reader, string parentRef, ref DependencyEdge[] dependencies, ref int dependencyCount)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
                continue;
            }

            if (dependencyCount == dependencies.Length)
            {
                var expanded = ArrayPool<DependencyEdge>.Shared.Rent(dependencies.Length * 2);
                dependencies.AsSpan(0, dependencyCount).CopyTo(expanded);
                ArrayPool<DependencyEdge>.Shared.Return(dependencies, clearArray: true);
                dependencies = expanded;
            }

            dependencies[dependencyCount] = new DependencyEdge(parentRef, reader.GetString() ?? string.Empty);
            dependencyCount++;
        }
    }

    private static void AddDependencyEdge(ref DependencyEdge[] dependencies, ref int dependencyCount, string parentRef, string childRef)
    {
        if (parentRef.Length == 0 || childRef.Length == 0)
        {
            return;
        }

        if (dependencyCount == dependencies.Length)
        {
            var expanded = ArrayPool<DependencyEdge>.Shared.Rent(dependencies.Length * 2);
            dependencies.AsSpan(0, dependencyCount).CopyTo(expanded);
            ArrayPool<DependencyEdge>.Shared.Return(dependencies, clearArray: true);
            dependencies = expanded;
        }

        dependencies[dependencyCount] = new DependencyEdge(parentRef, childRef);
        dependencyCount++;
    }

    private static void ApplyDependencyTypes(ScanComponent[] components, string rootRef, ReadOnlySpan<DependencyEdge> dependencies)
    {
        var directRefs = new HashSet<string>(StringComparer.Ordinal);
        var transitiveRefs = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        for (var i = 0; i < dependencies.Length; i++)
        {
            if (string.Equals(dependencies[i].ParentRef, rootRef, StringComparison.Ordinal))
            {
                directRefs.Add(dependencies[i].ChildRef);
                queue.Enqueue(dependencies[i].ChildRef);
            }
        }

        while (queue.Count != 0)
        {
            var current = queue.Dequeue();
            for (var i = 0; i < dependencies.Length; i++)
            {
                if (!string.Equals(dependencies[i].ParentRef, current, StringComparison.Ordinal) || directRefs.Contains(dependencies[i].ChildRef) || !transitiveRefs.Add(dependencies[i].ChildRef))
                {
                    continue;
                }

                queue.Enqueue(dependencies[i].ChildRef);
            }
        }

        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            if (string.Equals(component.SourceId, rootRef, StringComparison.Ordinal))
            {
                components[i] = component with { DependencyType = DependencyType.Root };
            }
            else if (directRefs.Contains(component.SourceId))
            {
                components[i] = component with { DependencyType = DependencyType.Direct };
            }
            else if (transitiveRefs.Contains(component.SourceId))
            {
                components[i] = component with { DependencyType = DependencyType.Transitive };
            }
        }
    }

    private readonly record struct DependencyEdge(string ParentRef, string ChildRef);
}
