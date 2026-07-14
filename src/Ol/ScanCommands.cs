using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Ol.Core;
using Ol.Update;

/// <summary>
/// Scan SBOM license evidence.
/// </summary>
internal sealed class ScanCommands
{
    /// <summary>
    /// Scan a CycloneDX or SPDX JSON SBOM.
    /// </summary>
    /// <param name="sbom">SBOM JSON path.</param>
    /// <param name="format">Output format: text, json, or markdown.</param>
    /// <param name="outFile">--out, Write output to this path.</param>
    /// <param name="verbose">Include verbose columns.</param>
    /// <param name="dependency">Dependency output filter: root,direct,transitive,unknown.</param>
    /// <param name="groupBy">Group output by fields: name,version,license,ecosystem,dependency,status.</param>
    /// <param name="sort">Sort keys: ecosystem,name,version,license,dependency,status,purl.</param>
    /// <param name="sortOrder">Sort order: asc or desc.</param>
    /// <param name="spdxData">Directory containing licenses.json and exceptions.json.</param>
    /// <param name="quiet">Suppress stderr summary.</param>
    [Command("scan")]
    public int Scan(
        string sbom,
        ReportFormat format = ReportFormat.Text,
        string? outFile = null,
        bool verbose = false,
        string? dependency = null,
        string? groupBy = null,
        string sort = "ecosystem,name,version",
        SortOrder sortOrder = SortOrder.Asc,
        string? spdxData = null,
        bool quiet = false)
    {
        if (!File.Exists(sbom))
        {
            Console.Error.WriteLine($"SBOM file not found: {sbom}");
            return 1;
        }

        var spdx = SpdxData.Load(spdxData);
        var sbomBytes = File.ReadAllBytes(sbom);
        var report = Ol.Core.SbomScanner.Scan(sbomBytes, spdx.Index);
        var components = ScanView.Apply(report.Components, dependency, sort, sortOrder);
        var dependencyFilteredCount = dependency is null or "" ? 0 : report.Components.Length - components.Length;
        var excludedUnknownCount = dependency is null or "" ? 0 : ScanView.CountExcludedUnknown(report.Components, dependency);
        var text = groupBy is null or ""
            ? format switch
            {
                ReportFormat.Text => ReportRenderer.RenderText(components, verbose),
                ReportFormat.Markdown => ReportRenderer.RenderMarkdown(components, verbose),
                ReportFormat.Json => ReportRenderer.RenderJson(report.Format, report.SpecVersion, components, sbom, sbomBytes, spdx),
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            }
            : RenderGrouped(format, ScanView.Group(components, groupBy), groupBy, report.Format, report.SpecVersion, sbom, sbomBytes, spdx);

        if (outFile is { Length: > 0 })
        {
            File.WriteAllText(outFile, text, Encoding.UTF8);
        }

        Console.Write(text);

        if (!quiet)
        {
            var summary = ScanSummary.Create(components);
            var filterSummary = dependency is null or "" ? string.Empty : $"; dependency-filtered: {dependencyFilteredCount}; excluded-unknown: {excludedUnknownCount}";
            var outputSummary = outFile is { Length: > 0 } ? $"; output: {Path.GetFileName(outFile)}" : string.Empty;
            Console.Error.WriteLine($"components: {components.Length}; matched: {summary.Matched}; conflict: {summary.Conflict}; unknown: {summary.Unknown}; ambiguous: {summary.Ambiguous}; invalid: {summary.Invalid}; warnings: 0; deprecated-spdx: 0; sbom: {Path.GetFileName(sbom)}; format: {report.Format}; spdx: {spdx.LicenseListVersion} ({spdx.Source}){filterSummary}{outputSummary}");
        }

        return 0;
    }

    private static string RenderGrouped(ReportFormat format, GroupRow[] groups, string groupBy, SbomFormat sbomFormat, string specVersion, string sbom, ReadOnlySpan<byte> sbomBytes, SpdxData spdx)
    {
        return format switch
        {
            ReportFormat.Text => ReportRenderer.RenderText(groups, groupBy),
            ReportFormat.Markdown => ReportRenderer.RenderMarkdown(groups, groupBy),
            ReportFormat.Json => ReportRenderer.RenderJson(sbomFormat, specVersion, groups, groupBy, sbom, sbomBytes, spdx),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }
}

internal enum ReportFormat
{
    Text,
    Json,
    Markdown,
}

internal enum SortOrder
{
    Asc,
    Desc,
}

internal readonly record struct SpdxData(
    SpdxLicenseIndex Index,
    string Source,
    string LicenseListVersion,
    string DataRef,
    string LicensesSha256,
    string ExceptionsSha256)
{
    public static SpdxData Load(string? directory)
    {
        if (directory is not null and not "")
        {
            return LoadFromDirectory(directory, "cli-argument", "cli-argument");
        }

        if (SpdxStore.TryGetActiveDirectory(out var activeDirectory))
        {
            var version = SpdxStore.GetActiveVersion();
            return LoadFromDirectory(activeDirectory, "user", $"ol/spdx/{version}");
        }

        var licenses = "Apache-2.0\nBSD-2-Clause\nBSD-3-Clause\nISC\nMIT"u8;
        var exceptions = "Classpath-exception-2.0"u8;
        return new SpdxData(
            new SpdxLicenseIndex(["Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "MIT"], ["Classpath-exception-2.0"]),
            "bundled",
            "builtin",
            "bundled/spdx/builtin",
            Convert.ToHexString(SHA256.HashData(licenses)).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(exceptions)).ToLowerInvariant());
    }

    private static SpdxData LoadFromDirectory(string directory, string source, string dataRef)
    {
        var licensesPath = Path.Combine(directory, "licenses.json");
        var exceptionsPath = Path.Combine(directory, "exceptions.json");
        if (!File.Exists(licensesPath) || !File.Exists(exceptionsPath))
        {
            throw new DirectoryNotFoundException("SPDX data directory must contain licenses.json and exceptions.json.");
        }

        var licenses = ReadSpdxData(licensesPath, "licenses", "licenseId");
        var exceptions = ReadSpdxData(exceptionsPath, "exceptions", "licenseExceptionId");
        return new SpdxData(
            new SpdxLicenseIndex(licenses.Ids, exceptions.Ids),
            source,
            licenses.Version,
            dataRef,
            HashFile(licensesPath),
            HashFile(exceptionsPath));
    }

    private static (string Version, string[] Ids) ReadSpdxData(string path, string arrayName, string propertyName)
    {
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(SkipUtf8Bom(bytes));
        var values = document.RootElement.GetProperty(arrayName);
        var ids = new string[values.GetArrayLength()];
        var index = 0;
        foreach (var item in values.EnumerateArray())
        {
            ids[index] = item.GetProperty(propertyName).GetString() ?? string.Empty;
            index++;
        }

        return (document.RootElement.TryGetProperty("licenseListVersion", out var version) ? version.GetString() ?? "unknown" : "unknown", ids);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static ReadOnlyMemory<byte> SkipUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsMemory(3) : bytes;
}

internal static class ScanView
{
    public static ScanComponent[] Apply(ScanComponent[] components, string? dependency, string sort, SortOrder sortOrder)
    {
        var filtered = FilterByDependency(components, dependency);
        Array.Sort(filtered, CreateComparison(sort, sortOrder));
        return filtered;
    }

    public static GroupRow[] Group(ScanComponent[] components, string groupBy)
    {
        var fields = ParseGroupFields(groupBy);
        var groups = new Dictionary<string, GroupRowBuilder>(StringComparer.Ordinal);
        for (var i = 0; i < components.Length; i++)
        {
            var values = CreateGroupValues(components[i], fields);
            var key = string.Join('\u001f', values);
            if (!groups.TryGetValue(key, out var builder))
            {
                builder = new GroupRowBuilder(values);
                groups[key] = builder;
            }

            builder.Components.Add(components[i]);
        }

        var result = new GroupRow[groups.Count];
        var index = 0;
        foreach (var group in groups.Values)
        {
            result[index] = new GroupRow(group.Values, group.Components.Count, group.Components.ToArray());
            index++;
        }

        Array.Sort(result, CompareGroupRows);
        return result;
    }

    public static int CountExcludedUnknown(ReadOnlySpan<ScanComponent> components, string dependency)
    {
        var includesUnknown = false;
        foreach (var token in dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParseDependency(token) == DependencyType.Unknown)
            {
                includesUnknown = true;
                break;
            }
        }

        if (includesUnknown)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (components[i].DependencyType == DependencyType.Unknown)
            {
                count++;
            }
        }

        return count;
    }

    private static ScanComponent[] FilterByDependency(ScanComponent[] components, string? dependency)
    {
        if (dependency is null or "")
        {
            return components.ToArray();
        }

        Span<bool> allowed = stackalloc bool[4];
        foreach (var token in dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            allowed[(int)ParseDependency(token)] = true;
        }

        var filtered = new ScanComponent[components.Length];
        var count = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (allowed[(int)components[i].DependencyType])
            {
                filtered[count] = components[i];
                count++;
            }
        }

        return filtered.AsSpan(0, count).ToArray();
    }

    private static DependencyType ParseDependency(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "root" => DependencyType.Root,
            "direct" => DependencyType.Direct,
            "transitive" => DependencyType.Transitive,
            "unknown" => DependencyType.Unknown,
            _ => throw new ArgumentException($"Unknown dependency value: {value}"),
        };
    }

    private static Comparison<ScanComponent> CreateComparison(string sort, SortOrder sortOrder)
    {
        var keys = sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (left, right) =>
        {
            for (var i = 0; i < keys.Length; i++)
            {
                var comparison = CompareByKey(left, right, keys[i]);
                if (comparison != 0)
                {
                    return sortOrder == SortOrder.Desc ? -comparison : comparison;
                }
            }

            return 0;
        };
    }

    private static int CompareByKey(ScanComponent left, ScanComponent right, string key)
    {
        return key.ToLowerInvariant() switch
        {
            "name" => string.CompareOrdinal(left.Name, right.Name),
            "version" => string.CompareOrdinal(left.Version, right.Version),
            "license" => string.CompareOrdinal(left.License, right.License),
            "ecosystem" => string.CompareOrdinal(left.Ecosystem, right.Ecosystem),
            "dependency" => left.DependencyType.CompareTo(right.DependencyType),
            "status" => left.Status.CompareTo(right.Status),
            "purl" => string.CompareOrdinal(left.Purl, right.Purl),
            _ => throw new ArgumentException($"Unknown sort key: {key}"),
        };
    }

    private static GroupField[] ParseGroupFields(string groupBy)
    {
        var tokens = groupBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fields = new GroupField[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            fields[i] = tokens[i].ToLowerInvariant() switch
            {
                "name" => GroupField.Name,
                "version" => GroupField.Version,
                "license" => GroupField.License,
                "ecosystem" => GroupField.Ecosystem,
                "dependency" => GroupField.Dependency,
                "status" => GroupField.Status,
                _ => throw new ArgumentException($"Unknown group key: {tokens[i]}"),
            };
        }

        return fields;
    }

    private static string[] CreateGroupValues(ScanComponent component, GroupField[] fields)
    {
        var values = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            values[i] = fields[i] switch
            {
                GroupField.Name => component.Name,
                GroupField.Version => component.Version,
                GroupField.License => component.License,
                GroupField.Ecosystem => component.Ecosystem,
                GroupField.Dependency => component.DependencyType.ToString().ToLowerInvariant(),
                GroupField.Status => component.Status.ToString().ToLowerInvariant(),
                _ => throw new ArgumentOutOfRangeException(nameof(fields)),
            };
        }

        return values;
    }

    private static int CompareGroupRows(GroupRow left, GroupRow right)
    {
        for (var i = 0; i < left.Values.Length; i++)
        {
            var comparison = string.CompareOrdinal(left.Values[i], right.Values[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }
}

internal enum GroupField
{
    Name,
    Version,
    License,
    Ecosystem,
    Dependency,
    Status,
}

internal sealed class GroupRowBuilder(string[] values)
{
    public string[] Values { get; } = values;

    public List<ScanComponent> Components { get; } = [];
}

internal readonly record struct GroupRow(string[] Values, int Count, ScanComponent[] Components);

internal static class ReportRenderer
{
    public static string RenderText(ReadOnlySpan<ScanComponent> components, bool verbose)
    {
        var builder = new StringBuilder();
        builder.AppendLine(verbose ? "NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS PURL" : "NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS");
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            builder.Append(Display(component.Name));
            builder.Append(' ');
            builder.Append(Display(component.Version));
            builder.Append(' ');
            builder.Append(component.License);
            builder.Append(' ');
            builder.Append(Display(component.Ecosystem));
            builder.Append(' ');
            builder.Append(component.DependencyType.ToString().ToLowerInvariant());
            builder.Append(' ');
            builder.Append(component.Status.ToString().ToLowerInvariant());
            if (verbose)
            {
                builder.Append(' ');
                builder.Append(Display(component.Purl));
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string RenderMarkdown(ReadOnlySpan<ScanComponent> components, bool verbose)
    {
        var builder = new StringBuilder();
        builder.AppendLine(verbose ? "| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS | PURL |" : "| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |");
        builder.AppendLine(verbose ? "|---|---|---|---|---|---|---|" : "|---|---|---|---|---|---|");
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            builder.Append("| ");
            AppendMarkdownValue(builder, component.Name);
            builder.Append(" | ");
            AppendMarkdownValue(builder, component.Version);
            builder.Append(" | ");
            AppendMarkdownValue(builder, component.License);
            builder.Append(" | ");
            AppendMarkdownValue(builder, component.Ecosystem);
            builder.Append(" | ");
            builder.Append(component.DependencyType.ToString().ToLowerInvariant());
            builder.Append(" | ");
            builder.Append(component.Status.ToString().ToLowerInvariant());
            if (verbose)
            {
                builder.Append(" | ");
                AppendMarkdownValue(builder, component.Purl);
            }

            builder.AppendLine(" |");
        }

        return builder.ToString();
    }

    public static string RenderText(ReadOnlySpan<GroupRow> groups, string groupBy)
    {
        var headers = groupBy.ToUpperInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var builder = new StringBuilder();
        builder.AppendJoin(' ', headers);
        builder.AppendLine(" COUNT");
        for (var i = 0; i < groups.Length; i++)
        {
            for (var valueIndex = 0; valueIndex < groups[i].Values.Length; valueIndex++)
            {
                if (valueIndex != 0)
                {
                    builder.Append(' ');
                }

                builder.Append(Display(groups[i].Values[valueIndex]));
            }

            builder.Append(' ');
            builder.Append(groups[i].Count);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string RenderMarkdown(ReadOnlySpan<GroupRow> groups, string groupBy)
    {
        var headers = groupBy.ToUpperInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var builder = new StringBuilder();
        builder.Append("| ");
        builder.AppendJoin(" | ", headers);
        builder.AppendLine(" | COUNT |");
        builder.Append('|');
        for (var i = 0; i < headers.Length + 1; i++)
        {
            builder.Append("---|");
        }

        builder.AppendLine();
        for (var i = 0; i < groups.Length; i++)
        {
            builder.Append("| ");
            for (var valueIndex = 0; valueIndex < groups[i].Values.Length; valueIndex++)
            {
                AppendMarkdownValue(builder, groups[i].Values[valueIndex]);
                builder.Append(" | ");
            }

            builder.Append(groups[i].Count);
            builder.AppendLine(" |");
        }

        return builder.ToString();
    }

    public static string RenderJson(SbomFormat format, string specVersion, ScanComponent[] components, string sbomPath, ReadOnlySpan<byte> sbomBytes, SpdxData spdx)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("metadata");
            writer.WriteString("tool", "ol");
            writer.WriteStartObject("input");
            writer.WriteString("sbomRef", Path.GetFileName(sbomPath));
            writer.WriteString("sbomFormat", GetFormatName(format));
            writer.WriteString("sbomSpecVersion", specVersion);
            writer.WriteString("sbomSha256", Convert.ToHexString(SHA256.HashData(sbomBytes)).ToLowerInvariant());
            writer.WriteEndObject();
            WriteSpdxMetadata(writer, spdx);
            writer.WriteEndObject();

            writer.WriteStartArray("components");
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                writer.WriteStartObject();
                writer.WriteString("name", component.Name);
                writer.WriteString("version", component.Version);
                writer.WriteString("license", component.License);
                writer.WriteString("ecosystem", component.Ecosystem);
                writer.WriteString("dependency", component.DependencyType.ToString().ToLowerInvariant());
                writer.WriteString("status", component.Status.ToString().ToLowerInvariant());
                writer.WriteString("purl", component.Purl);
                writer.WriteString("sourceId", component.SourceId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            var summary = ScanSummary.Create(components);
            writer.WriteStartObject("summary");
            writer.WriteNumber("matched", summary.Matched);
            writer.WriteNumber("conflict", summary.Conflict);
            writer.WriteNumber("unknown", summary.Unknown);
            writer.WriteNumber("ambiguous", summary.Ambiguous);
            writer.WriteNumber("invalid", summary.Invalid);
            writer.WriteEndObject();
            writer.WriteStartArray("warnings");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string RenderJson(SbomFormat format, string specVersion, GroupRow[] groups, string groupBy, string sbomPath, ReadOnlySpan<byte> sbomBytes, SpdxData spdx)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("metadata");
            writer.WriteString("tool", "ol");
            writer.WriteStartObject("input");
            writer.WriteString("sbomRef", Path.GetFileName(sbomPath));
            writer.WriteString("sbomFormat", GetFormatName(format));
            writer.WriteString("sbomSpecVersion", specVersion);
            writer.WriteString("sbomSha256", Convert.ToHexString(SHA256.HashData(sbomBytes)).ToLowerInvariant());
            writer.WriteEndObject();
            WriteSpdxMetadata(writer, spdx);
            writer.WriteEndObject();

            var headers = groupBy.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            writer.WriteStartArray("groups");
            for (var i = 0; i < groups.Length; i++)
            {
                writer.WriteStartObject();
                for (var valueIndex = 0; valueIndex < headers.Length; valueIndex++)
                {
                    writer.WriteString(headers[valueIndex], groups[i].Values[valueIndex]);
                }

                writer.WriteNumber("count", groups[i].Count);
                writer.WriteStartArray("components");
                for (var componentIndex = 0; componentIndex < groups[i].Components.Length; componentIndex++)
                {
                    var component = groups[i].Components[componentIndex];
                    writer.WriteStartObject();
                    writer.WriteString("name", component.Name);
                    writer.WriteString("version", component.Version);
                    writer.WriteString("ecosystem", component.Ecosystem);
                    writer.WriteString("purl", component.Purl);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("warnings");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void AppendMarkdownValue(StringBuilder builder, string value)
    {
        builder.Append(Display(value).Replace("|", "\\|", StringComparison.Ordinal));
    }

    private static string Display(string value) => value.Length == 0 ? "-" : value;

    private static void WriteSpdxMetadata(Utf8JsonWriter writer, SpdxData spdx)
    {
        writer.WriteStartObject("spdx");
        writer.WriteString("source", spdx.Source);
        writer.WriteString("licenseListVersion", spdx.LicenseListVersion);
        writer.WriteString("dataRef", spdx.DataRef);
        writer.WriteString("licensesSha256", spdx.LicensesSha256);
        writer.WriteString("exceptionsSha256", spdx.ExceptionsSha256);
        writer.WriteEndObject();
    }

    private static string GetFormatName(SbomFormat format) => format switch
    {
        SbomFormat.CycloneDxJson => "CycloneDX",
        SbomFormat.SpdxJson => "SPDX",
        _ => format.ToString(),
    };
}

internal readonly record struct ScanSummary(int Matched, int Conflict, int Unknown, int Ambiguous, int Invalid)
{
    public static ScanSummary Create(ReadOnlySpan<ScanComponent> components)
    {
        var matched = 0;
        var conflict = 0;
        var unknown = 0;
        var ambiguous = 0;
        var invalid = 0;

        for (var i = 0; i < components.Length; i++)
        {
            switch (components[i].Status)
            {
                case LicenseStatus.Matched:
                    matched++;
                    break;
                case LicenseStatus.Conflict:
                    conflict++;
                    break;
                case LicenseStatus.Unknown:
                    unknown++;
                    break;
                case LicenseStatus.Ambiguous:
                    ambiguous++;
                    break;
                case LicenseStatus.Invalid:
                    invalid++;
                    break;
            }
        }

        return new ScanSummary(matched, conflict, unknown, ambiguous, invalid);
    }
}
