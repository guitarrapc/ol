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
        var text = format switch
        {
            ReportFormat.Text => ReportRenderer.RenderText(components, verbose),
            ReportFormat.Markdown => ReportRenderer.RenderMarkdown(components, verbose),
            ReportFormat.Json => ReportRenderer.RenderJson(report.Format, components, sbom, sbomBytes, spdx),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        if (outFile is { Length: > 0 })
        {
            File.WriteAllText(outFile, text, Encoding.UTF8);
            Console.WriteLine(outFile);
        }
        else
        {
            Console.Write(text);
        }

        if (!quiet)
        {
            var summary = ScanSummary.Create(components);
            Console.Error.WriteLine($"components: {components.Length}; matched: {summary.Matched}; conflict: {summary.Conflict}; unknown: {summary.Unknown}; ambiguous: {summary.Ambiguous}; invalid: {summary.Invalid}; format: {report.Format}; spdx: {spdx.Source}");
        }

        return 0;
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

internal readonly record struct SpdxData(SpdxLicenseIndex Index, string Source)
{
    public static SpdxData Load(string? directory)
    {
        if (directory is not null and not "")
        {
            return LoadFromDirectory(directory, directory);
        }

        if (SpdxStore.TryGetActiveDirectory(out var activeDirectory))
        {
            return LoadFromDirectory(activeDirectory, SpdxStore.GetActiveVersion());
        }

        return new SpdxData(new SpdxLicenseIndex(["Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "MIT"], ["Classpath-exception-2.0"]), "bundled");
    }

    private static SpdxData LoadFromDirectory(string directory, string source)
    {
        var licensesPath = Path.Combine(directory, "licenses.json");
        var exceptionsPath = Path.Combine(directory, "exceptions.json");
        if (!File.Exists(licensesPath) || !File.Exists(exceptionsPath))
        {
            throw new DirectoryNotFoundException("SPDX data directory must contain licenses.json and exceptions.json.");
        }

        return new SpdxData(new SpdxLicenseIndex(ReadSpdxIds(licensesPath, "licenses", "licenseId"), ReadSpdxIds(exceptionsPath, "exceptions", "licenseExceptionId")), source);
    }

    private static string[] ReadSpdxIds(string path, string arrayName, string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var values = document.RootElement.GetProperty(arrayName);
        var ids = new string[values.GetArrayLength()];
        var index = 0;
        foreach (var item in values.EnumerateArray())
        {
            ids[index] = item.GetProperty(propertyName).GetString() ?? string.Empty;
            index++;
        }

        return ids;
    }
}

internal static class ScanView
{
    public static ScanComponent[] Apply(ScanComponent[] components, string? dependency, string sort, SortOrder sortOrder)
    {
        var filtered = FilterByDependency(components, dependency);
        Array.Sort(filtered, CreateComparison(sort, sortOrder));
        return filtered;
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
}

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

    public static string RenderJson(SbomFormat format, ScanComponent[] components, string sbomPath, ReadOnlySpan<byte> sbomBytes, SpdxData spdx)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("metadata");
            writer.WriteString("tool", "ol");
            writer.WriteString("sbom", Path.GetFileName(sbomPath));
            writer.WriteString("sbomSha256", Convert.ToHexString(SHA256.HashData(sbomBytes)).ToLowerInvariant());
            writer.WriteString("format", format.ToString());
            writer.WriteString("spdxSource", spdx.Source);
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
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void AppendMarkdownValue(StringBuilder builder, string value)
    {
        builder.Append(Display(value).Replace("|", "\\|", StringComparison.Ordinal));
    }

    private static string Display(string value) => value.Length == 0 ? "-" : value;
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
