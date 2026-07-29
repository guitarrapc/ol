using System.Text;
using System.Text.Json;
using Ol.Core.Licensing;

namespace Ol.Core.Reporting;

/// <summary>Holds the components and identity restored from a persisted scan report.</summary>
/// <param name="SchemaVersion">The report schema version.</param>
/// <param name="SourceReference">The logical input reference recorded by the producing scan.</param>
/// <param name="LicenseListVersion">The SPDX License List version recorded by the producing scan.</param>
/// <param name="Components">The restored components in report order.</param>
public readonly record struct ScanReport(
    int SchemaVersion,
    string SourceReference,
    string LicenseListVersion,
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
                    ReadMetadata(ref reader, ref sourceReference, ref licenseListVersion);
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

            report = new ScanReport(schemaVersion, sourceReference, licenseListVersion, components);
            error = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            error = $"The report is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static void ReadMetadata(ref Utf8JsonReader reader, ref string sourceReference, ref string licenseListVersion)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("input"u8))
            {
                sourceReference = ReadNestedString(ref reader, "sourceReference"u8);
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
