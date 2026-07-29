using System.Text;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ol.Core.Licensing;

namespace Ol.Core.Reporting;

/// <summary>Holds the components and identity restored from a persisted scan report.</summary>
/// <param name="SchemaVersion">The report schema version.</param>
/// <param name="SourceReference">The logical input reference recorded by the producing scan.</param>
/// <param name="LicenseListVersion">The SPDX License List version recorded by the producing scan.</param>
/// <param name="Inventory">The complete dependency inventory restored from the report.</param>
/// <param name="Components">The restored components in report order.</param>
public readonly record struct ScanReport(
    int SchemaVersion,
    string SourceReference,
    string LicenseListVersion,
    DependencyInventory Inventory,
    ScanComponent[] Components);

/// <summary>
/// Restores a persisted scan report so a policy can be re-evaluated without re-reading inputs or
/// recollecting evidence.
/// </summary>
/// <remarks>
/// The canonical report JSON is the input contract; there is no second schema. Keeping one document
/// means a report a user already has is directly usable as policy input, and prevents an output schema
/// and an input schema from drifting apart. Reading never performs network access.
/// </remarks>
public static class ScanReportReader
{
    /// <summary>The report schema version this build can consume.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Restores a report from canonical UTF-8 report JSON.</summary>
    public static bool TryRead(ReadOnlySpan<byte> utf8, out ScanReport report, out string error)
    {
        report = default;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = "The report must be a JSON object.";
                return false;
            }

            var schemaVersion = -1;
            var sourceReference = string.Empty;
            var licenseListVersion = string.Empty;
            var input = default(ScanInputDescriptor);
            DependencyInventory? inventory = null;
            ScanComponent[]? components = null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("schemaVersion"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out schemaVersion))
                    {
                        error = "The report schemaVersion must be a number.";
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("metadata"u8))
                {
                    ReadMetadata(ref reader, ref sourceReference, ref licenseListVersion, ref input);
                }
                else if (reader.ValueTextEquals("inventory"u8))
                {
                    if (!TryReadInventory(ref reader, out var restoredInventory, out error)) return false;
                    inventory = restoredInventory;
                }
                else if (reader.ValueTextEquals("components"u8))
                {
                    if (!TryReadComponents(ref reader, out components, out error)) return false;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            if (schemaVersion < 0)
            {
                error = "The report is missing schemaVersion.";
                return false;
            }

            if (schemaVersion != SupportedSchemaVersion)
            {
                error = $"Unsupported report schemaVersion {schemaVersion}; this build supports {SupportedSchemaVersion}.";
                return false;
            }

            if (components is null)
            {
                error = "The report has no components array. A grouped report cannot be used as policy input; produce it without --group-by.";
                return false;
            }

            var restored = inventory is { } value
                ? new DependencyInventory(input, value.Contexts, value.Components, value.Occurrences, value.Edges, value.OccurrenceVariants)
                : new DependencyInventory(input, [], [], [], [], []);
            report = new ScanReport(schemaVersion, sourceReference, licenseListVersion, restored, components);
            error = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            error = $"The report is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static void ReadMetadata(
        ref Utf8JsonReader reader,
        ref string sourceReference,
        ref string licenseListVersion,
        ref ScanInputDescriptor input)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("input"u8))
            {
                input = ReadInputMetadata(ref reader);
                sourceReference = input.SourceReference;
            }
            else if (reader.ValueTextEquals("spdx"u8))
            {
                licenseListVersion = ReadNestedString(ref reader, "licenseListVersion"u8);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }

    private static ScanInputDescriptor ReadInputMetadata(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return default;

        string kind = string.Empty, format = string.Empty, sourceReference = string.Empty, sourceSha256 = string.Empty;
        string parser = string.Empty, specificationVersion = string.Empty, displayName = string.Empty;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("kind"u8)) kind = ReadString(ref reader);
            else if (reader.ValueTextEquals("format"u8)) format = ReadString(ref reader);
            else if (reader.ValueTextEquals("sourceRef"u8) || reader.ValueTextEquals("sourceReference"u8)) sourceReference = ReadString(ref reader);
            else if (reader.ValueTextEquals("sourceSha256"u8)) sourceSha256 = ReadString(ref reader);
            else if (reader.ValueTextEquals("parser"u8)) parser = ReadString(ref reader);
            else if (reader.ValueTextEquals("specificationVersion"u8)) specificationVersion = ReadString(ref reader);
            else if (reader.ValueTextEquals("sbomFormat"u8)) displayName = ReadString(ref reader);
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return new ScanInputDescriptor(
            new ScanInputKind(kind),
            new ScanInputFormat(format, parser, displayName.Length == 0 ? format : displayName),
            sourceReference,
            sourceSha256,
            Utf8Slice.FromString(specificationVersion));
    }

    private static string ReadNestedString(ref Utf8JsonReader reader, ReadOnlySpan<byte> name)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return string.Empty;

        var result = string.Empty;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(name))
            {
                result = ReadString(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return result;
    }

    private static bool TryReadInventory(ref Utf8JsonReader reader, out DependencyInventory inventory, out string error)
    {
        inventory = default;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            error = "The report inventory value must be an object.";
            return false;
        }

        DependencyResolutionContext[]? contexts = null;
        ScanComponent[]? components = null;
        DependencyOccurrence[]? occurrences = null;
        DependencyEdge[]? edges = null;
        DependencyOccurrenceVariant[]? variants = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("contexts"u8)) contexts = ReadContexts(ref reader);
            else if (reader.ValueTextEquals("components"u8)) components = ReadInventoryComponents(ref reader);
            else if (reader.ValueTextEquals("occurrences"u8)) occurrences = ReadOccurrences(ref reader, out variants);
            else if (reader.ValueTextEquals("edges"u8)) edges = ReadEdges(ref reader);
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (contexts is null || components is null || occurrences is null || edges is null)
        {
            error = "The report inventory must contain contexts, components, occurrences, and edges arrays.";
            return false;
        }

        if (!ValidateInventory(contexts.Length, components.Length, occurrences, edges, out error)) return false;

        inventory = new DependencyInventory(default, contexts, components, occurrences, edges, variants ?? []);
        error = string.Empty;
        return true;
    }

    private static DependencyResolutionContext[]? ReadContexts(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;

        var result = ArrayPool<DependencyResolutionContext>.Shared.Rent(16);
        var count = 0;
        try
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                string projectOrigin = string.Empty, target = string.Empty, runtime = string.Empty;
                string platform = string.Empty, architecture = string.Empty, variant = string.Empty;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("projectOrigin"u8)) projectOrigin = ReadString(ref reader);
                    else if (reader.ValueTextEquals("target"u8)) target = ReadString(ref reader);
                    else if (reader.ValueTextEquals("runtime"u8)) runtime = ReadString(ref reader);
                    else if (reader.ValueTextEquals("platform"u8)) platform = ReadString(ref reader);
                    else if (reader.ValueTextEquals("architecture"u8)) architecture = ReadString(ref reader);
                    else if (reader.ValueTextEquals("variant"u8)) variant = ReadString(ref reader);
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                EnsureCapacity(ref result, count);
                result[count++] = new DependencyResolutionContext(
                    Utf8Slice.FromString(projectOrigin),
                    Utf8Slice.FromString(target),
                    Utf8Slice.FromString(runtime),
                    Utf8Slice.FromString(platform),
                    Utf8Slice.FromString(architecture),
                    Utf8Slice.FromString(variant));
            }

            return result.AsSpan(0, count).ToArray();
        }
        finally
        {
            Return(result, count);
        }
    }

    private static ScanComponent[]? ReadInventoryComponents(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;

        var result = ArrayPool<ScanComponent>.Shared.Rent(16);
        var count = 0;
        try
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                string name = string.Empty, version = string.Empty, ecosystem = string.Empty, purl = string.Empty, sourceId = string.Empty;
                var dependency = DependencyType.Unknown;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("name"u8)) name = ReadString(ref reader);
                    else if (reader.ValueTextEquals("version"u8)) version = ReadString(ref reader);
                    else if (reader.ValueTextEquals("ecosystem"u8)) ecosystem = ReadString(ref reader);
                    else if (reader.ValueTextEquals("dependency"u8)) dependency = ParseDependencyType(ReadString(ref reader));
                    else if (reader.ValueTextEquals("purl"u8)) purl = ReadString(ref reader);
                    else if (reader.ValueTextEquals("sourceId"u8)) sourceId = ReadString(ref reader);
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                EnsureCapacity(ref result, count);
                result[count++] = new ScanComponent(
                    Utf8Slice.FromString(name),
                    Utf8Slice.FromString(version),
                    default,
                    ecosystem,
                    dependency,
                    LicenseStatus.Unknown,
                    Utf8Slice.FromString(purl),
                    Utf8Slice.FromString(sourceId),
                    default,
                    [],
                    []);
            }

            return result.AsSpan(0, count).ToArray();
        }
        finally
        {
            Return(result, count);
        }
    }

    private static DependencyOccurrence[]? ReadOccurrences(
        ref Utf8JsonReader reader,
        out DependencyOccurrenceVariant[] variants)
    {
        variants = [];
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;

        var result = ArrayPool<DependencyOccurrence>.Shared.Rent(16);
        DependencyOccurrenceVariant[]? variantResults = null;
        var count = 0;
        var variantCount = 0;
        try
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                var contextIndex = DependencyOccurrence.UnspecifiedContext;
                var componentIndex = -1;
                string variant = string.Empty;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("contextIndex"u8)) contextIndex = ReadInt32(ref reader);
                    else if (reader.ValueTextEquals("componentIndex"u8)) componentIndex = ReadInt32(ref reader);
                    else if (reader.ValueTextEquals("variant"u8)) variant = ReadString(ref reader);
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                EnsureCapacity(ref result, count);
                result[count] = new DependencyOccurrence(contextIndex, componentIndex);
                if (variant.Length != 0)
                {
                    variantResults ??= ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(8);
                    EnsureCapacity(ref variantResults, variantCount);
                    variantResults[variantCount++] = new DependencyOccurrenceVariant(count, Utf8Slice.FromString(variant));
                }

                count++;
            }

            variants = variantResults is null ? [] : variantResults.AsSpan(0, variantCount).ToArray();
            return result.AsSpan(0, count).ToArray();
        }
        finally
        {
            Return(result, count);
            if (variantResults is not null) Return(variantResults, variantCount);
        }
    }

    private static DependencyEdge[]? ReadEdges(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;

        var result = ArrayPool<DependencyEdge>.Shared.Rent(16);
        var count = 0;
        try
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                var contextIndex = DependencyOccurrence.UnspecifiedContext;
                var fromOccurrenceIndex = -2;
                var toOccurrenceIndex = -1;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("contextIndex"u8)) contextIndex = ReadInt32(ref reader);
                    else if (reader.ValueTextEquals("fromOccurrenceIndex"u8)) fromOccurrenceIndex = ReadInt32(ref reader);
                    else if (reader.ValueTextEquals("toOccurrenceIndex"u8)) toOccurrenceIndex = ReadInt32(ref reader);
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                EnsureCapacity(ref result, count);
                result[count++] = new DependencyEdge(contextIndex, fromOccurrenceIndex, toOccurrenceIndex);
            }

            return result.AsSpan(0, count).ToArray();
        }
        finally
        {
            Return(result, count);
        }
    }

    private static bool ValidateInventory(
        int contextCount,
        int componentCount,
        ReadOnlySpan<DependencyOccurrence> occurrences,
        ReadOnlySpan<DependencyEdge> edges,
        out string error)
    {
        for (var i = 0; i < occurrences.Length; i++)
        {
            var occurrence = occurrences[i];
            if (occurrence.ContextIndex < DependencyOccurrence.UnspecifiedContext || occurrence.ContextIndex >= contextCount)
            {
                error = $"Inventory occurrence {i} has an invalid contextIndex.";
                return false;
            }

            if ((uint)occurrence.ComponentIndex >= (uint)componentCount)
            {
                error = $"Inventory occurrence {i} has an invalid componentIndex.";
                return false;
            }
        }

        for (var i = 0; i < edges.Length; i++)
        {
            var edge = edges[i];
            if (edge.ContextIndex < DependencyOccurrence.UnspecifiedContext || edge.ContextIndex >= contextCount)
            {
                error = $"Inventory edge {i} has an invalid contextIndex.";
                return false;
            }

            if (edge.FromOccurrenceIndex < DependencyOccurrence.ContextRoot
                || edge.FromOccurrenceIndex >= occurrences.Length
                || (uint)edge.ToOccurrenceIndex >= (uint)occurrences.Length)
            {
                error = $"Inventory edge {i} has an invalid occurrence index.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static int ReadInt32(ref Utf8JsonReader reader)
        => reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) ? value : int.MinValue;

    private static void EnsureCapacity<T>(ref T[] buffer, int count)
    {
        if (count < buffer.Length) return;

        var replacement = ArrayPool<T>.Shared.Rent(buffer.Length * 2);
        buffer.AsSpan(0, count).CopyTo(replacement);
        Return(buffer, count);
        buffer = replacement;
    }

    private static void Return<T>(T[] buffer, int count)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) buffer.AsSpan(0, count).Clear();
        ArrayPool<T>.Shared.Return(buffer);
    }

    private static bool TryReadComponents(ref Utf8JsonReader reader, out ScanComponent[] components, out string error)
    {
        components = [];
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            error = "The report components value must be an array.";
            return false;
        }

        var result = new List<ScanComponent>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            if (!TryReadComponent(ref reader, out var component, out error)) return false;
            result.Add(component);
        }

        components = result.ToArray();
        error = string.Empty;
        return true;
    }

    private static bool TryReadComponent(ref Utf8JsonReader reader, out ScanComponent component, out string error)
    {
        component = default;
        string name = string.Empty, version = string.Empty, license = string.Empty, ecosystem = string.Empty, purl = string.Empty, sourceId = string.Empty;
        var status = LicenseStatus.Unknown;
        var statusSeen = false;
        var dependency = DependencyType.Unknown;
        LicenseCandidate[] candidates = [];
        string[] warnings = [];

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("name"u8)) name = ReadString(ref reader);
            else if (reader.ValueTextEquals("version"u8)) version = ReadString(ref reader);
            else if (reader.ValueTextEquals("license"u8)) license = ReadString(ref reader);
            else if (reader.ValueTextEquals("ecosystem"u8)) ecosystem = ReadString(ref reader);
            else if (reader.ValueTextEquals("purl"u8)) purl = ReadString(ref reader);
            else if (reader.ValueTextEquals("sourceId"u8)) sourceId = ReadString(ref reader);
            else if (reader.ValueTextEquals("dependency"u8)) dependency = ParseDependencyType(ReadString(ref reader));
            else if (reader.ValueTextEquals("status"u8))
            {
                var raw = ReadString(ref reader);
                statusSeen = true;
                if (!LicenseStatusIdentifiers.TryParse(Encoding.UTF8.GetBytes(raw), out status))
                {
                    error = $"Unknown component status '{raw}'.";
                    return false;
                }
            }
            else if (reader.ValueTextEquals("licenseCandidates"u8)) candidates = ReadCandidates(ref reader);
            else if (reader.ValueTextEquals("warnings"u8)) warnings = ReadStringArray(ref reader);
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (!statusSeen)
        {
            error = "A report component is missing status.";
            return false;
        }

        // "-" is the human-readable placeholder written for an absent license value.
        var displayLicense = license is "-" ? string.Empty : license;
        component = new ScanComponent(
            Utf8Slice.FromString(name),
            Utf8Slice.FromString(version),
            Utf8Slice.FromString(displayLicense),
            ecosystem,
            dependency,
            status,
            Utf8Slice.FromString(purl),
            Utf8Slice.FromString(sourceId),
            candidates.Length == 0 ? default : candidates[0],
            candidates.Length <= 1 ? [] : candidates[1..],
            warnings);
        error = string.Empty;
        return true;
    }

    private static LicenseCandidate[] ReadCandidates(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return [];

        var result = new List<LicenseCandidate>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            var source = LicenseCandidateSource.None;
            var kind = LicenseCandidateKind.None;
            string raw = string.Empty, normalized = string.Empty;
            var status = LicenseStatus.Unknown;
            var deprecated = false;
            var warnings = LicenseCandidateWarnings.None;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("source"u8)) source = LicenseCandidateIdentifiers.ParseSource(Encoding.UTF8.GetBytes(ReadString(ref reader)));
                else if (reader.ValueTextEquals("kind"u8)) kind = LicenseCandidateIdentifiers.ParseKind(Encoding.UTF8.GetBytes(ReadString(ref reader)));
                else if (reader.ValueTextEquals("raw"u8)) raw = ReadString(ref reader);
                else if (reader.ValueTextEquals("normalized"u8)) normalized = ReadString(ref reader);
                else if (reader.ValueTextEquals("status"u8)) LicenseStatusIdentifiers.TryParse(Encoding.UTF8.GetBytes(ReadString(ref reader)), out status);
                else if (reader.ValueTextEquals("deprecated"u8)) deprecated = reader.Read() && reader.TokenType == JsonTokenType.True;
                else if (reader.ValueTextEquals("warnings"u8)) warnings = LicenseCandidateIdentifiers.ParseWarnings(ReadStringArray(ref reader));
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            result.Add(new LicenseCandidate(
                source,
                kind,
                Utf8Slice.FromString(raw),
                Utf8Slice.FromString(normalized),
                status,
                deprecated,
                warnings));
        }

        return result.ToArray();
    }

    private static string[] ReadStringArray(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return [];

        var result = new List<string>();
        while (reader.Read() && reader.TokenType == JsonTokenType.String)
        {
            result.Add(reader.GetString() ?? string.Empty);
        }

        return result.ToArray();
    }

    private static string ReadString(ref Utf8JsonReader reader)
        => reader.Read() && reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;

    private static DependencyType ParseDependencyType(string value) => value switch
    {
        "root" => DependencyType.Root,
        "direct" => DependencyType.Direct,
        "transitive" => DependencyType.Transitive,
        _ => DependencyType.Unknown,
    };
}
