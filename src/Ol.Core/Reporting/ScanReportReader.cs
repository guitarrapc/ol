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
/// <param name="ComponentUsages">The restored development usage per component, aligned with <paramref name="Components"/>.</param>
/// <param name="ExcludedInputPaths">The logical paths excluded from input discovery by the producing scan.</param>
/// <param name="View">The view the producing scan rendered, which is the population a policy can evaluate.</param>
/// <param name="Warnings">
/// The report's top-level warning identifiers, restored verbatim and in report order. A document that
/// states no <c>warnings</c> restores as an empty array: a warning is a positive statement, so making
/// none is having none. Nullable only because an optional parameter cannot default to an array.
/// </param>
/// <param name="InputDiscovery">What discovery found, ignored, and skipped, or null when the report never stated it.</param>
/// <param name="Tool">The tool that produced the report, or the default value when older input omitted it.</param>
public readonly record struct ScanReport(
    int SchemaVersion,
    string SourceReference,
    string LicenseListVersion,
    DependencyInventory Inventory,
    ScanComponent[] Components,
    DependencyUsage[] ComponentUsages,
    string[] ExcludedInputPaths,
    ScanReportViewScope View = default,
    string[]? Warnings = null,
    ScanReportInputDiscovery? InputDiscovery = null,
    ScanReportTool Tool = default)
{
    /// <summary>The warning identifier a scan writes when a recognized input contributed no inventory.</summary>
    public const string EmptyInventoryWarning = "input_declares_no_components";

    /// <summary>Reports whether the producing scan stated that its input declared no resolved dependencies.</summary>
    /// <remarks>
    /// Such a report proves nothing about licenses: every count is zero, so a pass on it is indistinguishable
    /// from a project whose dependencies are all allowed. The scan states the condition in every view it
    /// writes; a gate that consumed the report without restating it would leave that fact unable to reach
    /// the reader the warning exists for.
    /// </remarks>
    public bool DeclaresNoComponents
    {
        get
        {
            var warnings = Warnings;
            if (warnings is null) return false;
            for (var i = 0; i < warnings.Length; i++)
            {
                if (string.Equals(warnings[i], EmptyInventoryWarning, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }
}

/// <summary>Identifies the tool that produced a persisted scan report.</summary>
public readonly record struct ScanReportTool(string Name, string Version);

/// <summary>What input discovery found, ignored, and skipped in the producing scan.</summary>
/// <remarks>
/// Restored as an optional value rather than defaulted, because an Ol that predates the field still detected input
/// files and simply did not record how many. Reading an absent object as zeros would state something the report
/// never said, which is the distinction <see cref="ScanReportViewScope"/> already draws between a count Ol supplied
/// and a count Ol defaulted. Excluded input paths default instead, because an Ol without the option truly excluded
/// none.
/// </remarks>
/// <param name="DetectedFileCount">Physical input files discovery detected, including ones it then skipped.</param>
/// <param name="IgnoredCandidates">Known inputs Ol cannot consume, named by the directory pattern that found them.</param>
/// <param name="IncompleteInputSetCount">Companion sets discovery found incomplete and therefore skipped.</param>
public readonly record struct ScanReportInputDiscovery(
    int DetectedFileCount,
    string[] IgnoredCandidates,
    int IncompleteInputSetCount);

/// <summary>Describes how the producing scan narrowed the components it wrote.</summary>
/// <param name="DependencyFilter">
/// The <c>--dependency</c> filter the scan applied. Empty when it applied none, and null on a default-constructed
/// value, which is what a caller that states no view produces; both read as unfiltered through
/// <see cref="IsFiltered"/>, which is the only member that should decide on this field.
/// </param>
/// <param name="ExcludedCount">Components the filter removed from the report.</param>
/// <param name="ExcludedUnknownCount">Components among them whose relationship no input determined.</param>
public readonly record struct ScanReportViewScope(
    string DependencyFilter,
    int ExcludedCount,
    int ExcludedUnknownCount)
{
    /// <summary>Reports whether the producing scan wrote fewer components than it resolved.</summary>
    public bool IsFiltered => !string.IsNullOrEmpty(DependencyFilter);
}

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
            string[] excludedInputPaths = [];
            var view = new ScanReportViewScope(string.Empty, 0, 0);
            DependencyInventory? inventory = null;
            ScanComponent[]? components = null;
            DependencyUsage[] componentUsages = [];
            string[] warnings = [];
            ScanReportInputDiscovery? inputDiscovery = null;
            var tool = default(ScanReportTool);

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
                    if (!TryReadMetadata(ref reader, ref sourceReference, ref licenseListVersion, ref input, ref excludedInputPaths, ref view, ref inputDiscovery, ref tool, out error)) return false;
                }
                else if (reader.ValueTextEquals("inventory"u8))
                {
                    if (!TryReadInventory(ref reader, out var restoredInventory, out error)) return false;
                    inventory = restoredInventory;
                }
                else if (reader.ValueTextEquals("components"u8))
                {
                    if (!TryReadComponents(ref reader, out components, out componentUsages, out error)) return false;
                }
                else if (reader.ValueTextEquals("warnings"u8))
                {
                    warnings = ReadStringArray(ref reader);
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
            report = new ScanReport(schemaVersion, sourceReference, licenseListVersion, restored, components, componentUsages, excludedInputPaths, view, warnings, inputDiscovery, tool);
            error = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            error = $"The report is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadMetadata(
        ref Utf8JsonReader reader,
        ref string sourceReference,
        ref string licenseListVersion,
        ref ScanInputDescriptor input,
        ref string[] excludedInputPaths,
        ref ScanReportViewScope view,
        ref ScanReportInputDiscovery? inputDiscovery,
        ref ScanReportTool tool,
        out string error)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            error = "The report metadata value must be an object.";
            return false;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("tool"u8))
            {
                tool = ReadToolMetadata(ref reader);
            }
            else if (reader.ValueTextEquals("input"u8))
            {
                if (!TryReadInputMetadata(ref reader, out input))
                {
                    error = "The report metadata.input value must be an object.";
                    return false;
                }

                sourceReference = input.SourceReference;
            }
            else if (reader.ValueTextEquals("spdx"u8))
            {
                licenseListVersion = ReadNestedString(ref reader, "licenseListVersion"u8);
            }
            else if (reader.ValueTextEquals("inputScope"u8))
            {
                if (!TryReadInputScope(ref reader, out excludedInputPaths))
                {
                    error = "The report metadata.inputScope value must contain an excludedPaths array of strings.";
                    return false;
                }
            }
            else if (reader.ValueTextEquals("view"u8))
            {
                if (!TryReadView(ref reader, out view, out error)) return false;
            }
            else if (reader.ValueTextEquals("inputDiscovery"u8))
            {
                inputDiscovery = ReadInputDiscovery(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        error = string.Empty;
        return true;
    }

    private static ScanReportTool ReadToolMetadata(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return default;

        var name = string.Empty;
        var version = string.Empty;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("name"u8)) name = ReadString(ref reader);
            else if (reader.ValueTextEquals("version"u8)) version = ReadString(ref reader);
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return new ScanReportTool(name, version);
    }

    private static bool TryReadView(ref Utf8JsonReader reader, out ScanReportViewScope view, out string error)
    {
        view = new ScanReportViewScope(string.Empty, 0, 0);
        error = string.Empty;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            error = "The report metadata.view value must be an object.";
            return false;
        }

        var dependencyFilter = string.Empty;
        var excludedCount = 0;
        var excludedUnknownCount = 0;
        // Every field is required. A view stating no filter and a view stating nothing are different documents, and
        // only the first proves the report holds every component the scan resolved; a count Ol supplied and a count
        // Ol defaulted are likewise different claims, and printing a defaulted zero would state an exclusion figure
        // no producer wrote.
        var statedDependencyFilter = false;
        var statedExcludedCount = false;
        var statedExcludedUnknownCount = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("dependencyFilter"u8))
            {
                statedDependencyFilter = true;
                if (!reader.Read()) return Invalid(out error, "The report metadata.view dependencyFilter must be a string or null.");
                if (reader.TokenType == JsonTokenType.String) dependencyFilter = reader.GetString() ?? string.Empty;
                else if (reader.TokenType != JsonTokenType.Null) return Invalid(out error, "The report metadata.view dependencyFilter must be a string or null.");
            }
            else if (reader.ValueTextEquals("excludedCount"u8))
            {
                statedExcludedCount = true;
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out excludedCount) || excludedCount < 0)
                {
                    return Invalid(out error, "The report metadata.view excludedCount must be a non-negative integer.");
                }
            }
            else if (reader.ValueTextEquals("excludedUnknownCount"u8))
            {
                statedExcludedUnknownCount = true;
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out excludedUnknownCount) || excludedUnknownCount < 0)
                {
                    return Invalid(out error, "The report metadata.view excludedUnknownCount must be a non-negative integer.");
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            return Invalid(out error, "The report metadata.view value must be an object.");
        }

        if (!statedDependencyFilter || !statedExcludedCount || !statedExcludedUnknownCount)
        {
            return Invalid(out error, "The report metadata.view must state dependencyFilter, excludedCount, and excludedUnknownCount.");
        }

        // The counts describe what the filter removed, so they cannot outrun it. A view claiming no filter while
        // reporting exclusions is the narrowed-report-read-as-complete case in another shape, and an unknown-
        // relationship count above the total describes a subset larger than its set.
        if (excludedUnknownCount > excludedCount)
        {
            return Invalid(out error, $"The report metadata.view states excludedUnknownCount {excludedUnknownCount} above excludedCount {excludedCount}.");
        }

        if (dependencyFilter.Length == 0 && excludedCount > 0)
        {
            return Invalid(out error, $"The report metadata.view states no dependency filter but reports {excludedCount} excluded components.");
        }

        view = new ScanReportViewScope(dependencyFilter, excludedCount, excludedUnknownCount);
        return true;
    }

    private static bool Invalid(out string error, string message)
    {
        error = message;
        return false;
    }

    /// <summary>Restores what discovery observed, tolerating a document that states only some of it.</summary>
    /// <remarks>
    /// A missing member inside a stated object reads as its neutral value, unlike a missing object, which reads as
    /// unstated. The object's presence is the claim that Ol recorded discovery; which members it carries is a shape
    /// question the schema version answers.
    /// </remarks>
    private static ScanReportInputDiscovery ReadInputDiscovery(ref Utf8JsonReader reader)
    {
        var detectedFileCount = 0;
        string[] ignoredCandidates = [];
        var incompleteInputSetCount = 0;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return new ScanReportInputDiscovery(detectedFileCount, ignoredCandidates, incompleteInputSetCount);
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("detectedFileCount"u8)) detectedFileCount = ReadCount(ref reader);
            else if (reader.ValueTextEquals("ignoredCandidates"u8)) ignoredCandidates = ReadStringArray(ref reader);
            else if (reader.ValueTextEquals("incompleteInputSetCount"u8)) incompleteInputSetCount = ReadCount(ref reader);
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return new ScanReportInputDiscovery(detectedFileCount, ignoredCandidates, incompleteInputSetCount);
    }

    /// <summary>Reads a count, treating an absent, non-numeric, or negative value as none observed.</summary>
    private static int ReadCount(ref Utf8JsonReader reader)
    {
        var value = ReadInt32(ref reader);
        return value < 0 ? 0 : value;
    }

    private static bool TryReadInputScope(ref Utf8JsonReader reader, out string[] excludedInputPaths)
    {
        excludedInputPaths = [];
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

        var foundExcludedPaths = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("excludedPaths"u8))
            {
                foundExcludedPaths = true;
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return false;

                var paths = new List<string>();
                while (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    paths.Add(reader.GetString() ?? string.Empty);
                }

                if (reader.TokenType != JsonTokenType.EndArray) return false;
                excludedInputPaths = paths.ToArray();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return foundExcludedPaths && reader.TokenType == JsonTokenType.EndObject;
    }

    private static bool TryReadInputMetadata(ref Utf8JsonReader reader, out ScanInputDescriptor input)
    {
        input = default;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

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

        input = new ScanInputDescriptor(
            new ScanInputKind(kind),
            new ScanInputFormat(format, parser, displayName.Length == 0 ? format : displayName),
            sourceReference,
            sourceSha256,
            Utf8Slice.FromString(specificationVersion));
        return true;
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
                string inputPath = string.Empty, projectIdentity = string.Empty, target = string.Empty, runtime = string.Empty;
                string platform = string.Empty, architecture = string.Empty, variant = string.Empty;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("inputPath"u8)) inputPath = ReadString(ref reader);
                    else if (reader.ValueTextEquals("projectIdentity"u8)) projectIdentity = ReadString(ref reader);
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
                    Utf8Slice.FromString(projectIdentity),
                    Utf8Slice.FromString(target),
                    Utf8Slice.FromString(runtime),
                    Utf8Slice.FromString(platform),
                    Utf8Slice.FromString(architecture),
                    Utf8Slice.FromString(variant),
                    Utf8Slice.FromString(inputPath));
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

    private static bool TryReadComponents(ref Utf8JsonReader reader, out ScanComponent[] components, out DependencyUsage[] usages, out string error)
    {
        components = [];
        usages = [];
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            error = "The report components value must be an array.";
            return false;
        }

        var result = new List<ScanComponent>();
        var usageResult = new List<DependencyUsage>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            if (!TryReadComponent(ref reader, out var component, out var usage, out error)) return false;
            result.Add(component);
            usageResult.Add(usage);
        }

        components = result.ToArray();
        usages = usageResult.ToArray();
        error = string.Empty;
        return true;
    }

    private static bool TryReadComponent(ref Utf8JsonReader reader, out ScanComponent component, out DependencyUsage usage, out string error)
    {
        component = default;
        usage = DependencyUsage.Unknown;
        string name = string.Empty, version = string.Empty, license = string.Empty, ecosystem = string.Empty, purl = string.Empty, sourceId = string.Empty;
        var status = LicenseStatus.Unknown;
        var statusSeen = false;
        var dependency = DependencyType.Unknown;
        var suppliedBy = ComponentSupply.None;
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
            else if (reader.ValueTextEquals("suppliedBy"u8)) suppliedBy = ParseComponentSupply(ReadStringArray(ref reader));
            else if (reader.ValueTextEquals("usage"u8)) usage = ParseUsage(ReadString(ref reader));
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
            LicenseCandidateIdentifiers.ParseWarnings(warnings),
            default,
            suppliedBy);
        error = string.Empty;
        return true;
    }

    private static ComponentSupply ParseComponentSupply(string[] values)
    {
        var result = ComponentSupply.None;
        for (var i = 0; i < values.Length; i++)
        {
            result |= values[i] switch
            {
                "sbom" => ComponentSupply.Sbom,
                "package-manager" => ComponentSupply.PackageManager,
                _ => ComponentSupply.None,
            };
        }

        return result;
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
            var evidence = default(LicenseEvidence);

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("source"u8)) source = LicenseCandidateIdentifiers.ParseSource(Encoding.UTF8.GetBytes(ReadString(ref reader)));
                else if (reader.ValueTextEquals("kind"u8)) kind = LicenseCandidateIdentifiers.ParseKind(Encoding.UTF8.GetBytes(ReadString(ref reader)));
                else if (reader.ValueTextEquals("raw"u8)) raw = ReadString(ref reader);
                else if (reader.ValueTextEquals("normalized"u8)) normalized = ReadString(ref reader);
                else if (reader.ValueTextEquals("status"u8)) LicenseStatusIdentifiers.TryParse(Encoding.UTF8.GetBytes(ReadString(ref reader)), out status);
                else if (reader.ValueTextEquals("deprecated"u8)) deprecated = reader.Read() && reader.TokenType == JsonTokenType.True;
                else if (reader.ValueTextEquals("warnings"u8)) warnings = LicenseCandidateIdentifiers.ParseWarnings(ReadStringArray(ref reader));
                else if (reader.ValueTextEquals("evidence"u8)) evidence = ReadEvidence(ref reader);
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
                warnings,
                evidence));
        }

        return result.ToArray();
    }

    /// <summary>
    /// Restores the provenance a reader of the persisted report needs to say why a license never settled.
    /// </summary>
    /// <remarks>
    /// Only the two facts that name a place are restored: the reference a publisher declared, and the
    /// repository license URL GitHub answered with. Everything else in the evidence object describes how
    /// the scan reached its answer, and a command evaluating a finished report cannot act on it. Skipping
    /// the whole object was what left <c>check</c> unable to reproduce the mechanism its own scan printed.
    /// </remarks>
    private static LicenseEvidence ReadEvidence(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return default;

        var kind = LicenseEvidenceKind.None;
        var declaredKind = DeclaredLicenseReferenceKind.None;
        var declaredValue = string.Empty;
        var licenseUrl = string.Empty;
        var declared = false;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("type"u8)) kind = ParseEvidenceKind(ReadString(ref reader));
            else if (reader.ValueTextEquals("declaredLicenseReferenceKind"u8))
            {
                declared = true;
                declaredKind = ReadString(ref reader) switch
                {
                    "location" => DeclaredLicenseReferenceKind.Location,
                    "inline-text" => DeclaredLicenseReferenceKind.InlineText,
                    _ => DeclaredLicenseReferenceKind.ArtifactPath,
                };
            }
            else if (reader.ValueTextEquals("declaredLicenseReference"u8)) declaredValue = ReadString(ref reader);
            else if (reader.ValueTextEquals("licenseUrl"u8)) licenseUrl = ReadString(ref reader);
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        // The source-repository shape is rebuilt with only the field a reference can be taken from, because
        // the report is being read to be explained rather than to be collected from again.
        var sourceRepository = licenseUrl.Length == 0
            ? null
            : new SourceRepositoryEvidence(string.Empty, string.Empty, null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, licenseUrl);

        return new LicenseEvidence(
            kind,
            SourceRepository: sourceRepository,
            DeclaredReference: declared ? new DeclaredLicenseReference(declaredKind, Utf8Slice.FromString(declaredValue)) : null);
    }

    private static LicenseEvidenceKind ParseEvidenceKind(string value) => value switch
    {
        "sbom" => LicenseEvidenceKind.Sbom,
        "dependency-input" => LicenseEvidenceKind.DependencyInput,
        "package-registry" => LicenseEvidenceKind.PackageRegistry,
        "source-repository" => LicenseEvidenceKind.SourceRepository,
        "package-artifact" => LicenseEvidenceKind.PackageArtifact,
        _ => LicenseEvidenceKind.None,
    };

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

    private static DependencyUsage ParseUsage(string value) => value switch
    {
        "development" => DependencyUsage.Development,
        "runtime" => DependencyUsage.Runtime,
        _ => DependencyUsage.Unknown,
    };
}
